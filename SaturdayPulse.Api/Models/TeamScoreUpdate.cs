namespace SaturdayPulse.Models
{
    /// <summary>
    /// One team's score for a game, keyed by CFBD team id — never by a
    /// "home"/"away" label. GamesRepository resolves the stored row's
    /// orientation from the id, so a source that designates home/away
    /// differently (neutral-site games) can't land points on the wrong team.
    /// LineScores: comma-joined per-period ints; null = not supplied, leave
    /// the stored value unchanged.
    /// </summary>
    public sealed record TeamScoreUpdate(int TeamId, int? Points, string? LineScores);
}
