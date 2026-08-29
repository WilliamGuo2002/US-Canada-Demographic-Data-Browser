using System.Text.Json;
using CensusScope.Core.Models;

namespace CensusScope.Core.Services;

/// <summary>Loads the per-country dimension catalogs shipped as Catalogs/*.json next to the executable.</summary>
public static class CatalogService
{
    public static CountryCatalog Load(string countryCode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Catalogs", countryCode.ToLowerInvariant() + "-catalog.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Dimension catalog not found: " + path);
        var catalog = JsonSerializer.Deserialize<CountryCatalog>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Catalog deserialized to null: " + path);
        if (catalog.Datasets.Count == 0)
            throw new InvalidDataException("Catalog contains no datasets: " + path);
        return catalog;
    }
}
