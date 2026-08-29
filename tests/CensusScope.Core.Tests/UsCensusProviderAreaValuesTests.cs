// Offline tests for UsCensusProvider.GetAreaValuesAsync — the map layer's bulk fetch.
//
// Responses are synthetic array-of-arrays fixtures in the Census API's documented shape,
// served through StubHttpHandler (no key, no network). They validate URL assembly
// (for=/in= clauses), GEOID reconstruction with leading zeros, jam-value suppression,
// and name trimming — the provider's own logic, not the live API's behavior.

using System.Text.Json;
using CensusScope.Core.Http;
using CensusScope.Core.Providers;
using CensusScope.Core.Services;

namespace CensusScope.Core.Tests;

public class UsCensusProviderAreaValuesTests
{
    private const string TestKey = "test-key-0123456789";

    /// <summary>Builds a Census array-of-arrays payload from a header list plus data rows.</summary>
    private static string Table(string[] header, params string?[][] rows)
    {
        var table = new List<string?[]> { header };
        table.AddRange(rows);
        return JsonSerializer.Serialize(table);
    }

    /// <summary>A cache directory unique to one test, so the on-disk cache can never mask a request.</summary>
    private static string TempCacheDir() =>
        Path.Combine(Path.GetTempPath(), "gw-test-" + Guid.NewGuid().ToString("N"));

    private static (UsCensusProvider Provider, StubHttpHandler Stub) NewProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var stub = new StubHttpHandler(responder);
        var provider = new UsCensusProvider(
            new ApiClient(TempCacheDir(), stub, TimeSpan.FromMilliseconds(1)),
            new AppSettings { CensusApiKey = TestKey },
            CatalogService.Load("us"));
        return (provider, stub);
    }

    // ---- 1. counties under a state -----------------------------------------------------------

    [Fact]
    public async Task Counties_ScopesRequestToParentState_RebuildsGeoIdsAndSuppressesJamValues()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json(Table(
            ["NAME", "B01001_026E", "B01001_001E", "state", "county"],
            ["Alameda County, California", "840000", "1650000", "06", "001"],
            ["Los Angeles County, California", "-666666666", "9800000", "06", "037"],
            ["Alpine County, California", "600", null, "06", "003"])));

        var areas = await provider.GetAreaValuesAsync(
            "county", "06", ["B01001_026E", "B01001_001E"]);

        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("for=county:*", url, StringComparison.Ordinal);
        Assert.Contains("in=state:06", url, StringComparison.Ordinal);
        Assert.Contains("get=NAME,B01001_026E,B01001_001E", url, StringComparison.Ordinal);

        Assert.Equal(3, areas.Count);
        var byId = areas.ToDictionary(a => a.GeoId);

        // GEOID = state + county, leading zeros intact.
        Assert.Contains("06001", byId.Keys);
        Assert.Contains("06037", byId.Keys);
        Assert.Contains("06003", byId.Keys);

        // Names lose the trailing ", {state}".
        Assert.Equal("Alameda County", byId["06001"].Name);

        // Ordinary values pass through; every requested code is present per area.
        Assert.Equal(840000, byId["06001"].Values["B01001_026E"]);
        Assert.Equal(1650000, byId["06001"].Values["B01001_001E"]);

        // ACS jam value -666666666 becomes null, never a giant negative number.
        Assert.Null(byId["06037"].Values["B01001_026E"]);
        Assert.Equal(9800000, byId["06037"].Values["B01001_001E"]);

        // A null cell also becomes null.
        Assert.Null(byId["06003"].Values["B01001_001E"]);
        Assert.Equal(600, byId["06003"].Values["B01001_026E"]);
    }

    // ---- 2. states nationwide ----------------------------------------------------------------

    [Fact]
    public async Task States_NoParent_UsesForStateWildcardWithoutInClause()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json(Table(
            ["NAME", "B01003_001E", "state"],
            ["California", "39242785", "06"],
            ["Alabama", "5074296", "01"])));

        var areas = await provider.GetAreaValuesAsync("state", null, ["B01003_001E"]);

        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("for=state:*", url, StringComparison.Ordinal);
        Assert.DoesNotContain("in=state", url, StringComparison.Ordinal);
        Assert.DoesNotContain("&in=", url, StringComparison.Ordinal);

        // State ids are the bare 2-digit FIPS with the leading zero kept.
        Assert.Equal(2, areas.Count);
        var byId = areas.ToDictionary(a => a.GeoId);
        Assert.Equal(39242785, byId["06"].Values["B01003_001E"]);
        Assert.Equal(5074296, byId["01"].Values["B01003_001E"]);
    }

    // ---- 3. guard clauses --------------------------------------------------------------------

    [Fact]
    public async Task UnknownLevel_ThrowsWithoutRequesting()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json("[]"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetAreaValuesAsync("tract", "06", ["B01003_001E"]));

        Assert.Contains("tract", ex.Message, StringComparison.Ordinal);
        Assert.Empty(stub.RequestedUrls);
    }

    [Fact]
    public async Task NoVariableCodes_ThrowsWithoutRequesting()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json("[]"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetAreaValuesAsync("state", null, []));

        Assert.Empty(stub.RequestedUrls);
    }
}
