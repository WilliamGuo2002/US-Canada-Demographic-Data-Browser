using System.Globalization;

namespace CensusScope.Core.Models;

public enum QueryMode
{
    /// <summary>One geographic unit, all categories of one dataset (rows = categories).</summary>
    Profile,
    /// <summary>All units of one level under a parent, key variables of one dataset (rows = units).</summary>
    Compare,
}

/// <param name="GeoLevelCode">Level of the unit(s) the query returns data for.</param>
/// <param name="GeoUnitId">Profile mode: the selected unit id. Null in Compare mode.</param>
/// <param name="ParentGeoId">Compare mode: id of the ancestor containing the compared units. Null = whole country.</param>
public sealed record DemographicQuery(
    string CountryCode,
    QueryMode Mode,
    string DatasetKey,
    string GeoLevelCode,
    string? GeoUnitId,
    string? ParentGeoId);

/// <summary>One value cell. Value/Moe are null when suppressed or not applicable (see Flag).</summary>
public sealed record DataCell(double? Value, double? Moe, string? Flag, string Format = "count")
{
    public string Display
    {
        get
        {
            if (Value is null) return Flag ?? "—";
            var v = FormatValue(Value.Value, Format);
            return Moe is null ? v : v + " ±" + FormatValue(Moe.Value, Format);
        }
    }

    /// <summary>
    /// Formats a raw value per the shared cell conventions:
    /// "currency", "decimal", "percent", "area", or count (default).
    /// </summary>
    public static string FormatValue(double v, string format) => format switch
    {
        "currency" => v.ToString("$#,0", CultureInfo.InvariantCulture),
        "decimal" => v.ToString("#,0.0", CultureInfo.InvariantCulture),
        "percent" => v.ToString("#,0.#", CultureInfo.InvariantCulture) + "%",
        "area" => v.ToString("#,0.##", CultureInfo.InvariantCulture),
        _ => v.ToString("#,0.#", CultureInfo.InvariantCulture),
    };
}

/// <summary>A generic result table the UI can render for either country and either mode.</summary>
public sealed class QueryResult
{
    public required string Title { get; init; }
    /// <summary>Full citation, e.g. "U.S. Census Bureau, ACS 2020-2024 5-Year, Table B01001".</summary>
    public required string SourceAttribution { get; init; }
    public required string Universe { get; init; }
    public string? Notes { get; init; }
    /// <summary>True when data cells are plain numbers (Compare mode) so the grid can sort numerically.</summary>
    public bool NumericCells { get; init; }

    /// <summary>
    /// Value format per data column ("currency", "percent", …), aligned with
    /// <see cref="ColumnHeaders"/>. Set in Compare mode so the grid can format numeric
    /// columns ($ and %) instead of showing bare numbers; null elsewhere.
    /// </summary>
    public IReadOnlyList<string>? ColumnFormats { get; init; }

    public required IReadOnlyList<string> ColumnHeaders { get; init; }
    public required IReadOnlyList<ResultRow> Rows { get; init; }
}

public sealed class ResultRow
{
    /// <summary>Category label (Profile mode) or geographic unit name (Compare mode).</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The row's source code: the variable id in Profile mode (US "B01001_003E", Canada the
    /// characteristic id) or the geographic id in Compare mode (GEOID / DGUID). Exported to CSV
    /// as the join key so downstream tools can match rows without relying on display names.
    /// </summary>
    public string? Code { get; init; }

    public int Indent { get; init; }
    public required IReadOnlyList<DataCell> Cells { get; init; }
}
