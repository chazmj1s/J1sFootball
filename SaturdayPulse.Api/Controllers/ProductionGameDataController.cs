using Microsoft.AspNetCore.Mvc;
using SaturdayPulse.Interfaces;
using SaturdayPulse.Contracts.Requests;
using SaturdayPulse.Services;

namespace SaturdayPulse.Controllers
{
    /// <summary>
    /// Production API for game predictions and team data queries.
    ///
    /// All data-access and business logic lives in ProductionGameDataService.
    /// This controller is a thin HTTP wrapper: validate input, call the service,
    /// map results to HTTP responses.
    ///
    /// All legacy (non-V2) endpoints removed 2026-05-19. Client calls V2 exclusively.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ProductionGameDataController(
        ProductionGameDataService gameDataService,
        RosterCapacityService rosterCapacityService,
        IPlayoffSeedingService playoffSeedingService,
        ILogger<ProductionGameDataController> logger) : ControllerBase
    {
        #region Predictions

        /// <summary>
        /// Predicts the score for a single matchup between two teams.
        /// Location: 'H' = team is home, 'A' = team is away, 'N' = neutral site.
        /// Example: GET /api/productiongamedata/predictMatchup?year=2025&teamName=Ohio State&opponentName=Michigan&location=H&week=12
        /// </summary>
        [HttpGet("predictMatchup")]
        public async Task<IActionResult> PredictMatchup(
            [FromQuery] int? year,
            [FromQuery] string teamName,
            [FromQuery] string opponentName,
            [FromQuery] char location = 'N',
            [FromQuery] int week = 0)
        {
            if (string.IsNullOrEmpty(teamName) || string.IsNullOrEmpty(opponentName))
                return BadRequest("Both teamName and opponentName are required.");

            try
            {
                var targetYear = year ?? DateTime.Now.Year;
                var prediction = await gameDataService.PredictMatchupAsync(
                    targetYear, teamName, opponentName, location, week);

                return Ok(new
                {
                    matchup             = $"{prediction.TeamName} {prediction.LocationDisplay} {prediction.OpponentName}",
                    prediction          = $"{prediction.TeamName} {prediction.PredictedTeamScore:F1}, {prediction.OpponentName} {prediction.PredictedOpponentScore:F1}",
                    expectedMargin      = prediction.ExpectedMargin,
                    marginOfError       = prediction.MarginOfError,
                    confidence          = prediction.Confidence,
                    teamRecord          = $"{prediction.TeamWins}-?",
                    opponentRecord      = $"{prediction.OpponentWins}-?",
                    teamPowerRating     = prediction.TeamPowerRating,
                    opponentPowerRating = prediction.OpponentPowerRating,
                    rivalryNote         = prediction.RivalryNote,
                    summary             = prediction.PredictionSummary
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error predicting matchup");
                return StatusCode(500, ex.Message);
            }
        }

        /// <summary>
        /// Predicts scores for multiple matchups provided in the request body.
        /// </summary>
        [HttpPost("predictMatchups")]
        public async Task<IActionResult> PredictMatchups(
            [FromBody] MatchupBatchRequest request,
            CancellationToken token = default)
        {
            try
            {
                var predictions = await gameDataService.PredictMatchupsAsync(
                    request.Year, request.Matchups, token);

                return Ok(new
                {
                    message     = $"Predicted {predictions.Count} matchups for {request.Year}",
                    predictions = predictions.Select(p => new
                    {
                        matchup        = $"{p.TeamName} {p.LocationDisplay} {p.OpponentName}",
                        prediction     = $"{p.TeamName} {p.PredictedTeamScore:F1}, {p.OpponentName} {p.PredictedOpponentScore:F1}",
                        expectedMargin = p.ExpectedMargin,
                        marginOfError  = p.MarginOfError,
                        confidence     = p.Confidence,
                        rivalryNote    = p.RivalryNote,
                        summary        = p.PredictionSummary
                    })
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error predicting matchups");
                return StatusCode(500, ex.Message);
            }
        }

        #endregion

        /// <summary>
        /// Sandbox: predicts a matchup between two teams from potentially different years.
        /// Example: GET /api/productiongamedata/sandbox/predict?teamName=Texas&teamYear=1969&opponentName=Texas&opponentYear=2005
        /// </summary>
        [HttpGet("sandbox/predict")]
        public async Task<IActionResult> SandboxPredict(
            [FromQuery] string teamName,
            [FromQuery] int    teamYear,
            [FromQuery] string opponentName,
            [FromQuery] int    opponentYear,
            CancellationToken  token = default)
        {
            if (string.IsNullOrEmpty(teamName) || string.IsNullOrEmpty(opponentName))
                return BadRequest("Both teamName and opponentName are required.");

            try
            {
                var prediction = await gameDataService.PredictSandboxMatchupAsync(
                    teamName, teamYear, opponentName, opponentYear, token);

                return Ok(new
                {
                    teamName            = prediction.TeamName,
                    teamYear,
                    opponentName        = prediction.OpponentName,
                    opponentYear,
                    predictedTeamScore  = prediction.PredictedTeamScore,
                    predictedOppScore   = prediction.PredictedOpponentScore,
                    expectedMargin      = prediction.ExpectedMargin,
                    marginOfError       = prediction.MarginOfError,
                    confidence          = prediction.Confidence,
                    rivalryNote         = prediction.RivalryNote,
                    summary             = prediction.PredictionSummary
                });
            }
            catch (ArgumentException ex) { return NotFound(ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in sandbox prediction");
                return StatusCode(500, ex.Message);
            }
        }

        /// <summary>
        /// Returns the distinct years a team has WeeklyRankings data for.
        /// Used to populate the year picker in the sandbox team selector.
        /// Example: GET /api/productiongamedata/sandbox/team-years?teamId=47
        /// </summary>
        [HttpGet("sandbox/team-years")]
        public async Task<IActionResult> SandboxTeamYears(
            [FromQuery] int       teamId,
            CancellationToken     token = default)
        {
            try
            {
                var years = await gameDataService.GetTeamAvailableYearsAsync(teamId, token);
                return Ok(years);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving available years for team {TeamId}", teamId);
                return StatusCode(500, ex.Message);
            }
        }

        #region Diagnostics and Queries

        /// <summary>
        /// Diagnostic endpoint to check database data availability.
        /// GET /api/productiongamedata/diagnostic
        /// </summary>
        [HttpGet("diagnostic")]
        public async Task<IActionResult> GetDiagnostic(CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetDiagnosticAsync(token);
                return Ok(new
                {
                    database               = result.Database,
                    totalTeams             = result.TotalTeams,
                    totalGames             = result.TotalGames,
                    totalRecords           = result.TotalRecords,
                    recordsWithPowerRating = result.RecordsWithPowerRating,
                    yearsWithData          = result.YearsWithData,
                    yearStats              = result.YearStats
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting diagnostic info");
                return StatusCode(500, ex.Message);
            }
        }

        /// <summary>
        /// Query team records with filters.
        /// Example: GET /api/productiongamedata/queryTeamRecords?wins=13&losses=3
        /// </summary>
        [HttpGet("queryTeamRecords")]
        public async Task<IActionResult> QueryTeamRecords(
            [FromQuery] int? wins,
            [FromQuery] int? losses,
            [FromQuery] int? minWins,
            [FromQuery] int? maxWins,
            [FromQuery] int? startYear,
            [FromQuery] int? endYear,
            [FromQuery] decimal? minPowerRating,
            [FromQuery] decimal? maxPowerRating,
            [FromQuery] int limit = 50,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.QueryTeamRecordsAsync(
                    wins, losses, minWins, maxWins, startYear, endYear,
                    minPowerRating, maxPowerRating, limit, token);

                return Ok(new { count = result.Count, filters = result.Filters, results = result.Results });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error querying team records");
                return StatusCode(500, "An error occurred while querying team records.");
            }
        }

        /// <summary>
        /// Returns Seed/Trend/Pedigree ratings for all FBS teams in the given year.
        /// Example: GET /api/productiongamedata/rollingAverages?year=2025
        /// </summary>
        [HttpGet("rollingAverages")]
        public async Task<IActionResult> GetRollingAverages(
            [FromQuery] int? year,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetRollingAveragesAsync(year, token);
                return Ok(new { year = result.Year, teamCount = result.TeamCount, rankings = result.Rankings });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error fetching rolling averages for year={Year}", year);
                return StatusCode(500, "An error occurred fetching rolling averages.");
            }
        }

        /// <summary>
        /// Returns Seed/Trend/Pedigree ratings for a single team across all years.
        /// Example: GET /api/productiongamedata/rollingAverages/team?teamId=123
        /// </summary>
        [HttpGet("rollingAverages/team")]
        public async Task<IActionResult> GetTeamRollingAverages(
            [FromQuery] int teamId,
            [FromQuery] int? startYear,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetTeamRollingAveragesAsync(teamId, startYear, token);
                return Ok(new
                {
                    teamId     = result.TeamId,
                    teamName   = result.TeamName,
                    conference = result.Conference,
                    history    = result.History
                });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error fetching rolling averages for teamId={TeamId}", teamId);
                return StatusCode(500, "An error occurred fetching team rolling averages.");
            }
        }

        #endregion

        #region Rankings

        /// <summary>
        /// AP Top 25 / Coaches Poll for one week (CFBD /rankings, server-cached).
        /// Empty lists when no poll exists for the week.
        /// Example: GET /api/productiongamedata/polls/v2?year=2026&week=8
        /// </summary>
        [HttpGet("polls/v2")]
        public async Task<IActionResult> GetWeeklyPollsV2(
            [FromQuery] int? year,
            [FromQuery] int? week,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetWeeklyPollsAsync(year, week, token);
                return Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving polls for {Year} week {Week}", year, week);
                return StatusCode(500, "An error occurred while retrieving polls.");
            }
        }

        /// <summary>
        /// V2: Power rankings from Teams + Conferences (CFBD-sourced).
        /// Example: GET /api/productiongamedata/powerrankings/v2?year=2025&throughWeek=10
        /// </summary>
        [HttpGet("powerrankings/v2")]
        public async Task<IActionResult> GetPowerRankingsV2(
            [FromQuery] int? year,
            [FromQuery] int? throughWeek,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetPowerRankingsV2Async(year, throughWeek, token);
                logger.LogInformation("V2: Returned {Count} rankings for {Year}{Week}",
                    result.Rankings.Count, year ?? DateTime.Now.Year,
                    throughWeek.HasValue ? $" week {throughWeek}" : "");
                return Ok(result.Rankings);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving V2 power rankings");
                return StatusCode(500, "An error occurred while retrieving power rankings.");
            }
        }

        /// <summary>
        /// V2: Seed/Trend/Pedigree ratings for a single team — team lookup via Teams.
        /// Example: GET /api/productiongamedata/rollingAverages/team/v2?teamId=123
        /// </summary>
        [HttpGet("rollingAverages/team/v2")]
        public async Task<IActionResult> GetTeamRollingAveragesV2(
            [FromQuery] int teamId,
            [FromQuery] int? startYear,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetTeamRollingAveragesV2Async(teamId, startYear, token);
                return Ok(new
                {
                    teamId     = result.TeamId,
                    teamName   = result.TeamName,
                    conference = result.Conference,
                    history    = result.History
                });
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error fetching V2 rolling averages for teamId={TeamId}", teamId);
                return StatusCode(500, "An error occurred fetching team rolling averages.");
            }
        }

        /// <summary>
        /// V2: Named rivalries — team names resolved via Teams.
        /// Example: GET /api/productiongamedata/rivalries/named/v2
        /// </summary>
        [HttpGet("rivalries/named/v2")]
        public async Task<IActionResult> GetNamedRivalriesV2(CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetNamedRivalriesV2Async(token);
                return Ok(result.Rivalries);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving V2 named rivalries");
                return StatusCode(500, "An error occurred while retrieving named rivalries.");
            }
        }

        /// <summary>
        /// Query matchup histories and detected rivalries.
        /// Example: GET /api/productiongamedata/rivalries?tier=EPIC&minGames=50
        /// </summary>
        [HttpGet("rivalries")]
        public async Task<IActionResult> GetRivalries(
            [FromQuery] string? tier,
            [FromQuery] int? minGames,
            [FromQuery] double? minVarianceRatio,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetRivalriesAsync(tier, minGames, minVarianceRatio, token);
                return Ok(new
                {
                    totalMatchups   = result.TotalMatchups,
                    totalInDatabase = result.TotalInDatabase,
                    filters         = result.Filters,
                    rivalries       = result.Rivalries
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error querying rivalries");
                return StatusCode(500, "An error occurred while querying rivalries.");
            }
        }

        #endregion

        #region Schedule

        /// <summary>
        /// V2: Full season schedule for a single team from Games + Teams tables.
        /// Example: GET /api/productiongamedata/teamSchedule/v2?teamId=123&year=2025
        /// </summary>
        [HttpGet("postseason/v2")]
        public async Task<IActionResult> GetPostseasonV2(
            [FromQuery] int? year,
            CancellationToken token = default)
        {
            try
            {
                var targetYear = year ?? DateTime.Now.Year;
                var result = await gameDataService.GetPostseasonGamesV2Async(targetYear, token);
                return Ok(new {games = result.Games });
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving V2 postseason schedule for {Year}", year);
                return StatusCode(500, "An error occurred while retrieving the team schedule.");
            }
        }

        /// <summary>
        /// V2: Full season schedule for a single team from Games + Teams tables.
        /// Example: GET /api/productiongamedata/teamSchedule/v2?teamId=123&year=2025
        /// </summary>
        [HttpGet("teamSchedule/v2")]
        public async Task<IActionResult> GetTeamScheduleV2(
            [FromQuery] int teamId,
            [FromQuery] int? year,
            CancellationToken token = default)
        {
            try
            {
                var targetYear = year ?? DateTime.Now.Year;
                var result = await gameDataService.GetTeamScheduleV2Async(teamId, targetYear, token);
                return Ok(new { summary = result.Summary, games = result.Games });
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving V2 team schedule for {TeamId}", teamId);
                return StatusCode(500, "An error occurred while retrieving the team schedule.");
            }
        }

        /// <summary>
        /// V2: Full season schedule from Games table (CFBD-sourced).
        /// Example: GET /api/productiongamedata/schedule/v2?year=2025
        /// </summary>
        [HttpGet("schedule/v2")]
        public async Task<IActionResult> GetScheduleV2(
            [FromQuery] int? year,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetScheduleV2Async(year, token);
                return Ok(result.Games);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving V2 schedule");
                return StatusCode(500, "An error occurred while retrieving the schedule.");
            }
        }

        [HttpGet("conferences/{year:int}")]
        public async Task<IActionResult> GetConferencesForYear(int year, CancellationToken token)
        {
            var result = await gameDataService.GetConferencesForYearAsync(year, token);
            return Ok(result);
        }

        [HttpGet("weeks/{year:int}")]
        public async Task<IActionResult> GetPlayedWeeksByYear(int year, CancellationToken token)
        {
            var result = await gameDataService.GetPlayedWeeksByYearAsync(year, token);
            return Ok(result);
        }

        /// <summary>
        /// Manual single-game refresh — re-fetches score + odds for one game
        /// from CFBD and writes the result back to Games/Lines. Backs the
        /// mobile ⟳ icon on SchedulePage/PostseasonPage (Season-Pass-gated
        /// client-side). See ProductionGameDataService.GetGameAsync for the
        /// CFBD-call design note (year+week scope, not a gameId filter).
        /// Example: GET /api/productiongamedata/game?gameId=401864570
        /// </summary>
        [HttpGet("game")]
        public async Task<IActionResult> GetGame(
            [FromQuery] int gameId,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetGameAsync(gameId, token);
                if (result == null)
                    return NotFound($"Game {gameId} not found.");

                return Ok(new
                {
                    gameId     = result.Games.GameId,
                    homePoints = result.Games.HomePoints ?? 0,
                    awayPoints = result.Games.AwayPoints ?? 0,
                    lines      = result.Lines.Select(l => new
                    {
                        provider        = l.Provider,
                        spread          = l.Spread,
                        formattedSpread = l.FormattedSpread,
                        overUnder       = l.OverUnder,
                        homeMoneyline   = l.HomeMoneyline,
                        awayMoneyline   = l.AwayMoneyline
                    })
                });
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "CFBD call failed refreshing gameId={GameId}", gameId);
                return StatusCode(502, "CFBD request failed.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error refreshing gameId={GameId}", gameId);
                return StatusCode(500, "An error occurred while refreshing the game.");
            }
        }

        #endregion

        #region Teams and Rivalries

        /// <summary>
        /// V2: All FBS teams from Teams + Conferences tables (CFBD-sourced).
        /// Example: GET /api/productiongamedata/teams/v2
        /// </summary>
        [HttpGet("teams/v2")]
        public async Task<IActionResult> GetTeamsV2(CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetTeamsV2Async(token);
                return Ok(result.Teams);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving V2 teams");
                return StatusCode(500, "An error occurred while retrieving teams.");
            }
        }

        /// <summary>
        /// V2: Named rivalries — team names resolved via Teams.
        /// Example: GET /api/productiongamedata/rivalries/named/v2
        /// </summary>
        [HttpGet("rivalryhistory/v2")]
        public async Task<IActionResult> GetRivalryHistoryV2(
            [FromQuery] int team1Id,
            [FromQuery] int team2Id,
            [FromQuery] int years = 10,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetRivalryHistoryV2Async(team1Id, team2Id, years, token);
                return Ok(new
                {
                    Team1Id               = result.Team1Id,
                    Team1Name             = result.Team1Name,
                    Team1ShortName        = result.Team1ShortName,
                    Team2Id               = result.Team2Id,
                    Team2Name             = result.Team2Name,
                    Team2ShortName        = result.Team2ShortName,
                    RivalryName           = result.RivalryName,
                    RivalryTier           = result.RivalryTier,
                    GamesPlayed           = result.GamesPlayed,
                    AvgMargin             = result.AvgMargin,
                    UpsetRate             = result.UpsetRate,
                    History               = result.History,
                    CurrentYearProjection = result.CurrentYearProjection
                });
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving V2 rivalry history for {T1} vs {T2}", team1Id, team2Id);
                return StatusCode(500, "An error occurred while retrieving rivalry history.");
            }
        }

        #endregion

        #region Conference Standings and Projections

        /// <summary>
        /// V2: Projected conference standings from Games + TeamsConferenceHistory tables.
        /// Example: GET /api/productiongamedata/projected-standings/v2?year=2025&throughWeek=8&conference=SEC
        /// </summary>
        [HttpGet("projected-standings/v2")]
        public async Task<IActionResult> GetProjectedStandingsV2(
            [FromQuery] int? year,
            [FromQuery] int? throughWeek,
            [FromQuery] string? conference = null,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetProjectedStandingsV2Async(year, throughWeek, conference, token);
                return Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error computing V2 projected standings");
                return StatusCode(500, "An error occurred computing projected standings.");
            }
        }

        /// <summary>
        /// V2: Championship qualifiers from Games + TeamsConferenceHistory tables.
        /// Example: GET /api/productiongamedata/championship-qualifiers/v2?year=2025
        /// </summary>
        [HttpGet("championship-qualifiers/v2")]
        public async Task<IActionResult> GetChampionshipQualifiersV2(
            [FromQuery] int? year,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetChampionshipQualifiersV2Async(year, token);
                return Ok(result.Conferences);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error computing V2 championship qualifiers");
                return StatusCode(500, "An error occurred computing championship qualifiers.");
            }
        }

        /// <summary>
        /// V2: Projected championship qualifiers from Games + TeamsConferenceHistory tables.
        /// Example: GET /api/productiongamedata/projected-championship-qualifiers/v2?year=2025&throughWeek=8
        /// </summary>
        [HttpGet("projected-championship-qualifiers/v2")]
        public async Task<IActionResult> GetProjectedChampionshipQualifiersV2(
            [FromQuery] int? year,
            [FromQuery] int? throughWeek,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetProjectedChampionshipQualifiersV2Async(year, throughWeek, token);
                return Ok(result.Conferences);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error computing V2 projected championship qualifiers");
                return StatusCode(500, "An error occurred computing projected championship qualifiers.");
            }
        }

        /// <summary>
        /// Projected 12-team CFP field, off WeeklyRankings' Ranking field only —
        /// no conference championship game simulation (deferred).
        /// Example: GET /api/productiongamedata/playoff-seeding?year=2026&week=15
        /// </summary>
        [HttpGet("playoff-seeding")]
        public async Task<IActionResult> GetPlayoffSeeding(
            [FromQuery] int? year,
            [FromQuery] int week,
            CancellationToken token = default)
        {
            try
            {
                var targetYear = year ?? DateTime.Now.Year;
                var result = await playoffSeedingService.GetProjectedFieldAsync(targetYear, week, token);
                return Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error computing playoff seeding for {Year} week {Week}", year, week);
                return StatusCode(500, "An error occurred computing the projected playoff field.");
            }
        }

        /// <summary>
        /// Full projected 12-team CFP bracket — First Round through the
        /// National Championship, computed in order off the same field
        /// playoff-seeding returns. No toggle; every round is computed and
        /// returned together.
        /// Example: GET /api/productiongamedata/playoff-bracket?year=2026&week=15
        /// </summary>
        [HttpGet("playoff-bracket")]
        public async Task<IActionResult> GetPlayoffBracket(
            [FromQuery] int? year,
            [FromQuery] int week,
            CancellationToken token = default)
        {
            try
            {
                var targetYear = year ?? DateTime.Now.Year;
                var result = await playoffSeedingService.GetProjectedBracketAsync(targetYear, week, token);
                return Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error computing playoff bracket for {Year} week {Week}", year, week);
                return StatusCode(500, "An error occurred computing the projected playoff bracket.");
            }
        }

        #endregion

        #region Team History
        [HttpGet("teamseason")]
        public async Task<IActionResult> GetTeamSeasonArc(
            [FromQuery] int teamId,
            [FromQuery] int? year,
            CancellationToken token = default)
        {
            try
            {
                var targetYear = year ?? DateTime.Now.Year;
                var result = await gameDataService.GetTeamSeasonArcAsync(teamId, targetYear, token);
                return Ok(new
                {
                    teamId = result.TeamId,
                    teamName = result.TeamName,
                    year = result.Year,
                    weeks = result.Weeks
                });
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving season arc for {TeamId}", teamId);
                return StatusCode(500, "An error occurred retrieving the team season arc.");
            }
        }

        /// <summary>
        /// Returns yearly rank/rating/SOS history for a team.
        /// Example: GET /api/productiongamedata/teamhistory?teamId=47&years=10
        /// </summary>
        [HttpGet("teamhistory")]
        public async Task<IActionResult> GetTeamHistory(
            [FromQuery] int teamId,
            [FromQuery] int years = 10,
            CancellationToken token = default)
        {
            try
            {
                var result = await gameDataService.GetTeamHistoryAsync(teamId, years, token);
                return Ok(new
                {
                    TeamId         = result.TeamId,
                    TeamName       = result.TeamName,
                    ShortName      = result.ShortName,
                    ConferenceAbbr = result.ConferenceAbbr,
                    History        = result.History
                });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving team history for {TeamId}", teamId);
                return StatusCode(500, "An error occurred while retrieving team history.");
            }
        }

        /// <summary>
        /// Roster Changes popup — current+prior ZRoster/rank, this year's signing
        /// class, portal in/out, and a plain RosterPlayer Retained/Departed/New diff.
        /// Example: GET /api/productiongamedata/roster-changes?teamId=47&year=2026
        /// </summary>
        [HttpGet("roster-changes")]
        public async Task<IActionResult> GetRosterChanges(
            [FromQuery] int teamId,
            [FromQuery] int? year,
            CancellationToken token = default)
        {
            try
            {
                var targetYear = year ?? DateTime.Now.Year;
                var result = await rosterCapacityService.GetRosterChangesAsync(teamId, targetYear, token);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving roster changes for teamId={TeamId}", teamId);
                return StatusCode(500, "An error occurred retrieving roster changes.");
            }
        }

        #endregion
    }
}
