namespace SaturdayPulse.Models
{
    /// <summary>
    /// AP / Coaches polls for one (year, week), as returned by
    /// GET /api/productiongamedata/polls/v2?year=X&amp;week=Y.
    /// Either list is empty when no poll of that name exists for the week.
    /// </summary>
    public class PollWeekResponse
    {
        public int Year { get; set; }
        public int Week { get; set; }
        public List<PollRankingDto>? ApPoll { get; set; } = new();
        public List<PollRankingDto>? CoachesPoll { get; set; } = new();
    }

    /// <summary>
    /// One poll row. DisplayRank is server-formatted: "14", or "T-14" when
    /// another team shares the rank. TeamId is the CFBD team id — same value
    /// as TeamRanking.TeamID.
    /// </summary>
    public class PollRankingDto
    {
        public int Rank { get; set; }
        public string? DisplayRank { get; set; }
        public int TeamId { get; set; }
        public string? School { get; set; }
        public string? Conference { get; set; }
        public int? FirstPlaceVotes { get; set; }
        public int? Points { get; set; }
    }
}
