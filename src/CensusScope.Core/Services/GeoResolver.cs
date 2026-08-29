using System.Globalization;
using System.Text;
using CensusScope.Core.Models;

namespace CensusScope.Core.Services;

/// <summary>
/// Matches a user-supplied place name (possibly from Gemini, possibly misspelled or
/// missing accents) against a list of <see cref="GeoUnit"/>s, accent- and case-insensitively.
/// </summary>
public static class GeoResolver
{
    /// <summary>
    /// Normalizes a name for comparison: trims, lowercases (invariant), decomposes Unicode
    /// (FormD) and strips combining marks (so "Montreal" matches "Montréal"), folds
    /// apostrophe variants (U+2019 → ') and treats dashes as spaces (so "Trois Rivieres"
    /// matches "Trois-Rivières"), and collapses internal whitespace runs to single spaces.
    /// </summary>
    public static string Normalize(string s)
    {
        var decomposed = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (ch is '‘' or '’' or 'ʼ') // smart quotes / modifier apostrophe → ASCII
            {
                sb.Append('\'');
                lastWasSpace = false;
                continue;
            }
            if (char.IsWhiteSpace(ch) || category == UnicodeCategory.DashPunctuation)
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            sb.Append(ch);
            lastWasSpace = false;
        }
        if (lastWasSpace && sb.Length > 0)
            sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// Resolves a name to a single unit: unique normalized exact match first, then unique
    /// prefix match, then unique substring match; if several substring matches remain, the
    /// one with the shortest name wins (e.g. "York" -> "York" over "New York"). Null when
    /// nothing matches.
    /// </summary>
    public static GeoUnit? Resolve(IReadOnlyList<GeoUnit> units, string name)
    {
        var needle = Normalize(name);
        if (needle.Length == 0)
            return null;

        var exact = units.Where(u => Normalize(u.Name) == needle).ToList();
        if (exact.Count == 1)
            return exact[0];

        var prefix = units.Where(u => Normalize(u.Name).StartsWith(needle, StringComparison.Ordinal)).ToList();
        if (prefix.Count == 1)
            return prefix[0];

        var contains = units.Where(u => Normalize(u.Name).Contains(needle, StringComparison.Ordinal)).ToList();
        if (contains.Count == 1)
            return contains[0];
        if (contains.Count > 1)
            return contains.OrderBy(u => u.Name.Length).First();

        return null;
    }

    /// <summary>
    /// Returns up to <paramref name="max"/> units whose normalized names contain the
    /// normalized <paramref name="name"/> — intended for "did you mean" error messages.
    /// </summary>
    public static IReadOnlyList<GeoUnit> Candidates(IReadOnlyList<GeoUnit> units, string name, int max = 5)
    {
        var needle = Normalize(name);
        if (needle.Length == 0)
            return [];
        return units
            .Where(u => Normalize(u.Name).Contains(needle, StringComparison.Ordinal))
            .Take(max)
            .ToList();
    }
}
