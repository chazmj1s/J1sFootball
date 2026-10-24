using System.Globalization;
using SaturdayPulse.Models;

namespace SaturdayPulse.Converters;

/// <summary>
/// XAML wrapper around RatingDisplay.Format for bindings that point straight
/// at a raw numeric Rating/SOS value (no Display* string property on the
/// model) — e.g. PlayoffSeed.Ranking / PlayoffSeed.CombinedSOS. Accepts
/// decimal, double, float or any other IConvertible numeric; anything else,
/// or null, renders "N/A". One-way only.
/// </summary>
public class RatingDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null       => RatingDisplay.NotAvailable,
            decimal m  => RatingDisplay.Format(m),
            double d   => RatingDisplay.Format(d),
            float f    => RatingDisplay.Format((double)f),
            IConvertible c when TryToDouble(c, out var d) => RatingDisplay.Format(d),
            _          => RatingDisplay.NotAvailable
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("RatingDisplayConverter is one-way.");

    private static bool TryToDouble(IConvertible value, out double result)
    {
        try
        {
            result = value.ToDouble(CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }
}
