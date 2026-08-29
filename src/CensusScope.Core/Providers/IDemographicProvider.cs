using CensusScope.Core.Models;

namespace CensusScope.Core.Providers;

/// <summary>
/// One country's data source. The abstraction describes structure (levels, datasets,
/// categories) but never semantics — each country keeps its native classifications.
/// </summary>
public interface IDemographicProvider
{
    string CountryCode { get; }
    CountryCatalog Catalog { get; }

    /// <summary>Units of the top geographic level (US states / Canadian provinces).</summary>
    Task<IReadOnlyList<GeoUnit>> GetTopLevelUnitsAsync(CancellationToken ct = default);

    /// <summary>
    /// All units of <paramref name="targetLevelCode"/> contained in <paramref name="ancestor"/>.
    /// The ancestor does not have to be the immediate parent (e.g. PR → all CSDs in the province).
    /// </summary>
    Task<IReadOnlyList<GeoUnit>> GetChildUnitsAsync(GeoUnit ancestor, string targetLevelCode, CancellationToken ct = default);

    Task<QueryResult> QueryAsync(DemographicQuery query, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Values of the given variables for ALL units of a level (optionally within an ancestor).</summary>
    Task<IReadOnlyList<AreaValue>> GetAreaValuesAsync(string levelCode, string? parentId, IReadOnlyList<string> variableCodes, IProgress<string>? progress = null, CancellationToken ct = default);
}
