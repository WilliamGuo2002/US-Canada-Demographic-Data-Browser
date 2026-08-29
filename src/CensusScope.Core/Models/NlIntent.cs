using System.Text.Json.Serialization;

namespace CensusScope.Core.Models;

/// <summary>
/// The structured intent extracted by Gemini from a natural-language question.
/// Every property is nullable — the caller validates values against the catalog
/// before building a <see cref="DemographicQuery"/>.
/// </summary>
/// <param name="Country">Country code the question is about: "US" or "CA".</param>
/// <param name="Mode">"profile" (one named area) or "compare" (ranking of all areas at a level).</param>
/// <param name="DatasetKey">Dataset key chosen from the catalog options.</param>
/// <param name="GeoLevel">Geographic level code chosen from the catalog options.</param>
/// <param name="GeoName">The specific area the user named, or null (compare mode without a named area).</param>
/// <param name="ParentGeoName">The containing state/province the user named or implied, or null.</param>
/// <param name="Explanation">One short sentence, in the user's language, describing the interpretation.</param>
public sealed record NlIntent(
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("datasetKey")] string? DatasetKey,
    [property: JsonPropertyName("geoLevel")] string? GeoLevel,
    [property: JsonPropertyName("geoName")] string? GeoName,
    [property: JsonPropertyName("parentGeoName")] string? ParentGeoName,
    [property: JsonPropertyName("explanation")] string? Explanation);
