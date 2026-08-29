using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CensusScope.App.Demo;

/// <summary>
/// Builds canned responses for demo mode. Values are deterministic (derived from the variable
/// code and geography id) so the same demo always shows the same numbers, and are SYNTHETIC —
/// they are not U.S. Census estimates.
/// </summary>
internal static class DemoData
{
    /// <summary>A handful of states, enough to exercise every level of the U.S. hierarchy.</summary>
    private static readonly (string Fips, string Name)[] States =
    [
        ("01", "Alabama"), ("06", "California"), ("12", "Florida"),
        ("36", "New York"), ("48", "Texas"), ("53", "Washington"),
    ];

    private static readonly string[] CountyNames =
        ["Alameda County", "Fresno County", "Kern County", "Los Angeles County", "Orange County",
         "Riverside County", "Sacramento County", "San Diego County", "San Francisco County", "Santa Clara County"];

    private static readonly string[] PlaceNames =
        ["Anaheim city", "Bakersfield city", "Fresno city", "Long Beach city", "Los Angeles city",
         "Oakland city", "Sacramento city", "San Diego city", "San Francisco city", "San Jose city"];

    /// <summary>
    /// Synthesizes a Census API response for <paramref name="url"/>: a JSON array-of-arrays whose
    /// first row is the header, matching the columns the caller asked for in <c>get=</c>.
    /// </summary>
    internal static string Census(string url)
    {
        var query = ParseQuery(url);
        var requested = (query.GetValueOrDefault("get") ?? "NAME").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var (level, wildcard, ownFilter, stateFilter) = ParseGeography(query);

        var geoColumns = level switch
        {
            "county" or "place" => new[] { "state", level },
            _ => ["state"],
        };
        var header = requested.Concat(geoColumns).ToArray();

        var units = BuildUnits(level, wildcard, ownFilter, stateFilter);
        var table = new List<string?[]> { header! };
        foreach (var (geoId, ownCode, stateCode, name) in units)
        {
            var row = new string?[header.Length];
            for (var i = 0; i < requested.Length; i++)
                row[i] = requested[i] == "NAME" ? name : Value(requested[i], geoId);

            row[requested.Length] = stateCode;
            if (geoColumns.Length == 2)
                row[requested.Length + 1] = ownCode;
            table.Add(row);
        }
        return JsonSerializer.Serialize(table);
    }

    /// <summary>
    /// A canned Gemini reply, so the plain-language box responds in demo mode. The two calls the
    /// app makes are distinguished by their body: intent parsing sends a responseSchema, the
    /// follow-up summarization does not.
    /// </summary>
    internal static string Gemini(string requestBody)
    {
        var isIntent = requestBody.Contains("responseSchema", StringComparison.Ordinal);

        var text = isIntent
            ? """
              {"country":"CA","mode":"compare","datasetKey":"population_dwellings","geoLevel":"PR",
               "geoName":null,"parentGeoName":null,
               "explanation":"Demo mode: interpreting every question as a comparison of Canadian provinces by population."}
              """
            : "This is a canned demo answer, not a Gemini response. The table below is real "
              + "Statistics Canada data: Ontario is the most populous province at 14,223,942, "
              + "followed by Quebec at 8,501,833 and British Columbia at 5,000,879. "
              + "Enter a Gemini API key in Settings and restart without --demo to ask your own questions.";

        return JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text } } } },
            },
        });
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<(string GeoId, string OwnCode, string StateCode, string Name)> BuildUnits(
        string level, bool wildcard, string? ownFilter, string? stateFilter)
    {
        if (level == "state")
        {
            var states = wildcard ? States : States.Where(s => s.Fips == ownFilter);
            foreach (var (fips, name) in states)
                yield return (fips, fips, fips, name);
            yield break;
        }

        var parentFips = stateFilter ?? "06";
        var parentName = States.FirstOrDefault(s => s.Fips == parentFips).Name ?? "California";
        var names = level == "county" ? CountyNames : PlaceNames;

        for (var i = 0; i < names.Length; i++)
        {
            // 3-digit county codes / 5-digit place codes, odd-numbered as the real API uses.
            var code = level == "county"
                ? (i * 2 + 1).ToString("D3", CultureInfo.InvariantCulture)
                : (i * 1234 + 1670).ToString("D5", CultureInfo.InvariantCulture);
            if (!wildcard && code != ownFilter) continue;
            yield return (parentFips + code, code, parentFips, names[i] + ", " + parentName);
        }
    }

    /// <summary>
    /// A stable pseudo-value for a variable in a geography. Estimates scale with the geography,
    /// margins of error stay small, medians and per-capita figures land in believable ranges.
    /// </summary>
    private static string Value(string code, string geoId)
    {
        var seed = Hash(code + "|" + geoId);
        var isMoe = code.EndsWith('M');

        if (code.StartsWith("B01002", StringComparison.Ordinal))                 // median age
            return (isMoe ? 0.2 + seed % 18 / 10.0 : 30 + seed % 150 / 10.0)
                .ToString("0.0", CultureInfo.InvariantCulture);
        if (code.StartsWith("B19013", StringComparison.Ordinal)
            || code.StartsWith("B19301", StringComparison.Ordinal))              // dollar amounts
            return (isMoe ? 800 + seed % 2000 : 28000 + seed % 90000).ToString(CultureInfo.InvariantCulture);

        var scale = geoId.Length <= 2 ? 40_000 : geoId.Length <= 5 ? 4_000 : 900;
        var magnitude = code.EndsWith("_001E", StringComparison.Ordinal) ? 60 : 1;
        var value = (long)(seed % 900 + 100) * scale * magnitude / 100;
        return (isMoe ? Math.Max(12, value / 40) : value).ToString(CultureInfo.InvariantCulture);
    }

    private static int Hash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in s) h = h * 31 + ch;
            return Math.Abs(h);
        }
    }

    private static (string Level, bool Wildcard, string? OwnFilter, string? StateFilter) ParseGeography(
        Dictionary<string, string> query)
    {
        var forClause = query.GetValueOrDefault("for") ?? "state:*";
        var parts = forClause.Split(':', 2);
        var level = parts[0].Trim();
        var selector = parts.Length > 1 ? parts[1] : "*";
        var stateFilter = query.GetValueOrDefault("in")?.Replace("state:", "", StringComparison.Ordinal);
        return (level, selector == "*", selector == "*" ? null : selector, stateFilter);
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idx = url.IndexOf('?');
        if (idx < 0) return result;
        foreach (var pair in url[(idx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2)
                result[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }
        return result;
    }
}
