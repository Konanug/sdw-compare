using System.Globalization;

namespace SolidWorksPartMatcher.Domain.Utilities;

/// <summary>
/// Parses measurement strings expressed as fractions, decimals, or unit-qualified values
/// into full-precision doubles. Provides display formatting to two decimal places.
///
/// Examples:
///   "1/7"      → 0.142857...  displayed as "0.14"
///   "6/8"      → 0.75         displayed as "0.75"
///   "0.553231" → 0.553231     displayed as "0.55"
///   "0.55"     → 0.55         displayed as "0.55"
///
/// Tolerance comparisons must use the full-precision double, not the display string.
/// </summary>
public static class MeasurementParser
{
    /// <summary>
    /// Parses a measurement string into a double. Handles:
    ///   • Fractions:  "1/7", "3/8", "6/8"
    ///   • Decimals:   "0.553231", "12.5"
    ///   • Integers:   "5", "100"
    /// Returns false when the string is null, empty, or not a recognisable number.
    /// The returned value has full precision; rounding for display is separate.
    /// </summary>
    public static bool TryParseNumber(string? input, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = input.Trim();

        var slashIdx = s.IndexOf('/');
        if (slashIdx > 0)
        {
            var numStr = s[..slashIdx].Trim();
            var denStr = s[(slashIdx + 1)..].Trim();
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
                double.TryParse(denStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
                den != 0)
            {
                result = num / den;
                return true;
            }
            return false;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Formats a value for display with two decimal places.
    /// Uses invariant culture so the decimal separator is always a period.
    /// Example: 0.142857 → "0.14", 0.75 → "0.75", 0.553231 → "0.55".
    /// </summary>
    public static string FormatDisplay(double value)
        => value.ToString("F2", CultureInfo.InvariantCulture);
}
