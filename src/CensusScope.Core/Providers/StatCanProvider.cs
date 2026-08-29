using System.Globalization;
using CensusScope.Core.Http;
using CensusScope.Core.Models;
using CensusScope.Core.Parsing;

namespace CensusScope.Core.Providers;

/// <summary>
/// Statistics Canada provider backed by the 2021 Census Profile SDMX web service.
/// Geography is enumerated from the CL_GEO_* codelists; observations are fetched as
/// long-format CSV from the DF_PR / DF_CD / DF_CSD dataflows.
/// </summary>
public sealed class StatCanProvider : IDemographicProvider
{
    private const string BaseUrl = "https://api.statcan.gc.ca/census-recensement/profile/sdmx/rest";
    private const string CanadaDguid = "2021A000011124";
    private const string RoundingNote = "Values are randomly rounded to base 5; totals may not equal the sum of parts.";
    private const int CompareChunkSize = 30;

    private static readonly TimeSpan CodelistTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan DataTtl = TimeSpan.FromDays(7);

    private readonly ApiClient _http;

    /// <summary>Creates the provider over an HTTP client and the loaded Canada catalog.</summary>
    public StatCanProvider(ApiClient http, CountryCatalog catalog)
    {
        _http = http;
        Catalog = catalog;
    }

    /// <inheritdoc />
    public string CountryCode => "CA";

    /// <inheritdoc />
    public CountryCatalog Catalog { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GeoUnit>> GetTopLevelUnitsAsync(CancellationToken ct = default)
    {
        var codes = await GetCodelistAsync("PR", ct).ConfigureAwait(false);
        return codes
            .Where(c => c.Id != CanadaDguid)
            .Select(c => new GeoUnit(c.Id, c.NameEn, "PR", null))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GeoUnit>> GetChildUnitsAsync(GeoUnit ancestor, string targetLevelCode, CancellationToken ct = default)
    {
        var prefix = ChildDguidPrefix(ancestor.LevelCode, targetLevelCode, ancestor.Id);
        var codes = await GetCodelistAsync(targetLevelCode, ct).ConfigureAwait(false);
        return codes
            .Where(c => c.Id.StartsWith(prefix, StringComparison.Ordinal))
            .Select(c => new GeoUnit(c.Id, DisplayName(c), targetLevelCode, ancestor.Id))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<QueryResult> QueryAsync(DemographicQuery query, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dataset = Catalog.Datasets.FirstOrDefault(d => d.Key == query.DatasetKey)
            ?? throw new ArgumentException($"Dataset key '{query.DatasetKey}' is not defined in the {CountryCode} catalog.");

        return query.Mode == QueryMode.Profile
            ? await QueryProfileAsync(query, dataset, ct).ConfigureAwait(false)
            : await QueryCompareAsync(query, dataset, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Profile mode: one geographic unit, all catalog variables of the dataset as rows.</summary>
    private async Task<QueryResult> QueryProfileAsync(DemographicQuery query, DatasetDef dataset, CancellationToken ct)
    {
        var geoUnitId = query.GeoUnitId
            ?? throw new ArgumentException("Profile queries require GeoUnitId.");

        // Key positions: FREQ . REF_AREA . GENDER . CHARACTERISTIC . STATISTIC
        // (an empty position means "all values of that dimension").
        var gender = dataset.Gendered ? "" : "1";
        var characteristics = string.Join("+", dataset.Variables.Select(v => v.Code));
        var key = $"A5.{geoUnitId}.{gender}.{characteristics}.";
        var flow = FlowFor(query.GeoLevelCode);

        var csv = await _http.GetStringAsync(
            $"{BaseUrl}/data/STC_CP,{flow}/{key}?format=csv", DataTtl, ct).ConfigureAwait(false);

        // Rows arrive unordered; index them by (characteristic, gender), preferring Counts
        // (STATISTIC 1) over Rates (STATISTIC 4) when both are present.
        var observations = new Dictionary<(string Characteristic, string Gender), Dictionary<string, string>>();
        foreach (var row in CsvParser.Parse(csv))
            MergePreferred(observations, (Field(row, "CHARACTERISTIC"), Field(row, "GENDER")), row);

        var columns = dataset.Gendered
            ? new[] { ("Total", "1"), ("Men+", "2"), ("Women+", "3") }
            : [("Total", "1")];

        var rows = new List<ResultRow>(dataset.Variables.Count);
        foreach (var variable in dataset.Variables)
        {
            var cells = new List<DataCell>(columns.Length);
            foreach (var (_, genderCode) in columns)
            {
                observations.TryGetValue((variable.Code, genderCode), out var obs);
                cells.Add(MakeCell(obs, variable.Format));
            }
            rows.Add(new ResultRow
            {
                Label = variable.Label,
                Code = variable.Code,
                Indent = variable.Indent,
                Cells = cells,
            });
        }

        var geoName = await ResolveGeoNameAsync(geoUnitId, ct).ConfigureAwait(false);
        return new QueryResult
        {
            Title = dataset.DisplayName + " — " + geoName,
            SourceAttribution = SourceAttribution(dataset),
            Universe = dataset.Universe,
            Notes = BuildNotes(dataset),
            NumericCells = false,
            ColumnHeaders = columns.Select(c => c.Item1).ToList(),
            Rows = rows,
        };
    }

    /// <summary>Compare mode: all sibling units under a parent as rows, key variables as columns.</summary>
    private async Task<QueryResult> QueryCompareAsync(
        DemographicQuery query, DatasetDef dataset, IProgress<string>? progress, CancellationToken ct)
    {
        IReadOnlyList<GeoUnit> children;
        string? parentName = null;
        if (query.ParentGeoId is null)
        {
            children = await GetTopLevelUnitsAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var ancestor = new GeoUnit(query.ParentGeoId, query.ParentGeoId, LevelFromDguid(query.ParentGeoId), null);
            children = await GetChildUnitsAsync(ancestor, query.GeoLevelCode, ct).ConfigureAwait(false);
            parentName = await ResolveGeoNameAsync(query.ParentGeoId, ct).ConfigureAwait(false);
        }

        var flow = FlowFor(query.GeoLevelCode);
        var characteristics = string.Join("+", dataset.CompareVariables);
        var observations = new Dictionary<(string RefArea, string Characteristic), Dictionary<string, string>>();

        for (var offset = 0; offset < children.Count; offset += CompareChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = children.Skip(offset).Take(CompareChunkSize).ToList();
            progress?.Report($"Fetching areas {Math.Min(offset + chunk.Count, children.Count)}/{children.Count}...");

            var refAreas = string.Join("+", chunk.Select(c => c.Id));
            var key = $"A5.{refAreas}.1.{characteristics}.";
            var csv = await _http.GetStringAsync(
                $"{BaseUrl}/data/STC_CP,{flow}/{key}?format=csv", DataTtl, ct).ConfigureAwait(false);

            foreach (var row in CsvParser.Parse(csv))
                MergePreferred(observations, (Field(row, "REF_AREA"), Field(row, "CHARACTERISTIC")), row);
        }

        var formats = dataset.CompareVariables
            .Select(code => dataset.Variables.FirstOrDefault(v => v.Code == code)?.Format ?? "count")
            .ToList();

        var rows = new List<ResultRow>(children.Count);
        foreach (var child in children)
        {
            var cells = new List<DataCell>(dataset.CompareVariables.Count);
            for (var i = 0; i < dataset.CompareVariables.Count; i++)
            {
                observations.TryGetValue((child.Id, dataset.CompareVariables[i]), out var obs);
                cells.Add(MakeCell(obs, formats[i]));
            }
            rows.Add(new ResultRow { Label = child.Name, Code = child.Id, Indent = 0, Cells = cells });
        }

        // Sort by the first column, largest first, suppressed/missing values last.
        var ordered = rows
            .OrderBy(r => r.Cells.Count > 0 && r.Cells[0].Value is not null ? 0 : 1)
            .ThenByDescending(r => r.Cells.Count > 0 ? r.Cells[0].Value ?? 0 : 0)
            .ToList();

        var headers = dataset.CompareVariables
            .Select(code => Truncate(dataset.Variables.FirstOrDefault(v => v.Code == code)?.Label ?? code, 60))
            .ToList();

        var levelName = Catalog.GeoLevels.FirstOrDefault(l => l.Code == query.GeoLevelCode)?.DisplayName
            ?? query.GeoLevelCode;
        return new QueryResult
        {
            Title = $"{dataset.DisplayName} — {parentName ?? "Canada"}, by {levelName}",
            SourceAttribution = SourceAttribution(dataset),
            Universe = dataset.Universe,
            Notes = BuildNotes(dataset),
            NumericCells = true,
            ColumnFormats = formats,
            ColumnHeaders = headers,
            Rows = ordered,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AreaValue>> GetAreaValuesAsync(
        string levelCode, string? parentId, IReadOnlyList<string> variableCodes,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (variableCodes.Count == 0)
            throw new ArgumentException("At least one variable code is required.", nameof(variableCodes));

        var flow = FlowFor(levelCode);
        progress?.Report("Fetching values for all " + levelCode + " areas...");

        // Leaving REF_AREA empty returns every geography of the dataflow in one request.
        // Key positions: FREQ . REF_AREA . GENDER . CHARACTERISTIC . STATISTIC.
        var key = $"A5..1.{string.Join("+", variableCodes)}.";
        var csv = await _http.GetStringAsync(
            $"{BaseUrl}/data/STC_CP,{flow}/{key}?format=csv", DataTtl, ct).ConfigureAwait(false);

        var observations = new Dictionary<(string RefArea, string Characteristic), Dictionary<string, string>>();
        foreach (var row in CsvParser.Parse(csv))
            MergePreferred(observations, (Field(row, "REF_AREA"), Field(row, "CHARACTERISTIC")), row);

        var prefix = parentId is null
            ? null
            : ChildDguidPrefix(LevelFromDguid(parentId), levelCode, parentId);

        var areas = new Dictionary<string, Dictionary<string, double?>>(StringComparer.Ordinal);
        foreach (var ((refArea, characteristic), row) in observations)
        {
            if (refArea == CanadaDguid)
                continue; // the whole-country row is not a mappable unit
            if (prefix is not null && !refArea.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (!areas.TryGetValue(refArea, out var values))
                areas[refArea] = values = new Dictionary<string, double?>(variableCodes.Count, StringComparer.Ordinal);
            values[characteristic] = ParseObsValue(row);
        }

        var names = (await GetCodelistAsync(levelCode, ct).ConfigureAwait(false))
            .ToDictionary(c => c.Id, DisplayName, StringComparer.Ordinal);

        var result = new List<AreaValue>(areas.Count);
        foreach (var (dguid, values) in areas)
        {
            foreach (var code in variableCodes)
                if (!values.ContainsKey(code))
                    values[code] = null; // no observation returned at all
            result.Add(new AreaValue(dguid, names.GetValueOrDefault(dguid, dguid), values));
        }
        return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Numeric OBS_VALUE of an observation row, or null when empty or non-numeric (suppressed).</summary>
    private static double? ParseObsValue(Dictionary<string, string> row)
    {
        var raw = Field(row, "OBS_VALUE");
        return raw.Length > 0 &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// DGUID prefix shared by all units of <paramref name="targetLevel"/> under an ancestor.
    /// DGUID layout: vintage(4) + type(1) + schema(4) + geographic code. Every CD of a
    /// province shares the province's 2-digit code as a prefix, and every CSD shares its
    /// province (2-digit) or census division (4-digit) prefix, so containment is a
    /// simple prefix test on the code portion.
    /// </summary>
    private static string ChildDguidPrefix(string ancestorLevel, string targetLevel, string ancestorId) =>
        (ancestorLevel, targetLevel) switch
        {
            ("PR", "CD") => "2021A0003" + ancestorId[9..],
            ("PR", "CSD") => "2021A0005" + ancestorId[9..],
            ("CD", "CSD") => "2021A0005" + ancestorId[9..],
            _ => throw new ArgumentException(
                $"Cannot enumerate '{targetLevel}' units under a '{ancestorLevel}' ancestor."),
        };

    /// <summary>Keeps the preferred observation per key: STATISTIC 1 (Counts) beats 4 (Rates) beats anything else.</summary>
    private static void MergePreferred(
        Dictionary<(string, string), Dictionary<string, string>> observations,
        (string, string) key,
        Dictionary<string, string> row)
    {
        if (!observations.TryGetValue(key, out var existing))
        {
            observations[key] = row;
            return;
        }
        var existingStat = Field(existing, "STATISTIC");
        var newStat = Field(row, "STATISTIC");
        if (existingStat != "1" && (newStat == "1" || (newStat == "4" && existingStat != "4")))
            observations[key] = row;
    }

    /// <summary>Builds one cell from an observation row (null row = no observation returned at all).</summary>
    private static DataCell MakeCell(Dictionary<string, string>? row, string format)
    {
        if (row is null)
            return new DataCell(null, null, "—", format);

        var rawFlag = Field(row, "FLAG");
        var flag = rawFlag.Length == 0 ? null : rawFlag;
        var rawValue = Field(row, "OBS_VALUE");
        if (rawValue.Length == 0 ||
            !double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return new DataCell(null, null, flag ?? "suppressed", format);
        }
        return new DataCell(value, null, flag, format);
    }

    /// <summary>Resolves a DGUID to its display name via the codelist of its level.</summary>
    private async Task<string> ResolveGeoNameAsync(string dguid, CancellationToken ct)
    {
        var codes = await GetCodelistAsync(LevelFromDguid(dguid), ct).ConfigureAwait(false);
        var match = codes.FirstOrDefault(c => c.Id == dguid);
        return match is null ? dguid : DisplayName(match);
    }

    /// <summary>Fetches and parses the geography codelist for a level (cached 30 days).</summary>
    private async Task<List<SdmxCode>> GetCodelistAsync(string levelCode, CancellationToken ct)
    {
        var codelist = CodelistFor(levelCode);
        var xml = await _http.GetStringAsync(
            $"{BaseUrl}/codelist/STC_CP/{codelist}/latest", CodelistTtl, ct).ConfigureAwait(false);
        return SdmxCodelistParser.Parse(xml);
    }

    /// <summary>
    /// Display name for a geography code. CSD descriptions look like
    /// "Toronto, City [Census subdivision], Ontario" — the part before " [" is the name.
    /// </summary>
    private static string DisplayName(SdmxCode code)
    {
        if (!string.IsNullOrEmpty(code.DescriptionEn))
        {
            var bracket = code.DescriptionEn.IndexOf(" [", StringComparison.Ordinal);
            return bracket >= 0 ? code.DescriptionEn[..bracket] : code.DescriptionEn;
        }
        return code.NameEn;
    }

    /// <summary>Infers the geographic level from a DGUID's type+schema digits (positions 4-8).</summary>
    private static string LevelFromDguid(string dguid)
    {
        if (dguid.Length < 9)
            throw new ArgumentException("Malformed DGUID: " + dguid);
        return dguid.Substring(4, 5) switch
        {
            "A0002" => "PR",
            "A0003" => "CD",
            "A0005" => "CSD",
            _ => throw new ArgumentException("Cannot infer geographic level from DGUID: " + dguid),
        };
    }

    private static string FlowFor(string levelCode) => levelCode switch
    {
        "PR" => "DF_PR",
        "CD" => "DF_CD",
        "CSD" => "DF_CSD",
        _ => throw new ArgumentException("Unsupported geographic level: " + levelCode),
    };

    private static string CodelistFor(string levelCode) => levelCode switch
    {
        "PR" => "CL_GEO_PR",
        "CD" => "CL_GEO_CD",
        "CSD" => "CL_GEO_CSD",
        _ => throw new ArgumentException("Unsupported geographic level: " + levelCode),
    };

    private static string Field(Dictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var value) ? value : "";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private string SourceAttribution(DatasetDef dataset) =>
        Catalog.SourceName + " — Web Data Service (SDMX), " + dataset.SourceTable;

    private static string BuildNotes(DatasetDef dataset) =>
        string.IsNullOrEmpty(dataset.Notes) ? RoundingNote : dataset.Notes + " " + RoundingNote;
}
