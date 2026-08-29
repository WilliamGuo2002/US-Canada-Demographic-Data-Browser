using System.Text.Json;
using CensusScope.Core.Http;
using CensusScope.Core.Models;
using CensusScope.Core.Services;

namespace CensusScope.Core.Providers;

/// <summary>
/// U.S. data source backed by the Census Bureau's ACS 2020-2024 5-year estimates API.
/// <see cref="GeoUnit.Id"/> is always the full GEOID with leading zeros:
/// state = 2-digit FIPS, county = 5-digit (state+county), place = 7-digit (state+place).
/// </summary>
public sealed class UsCensusProvider : IDemographicProvider
{
    private const string Base = "https://api.census.gov/data/2024/acs/acs5";

    /// <summary>Census caps requests at 50 variables: 24 estimate/MOE pairs + NAME = 49.</summary>
    private const int PairsPerRequest = 24;

    private static readonly TimeSpan GeoCacheTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan DataCacheTtl = TimeSpan.FromDays(7);

    private readonly ApiClient _http;
    private readonly AppSettings _settings;
    private readonly CountryCatalog _catalog;

    /// <summary>Creates the provider from the shared HTTP client, user settings, and the US catalog.</summary>
    public UsCensusProvider(ApiClient http, AppSettings settings, CountryCatalog catalog)
    {
        _http = http;
        _settings = settings;
        _catalog = catalog;
    }

    /// <inheritdoc />
    public string CountryCode => "US";

    /// <inheritdoc />
    public CountryCatalog Catalog => _catalog;

    /// <summary>
    /// The user's Census API key. Since 2026 the Census API requires a key on every
    /// data request, so a missing key fails fast with setup instructions.
    /// </summary>
    private string Key
    {
        get
        {
            var key = _settings.CensusApiKey;
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    "A free U.S. Census API key is required. Get one at https://api.census.gov/data/key_signup.html and enter it in Settings.");
            return key;
        }
    }

    /// <summary>
    /// Fetches a Census API response, translating the service's key-error redirect into an
    /// actionable message. The API answers a missing or unactivated key with a 302 to
    /// missing_key.html / invalid_key.html rather than a 401, which would otherwise surface
    /// as an opaque JSON parse failure.
    /// </summary>
    private async Task<string> FetchAsync(string url, TimeSpan cacheTtl, CancellationToken ct)
    {
        try
        {
            return await _http.GetStringAsync(url, cacheTtl, ct).ConfigureAwait(false);
        }
        catch (ApiHttpException ex) when (ex.Location?.AbsoluteUri.Contains("key.html", StringComparison.OrdinalIgnoreCase) == true)
        {
            var invalid = ex.Location.AbsoluteUri.Contains("invalid_key", StringComparison.OrdinalIgnoreCase);
            throw new InvalidOperationException(invalid
                ? "The U.S. Census API rejected this key. Check it in Settings, and make sure you clicked the "
                  + "activation link in the email from the Census Bureau — an unactivated key is rejected."
                : "The U.S. Census API requires a key. Get a free one at "
                  + "https://api.census.gov/data/key_signup.html and enter it in Settings.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GeoUnit>> GetTopLevelUnitsAsync(CancellationToken ct = default)
    {
        var url = $"{Base}?get=NAME&for=state:*&key={Uri.EscapeDataString(Key)}";
        var (header, rows) = ParseTable(await FetchAsync(url, GeoCacheTtl, ct).ConfigureAwait(false));
        var nameCol = Col(header, "NAME");
        var stateCol = Col(header, "state");
        return rows
            .Select(r => new GeoUnit(r[stateCol] ?? "", r[nameCol] ?? "", "state", null))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GeoUnit>> GetChildUnitsAsync(GeoUnit ancestor, string targetLevelCode, CancellationToken ct = default)
    {
        if (targetLevelCode is not ("county" or "place"))
            throw new ArgumentException("Unknown child geographic level: " + targetLevelCode, nameof(targetLevelCode));

        var url = $"{Base}?get=NAME&for={targetLevelCode}:*&in=state:{ancestor.Id}&key={Uri.EscapeDataString(Key)}";
        var (header, rows) = ParseTable(await FetchAsync(url, GeoCacheTtl, ct).ConfigureAwait(false));
        var nameCol = Col(header, "NAME");
        var ownCol = Col(header, targetLevelCode);
        return rows
            .Select(r => new GeoUnit(
                ancestor.Id + r[ownCol],
                TrimStateSuffix(r[nameCol] ?? ""),
                targetLevelCode,
                ancestor.Id))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public Task<QueryResult> QueryAsync(DemographicQuery query, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dataset = _catalog.Datasets.FirstOrDefault(d => d.Key == query.DatasetKey)
            ?? throw new InvalidOperationException("Dataset not found in US catalog: " + query.DatasetKey);

        return query.Mode == QueryMode.Profile
            ? QueryProfileAsync(query, dataset, progress, ct)
            : QueryCompareAsync(query, dataset, ct);
    }

    /// <summary>Profile mode: every catalog variable (plus its MOE twin) for one geographic unit.</summary>
    private async Task<QueryResult> QueryProfileAsync(DemographicQuery query, DatasetDef dataset, IProgress<string>? progress, CancellationToken ct)
    {
        var geoId = query.GeoUnitId
            ?? throw new InvalidOperationException("Profile queries require a selected geographic unit.");
        var geo = GeoClause(geoId, query.GeoLevelCode);
        var variables = dataset.Variables;
        var raw = new Dictionary<string, string?>(StringComparer.Ordinal);

        // The API caps 50 variables per request: chunk into 24 E/M pairs (48 vars) + NAME.
        for (var start = 0; start < variables.Count; start += PairsPerRequest)
        {
            var chunk = variables.Skip(start).Take(PairsPerRequest).ToList();
            progress?.Report($"Fetching variables {start + 1}-{start + chunk.Count}...");

            var codes = new List<string> { "NAME" };
            foreach (var v in chunk)
            {
                codes.Add(v.Code);
                if (MoeCode(v.Code) is { } moeCode)
                    codes.Add(moeCode);
            }

            var url = $"{Base}?get={string.Join(',', codes)}&{geo}&key={Uri.EscapeDataString(Key)}";
            var (header, rows) = ParseTable(await FetchAsync(url, DataCacheTtl, ct).ConfigureAwait(false));
            if (rows.Count == 0)
                throw new InvalidDataException("Census API returned no data for " + geo + ".");

            var row = rows[0];
            foreach (var code in codes)
                raw[code] = row[Col(header, code)];
        }

        var resultRows = new List<ResultRow>(variables.Count);
        foreach (var v in variables)
        {
            var (value, flag) = CensusValues.ParseEstimate(raw.GetValueOrDefault(v.Code));
            var moe = MoeCode(v.Code) is { } moeCode
                ? CensusValues.ParseMoe(raw.GetValueOrDefault(moeCode))
                : null;
            resultRows.Add(new ResultRow
            {
                Label = v.Label,
                Code = v.Code,
                Indent = v.Indent,
                Cells = [new DataCell(value, moe, flag, v.Format)],
            });
        }

        return new QueryResult
        {
            Title = dataset.DisplayName + " — " + (raw.GetValueOrDefault("NAME") ?? ""),
            SourceAttribution = Attribution(dataset),
            Universe = dataset.Universe,
            Notes = dataset.Notes,
            NumericCells = false,
            ColumnHeaders = ["Estimate ±MOE (90% CI)"],
            Rows = resultRows,
        };
    }

    /// <summary>Compare mode: the dataset's key variables across all units of one level.</summary>
    private async Task<QueryResult> QueryCompareAsync(DemographicQuery query, DatasetDef dataset, CancellationToken ct)
    {
        string geo;
        if (query.GeoLevelCode == "state")
        {
            geo = "for=state:*";
        }
        else if (query.GeoLevelCode is "county" or "place")
        {
            if (string.IsNullOrEmpty(query.ParentGeoId))
                throw new InvalidOperationException("Comparing " + query.GeoLevelCode + " units requires a parent state.");
            geo = $"for={query.GeoLevelCode}:*&in=state:{query.ParentGeoId}";
        }
        else
        {
            throw new ArgumentException("Unknown geographic level: " + query.GeoLevelCode);
        }

        var codes = dataset.CompareVariables;
        var defs = codes
            .Select(c => dataset.Variables.FirstOrDefault(v => v.Code == c) ?? new VariableDef { Code = c, Label = c })
            .ToList();

        var url = $"{Base}?get=NAME,{string.Join(',', codes)}&{geo}&key={Uri.EscapeDataString(Key)}";
        var (header, rows) = ParseTable(await FetchAsync(url, DataCacheTtl, ct).ConfigureAwait(false));
        var nameCol = Col(header, "NAME");
        var varCols = codes.Select(c => Col(header, c)).ToList();

        // Rebuild the full GEOID from the geography columns the API appends, so exported rows
        // carry the same id the rest of the app uses (state = 2 digits, county/place = state + own).
        var stateCol = Col(header, "state");
        var ownCol = query.GeoLevelCode == "state" ? -1 : Col(header, query.GeoLevelCode);

        var resultRows = rows
            .Select(r =>
            {
                var cells = new List<DataCell>(codes.Count);
                for (var i = 0; i < codes.Count; i++)
                {
                    var (value, flag) = CensusValues.ParseEstimate(r[varCols[i]]);
                    cells.Add(new DataCell(value, null, flag, defs[i].Format));
                }
                var geoId = ownCol < 0 ? r[stateCol] : r[stateCol] + r[ownCol];
                return new ResultRow
                {
                    Label = TrimStateSuffix(r[nameCol] ?? ""),
                    Code = geoId,
                    Indent = 0,
                    Cells = cells,
                };
            })
            .OrderByDescending(r => r.Cells.FirstOrDefault()?.Value) // null values sort last
            .ToList();

        var levelName = _catalog.GeoLevels.FirstOrDefault(l => l.Code == query.GeoLevelCode)?.DisplayName
            ?? query.GeoLevelCode;
        const string moeNote = "MOE omitted in comparison view — open a single area's profile for margins of error.";

        return new QueryResult
        {
            Title = dataset.DisplayName + " — " + levelName + " comparison",
            SourceAttribution = Attribution(dataset),
            Universe = dataset.Universe,
            Notes = string.IsNullOrWhiteSpace(dataset.Notes) ? moeNote : dataset.Notes + " " + moeNote,
            NumericCells = true,
            ColumnFormats = defs.Select(d => d.Format).ToList(),
            ColumnHeaders = defs.Select(d => d.Label).ToList(),
            Rows = resultRows,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AreaValue>> GetAreaValuesAsync(
        string levelCode, string? parentId, IReadOnlyList<string> variableCodes,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (levelCode is not ("state" or "county" or "place"))
            throw new ArgumentException("Unknown geographic level: " + levelCode, nameof(levelCode));
        if (variableCodes.Count == 0)
            throw new ArgumentException("At least one variable code is required.", nameof(variableCodes));

        var geo = $"for={levelCode}:*" + (string.IsNullOrEmpty(parentId) ? "" : "&in=state:" + parentId);
        progress?.Report("Fetching values for all " + levelCode + " areas...");

        var url = $"{Base}?get=NAME,{string.Join(',', variableCodes)}&{geo}&key={Uri.EscapeDataString(Key)}";
        var (header, rows) = ParseTable(await FetchAsync(url, DataCacheTtl, ct).ConfigureAwait(false));
        var nameCol = Col(header, "NAME");
        var varCols = variableCodes.Select(c => Col(header, c)).ToList();

        // Rebuild the full GEOID from the geography columns the API appends
        // (state = 2 digits, county/place = state + own code).
        var stateCol = Col(header, "state");
        var ownCol = levelCode == "state" ? -1 : Col(header, levelCode);

        var result = new List<AreaValue>(rows.Count);
        foreach (var r in rows)
        {
            var values = new Dictionary<string, double?>(variableCodes.Count, StringComparer.Ordinal);
            for (var i = 0; i < variableCodes.Count; i++)
            {
                var (value, _) = CensusValues.ParseEstimate(r[varCols[i]]);
                values[variableCodes[i]] = value;
            }
            var geoId = ownCol < 0 ? r[stateCol] ?? "" : r[stateCol] + r[ownCol];
            result.Add(new AreaValue(geoId, TrimStateSuffix(r[nameCol] ?? ""), values));
        }
        return result;
    }

    /// <summary>Builds the for=/in= geography clause for one full GEOID at a given level.</summary>
    private static string GeoClause(string geoId, string levelCode) => levelCode switch
    {
        "state" => "for=state:" + geoId,
        "county" => "for=county:" + geoId[2..] + "&in=state:" + geoId[..2],
        "place" => "for=place:" + geoId[2..] + "&in=state:" + geoId[..2],
        _ => throw new ArgumentException("Unknown geographic level: " + levelCode, nameof(levelCode)),
    };

    /// <summary>MOE twin of an estimate code (trailing 'E' -> 'M'), or null if the code has no 'E' suffix.</summary>
    private static string? MoeCode(string estimateCode) =>
        estimateCode.EndsWith('E') ? estimateCode[..^1] + "M" : null;

    /// <summary>Drops the trailing ", {state}" a Census NAME carries (substring before the last comma).</summary>
    private static string TrimStateSuffix(string name)
    {
        var comma = name.LastIndexOf(',');
        return comma < 0 ? name : name[..comma];
    }

    private string Attribution(DatasetDef dataset) =>
        _catalog.SourceName + ", " + dataset.SourceTable + " — api.census.gov/data/2024/acs/acs5";

    /// <summary>
    /// Parses the Census array-of-arrays payload: row 0 is the header, every cell is a string.
    /// Returns a header-name-to-index map plus the data rows.
    /// </summary>
    private static (Dictionary<string, int> Header, List<string?[]> Rows) ParseTable(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidDataException("Unexpected Census API response (expected a JSON array of arrays).");

        var header = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var cell in root[0].EnumerateArray())
            header[cell.GetString() ?? ""] = i++;

        var rows = new List<string?[]>();
        for (var r = 1; r < root.GetArrayLength(); r++)
        {
            var rowElement = root[r];
            var row = new string?[rowElement.GetArrayLength()];
            var c = 0;
            foreach (var cell in rowElement.EnumerateArray())
                row[c++] = cell.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => cell.GetString(),
                    _ => cell.GetRawText(),
                };
            rows.Add(row);
        }
        return (header, rows);
    }

    /// <summary>Looks up a column index by header name; columns are never addressed by position.</summary>
    private static int Col(Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index)
            ? index
            : throw new InvalidDataException("Column \"" + name + "\" missing from Census API response.");
}
