using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SaturdayPulse.Models;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaturdayPulse.Utilities
{
    /// <summary>
    /// Reads AP Top 25 and Coaches Poll data from CFBD's /rankings endpoint and
    /// serves it per (year, week) from IMemoryCache. Standalone — not wired into
    /// GameScorePollingService and makes no DB writes.
    ///
    /// One CFBD call returns every week of the season, so the whole year is
    /// fetched and cached as a unit (key "polls:{year}"). Published polls don't
    /// change, so a cached week is served as-is. A requested week that isn't in
    /// the cache (e.g. this week's poll not released yet) triggers a re-fetch of
    /// the year, throttled to once per MissingWeekRefetchInterval so a rankings
    /// page load during the week doesn't hit CFBD on every request. Past seasons
    /// are never re-fetched for a missing week.
    ///
    /// Only seasonType "regular" is kept: CFBD numbers the postseason/final poll
    /// as its own week 1, which would otherwise collide with the preseason poll.
    /// Week matching is exact against the header Week selector — a week with no
    /// poll returns WeeklyPolls.Empty, and the caller shows J1s only.
    ///
    /// A CFBD failure is logged and returns Empty; it never throws into the
    /// rankings call.
    /// </summary>
    public class PollRankingsLoader(
        IHttpClientFactory _httpClientFactory,
        IMemoryCache _memoryCache,
        ILogger<PollRankingsLoader> _logger)
    {
        public const string ApPollName = "AP Top 25";
        public const string CoachesPollName = "Coaches Poll";

        private const string RegularSeasonType = "regular";

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
        private static readonly TimeSpan MissingWeekRefetchInterval = TimeSpan.FromMinutes(15);

        // Prevents a burst of concurrent rankings requests from each firing its
        // own CFBD /rankings call on a cold or stale cache.
        private static readonly SemaphoreSlim FetchLock = new(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private HttpClient CfbdClient => _httpClientFactory.CreateClient("cfbd");

        private static string CacheKey(int year) => $"polls:{year}";

        /// <summary>
        /// AP and Coaches polls for the given regular-season week. Returns
        /// WeeklyPolls.Empty when no poll exists for that week or CFBD is
        /// unavailable.
        /// </summary>
        public async Task<WeeklyPolls> GetWeekAsync(int year, int week, CancellationToken cancellationToken = default)
        {
            if (year <= 0 || week <= 0)
                return WeeklyPolls.Empty(year, week);

            if (TryServeFromCache(year, week, out var served))
                return served;

            await FetchLock.WaitAsync(cancellationToken);
            try
            {
                // Re-check: another request may have refreshed the cache while
                // this one was waiting on the lock.
                if (TryServeFromCache(year, week, out served))
                    return served;

                var existing = _memoryCache.Get<PollYearCache>(CacheKey(year));
                var fetched = await FetchYearAsync(year, cancellationToken);

                if (fetched == null)
                {
                    // Keep any weeks already cached, but stamp the attempt so the
                    // throttle applies and a CFBD outage isn't retried per request.
                    var fallback = new PollYearCache(
                        existing?.Weeks ?? new Dictionary<int, WeeklyPolls>(),
                        DateTime.UtcNow);

                    _memoryCache.Set(
                        CacheKey(year),
                        fallback,
                        existing == null ? MissingWeekRefetchInterval : CacheLifetime);

                    return fallback.Weeks.TryGetValue(week, out var stale)
                        ? stale
                        : WeeklyPolls.Empty(year, week);
                }

                _memoryCache.Set(CacheKey(year), fetched, CacheLifetime);

                return fetched.Weeks.TryGetValue(week, out var result)
                    ? result
                    : WeeklyPolls.Empty(year, week);
            }
            finally
            {
                FetchLock.Release();
            }
        }

        /// <summary>
        /// True when the request can be answered without calling CFBD: either the
        /// week is cached, or it's known-missing and a re-fetch isn't due yet.
        /// </summary>
        private bool TryServeFromCache(int year, int week, out WeeklyPolls result)
        {
            result = WeeklyPolls.Empty(year, week);

            var cached = _memoryCache.Get<PollYearCache>(CacheKey(year));
            if (cached == null)
                return false;

            if (cached.Weeks.TryGetValue(week, out var hit))
            {
                result = hit;
                return true;
            }

            // Past seasons are complete — a missing week there will never appear.
            if (year < DateTime.UtcNow.Year)
                return true;

            return DateTime.UtcNow - cached.FetchedUtc < MissingWeekRefetchInterval;
        }

        private async Task<PollYearCache?> FetchYearAsync(int year, CancellationToken cancellationToken)
        {
            List<CfbdRankingsWeekDto>? payload;

            try
            {
                var response = await CfbdClient.GetAsync($"/rankings?year={year}", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "PollRankingsLoader: CFBD /rankings?year={Year} returned {StatusCode}.",
                        year, response.StatusCode);
                    return null;
                }

                payload = await response.Content.ReadFromJsonAsync<List<CfbdRankingsWeekDto>>(JsonOptions, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PollRankingsLoader: CFBD /rankings?year={Year} call failed.", year);
                return null;
            }

            var weeks = new Dictionary<int, WeeklyPolls>();

            foreach (var weekDto in payload ?? [])
            {
                if (!string.Equals(weekDto.SeasonType, RegularSeasonType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (weekDto.Week is not int weekNumber || weekNumber <= 0)
                    continue;

                var ap = BuildRows(FindPoll(weekDto, ApPollName));
                var coaches = BuildRows(FindPoll(weekDto, CoachesPollName));

                if (ap.Count == 0 && coaches.Count == 0)
                    continue;

                weeks[weekNumber] = new WeeklyPolls(year, weekNumber, ap, coaches);
            }

            _logger.LogInformation(
                "PollRankingsLoader: loaded AP/Coaches polls for {Year}, weeks [{Weeks}].",
                year, string.Join(",", weeks.Keys.OrderBy(w => w)));

            return new PollYearCache(weeks, DateTime.UtcNow);
        }

        private static CfbdPollDto? FindPoll(CfbdRankingsWeekDto weekDto, string pollName) =>
            weekDto.Polls?.FirstOrDefault(p =>
                string.Equals(p.Poll, pollName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Converts a CFBD poll to display rows. Ties (two teams sharing a rank,
        /// e.g. AP week 1 USC/BYU at 14) render as "T-14"; a tie at 25 produces a
        /// 26th row, which is kept rather than truncated.
        /// </summary>
        private static IReadOnlyList<PollRankingRow> BuildRows(CfbdPollDto? poll)
        {
            if (poll?.Ranks == null || poll.Ranks.Count == 0)
                return [];

            var valid = poll.Ranks
                .Where(r => r.Rank.HasValue && r.TeamId.HasValue)
                .ToList();

            var countPerRank = valid
                .GroupBy(r => r.Rank!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            return valid
                .OrderBy(r => r.Rank!.Value)
                .ThenByDescending(r => r.Points ?? 0)
                .ThenBy(r => r.School, StringComparer.OrdinalIgnoreCase)
                .Select(r =>
                {
                    var rank = r.Rank!.Value;
                    var display = countPerRank[rank] > 1
                        ? $"T-{rank.ToString(CultureInfo.InvariantCulture)}"
                        : rank.ToString(CultureInfo.InvariantCulture);

                    return new PollRankingRow(
                        rank,
                        display,
                        r.TeamId!.Value,
                        r.School ?? string.Empty,
                        r.Conference,
                        r.FirstPlaceVotes,
                        r.Points);
                })
                .ToList();
        }

        private sealed record PollYearCache(IReadOnlyDictionary<int, WeeklyPolls> Weeks, DateTime FetchedUtc);

        // ── CFBD /rankings wire shape (confirmed against 2026 payload) ─────────
        // Everything nullable: real data overrules the Swagger docs.

        private sealed class CfbdRankingsWeekDto
        {
            [JsonPropertyName("season")] public int? Season { get; set; }
            [JsonPropertyName("seasonType")] public string? SeasonType { get; set; }
            [JsonPropertyName("week")] public int? Week { get; set; }
            [JsonPropertyName("polls")] public List<CfbdPollDto>? Polls { get; set; }
        }

        private sealed class CfbdPollDto
        {
            [JsonPropertyName("poll")] public string? Poll { get; set; }
            [JsonPropertyName("isFinal")] public bool? IsFinal { get; set; }
            [JsonPropertyName("ranks")] public List<CfbdPollRankDto>? Ranks { get; set; }
        }

        private sealed class CfbdPollRankDto
        {
            [JsonPropertyName("rank")] public int? Rank { get; set; }
            [JsonPropertyName("teamId")] public int? TeamId { get; set; }
            [JsonPropertyName("school")] public string? School { get; set; }
            [JsonPropertyName("conference")] public string? Conference { get; set; }
            [JsonPropertyName("firstPlaceVotes")] public int? FirstPlaceVotes { get; set; }
            [JsonPropertyName("points")] public int? Points { get; set; }
        }
    }
}
