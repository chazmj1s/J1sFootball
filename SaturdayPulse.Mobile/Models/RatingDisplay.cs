using System.Globalization;

namespace SaturdayPulse.Models;

/// <summary>
/// Single display formatter for Rating and SOS values (2026-10-18).
/// Display-only: the raw value is multiplied by 100 and shown with two
/// decimals (1.6461 → "164.61", 0.8130 → "81.30"). Sorting, comparisons,
/// charts and every calculation keep using the raw value — only the text
/// shown to the user changes. Null / non-finite → "N/A", matching the
/// previous per-property behavior.
///
/// Used by TeamRanking (Rankings, My Teams, Sandbox, J1s Top 25 cards),
/// ConferenceSummary (Rankings → Conferences), and RatingDisplayConverter
/// (XAML bindings straight to a raw numeric, e.g. the playoff seed table).
/// </summary>
public static class RatingDisplay
{
    public const string NotAvailable = "N/A";

    private const string DisplayFormat = "F2";

    public static string Format(decimal? value) =>
        value.HasValue
            ? (value.Value * 100m).ToString(DisplayFormat, CultureInfo.CurrentCulture)
            : NotAvailable;

    public static string Format(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? (value.Value * 100d).ToString(DisplayFormat, CultureInfo.CurrentCulture)
            : NotAvailable;
}
