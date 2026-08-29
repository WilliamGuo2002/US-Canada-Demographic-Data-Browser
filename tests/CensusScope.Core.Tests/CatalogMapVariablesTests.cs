using CensusScope.Core.Services;

namespace CensusScope.Core.Tests;

/// <summary>
/// Config-integrity tests for the map layer: every mapVariable in both shipped catalogs must
/// be renderable by MapService — a label for the legend, a known derivation kind, numerator
/// and denominator codes that actually exist in the dataset, and a display format.
/// </summary>
public class CatalogMapVariablesTests
{
    private static readonly string[] ValidKinds = ["percent", "direct", "density"];

    [Theory]
    [InlineData("us")]
    [InlineData("ca")]
    public void EveryDataset_OffersAtLeastOneMapVariable(string countryCode)
    {
        var catalog = CatalogService.Load(countryCode);

        foreach (var dataset in catalog.Datasets)
            Assert.True(dataset.MapVariables.Count > 0,
                $"Dataset '{dataset.Key}' ({countryCode}) has no mapVariables.");
    }

    [Theory]
    [InlineData("us")]
    [InlineData("ca")]
    public void EveryMapVariable_IsWellFormedAndReferencesExistingCodes(string countryCode)
    {
        var catalog = CatalogService.Load(countryCode);

        foreach (var dataset in catalog.Datasets)
        {
            var codes = dataset.Variables.Select(v => v.Code).ToHashSet(StringComparer.Ordinal);

            foreach (var mapVar in dataset.MapVariables)
            {
                var where = $"mapVariable '{mapVar.Key}' of dataset '{dataset.Key}' ({countryCode})";

                Assert.False(string.IsNullOrWhiteSpace(mapVar.Key), where + " has an empty key.");
                Assert.False(string.IsNullOrWhiteSpace(mapVar.Label), where + " has an empty label.");
                Assert.True(ValidKinds.Contains(mapVar.Kind),
                    $"{where} has unknown kind \"{mapVar.Kind}\".");
                Assert.False(string.IsNullOrWhiteSpace(mapVar.Format), where + " has an empty format.");

                Assert.True(mapVar.Numerator.Count > 0, where + " has no numerator codes.");
                foreach (var code in mapVar.Numerator)
                    Assert.True(codes.Contains(code),
                        $"{where} numerator '{code}' is not among the dataset's variables.");

                if (mapVar.Denominator is not null)
                    Assert.True(codes.Contains(mapVar.Denominator),
                        $"{where} denominator '{mapVar.Denominator}' is not among the dataset's variables.");

                if (mapVar.Kind == "percent")
                    Assert.False(string.IsNullOrEmpty(mapVar.Denominator),
                        where + " has kind \"percent\" but no denominator.");
            }
        }
    }

    [Theory]
    [InlineData("us")]
    [InlineData("ca")]
    public void MapVariableKeys_AreUniqueWithinEachDataset(string countryCode)
    {
        var catalog = CatalogService.Load(countryCode);

        foreach (var dataset in catalog.Datasets)
        {
            var keys = dataset.MapVariables.Select(m => m.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }
}
