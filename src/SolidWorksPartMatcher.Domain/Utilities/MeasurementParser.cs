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
///   "3/8in"    → 9.525 mm     (0.375 × 25.4)
///   "12.7mm"   → 12.7 mm
///
/// Tolerance comparisons must use the full-precision double, not the display string.
/// </summary>
public static class MeasurementParser
{
    public enum LengthUnit { Unknown, Millimetre, Inch }

    private const double InchToMm = 25.4;

    // Ordered longest-first so "inches" is matched before "inch", "inch" before "in".
    private static readonly (string Suffix, LengthUnit Unit)[] UnitSuffixes =
    [
        ("inches", LengthUnit.Inch),
        ("inch",   LengthUnit.Inch),
        ("in",     LengthUnit.Inch),
        ("\"",     LengthUnit.Inch),
        ("mm",     LengthUnit.Millimetre),
    ];

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
    /// Tries to parse a measurement string that may carry a unit suffix ("in", "mm", etc.).
    /// When a recognised suffix is present, the numeric part is converted to millimetres
    /// (inches × 25.4; mm stays as-is). When no suffix is found, unit is Unknown and
    /// millimetres contains the raw parsed number without conversion.
    /// Returns false when the string is null, empty, or cannot be parsed.
    /// </summary>
    public static bool TryParseToMm(string? input, out double millimetres, out LengthUnit unit)
    {
        millimetres = 0;
        unit = LengthUnit.Unknown;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = input.Trim();
        var numeric = s;

        foreach (var (suffix, u) in UnitSuffixes)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                numeric = s[..^suffix.Length].TrimEnd();
                unit = u;
                break;
            }
        }

        if (!TryParseNumber(numeric, out var raw)) return false;
        millimetres = unit == LengthUnit.Inch ? raw * InchToMm : raw;
        return true;
    }

    /// <summary>
    /// Formats a value for display with two decimal places.
    /// Uses invariant culture so the decimal separator is always a period.
    /// Example: 0.142857 → "0.14", 0.75 → "0.75", 0.553231 → "0.55".
    /// </summary>
    public static string FormatDisplay(double value)
        => value.ToString("F2", CultureInfo.InvariantCulture);
}
