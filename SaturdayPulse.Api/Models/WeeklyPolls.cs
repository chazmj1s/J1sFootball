namespace SaturdayPulse.Models
{
    /// <summary>
    /// One row of an external poll (AP Top 25 / Coaches Poll) for a single
    /// week, as returned by PollRankingsLoader. DisplayRank is pre-formatted
    /// server-side ("14" or "T-14" when another team shares the rank) so the
    /// Mobile row template can bind it directly.
    /// </summary>
    public sealed record PollRankingRow(
        int Rank,
        string DisplayRank,
        int TeamId,
        string School,
        string? Conference,
        int? FirstPlaceVotes,
        int? Points);

    /// <summary>
    /// AP and Coaches polls for one regular-season week. Either list is empty
    /// when CFBD has no poll of that name for the week (e.g. a week whose poll
    /// hasn't been released yet) — callers treat empty as "no poll," not an error.
    /// </summary>
    public sealed record WeeklyPolls(
        int Year,
        int Week,
        IReadOnlyList<PollRankingRow> ApPoll,
        IReadOnlyList<PollRankingRow> CoachesPoll)
    {
        public static WeeklyPolls Empty(int year, int week) => new(year, week, [], []);
    }
}
