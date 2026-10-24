using Syncfusion.Licensing;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SaturdayPulse.Models;

[Preserve(AllMembers = true)]
public class TeamRanking : INotifyPropertyChanged
{
    // =========================================================
    // Core Team Data
    // =========================================================

    public int TeamID { get; set; }
    public string TeamName { get; set; } = string.Empty;

    public string? Conference { get; set; }
    public string? ConferenceAbbr { get; set; }
    public string? Division { get; set; }
    public string? Tier { get; set; }

    public int Year { get; set; }

    public byte Wins { get; set; }
    public byte Losses { get; set; }

    /// <summary>
    /// Real wins/losses so far, plus predicted W/L for each remaining game on
    /// the schedule (rolled up server-side from the existing Projections
    /// snapshot). Added 2026-08 for the Rankings/MyTeam season-projection
    /// feature — see ProjectedRecord below for the display string.
    /// </summary>
    public byte ProjectedWins { get; set; }
    public byte ProjectedLosses { get; set; }

    public int OverallRank { get; set; }
    public int TierRank { get; set; }

    public decimal? Ranking { get; set; }

    // =========================================================
    // Ratings
    // =========================================================

    public decimal? BaseSOS { get; set; }
    public decimal? CombinedSOS { get; set; }

    public decimal AvgPointsScored { get; set; }
    public decimal AvgPointsAllowed { get; set; }

    public decimal OffensiveZScore { get; set; }
    public decimal DefensiveZScore { get; set; }

    public int OffensiveRank { get; set; }
    public int DefensiveRank { get; set; }

    /// <summary>
    /// National ordinal rank (1 = best) of this team's roster talent (ZRoster).
    /// Null when ZRoster hasn't been computed for this team/year — render as
    /// "no data," not as an unranked-last placement. Mirrors
    /// PowerRankingRowResponse.RosterRank / TeamRankingDto.RosterRank.
    /// </summary>
    public int? RosterRank { get; set; }

    public double? TrendRating { get; set; }
    public double? PedigreeRating { get; set; }
    public double? SeedRating { get; set; }

    // =========================================================
    // Raw History Data
    // =========================================================

    private List<double>? _trendHistory;

    public List<double>? TrendHistory
    {
        get => _trendHistory;
        set
        {
            if (SetProperty(ref _trendHistory, value))
                OnPropertyChanged(nameof(HasTrendData));
        }
    }

    private List<double>? _pedigreeHistory;

    public List<double>? PedigreeHistory
    {
        get => _pedigreeHistory;
        set
        {
            if (SetProperty(ref _pedigreeHistory, value))
                OnPropertyChanged(nameof(HasTrendData));
        }
    }

    // =========================================================
    // Display Helpers
    // =========================================================

    public string Record => $"{Wins}-{Losses}";

    /// <summary>Real record so far plus predicted W/L for remaining games.</summary>
    public string ProjectedRecord => $"{ProjectedWins}-{ProjectedLosses}";

    /// <summary>
    /// Actual record, prefixed with the projected full-season record in
    /// parentheses — e.g. "(10-3) 0-0" at preseason. Collapses to plain
    /// "10-3" once ProjectedWins/Losses matches Wins/Losses (no remaining
    /// games left to project, so the parenthetical would be redundant).
    /// </summary>
    public string RecordWithProjection =>
        (ProjectedWins != Wins || ProjectedLosses != Losses)
            ? $"({ProjectedRecord}) {Record}"
            : Record;

    /// <summary>Rating ×100, two decimals (display only — see RatingDisplay).</summary>
    public string DisplayRank =>
        RatingDisplay.Format(Ranking);

    public string DisplayTier =>
        Tier ?? "N/A";

    public string DisplayTierRank =>
        $"#{TierRank}";

    public string DisplayTierWithRank => Tier switch
    {
        "P4" => $"P4 (#{TierRank})",
        "G5" => $"G5 (#{TierRank})",
        "Independent" => "Ind",
        _ => Tier ?? "N/A"
    };

    public string DisplayConferenceTier =>
        $"{ConferenceAbbr} · {DisplayTierWithRank}";

    /// <summary>SOS ×100, two decimals (display only — see RatingDisplay).</summary>
    public string DisplaySOS =>
        RatingDisplay.Format(CombinedSOS);

    public string OffensiveRankDisplay =>
        OffensiveRank > 0 ? $"#{OffensiveRank}" : "—";

    public string DefensiveRankDisplay =>
        DefensiveRank > 0 ? $"#{DefensiveRank}" : "—";

    public string DisplayRosterRank =>
        RosterRank.HasValue ? $"#{RosterRank}" : "—";

    // =========================================================
    // UI State
    // =========================================================

    public bool IsTop25 { get; set; }
    public bool IsOddRow { get; set; }

    public string ActiveSortValue { get; set; } = string.Empty;

    // =========================================================
    // Follow State
    // =========================================================

    private bool _isFollowed;

    public bool IsFollowed
    {
        get => _isFollowed;
        set => SetProperty(ref _isFollowed, value);
    }

    // =========================================================
    // Offense / Defense Expansion
    // =========================================================

    public bool HasOffenseDefenseData =>
        AvgPointsScored > 0 && OffensiveRank > 0;

    public string StatsExpandIcon =>
        IsStatsExpanded ? "▲" : "▼";

    private bool _isStatsExpanded;

    public bool IsStatsExpanded
    {
        get => _isStatsExpanded;
        set
        {
            if (SetProperty(ref _isStatsExpanded, value))
                OnPropertyChanged(nameof(StatsExpandIcon));
        }
    }

    // =========================================================
    // Roster Changes Expansion
    // =========================================================

    public string RosterExpandIcon =>
        IsRosterExpanded ? "▲" : "▼";

    private bool _isRosterExpanded;

    public bool IsRosterExpanded
    {
        get => _isRosterExpanded;
        set
        {
            if (SetProperty(ref _isRosterExpanded, value))
            {
                OnPropertyChanged(nameof(RosterExpandIcon));
                OnPropertyChanged(nameof(ShowRosterEmptyState));
            }
        }
    }

    private RosterChangesResponse? _rosterChanges;

    public RosterChangesResponse? RosterChanges
    {
        get => _rosterChanges;
        set
        {
            if (SetProperty(ref _rosterChanges, value))
            {
                OnPropertyChanged(nameof(HasRosterData));
                OnPropertyChanged(nameof(ShowRosterEmptyState));
            }
        }
    }

    /// <summary>True once RosterChanges has been fetched successfully.</summary>
    public bool HasRosterData => RosterChanges != null;

    /// <summary>
    /// True when the panel is open but the fetch returned nothing (API error, or a
    /// genuine "no ZRoster computed for this team/year" case — see
    /// RosterCapacityService.GetRosterChangesAsync's documented coverage caveat).
    /// Safe from a loading-state flicker: ToggleRosterExpandCommand awaits the fetch
    /// before flipping IsRosterExpanded, so this is never true mid-fetch.
    /// </summary>
    public bool ShowRosterEmptyState => IsRosterExpanded && RosterChanges == null;

    // ── Recruiting / Portal In / Portal Out — single shared list area below the
    // summary table. Only one selection at a time; selecting a different one
    // overwrites what's shown, selecting the active one again closes it. Backed by
    // one key rather than three independent bools so mutual exclusivity can't drift
    // out of sync (the ViewModel commands only ever set this one property). ──

    private string? _activeRosterListKey;

    public string? ActiveRosterListKey
    {
        get => _activeRosterListKey;
        set
        {
            if (SetProperty(ref _activeRosterListKey, value))
            {
                OnPropertyChanged(nameof(IsRecruitingListActive));
                OnPropertyChanged(nameof(IsPortalInListActive));
                OnPropertyChanged(nameof(IsPortalOutListActive));
                OnPropertyChanged(nameof(RecruitingListIcon));
                OnPropertyChanged(nameof(PortalInListIcon));
                OnPropertyChanged(nameof(PortalOutListIcon));
            }
        }
    }

    public bool IsRecruitingListActive => ActiveRosterListKey == "Recruiting";
    public bool IsPortalInListActive   => ActiveRosterListKey == "PortalIn";
    public bool IsPortalOutListActive  => ActiveRosterListKey == "PortalOut";

    public string RecruitingListIcon => IsRecruitingListActive ? "▲" : "▼";
    public string PortalInListIcon   => IsPortalInListActive   ? "▲" : "▼";
    public string PortalOutListIcon  => IsPortalOutListActive  ? "▲" : "▼";

    // =========================================================
    // Trend / Pedigree Expansion
    // =========================================================

    public bool HasTrendData =>
        TrendHistory?.Count > 0 || PedigreeHistory?.Count > 0;

    public string TrendExpandIcon =>
        IsTrendExpanded ? "▲" : "▼";

    private bool _isTrendExpanded;

    public bool IsTrendExpanded
    {
        get => _isTrendExpanded;
        set
        {
            if (!SetProperty(ref _isTrendExpanded, value)) return;
            OnPropertyChanged(nameof(TrendExpandIcon));
            OnPropertyChanged(nameof(HasTrendData));
            if (_isTrendExpanded) EnsureTrendChartDataLoaded();
        }
    }

    // =========================================================
    // Season Arc Expansion
    // =========================================================

    public bool HasArcData => SeasonArcWeeks?.Count > 0;

    public string ArcExpandIcon => IsArcExpanded ? "▲" : "▼";

    private bool _isArcExpanded;

    public bool IsArcExpanded
    {
        get => _isArcExpanded;
        set
        {
            if (!SetProperty(ref _isArcExpanded, value)) return;
            OnPropertyChanged(nameof(ArcExpandIcon));
            if (_isArcExpanded) EnsureArcChartDataLoaded();
        }
    }

    private List<TeamSeasonWeek>? _seasonArcWeeks;

    public List<TeamSeasonWeek>? SeasonArcWeeks
    {
        get => _seasonArcWeeks;
        set
        {
            if (SetProperty(ref _seasonArcWeeks, value))
            {
                OnPropertyChanged(nameof(HasArcData));
                _arcChartDataLoaded = false;
                if (_isArcExpanded) EnsureArcChartDataLoaded();
            }
        }
    }

    // =========================================================
    // Trend / Pedigree Chart Data
    // =========================================================

    public ObservableCollection<ChartPoint> TrendPoints    { get; } = new();
    public ObservableCollection<ChartPoint> PedigreePoints { get; } = new();

    private bool _trendChartDataLoaded;

    private void EnsureTrendChartDataLoaded()
    {
        if (_trendChartDataLoaded) return;
        LoadTrendChartPoints();
        _trendChartDataLoaded = true;
    }

    public void LoadTrendChartPoints()
    {
        TrendPoints.Clear();
        PedigreePoints.Clear();

        if (TrendHistory != null)
            for (int i = 0; i < TrendHistory.Count; i++)
                TrendPoints.Add(new ChartPoint { Index = i + 1, Value = TrendHistory[i] });

        if (PedigreeHistory != null)
            for (int i = 0; i < PedigreeHistory.Count; i++)
                PedigreePoints.Add(new ChartPoint { Index = i + 1, Value = PedigreeHistory[i] });

        OnPropertyChanged(nameof(HasTrendData));
        OnPropertyChanged(nameof(TrendYMin));
        OnPropertyChanged(nameof(TrendYMax));
        OnPropertyChanged(nameof(TrendYInterval));
    }

    // keep old name as alias so existing callers don't break
    public void LoadChartPoints() => LoadTrendChartPoints();

    // ── Trend / Pedigree dynamic Y axis ──────────────────────────────────

    /// <summary>Per-team Y axis minimum for the Trend/Pedigree chart.</summary>
    public double TrendYMin
    {
        get
        {
            var values = TrendPoints.Select(p => p.Value)
                .Concat(PedigreePoints.Select(p => p.Value))
                .ToList();
            if (values.Count == 0) return 0;
            var min = values.Min();
            // Pad 15% below, snap to nearest 0.05
            return Math.Max(0, Math.Floor((min - (min * 0.15)) * 20) / 20);
        }
    }

    /// <summary>Per-team Y axis maximum for the Trend/Pedigree chart.</summary>
    public double TrendYMax
    {
        get
        {
            var values = TrendPoints.Select(p => p.Value)
                .Concat(PedigreePoints.Select(p => p.Value))
                .ToList();
            if (values.Count == 0) return 1;
            var max = values.Max();
            // Pad 15% above, snap to nearest 0.05
            return Math.Ceiling((max + (max * 0.15)) * 20) / 20;
        }
    }

    /// <summary>A clean interval that produces ~4 gridlines between min and max.</summary>
    public double TrendYInterval
    {
        get
        {
            var range = TrendYMax - TrendYMin;
            if (range <= 0) return 0.1;
            // Round to nearest 0.05 for clean labels
            return Math.Max(0.05, Math.Round(range / 4 * 20) / 20);
        }
    }

    // =========================================================
    // Season Arc Chart Data
    // =========================================================

    public ObservableCollection<ChartPoint> ArcRankingPoints { get; } = new();
    public ObservableCollection<ChartPoint> ArcSosPoints     { get; } = new();
    public ObservableCollection<ChartPoint> ArcWinPctPoints  { get; } = new();

    private bool _arcChartDataLoaded;

    private void EnsureArcChartDataLoaded()
    {
        if (_arcChartDataLoaded || SeasonArcWeeks == null) return;
        LoadArcChartPoints();
        _arcChartDataLoaded = true;
    }

    private void LoadArcChartPoints()
    {
        ArcRankingPoints.Clear();
        ArcSosPoints.Clear();
        ArcWinPctPoints.Clear();

        if (SeasonArcWeeks == null) return;

        foreach (var w in SeasonArcWeeks)
        {
            ArcRankingPoints.Add(new ChartPoint { Index = w.Week, Value = w.Ranking  ?? 0 });
            //ArcSosPoints    .Add(new ChartPoint { Index = w.Week, Value = (w.CombinedSOS - 1) * 10 ?? 0 });
            ArcSosPoints.Add(new ChartPoint { Index = w.Week, Value = (double)(w.CombinedSOS ?? 0) });
            ArcWinPctPoints .Add(new ChartPoint { Index = w.Week, Value = w.WinPct });
        }

        OnPropertyChanged(nameof(ArcRatingYMin));
        OnPropertyChanged(nameof(ArcRatingYMax));
        OnPropertyChanged(nameof(ArcRatingYInterval));
        OnPropertyChanged(nameof(ArcSosYMin));
        OnPropertyChanged(nameof(ArcSosYMax));
        OnPropertyChanged(nameof(ArcSosYInterval));
        OnPropertyChanged(nameof(ArcPctYMin));
        OnPropertyChanged(nameof(ArcPctYMax));
        OnPropertyChanged(nameof(ArcPctYInterval));
    }

    // ── Season Arc dynamic Y axis — shared bounds across all three series ─

    private static (double Min, double Max, double Interval) ComputeAxisBounds(
        IEnumerable<ChartPoint> points, double snapTo, double fallbackMin, double fallbackMax)
    {
        var values = points.Select(p => p.Value).ToList();
        if (values.Count == 0) return (fallbackMin, fallbackMax, (fallbackMax - fallbackMin) / 4);

        var rawMin = values.Min();
        var rawMax = values.Max();
        var pad    = Math.Max(Math.Abs(rawMin), Math.Abs(rawMax)) * 0.15;
        if (pad < snapTo) pad = snapTo;

        double snap     = 1.0 / snapTo;
        double min      = Math.Floor((rawMin  - pad) * snap) / snap;
        double max      = Math.Ceiling((rawMax + pad) * snap) / snap;
        double range    = max - min;
        double interval = Math.Max(snapTo, Math.Round(range / 4 * snap) / snap);

        return (min, max, interval);
    }

    private (double Min, double Max, double Interval) ArcSharedBounds =>
        ComputeAxisBounds(
            ArcRankingPoints
                .Concat(ArcSosPoints)
                .Concat(ArcWinPctPoints),
            0.25, -1, 2);

    public double ArcRatingYMin      => ArcSharedBounds.Min;
    public double ArcRatingYMax      => ArcSharedBounds.Max;
    public double ArcRatingYInterval => ArcSharedBounds.Interval;

    public double ArcSosYMin      => ArcSharedBounds.Min;
    public double ArcSosYMax      => ArcSharedBounds.Max;
    public double ArcSosYInterval => ArcSharedBounds.Interval;

    public double ArcPctYMin      => ArcSharedBounds.Min;
    public double ArcPctYMax      => ArcSharedBounds.Max;
    public double ArcPctYInterval => ArcSharedBounds.Interval;

    // =========================================================
    // Schedule Expansion
    // =========================================================

    public string ScheduleExpandIcon => IsScheduleExpanded ? "▲" : "▼";

    private bool _isScheduleExpanded;

    public bool IsScheduleExpanded
    {
        get => _isScheduleExpanded;
        set
        {
            if (!SetProperty(ref _isScheduleExpanded, value)) return;
            OnPropertyChanged(nameof(ScheduleExpandIcon));
        }
    }

    private List<TeamGameResultView>? _scheduleGames;

    public List<TeamGameResultView>? ScheduleGames
    {
        get => _scheduleGames;
        set => SetProperty(ref _scheduleGames, value);
    }

    public bool HasScheduleData => ScheduleGames?.Count > 0;

    // =========================================================
    // INotifyPropertyChanged
    // =========================================================

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(
        ref T backingStore, T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(backingStore, value)) return false;
        backingStore = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
