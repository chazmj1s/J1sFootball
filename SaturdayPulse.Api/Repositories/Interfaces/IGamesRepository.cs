using SaturdayPulse.Api.Contracts.Responses;
using SaturdayPulse.Contracts.Responses;
using SaturdayPulse.Models;

namespace SaturdayPulse.Repositories.Interfaces
{
    public interface IGamesRepository
    {
        Task<List<int>> GetUnplayedYearDistinct(int year, CancellationToken token = default);
        Task<List<Games>> GetByYearAsync(int year, CancellationToken token = default);
        Task<List<Games>> GetByYearAndWeekAsync(int year, int week, CancellationToken token = default);
        Task<List<Games>> GetGamesForCurrentWeekAsync(int year, CancellationToken token = default);
        Task<Games?> GetByGameIdAsync(int gameId, CancellationToken token = default);
        Task UpsertAsync(Games game, CancellationToken token = default);
        Task UpsertRangeAsync(IEnumerable<Games> games, CancellationToken token = default);
        Task<List<Games>> GetRivalryHistoryAsync(int team1Id, int team2Id, int cutoffYear, CancellationToken token = default);
        Task<List<Games>> GetGamesSinceYearAsync(int fromYear, CancellationToken token = default);
        Task<List<Games>> GetPlayedGamesSinceYearAsync(int fromYear, CancellationToken token = default);
        Task<List<Games>> GetPlayedGamesByYearAndWeekAsync(int year, int week, CancellationToken token = default);
        Task<List<PlayedWeekDto>> GetPlayedWeeksByYearAsync(int year, CancellationToken token = default);
        Task<Dictionary<int, int>> GetLastPlayedWeekByTeamAsync(int year, CancellationToken token = default);
        Task<List<GameParticipant>> GetGameParticipantsAsync(int year, CancellationToken token = default);
        Task<List<Games>> GetPostSeasonByYear(int year, CancellationToken token = default);
        Task<List<Games>> GetByIds(List<int> gameIds, CancellationToken token = default);
        Task<List<int>> GetUnplayedWeeksAsync(int year, CancellationToken token = default);
        Task<bool> ApplyScoresByTeamAsync(int gameId, TeamScoreUpdate first, TeamScoreUpdate second, CancellationToken token = default);

    }
}
