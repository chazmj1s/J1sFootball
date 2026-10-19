using Microsoft.Extensions.Caching.Memory;
using SaturdayPulse.Contracts;
using SaturdayPulse.Contracts.Requests;
using SaturdayPulse.Contracts.Responses;
using SaturdayPulse.Interfaces;
using SaturdayPulse.Models;
using SaturdayPulse.Utilities;

namespace SaturdayPulse.Services
{
    /// <summary>
    /// Encapsulates all data-access and business logic for the production read-only
    /// endpoints.
    ///
    /// This file holds only the constructor and DI fields — every partial needs
    /// these visible, and C# primary-constructor parameters can only be declared
    /// once, on this file. All actual methods live in function-based partials:
    ///   ProductionGameDataService.Predictions.cs      — PredictMatchup/PredictMatchups/Sandbox
    ///   ProductionGameDataService.Schedule.cs          — GetScheduleV2Async and week/conference metadata
    ///   ProductionGameDataService.Postseason.cs        — championship qualifiers, standings, bowls/playoffs
    ///   ProductionGameDataService.TeamsAndRankings.cs  — team records, rolling averages, Power Rankings
    ///   ProductionGameDataService.Polls.cs             — AP / Coaches poll lookup (CFBD /rankings, cached)
    ///   ProductionGameDataService.Rivalries.cs         — rivalry lookups and RivalryNotes builder
    ///   ProductionGameDataService.Diagnostics.cs       — read-only DB/health introspection
    ///   ProductionGameDataService.GameRefresh.cs       — single-game live-score/odds refresh
    /// Split 2026-09-15 out of the former ProductionGameDataService.cs /
    /// ProductionGameDataService_V2.cs pair — that V1/V2 boundary tracked file
    /// history, not any real functional grouping, and both files had grown too
    /// large to navigate. No behavior changes; methods moved as-is.
    /// </summary>
    public partial class ProductionGameDataService(
        IUnitOfWork uow,
        IGameDataService cfbdLoadService,
        GamePredictionService predictionService,
        ProjectionCacheService projectionCache,
        WeeklyRankingsService weeklyRankingsService,
        RollingAverageService rollingAverageService,
        ConferenceTierService tierService,
        PollRankingsLoader pollRankingsLoader,
        IMemoryCache memoryCache,
        ILogger<ProductionGameDataService> logger)
    {
        private readonly IUnitOfWork _uow = uow;
        private readonly IGameDataService _cfbdLoadService = cfbdLoadService;
        private readonly GamePredictionService _predictionService = predictionService;
        private readonly ProjectionCacheService _projectionCache = projectionCache;
        private readonly WeeklyRankingsService _weeklyRankingsService = weeklyRankingsService;
        private readonly RollingAverageService _rollingAverageService = rollingAverageService;
        private readonly ConferenceTierService _tierService = tierService;
        private readonly PollRankingsLoader _pollRankingsLoader = pollRankingsLoader;
        private readonly IMemoryCache _memoryCache = memoryCache;
        private readonly ILogger<ProductionGameDataService> _logger = logger;
    }
}
