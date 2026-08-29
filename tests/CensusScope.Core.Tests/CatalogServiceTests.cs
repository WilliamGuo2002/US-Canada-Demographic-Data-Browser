using CensusScope.Core.Models;
using CensusScope.Core.Services;

namespace CensusScope.Core.Tests;

public class CatalogServiceTests
{
    [Fact]
    public void Load_Us_HasSixDatasetsIncludingSexByAgeWith49Variables()
    {
        var catalog = CatalogService.Load("us");

        Assert.Equal(6, catalog.Datasets.Count);
        var sexByAge = catalog.Datasets.SingleOrDefault(d => d.Key == "sex_by_age");
        Assert.NotNull(sexByAge);
        Assert.Equal(49, sexByAge.Variables.Count);
    }

    [Fact]
    public void Load_Ca_HasSixDatasetsIncludingEducation2564With16Variables()
    {
        var catalog = CatalogService.Load("ca");

        Assert.Equal(6, catalog.Datasets.Count);
        var education = catalog.Datasets.SingleOrDefault(d => d.Key == "education_25_64");
        Assert.NotNull(education);
        Assert.Equal(16, education.Variables.Count);
    }

    [Theory]
    [InlineData("us")]
    [InlineData("ca")]
    public void Load_EveryDataset_HasUniverseAndValidCompareVariables(string countryCode)
    {
        var catalog = CatalogService.Load(countryCode);

        foreach (var dataset in catalog.Datasets)
        {
            Assert.False(string.IsNullOrWhiteSpace(dataset.Universe),
                $"Dataset '{dataset.Key}' has an empty universe.");
            Assert.NotEmpty(dataset.CompareVariables);

            var codes = dataset.Variables.Select(v => v.Code).ToHashSet(StringComparer.Ordinal);
            foreach (var compareVariable in dataset.CompareVariables)
                Assert.True(codes.Contains(compareVariable),
                    $"Dataset '{dataset.Key}' compareVariable '{compareVariable}' is not among its variables.");
        }
    }
}
