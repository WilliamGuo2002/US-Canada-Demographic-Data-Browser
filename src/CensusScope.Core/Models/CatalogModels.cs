using System.Text.Json.Serialization;

namespace CensusScope.Core.Models;

/// <summary>
/// The dimension catalog for one country. Loaded from Catalogs/*.json at startup —
/// adding a dataset or variable is a config change, not a code change.
/// </summary>
public sealed class CountryCatalog
{
    [JsonPropertyName("countryCode")] public string CountryCode { get; set; } = "";
    [JsonPropertyName("countryName")] public string CountryName { get; set; } = "";
    /// <summary>Human-readable source label, e.g. "ACS 2020–2024 5-Year Estimates".</summary>
    [JsonPropertyName("sourceName")] public string SourceName { get; set; } = "";
    [JsonPropertyName("geoLevels")] public List<GeoLevelDef> GeoLevels { get; set; } = [];
    [JsonPropertyName("datasets")] public List<DatasetDef> Datasets { get; set; } = [];
}

public sealed class GeoLevelDef
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    /// <summary>Code of the parent level, or null for the top level.</summary>
    [JsonPropertyName("parent")] public string? Parent { get; set; }
}

public sealed class DatasetDef
{
    /// <summary>Stable key used by the UI and the Gemini intent layer, e.g. "sex_by_age".</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    /// <summary>Native source id: US table id ("B01001") or a Canada grouping label.</summary>
    [JsonPropertyName("sourceTable")] public string SourceTable { get; set; } = "";
    /// <summary>The statistical universe (denominator population) — always shown in the UI.</summary>
    [JsonPropertyName("universe")] public string Universe { get; set; } = "";
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    /// <summary>Canada only: false = characteristic has no gender split (single Total column).</summary>
    [JsonPropertyName("gendered")] public bool Gendered { get; set; } = true;
    [JsonPropertyName("variables")] public List<VariableDef> Variables { get; set; } = [];
    /// <summary>Variable codes used as columns in Compare mode (a small, readable subset).</summary>
    [JsonPropertyName("compareVariables")] public List<string> CompareVariables { get; set; } = [];
    /// <summary>Derived indicators offered on the choropleth map for this dataset.</summary>
    [JsonPropertyName("mapVariables")] public List<MapVariableDef> MapVariables { get; set; } = [];
}

/// <summary>
/// A derived indicator the map view can shade areas by: one or more source variable codes
/// (summed) optionally divided by a denominator variable or by land area.
/// </summary>
public sealed class MapVariableDef
{
    /// <summary>Stable key used by the UI, e.g. "pct_65plus".</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    /// <summary>Human-readable legend label, e.g. "Population 65 and over (%)".</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>One or more variable codes, summed.</summary>
    [JsonPropertyName("numerator")] public List<string> Numerator { get; set; } = [];
    /// <summary>Denominator variable code; required when <see cref="Kind"/> is "percent".</summary>
    [JsonPropertyName("denominator")] public string? Denominator { get; set; }
    /// <summary>How the value is derived: "percent" | "direct" | "density".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "direct";
    /// <summary>Value formatting; reuses the <see cref="DataCell"/> formats ("percent" for shares).</summary>
    [JsonPropertyName("format")] public string Format { get; set; } = "decimal";
}

public sealed class VariableDef
{
    /// <summary>US: ACS variable name ("B01001_003E"); Canada: CHARACTERISTIC_ID as string.</summary>
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>Display indent level (0 = total row, 1/2 = nested categories).</summary>
    [JsonPropertyName("indent")] public int Indent { get; set; }
    /// <summary>Value formatting: "count" (default), "currency", "decimal", "percent", "area".</summary>
    [JsonPropertyName("format")] public string Format { get; set; } = "count";
}
