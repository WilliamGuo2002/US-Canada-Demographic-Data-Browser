namespace CensusScope.Core.Models;

/// <summary>A level in a country's administrative hierarchy (e.g. state, county, place).</summary>
public sealed record GeoLevel(string Code, string DisplayName, string? ParentLevelCode);

/// <summary>
/// A single geographic unit. <see cref="Id"/> is the country-native identifier
/// (US: FIPS GEOID, Canada: DGUID) and is ALWAYS a string — leading zeros are significant.
/// </summary>
public sealed record GeoUnit(string Id, string Name, string LevelCode, string? ParentId)
{
    public override string ToString() => Name;
}

/// <summary>One geography's values for a set of variables, used by the map layer.</summary>
public sealed record AreaValue(string GeoId, string Name, IReadOnlyDictionary<string, double?> Values);
