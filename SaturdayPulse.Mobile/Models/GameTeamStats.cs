using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SaturdayPulse.Models
{
    /// <summary>
    /// Rankings snapshot for one team at the week going into a game.
    /// Populated by GetScheduleV2Async from WeeklyRankings (week - 1).
    /// </summary>
    public class GameTeamStats : INotifyPropertyChanged
    {
        public int     TeamId        { get; set; }
        public string  TeamName      { get; set; } = string.Empty;
        public int     OverallRank   { get; set; }
        public string  Record        { get; set; } = string.Empty;
        public double? PowerRating   { get; set; }
        public double? CombinedSOS   { get; set; }

        // Offense
        public int?    OffensiveRank     { get; set; }
        public double? AvgPointsScored   { get; set; }
        public double? OffensiveZScore   { get; set; }

        // Defense
        public int?    DefensiveRank     { get; set; }
        public double? AvgPointsAllowed  { get; set; }
        public double? DefensiveZScore   { get; set; }

        // ── Display helpers ───────────────────────────────────────────────
        public string DisplayRank       => OverallRank > 0       ? $"#{OverallRank}"               : "–";
        // Rating / SOS ×100, two decimals (display only — see RatingDisplay).
        // Keeps this card's "–" placeholder rather than RatingDisplay's "N/A".
        public string DisplayRating     => PowerRating.HasValue   ? RatingDisplay.Format(PowerRating) : "–";
        public string DisplaySOS        => CombinedSOS.HasValue   ? RatingDisplay.Format(CombinedSOS) : "–";
        public string DisplayOffRank    => OffensiveRank.HasValue ? $"#{OffensiveRank}"             : "–";
        public string DisplayDefRank    => DefensiveRank.HasValue ? $"#{DefensiveRank}"             : "–";
        public string DisplayPtsScored  => AvgPointsScored.HasValue  ? $"{AvgPointsScored.Value:F1}"  : "–";
        public string DisplayPtsAllowed => AvgPointsAllowed.HasValue ? $"{AvgPointsAllowed.Value:F1}" : "–";
        public string DisplayOffZScore  => OffensiveZScore.HasValue  ? $"{OffensiveZScore.Value:F4}"  : "–";
        public string DisplayDefZScore  => DefensiveZScore.HasValue  ? $"{DefensiveZScore.Value:F4}"  : "–";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Consensus Vegas lines for a game — averaged across all available providers.
    /// Populated by GetScheduleV2Async from the Lines table.
    /// </summary>
    public class GameLines : INotifyPropertyChanged
    {
        public decimal? Spread         { get; set; }   // closing, home perspective
        public decimal? SpreadOpen     { get; set; }   // opening
        public decimal? OverUnder      { get; set; }   // closing
        public decimal? OverUnderOpen  { get; set; }   // opening
        public int?     HomeMoneyline  { get; set; }   // avg closing
        public int?     AwayMoneyline  { get; set; }   // avg closing
        public int      ProviderCount  { get; set; }   // how many lines averaged

        // ── Display helpers ───────────────────────────────────────────────
        public string DisplaySpread
        {
            get
            {
                if (!Spread.HasValue) return "–";
                return Spread.Value == 0 ? "PK" : Spread.Value > 0
                    ? $"+{Spread.Value:F1}" : $"{Spread.Value:F1}";
            }
        }

        public string DisplaySpreadOpen
        {
            get
            {
                if (!SpreadOpen.HasValue) return "–";
                return SpreadOpen.Value == 0 ? "PK" : SpreadOpen.Value > 0
                    ? $"+{SpreadOpen.Value:F1}" : $"{SpreadOpen.Value:F1}";
            }
        }

        public string DisplayOU     => OverUnder.HasValue     ? $"{OverUnder.Value:F1}"     : "–";
        public string DisplayOUOpen => OverUnderOpen.HasValue ? $"{OverUnderOpen.Value:F1}" : "–";

        public string DisplayHomeMoneyline => HomeMoneyline.HasValue
            ? (HomeMoneyline.Value > 0 ? $"+{HomeMoneyline}" : $"{HomeMoneyline}") : "–";
        public string DisplayAwayMoneyline => AwayMoneyline.HasValue
            ? (AwayMoneyline.Value > 0 ? $"+{AwayMoneyline}" : $"{AwayMoneyline}") : "–";

        public bool HasLines => Spread.HasValue || OverUnder.HasValue;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
