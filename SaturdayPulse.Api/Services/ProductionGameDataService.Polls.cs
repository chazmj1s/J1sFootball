using SaturdayPulse.Models;

namespace SaturdayPulse.Services
{
    /// <summary>
    /// ProductionGameDataService — AP / Coaches polls.
    ///
    /// Kept separate from GetPowerRankingsV2Async on purpose: that method is also
    /// consumed by PlayoffSeedingService, which has no use for poll data, so the
    /// polls are fetched alongside rankings at the controller level instead of
    /// being folded into PowerRankingsResult.
    /// </summary>
    public partial class ProductionGameDataService
    {
        /// <summary>
        /// AP and Coaches polls for the same (year, week) the Rankings page is
        /// showing. Year defaults the same way GetPowerRankingsV2Async does. No
        /// week (the season-aggregate rankings view) or a week with no published
        /// poll returns WeeklyPolls.Empty — the page then shows J1s only.
        /// </summary>
        public Task<WeeklyPolls> GetWeeklyPollsAsync(
            int? year, int? week, CancellationToken token = default)
        {
            var targetYear = year ?? DateTime.Now.Year;

            if (!week.HasValue || week.Value <= 0)
                return Task.FromResult(WeeklyPolls.Empty(targetYear, week ?? 0));

            return _pollRankingsLoader.GetWeekAsync(targetYear, week.Value, token);
        }
    }
}
