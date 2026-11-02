using Microsoft.EntityFrameworkCore;
using SaturdayPulse.Contracts.Responses;
using SaturdayPulse.Data;
using SaturdayPulse.Extensions;
using SaturdayPulse.Models;
using SaturdayPulse.Repositories.Interfaces;
using System.Globalization;

namespace SaturdayPulse.Repositories.Implementations
{
    public class GamesRepository : IGamesRepository
    {
        private readonly NCAAContext _context;
        public GamesRepository(NCAAContext context) => _context = context;

        public async Task<List<int>> GetUnplayedYearDistinct(int year, CancellationToken token = default)
            => await _context.Games
                .Where(g => g.Year >= year && g.HomePoints == null && g.AwayPoints == null)
                .Select(wr => wr.Year)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync(token);

        /// <summary>
        /// Distinct weeks for a year where a game exists with BOTH HomePoints and
        /// AwayPoints still null — i.e. not yet played. Ascending order, so the
        /// highest unplayed week is always the last element. Added for
        /// GamePredictionService.PredictSandboxMatchupAsync to detect an
        /// in-progress season and route it through the live K=4 blend instead of
        /// the raw TeamRecords snapshot (which, for an in-progress year, was last
        /// synced from WeeklyRankings' locked-projection rows, not real results).
        /// </summary>
        public Task<List<int>> GetUnplayedWeeksAsync(int year, CancellationToken token = default)
            => _context.Games
                .Where(g => g.Year == year && g.HomePoints == null && g.AwayPoints == null)
                .Select(g => (int)g.Week)
                .Distinct()
                .OrderBy(w => w)
                .ToListAsync(token);

        public Task<List<Games>> GetByYearAsync(int year, CancellationToken token = default)
            => _context.Games.Where(g => g.Year == year).OrderBy(g => g.Week).ToListAsync(token);
        public Task<List<Games>> GetByYearAndWeekAsync(int year, int week, CancellationToken token = default)
            => _context.Games.Where(g => g.Year == year && g.Week == week).ToListAsync(token);

        public async Task<List<Games>> GetGamesForCurrentWeekAsync(int year, CancellationToken token = default)
        {
            var weekMins = await _context.Games
                    .Where(g => g.Year == year && g.GameDate != null)
                    .GroupBy(g => g.Week)
                    .Select(gr => new { Week = gr.Key, MinDate = gr.Min(g => g.GameDate) })
                    .OrderBy(w => w.Week)
                    .ToListAsync(token);

            if (weekMins.Count == 0) return new List<Games>();

            var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            int? activeWeek = null;
            for (int i = 0; i < weekMins.Count; i++)
            {
                var currentMin = weekMins[i].MinDate;
                if (currentMin == null) continue;

                var nextMin = i + 1 < weekMins.Count ? weekMins[i + 1].MinDate : null;

                if (nextMin == null) continue;

                if (string.Compare(todayStr, currentMin, StringComparison.Ordinal) >= 0 &&
                    string.Compare(todayStr, nextMin, StringComparison.Ordinal) < 0)
                {
                    activeWeek = weekMins[i].Week;
                    break;
                }
            }

            if (activeWeek == null) return new List<Games>();

            return await _context.Games
                .Where(g => g.Year == year && (int)g.Week == activeWeek.Value)
                .ToListAsync(token);
        }


        public Task<Games?> GetByGameIdAsync(int gameId, CancellationToken token = default)
            => _context.Games.FirstOrDefaultAsync(g => g.GameId == gameId, token);

        public async Task UpsertAsync(Games game, CancellationToken token = default)
        {
            var existing = await _context.Games
                .FirstOrDefaultAsync(g => g.GameId == game.GameId, token);

            if (existing == null)
                _context.Games.Add(game);
            else
            {
                // Team identity must be synced BEFORE scores — see SyncTeamIdentity.
                SyncTeamIdentity(existing, game);
                ApplyScoresFromGame(existing, game);

                existing.Attendance     = game.Attendance;
                existing.NeutralSite    = game.NeutralSite;
                existing.ConferenceGame = game.ConferenceGame;
                existing.Venue          = game.Venue;
            }
        }
        public async Task<List<Games>> GetRivalryHistoryAsync(int team1Id, int team2Id, int cutoffYear, CancellationToken token = default)
        {
            return await _context.Games
                .Where(g => g.Year >= cutoffYear &&
                            ((g.HomeId == team1Id && g.AwayId == team2Id) ||
                             (g.HomeId == team2Id && g.AwayId == team1Id)))
                .OrderByDescending(g => g.Year)
                .ThenBy(g => g.Week)
                .ToListAsync(token);
        }

        public Task<List<Games>> GetGamesSinceYearAsync(int fromYear, CancellationToken token = default)
            => _context.Games
                .Where(g => g.Year >= fromYear)
                .ToListAsync(token);

        public Task<List<Games>> GetPlayedGamesSinceYearAsync(int fromYear, CancellationToken token = default)
            => _context.Games
                .Where(g => g.Year >= fromYear &&
                            ((g.HomePoints ?? 0) > 0 || (g.AwayPoints ?? 0) > 0))
                .ToListAsync(token);

        public Task<List<Games>> GetPlayedGamesByYearAndWeekAsync(int year, int week, CancellationToken token = default)
            => _context.Games
                .Where(g => g.Year == year &&
                            g.Week <= week &&
                            ((g.HomePoints ?? 0) > 0 || (g.AwayPoints ?? 0) > 0))
                .ToListAsync(token);

        public async Task<List<PlayedWeekDto>> GetPlayedWeeksByYearAsync(
            int year,
            CancellationToken token = default)
        {
            var databaseData = await _context.Games
                .Where(g => g.Year == year && g.HomePoints != 0 && g.AwayPoints != 0)
                .Select(g => new { g.Week, g.GameDate })
                .Distinct()
                .OrderBy(w => w.Week)
                .ToListAsync(token);

            return databaseData
                .Select(w => new PlayedWeekDto((int)w.Week, w.GameDate))
                .ToList();
        }

        public async Task<Dictionary<int, int>> GetLastPlayedWeekByTeamAsync(
            int year,
            CancellationToken token = default)
        {
            // HomeId/AwayId (nullable int, ?? 0) — matches GetRivalryHistoryAsync,
            // GetGameParticipantsAsync, etc. below. NOT HomeTeamId/AwayTeamId —
            // that was Games confused with Projection's column names, doesn't
            // compile against this model.
            var homeRows = await _context.Games
                .Where(g => g.Year == year && g.HomePoints != 0 && g.AwayPoints != 0)
                .Select(g => new { TeamId = g.HomeId ?? 0, g.Week })
                .ToListAsync(token);

            var awayRows = await _context.Games
                .Where(g => g.Year == year && g.HomePoints != 0 && g.AwayPoints != 0)
                .Select(g => new { TeamId = g.AwayId ?? 0, g.Week })
                .ToListAsync(token);

            return homeRows
                .Concat(awayRows)
                .GroupBy(r => r.TeamId)
                .ToDictionary(g => g.Key, g => (int)g.Max(r => r.Week));
        }

        public async Task<List<GameParticipant>> GetGameParticipantsAsync(
            int year, CancellationToken token = default)
        {
            var fromHome = from g in _context.Games
                where g.Year == year
                join t   in _context.Teams on g.HomeId equals t.TeamId
                join opp in _context.Teams on g.AwayId equals opp.TeamId
                select new GameParticipant
                {
                    TeamId           = g.HomeId ?? 0,
                    TeamDivision     = t.Division,
                    OpponentId       = g.AwayId ?? 0,
                    OpponentDivision = opp.Division,
                    TeamPoints       = g.HomePoints ?? 0,
                    OpponentPoints   = g.AwayPoints ?? 0,
                    Location         = g.NeutralSite == true ? 'N' : 'H',
                    IsHomeTeam       = true,
                    Week             = g.Week
                };

            var fromAway = from g in _context.Games
                where g.Year == year
                join t   in _context.Teams on g.AwayId equals t.TeamId
                join opp in _context.Teams on g.HomeId equals opp.TeamId
                select new GameParticipant
                {
                    TeamId           = g.AwayId ?? 0,
                    TeamDivision     = t.Division,
                    OpponentId       = g.HomeId ?? 0,
                    OpponentDivision = opp.Division,
                    TeamPoints       = g.AwayPoints ?? 0,
                    OpponentPoints   = g.HomePoints ?? 0,
                    Location         = g.NeutralSite == true ? 'N' : 'A',
                    IsHomeTeam       = false,
                    Week = g.Week
                };

            return await fromHome.Union(fromAway).ToListAsync(token);
        }

        public Task<List<Games>> GetPostSeasonByYear(int year, CancellationToken token = default)
            => _context.Games
                .Where(g => g.Year == year &&
                            new List<string> { "postseason", "playoff" }.Contains(g.SeasonType))
                .OrderBy(g => g.SeasonType)
                .ThenBy(g => g.Week)
                .ToListAsync(token);
        
        public async Task<List<Games>> GetByIds(List<int> gameIds, CancellationToken token = default)
            => await _context.Games
                        .Where(g => gameIds.Contains(g.GameId))
                        .ToListAsync(token);


        public async Task UpsertRangeAsync(IEnumerable<Games> games, CancellationToken token = default)
        {
            var incoming    = games.ToList();
            var incomingIds = incoming.Select(g => g.GameId).ToHashSet();

            var existing = await _context.Games
                .Where(g => incomingIds.Contains(g.GameId))
                .ToDictionaryAsync(g => g.GameId, token);

            foreach (var game in incoming)
            {
                if (existing.TryGetValue(game.GameId, out var dbGame))
                {
                    // Team identity must be synced BEFORE scores — see SyncTeamIdentity.
                    SyncTeamIdentity(dbGame, game);
                    ApplyScoresFromGame(dbGame, game);

                    dbGame.Attendance     = game.Attendance;
                    dbGame.NeutralSite    = game.NeutralSite;
                    dbGame.ConferenceGame = game.ConferenceGame;
                    dbGame.Venue          = game.Venue;
                    dbGame.KickoffTime    = game.KickoffTime;
                    dbGame.GameDate       = game.GameDate;
                    dbGame.GameDay        = game.GameDay;
                    // Week / SeasonType intentionally NOT synced — owned by
                    // AssignPostseasonWeeksAsync / SetSeasonTypeAsync.
                }
                else
                    _context.Games.Add(game);
            }
        }

        /// <summary>
        /// Score-only update for an existing game, keyed by team id. Used by
        /// partial sources (/live/plays, the game-day poller) that are NOT
        /// authoritative for home/away designation — the stored row's
        /// orientation is kept and each score is matched to its team by id.
        /// Returns false (no change) if the row is missing or the two team ids
        /// don't match the stored participants. Caller saves.
        /// </summary>
        public async Task<bool> ApplyScoresByTeamAsync(
            int gameId, TeamScoreUpdate first, TeamScoreUpdate second, CancellationToken token = default)
        {
            var dbGame = await _context.Games.FirstOrDefaultAsync(g => g.GameId == gameId, token);
            if (dbGame == null)
            {
                Console.WriteLine($"GamesRepository.ApplyScoresByTeamAsync: no Games row for gameId={gameId}");
                return false;
            }

            return TryApplyScores(dbGame, first, second);
        }

        /// <summary>
        /// Full-row upsert path (UpsertRangeAsync / UpsertAsync). Runs after
        /// SyncTeamIdentity, so ids match the incoming orientation and the
        /// shared resolver applies cleanly. TBD placeholders (null ids) have no
        /// team to key on, so they fall back to a positional copy.
        /// </summary>
        private static void ApplyScoresFromGame(Games dbGame, Games incoming)
        {
            if (incoming.HomeId.HasValue && incoming.AwayId.HasValue &&
                TryApplyScores(
                    dbGame,
                    new TeamScoreUpdate(incoming.HomeId.Value, incoming.HomePoints, incoming.HomeLineScores),
                    new TeamScoreUpdate(incoming.AwayId.Value, incoming.AwayPoints, incoming.AwayLineScores)))
                return;

            dbGame.HomePoints     = incoming.HomePoints;
            dbGame.AwayPoints     = incoming.AwayPoints;
            dbGame.HomeLineScores = incoming.HomeLineScores;
            dbGame.AwayLineScores = incoming.AwayLineScores;
        }

        /// <summary>
        /// The single score writer for Games. Resolves each TeamScoreUpdate to
        /// the stored Home/Away slot by TeamId, in either argument order.
        /// LineScores null = leave stored value unchanged.
        /// </summary>
        private static bool TryApplyScores(Games dbGame, TeamScoreUpdate first, TeamScoreUpdate second)
        {
            TeamScoreUpdate home;
            TeamScoreUpdate away;

            if (dbGame.HomeId == first.TeamId && dbGame.AwayId == second.TeamId)
            {
                home = first;
                away = second;
            }
            else if (dbGame.HomeId == second.TeamId && dbGame.AwayId == first.TeamId)
            {
                home = second;
                away = first;
            }
            else
            {
                Console.WriteLine($"GamesRepository.TryApplyScores: gameId={dbGame.GameId} team mismatch — " +
                                  $"stored {dbGame.HomeId}/{dbGame.AwayId}, incoming {first.TeamId}/{second.TeamId}; scores not applied");
                return false;
            }

            dbGame.HomePoints = home.Points;
            dbGame.AwayPoints = away.Points;

            if (home.LineScores != null)
                dbGame.HomeLineScores = home.LineScores;
            if (away.LineScores != null)
                dbGame.AwayLineScores = away.LineScores;

            return true;
        }

        /// <summary>
        /// Syncs HomeId/HomeName/AwayId/AwayName on an existing row to the
        /// incoming CFBD orientation. Scores and line scores are copied
        /// positionally (Home→Home, Away→Away), so the stored team identity MUST
        /// match the incoming orientation or the scores land on the wrong team.
        ///
        /// CFBD (via ESPN) re-designates home/away on neutral-site games after
        /// initial scheduling — e.g. Navy/Notre Dame 2026 wk 9 (401859185) was
        /// originally inserted ND-home, CFBD later reported Navy-home, and the
        /// positional score copy put Navy's 13 on Notre Dame. CFBD's current
        /// orientation is adopted (rather than swapping incoming values to fit
        /// the stored row) so Games stays consistent with Lines, which
        /// LoadLinesAsync reloads in CFBD's current home perspective.
        ///
        /// Also picks up TBD postseason opponents once CFBD fills them in.
        /// Skipped when CFBD sends null ids so a known team is never blanked.
        /// </summary>
        private static void SyncTeamIdentity(Games dbGame, Games incoming)
        {
            if (!incoming.HomeId.HasValue || !incoming.AwayId.HasValue)
                return;

            if (dbGame.HomeId == incoming.HomeId && dbGame.AwayId == incoming.AwayId)
                return;

            var flipped = dbGame.HomeId == incoming.AwayId && dbGame.AwayId == incoming.HomeId;

            Console.WriteLine(flipped
                ? $"GamesRepository: gameId={dbGame.GameId} home/away re-designated by CFBD — " +
                  $"was {dbGame.HomeName}(home) vs {dbGame.AwayName}, now {incoming.HomeName}(home) vs {incoming.AwayName}"
                : $"GamesRepository: gameId={dbGame.GameId} participants changed — " +
                  $"was {dbGame.HomeId}/{dbGame.AwayId}, now {incoming.HomeId}/{incoming.AwayId}");

            dbGame.HomeId   = incoming.HomeId;
            dbGame.HomeName = incoming.HomeName;
            dbGame.AwayId   = incoming.AwayId;
            dbGame.AwayName = incoming.AwayName;
        }
    }
}
