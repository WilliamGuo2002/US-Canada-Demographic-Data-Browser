namespace CensusScope.Core.Geo;

/// <summary>
/// Identity and land area of one boundary feature. <paramref name="Id"/> is the country-native
/// geographic id (US: FIPS GEOID, Canada: DGUID) and matches <c>GeoUnit.Id</c> exactly, so the
/// caller can build the land-area lookup and detect join gaps against demographic results.
/// </summary>
/// <param name="Id">Country-native geographic id (US GEOID / Canadian DGUID).</param>
/// <param name="Name">Feature display name.</param>
/// <param name="LandKm2">Land area in square kilometres, or null when the source omits it.</param>
public sealed record BoundaryArea(string Id, string Name, double? LandKm2);

/// <summary>A ready-to-render boundary layer for one (country, level) combination.</summary>
public sealed class BoundarySet
{
    /// <summary>
    /// GeoJSON FeatureCollection string, coordinates in lon/lat degrees (WGS84-compatible).
    /// Every feature carries properties <c>id</c>, <c>name</c>, and <c>landKm2</c>.
    /// </summary>
    public required string GeoJson { get; init; }

    /// <summary>One entry per feature in <see cref="GeoJson"/> (post-filter), in file order.</summary>
    public required IReadOnlyList<BoundaryArea> Areas { get; init; }

    /// <summary>
    /// Source credit line, e.g. "Boundaries: U.S. Census Bureau cartographic boundary files (2024)"
    /// or "Boundaries: Statistics Canada, 2021 Census cartographic boundary files".
    /// </summary>
    public required string Attribution { get; init; }
}
