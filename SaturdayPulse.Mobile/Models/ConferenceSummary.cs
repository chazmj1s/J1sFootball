namespace SaturdayPulse.Models;

/// <summary>
/// One row of the Rankings page's Conferences view — per-conference plain-mean
/// aggregates built client-side from the already-loaded TeamRanking list
/// (PowerRankingsViewModel.BuildConferenceSummaries). Rebuilt wholesale on every
/// filter/sort pass, so no INotifyPropertyChanged is needed.
/// </summary>
public class ConferenceSummary
{
    /// <summary>Abbreviation (falls back to full name) — the grouping key and the
    /// value handed to SharedNavigationStateService.SelectedConference on tap.</summary>
    public string ConferenceKey  { get; init; } = string.Empty;
    public string ConferenceName { get; init; } = string.Empty;
    public string? Tier          { get; init; }
    public int    TeamCount      { get; init; }

    /// <summary>Combined conference total ÷ TeamCount.</summary>
    public double AvgWins            { get; init; }
    public double AvgLosses          { get; init; }
    public double AvgProjectedWins   { get; init; }
    public double AvgProjectedLosses { get; init; }

    /// <summary>Mean of non-null team values; null when no team has one.</summary>
    public decimal? AvgRating { get; init; }
    public decimal? AvgSOS    { get; init; }

    /// <summary>Mean of non-null RosterRank; teams with no ZRoster are excluded.</summary>
    public double? AvgRosterRank  { get; init; }
    public int     RosterCoverage { get; init; }

    /// <summary>1-based rank by AvgRating (desc), assigned once per build.</summary>
    public int  ConferenceRank { get; set; }
    public bool IsOddRow       { get; set; }

    public double ProjectedWinPct
    {
        get
        {
            var total = AvgProjectedWins + AvgProjectedLosses;
            return total > 0 ? AvgProjectedWins / total : 0.0;
        }
    }

    // ── Display ──────────────────────────────────────────────────────────

    /// <summary>Mirrors TeamRanking.RecordWithProjection's collapse rule.</summary>
    public bool HasProjection =>
        Math.Abs(AvgProjectedWins - AvgWins) > 0.001 ||
        Math.Abs(AvgProjectedLosses - AvgLosses) > 0.001;

    public string DisplayActualRecord    => $"{AvgWins:F1}-{AvgLosses:F1}";
    public string DisplayProjectedRecord => $"({AvgProjectedWins:F1}-{AvgProjectedLosses:F1})";

    /// <summary>Avg Rating / Avg SOS ×100, two decimals — same scale as the
    /// team rows (display only, see RatingDisplay).</summary>
    public string DisplayRating     => RatingDisplay.Format(AvgRating);
    public string DisplaySOS        => RatingDisplay.Format(AvgSOS);
    public string DisplayRosterRank => AvgRosterRank.HasValue ? $"#{AvgRosterRank.Value:F1}" : "—";

    public string DisplaySubtitle
    {
        get
        {
            var tier = string.IsNullOrWhiteSpace(Tier) ? "N/A" : Tier;
            var baseText = $"{ConferenceKey} · {tier} · {TeamCount} teams";
            return RosterCoverage < TeamCount
                ? $"{baseText} · roster {RosterCoverage}/{TeamCount}"
                : baseText;
        }
    }
}
