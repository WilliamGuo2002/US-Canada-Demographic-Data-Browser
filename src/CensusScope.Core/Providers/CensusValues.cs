using System.Globalization;

namespace CensusScope.Core.Providers;

/// <summary>
/// Interprets raw string cells returned by the Census API, including the ACS
/// "jam values" — large negative sentinels that encode suppression, sample-size,
/// and applicability conditions instead of a real estimate.
/// </summary>
public static class CensusValues
{
    /// <summary>Any numeric value at or below this threshold is an ACS jam (sentinel) value.</summary>
    private const double JamThreshold = -222222222;

    /// <summary>
    /// Parses an estimate cell. Returns the numeric value with a null flag, or a null
    /// value plus a short human-readable flag when the cell is missing, non-numeric,
    /// or an ACS jam value.
    /// </summary>
    /// <param name="raw">The raw cell text from the API (all Census cells are strings).</param>
    public static (double? Value, string? Flag) ParseEstimate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null" ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return (null, "n/a");

        if (value <= JamThreshold)
            return value switch
            {
                -666666666 => (null, "cannot compute"),
                -999999999 => (null, "insufficient sample"),
                -888888888 => (null, "not applicable"),
                _ => (null, "suppressed"),
            };

        return (value, null);
    }

    /// <summary>
    /// Parses a margin-of-error cell. Returns null for missing or non-numeric cells and
    /// for all jam values — including -555555555 ("controlled"), which by definition has
    /// no displayable margin of error.
    /// </summary>
    /// <param name="raw">The raw cell text from the API.</param>
    public static double? ParseMoe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        return value <= JamThreshold ? null : value;
    }
}
