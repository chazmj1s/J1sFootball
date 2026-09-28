namespace SaturdayPulse.Models;

/// <summary>
/// Compressed card for the Rankings page's Top 25 view — rank, team, actual
/// record (no projection) and rating only. A separate type rather than binding
/// TeamRanking directly, because TeamRanking.IsOddRow is owned by the Teams
/// list's sort order and the Top 25 columns need their own alternation.
/// </summary>
public class Top25Card
{
    public int    TeamID        { get; init; }
    public int    Rank          { get; init; }
    public string TeamName      { get; init; } = string.Empty;
    public string? ConferenceAbbr { get; init; }
    public string Record        { get; init; } = string.Empty;
    public string DisplayRating { get; init; } = "N/A";
    public bool   IsOddRow      { get; init; }

    /// <summary>" (SEC)" suffix shown after the team name; empty when unknown.</summary>
    public string DisplayConferenceSuffix =>
        string.IsNullOrWhiteSpace(ConferenceAbbr) ? string.Empty : $" ({ConferenceAbbr})";

    /// <summary>Always true — lets the rank badge bind through
    /// BoolToColorConverter so it matches the Teams list's top-25 badge color.</summary>
    public bool   IsTop25       => true;
}
