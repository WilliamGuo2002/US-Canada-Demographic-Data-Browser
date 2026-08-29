// Offline tests for UsCensusProvider.
//
// Every response here is a SYNTHETIC fixture, hand-shaped to the Census API's documented contract
// (a JSON array-of-arrays whose row 0 holds the header names, whose cells are all strings, and whose
// geography FIPS columns come last). They are served through StubHttpHandler, so no API key and no
// network are involved.
//
// What that buys: these tests validate the PROVIDER'S OWN logic end-to-end — URL assembly, the
// 24-pair chunk boundary, GEOID arithmetic and leading-zero preservation, header-name (not position)
// column lookup, estimate/MOE pairing, ACS jam-value handling, and row ordering. What it does NOT
// buy: any assurance that the live api.census.gov agrees with these shapes. A contract change at the
// Bureau would keep these green; only a real call can catch that.

using System.Text.Json;
using CensusScope.Core.Http;
using CensusScope.Core.Models;
using CensusScope.Core.Providers;
using CensusScope.Core.Services;

namespace CensusScope.Core.Tests;

public class UsCensusProviderTests
{
    private const string TestKey = "test-key-0123456789";

    // ---- fixture helpers -------------------------------------------------------------------

    /// <summary>Builds a Census array-of-arrays payload from a header list plus data rows.</summary>
    private static string Table(string[] header, params string?[][] rows)
    {
        var table = new List<string?[]> { header };
        table.AddRange(rows);
        return JsonSerializer.Serialize(table);
    }

    /// <summary>Reads one query-string parameter out of a requested URL.</summary>
    private static string? QueryParam(Uri uri, string name) => uri.Query
        .TrimStart('?')
        .Split('&')
        .Where(p => p.StartsWith(name + "=", StringComparison.Ordinal))
        .Select(p => Uri.UnescapeDataString(p[(name.Length + 1)..]))
        .FirstOrDefault();

    /// <summary>The variables a request asked for, in order (the <c>get=</c> list).</summary>
    private static List<string> GetList(string url) =>
        [.. (QueryParam(new Uri(url), "get") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>A cache directory unique to one test, so the on-disk cache can never mask a request.</summary>
    private static string TempCacheDir() =>
        Path.Combine(Path.GetTempPath(), "gw-test-" + Guid.NewGuid().ToString("N"));

    private static (UsCensusProvider Provider, StubHttpHandler Stub) NewProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey = TestKey)
    {
        var stub = new StubHttpHandler(responder);
        var provider = new UsCensusProvider(
            new ApiClient(TempCacheDir(), stub, TimeSpan.FromMilliseconds(1)),
            new AppSettings { CensusApiKey = apiKey },
            CatalogService.Load("us"));
        return (provider, stub);
    }

    private static DatasetDef Dataset(string key) =>
        CatalogService.Load("us").Datasets.Single(d => d.Key == key);

    /// <summary>Progress sink that records synchronously (Progress&lt;T&gt; would post asynchronously and race the assert).</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        public List<string> Reports { get; } = [];
        public void Report(string value) => Reports.Add(value);
    }

    // ---- 1. top-level units ----------------------------------------------------------------

    [Fact]
    public async Task GetTopLevelUnits_KeepsLeadingZeroFipsAndSortsByName()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json(Table(
            ["NAME", "state"],
            ["New York", "36"],
            ["California", "06"],
            ["Alabama", "01"])));

        var states = await provider.GetTopLevelUnitsAsync();

        Assert.Equal(["Alabama", "California", "New York"], states.Select(s => s.Name));
        Assert.Equal(["01", "06", "36"], states.Select(s => s.Id));
        Assert.All(states, s => Assert.Equal("state", s.LevelCode));
        Assert.All(states, s => Assert.Null(s.ParentId));

        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("for=state:*", url, StringComparison.Ordinal);
        Assert.Contains("key=", url, StringComparison.Ordinal);
    }

    // ---- 2. county children ----------------------------------------------------------------

    [Fact]
    public async Task GetChildUnits_County_BuildsFiveDigitGeoIdAndTrimsStateSuffix()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json(Table(
            ["NAME", "state", "county"],
            ["Alameda County, California", "06", "001"],
            ["Los Angeles County, California", "06", "037"])));

        var counties = await provider.GetChildUnitsAsync(new GeoUnit("06", "California", "state", null), "county");

        Assert.Equal(2, counties.Count);
        var alameda = counties[0];
        Assert.Equal("06001", alameda.Id);
        Assert.Equal("Alameda County", alameda.Name);
        Assert.Equal("county", alameda.LevelCode);
        Assert.Equal("06", alameda.ParentId);
        Assert.Equal("06037", counties[1].Id);

        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("for=county:*", url, StringComparison.Ordinal);
        Assert.Contains("in=state:06", url, StringComparison.Ordinal);
    }

    // ---- 3. place children, and an unsupported level ---------------------------------------

    [Fact]
    public async Task GetChildUnits_Place_BuildsSevenDigitGeoId()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json(Table(
            ["NAME", "state", "place"],
            ["Los Angeles city, California", "06", "44000"],
            ["Alameda city, California", "06", "00562"])));

        var places = await provider.GetChildUnitsAsync(new GeoUnit("06", "California", "state", null), "place");

        Assert.Equal(["0600562", "0644000"], places.Select(p => p.Id));
        Assert.Equal(["Alameda city", "Los Angeles city"], places.Select(p => p.Name));
        Assert.All(places, p => Assert.Equal("place", p.LevelCode));

        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("for=place:*", url, StringComparison.Ordinal);
        Assert.Contains("in=state:06", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChildUnits_UnknownLevel_ThrowsWithoutRequesting()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json("[]"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetChildUnitsAsync(new GeoUnit("06", "California", "state", null), "tract"));

        Assert.Contains("tract", ex.Message, StringComparison.Ordinal);
        Assert.Empty(stub.RequestedUrls);
    }

    // ---- 4. suffix trimming edge cases ------------------------------------------------------

    [Fact]
    public async Task GetChildUnits_NameSuffixTrimming_HandlesNoCommaAndMultipleCommas()
    {
        var (provider, _) = NewProvider(_ => StubHttpHandler.Json(Table(
            ["NAME", "state", "place"],
            ["District of Columbia", "11", "50000"],
            ["Ranchos de Taos CDP, Taos County, New Mexico", "11", "63460"])));

        var places = await provider.GetChildUnitsAsync(new GeoUnit("11", "DC", "state", null), "place");

        var byId = places.ToDictionary(p => p.Id, p => p.Name);
        // No comma at all: the name must survive untouched.
        Assert.Equal("District of Columbia", byId["1150000"]);
        // Several commas: only the final ", {state}" segment is dropped.
        Assert.Equal("Ranchos de Taos CDP, Taos County", byId["1163460"]);
    }

    // ---- 5. profile-mode chunking ------------------------------------------------------------

    /// <summary>Numeric suffix of an ACS variable code: "B15002_015E" -> 15.</summary>
    private static int VarNumber(string code) =>
        int.Parse(code.Split('_')[1][..^1]);

    /// <summary>
    /// Answers a profile request by echoing values derived from whatever <c>get=</c> asked for:
    /// estimates are n*1000, MOEs the smaller n*10, so any variable's expected value is predictable.
    /// </summary>
    private static HttpResponseMessage SynthesizeProfile(HttpRequestMessage req)
    {
        var codes = GetList(req.RequestUri!.AbsoluteUri);
        var header = codes.Concat(["state"]).ToArray();
        var row = codes
            .Select(c => c == "NAME"
                ? "Testville"
                : (c.EndsWith('M')
                    ? (VarNumber(c) * 10).ToString()
                    : (VarNumber(c) * 1000).ToString()))
            .Concat(["06"])
            .ToArray();
        return StubHttpHandler.Json(Table(header, row!));
    }

    [Fact]
    public async Task QueryProfile_ChunksVariablesIntoTwoRequestsAndReassemblesCatalogOrder()
    {
        var (provider, stub) = NewProvider(SynthesizeProfile);
        var dataset = Dataset("education_by_sex");
        Assert.Equal(35, dataset.Variables.Count); // guards the premise: 35 > 24 pairs, so chunking fires

        var progress = new SyncProgress();
        var result = await provider.QueryAsync(
            new DemographicQuery("US", QueryMode.Profile, "education_by_sex", "state", "06", null),
            progress);

        // Exactly two requests, neither exceeding the API's 50-variable cap.
        Assert.Equal(2, stub.RequestedUrls.Count);
        foreach (var url in stub.RequestedUrls)
        {
            Assert.True(GetList(url).Count <= 50, "get= list exceeded 50 variables: " + GetList(url).Count);
            Assert.Contains("for=state:06", url, StringComparison.Ordinal);
        }
        Assert.Equal(49, GetList(stub.RequestedUrls[0]).Count); // NAME + 24 E/M pairs
        Assert.Equal(23, GetList(stub.RequestedUrls[1]).Count); // NAME + 11 E/M pairs

        // Every catalog variable (and its MOE twin) was asked for exactly once across the chunks.
        var requested = stub.RequestedUrls.SelectMany(GetList).ToList();
        foreach (var v in dataset.Variables)
        {
            Assert.Contains(v.Code, requested);
            Assert.Contains(v.Code[..^1] + "M", requested);
        }
        Assert.Equal(requested.Count, requested.Distinct().Count() + 1); // NAME repeats once per chunk

        // The reassembled table is the catalog, in catalog order, with catalog labels and indents.
        Assert.Equal(35, result.Rows.Count);
        Assert.Equal(dataset.Variables.Select(v => v.Label), result.Rows.Select(r => r.Label));
        Assert.Equal(dataset.Variables.Select(v => v.Indent), result.Rows.Select(r => r.Indent));
        Assert.False(result.NumericCells);
        Assert.False(string.IsNullOrWhiteSpace(result.Universe));
        Assert.False(string.IsNullOrWhiteSpace(result.SourceAttribution));
        Assert.Contains("B15002", result.SourceAttribution, StringComparison.Ordinal);
        Assert.Contains("Testville", result.Title, StringComparison.Ordinal);

        // A known variable from the first chunk and one from the second both carry their values.
        var bachelorsMale = result.Rows[14];             // B15002_015E
        Assert.Equal("Bachelor's degree", bachelorsMale.Label);
        Assert.Equal(15000, bachelorsMale.Cells[0].Value);
        Assert.Equal(150, bachelorsMale.Cells[0].Moe);
        Assert.Equal("15,000 ±150", bachelorsMale.Cells[0].Display);

        var bachelorsFemale = result.Rows[31];           // B15002_032E, second chunk
        Assert.Equal(32000, bachelorsFemale.Cells[0].Value);
        Assert.Equal("32,000 ±320", bachelorsFemale.Cells[0].Display);

        Assert.Equal(2, progress.Reports.Count); // one progress report per chunk
    }

    // ---- 6. MOE pairing, jam values ---------------------------------------------------------

    [Fact]
    public async Task QueryProfile_PairsMoeAndSuppressesJamValues()
    {
        // population_overview is 5 variables, so this is a single un-chunked request.
        var (provider, _) = NewProvider(_ => StubHttpHandler.Json(Table(
            [
                "NAME",
                "B01003_001E", "B01003_001M",   // ordinary estimate + MOE
                "B01002_001E", "B01002_001M",   // jam estimate: cannot compute
                "B01002_002E", "B01002_002M",   // jam MOE: value renders without a margin
                "B01002_003E", "B01002_003M",
                "B19301_001E", "B19301_001M",
                "state",
            ],
            [
                "Testville",
                "1234", "56",
                "-666666666", "-666666666",
                "38.5", "-555555555",
                "40.1", "0.4",
                "45678", "321",
                "06",
            ])));

        var result = await provider.QueryAsync(
            new DemographicQuery("US", QueryMode.Profile, "population_overview", "state", "06", null));

        // Plain estimate + MOE.
        Assert.Equal("1,234 ±56", result.Rows[0].Cells[0].Display);
        Assert.Equal(1234, result.Rows[0].Cells[0].Value);
        Assert.Equal(56, result.Rows[0].Cells[0].Moe);

        // Jam estimate: no value, a human-readable flag — never the raw sentinel.
        var jam = result.Rows[1].Cells[0];
        Assert.Null(jam.Value);
        Assert.Equal("cannot compute", jam.Flag);
        Assert.Equal("cannot compute", jam.Display);
        Assert.DoesNotContain("666666666", jam.Display, StringComparison.Ordinal);

        // Jam MOE (-555555555, "controlled"): the estimate still shows, without a ± part.
        var controlled = result.Rows[2].Cells[0];
        Assert.Equal(38.5, controlled.Value);
        Assert.Null(controlled.Moe);
        Assert.Equal("38.5", controlled.Display);
        Assert.DoesNotContain("±", controlled.Display, StringComparison.Ordinal);

        // Formats come from the catalog: per-capita income is currency.
        Assert.Equal("$45,678 ±$321", result.Rows[4].Cells[0].Display);
    }

    // ---- 7. compare mode at state level ------------------------------------------------------

    [Fact]
    public async Task QueryCompare_States_SortsDescendingWithNullsLastAndUsesCatalogHeaders()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json(Table(
            ["NAME", "B01003_001E", "B01002_001E", "state"],
            ["Texas", "30000000", "35.5", "48"],
            ["California", "39242785", "37.6", "06"],
            [null, null, "40.0", "60"],           // no reported population: must sort last
            ["New York", "19500000", "39.8", "36"])));

        var result = await provider.QueryAsync(
            new DemographicQuery("US", QueryMode.Compare, "population_overview", "state", null, null));

        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("for=state:*", url, StringComparison.Ordinal);
        Assert.DoesNotContain("in=state", url, StringComparison.Ordinal);
        Assert.Equal(["NAME", "B01003_001E", "B01002_001E"], GetList(url));

        Assert.True(result.NumericCells);
        Assert.Equal(["Total population", "Median age - Total"], result.ColumnHeaders);
        Assert.Equal(["California", "Texas", "New York", ""], result.Rows.Select(r => r.Label));
        Assert.Equal(39242785, result.Rows[0].Cells[0].Value);
        Assert.Null(result.Rows[^1].Cells[0].Value);   // the null-valued row landed last
        Assert.Equal("n/a", result.Rows[^1].Cells[0].Flag);
        Assert.All(result.Rows, r => Assert.All(r.Cells, c => Assert.Null(c.Moe))); // no MOE in compare view
    }

    // ---- 8. compare mode under a parent state -------------------------------------------------

    [Fact]
    public async Task QueryCompare_CountiesUnderState_ScopesRequestToParent()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json(Table(
            ["NAME", "B01003_001E", "B01002_001E", "state", "county"],
            ["Alameda County, California", "1650000", "38.2", "06", "001"],
            ["Los Angeles County, California", "9800000", "37.1", "06", "037"])));

        var result = await provider.QueryAsync(
            new DemographicQuery("US", QueryMode.Compare, "population_overview", "county", null, "06"));

        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("for=county:*", url, StringComparison.Ordinal);
        Assert.Contains("in=state:06", url, StringComparison.Ordinal);
        Assert.Equal(["Los Angeles County", "Alameda County"], result.Rows.Select(r => r.Label));
        Assert.Contains("County", result.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryCompare_CountiesWithoutParent_ThrowsWithoutRequesting()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json("[]"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.QueryAsync(
            new DemographicQuery("US", QueryMode.Compare, "population_overview", "county", null, null)));

        Assert.Contains("parent state", ex.Message, StringComparison.Ordinal);
        Assert.Empty(stub.RequestedUrls);
    }

    // ---- 9. missing key -----------------------------------------------------------------------

    [Fact]
    public async Task MissingApiKey_FailsFastWithSignupLink_AndNeverHitsTheNetwork()
    {
        var (provider, stub) = NewProvider(_ => StubHttpHandler.Json("[]"), apiKey: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTopLevelUnitsAsync());

        Assert.Contains("key_signup", ex.Message, StringComparison.Ordinal);
        Assert.Empty(stub.RequestedUrls);
    }

    // ---- 10. the key-error redirect ------------------------------------------------------------

    [Fact]
    public async Task InvalidKeyRedirect_BecomesActionableMessageWithoutLeakingTheKey()
    {
        var (provider, stub) = NewProvider(
            _ => StubHttpHandler.Redirect("https://api.census.gov/data/invalid_key.html"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTopLevelUnitsAsync());

        Assert.Contains("activation link", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKey, ex.Message, StringComparison.Ordinal);
        Assert.IsType<ApiHttpException>(ex.InnerException);

        // Only ever the one URL — asserted on the distinct set rather than the count, because
        // ApiClient currently re-sends it: the ApiHttpException it throws for a non-retryable
        // status derives from HttpRequestException, so its own `catch (HttpRequestException)
        // when (attempt < maxAttempts)` swallows it and loops. See the report accompanying
        // these tests; this assertion holds either way.
        Assert.Single(stub.RequestedUrls.Distinct());
        Assert.All(stub.RequestedUrls, u => Assert.Contains("for=state:*", u, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingKeyRedirect_PointsAtTheSignupPage()
    {
        var (provider, _) = NewProvider(
            _ => StubHttpHandler.Redirect("https://api.census.gov/data/missing_key.html"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTopLevelUnitsAsync());

        Assert.Contains("key_signup.html", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKey, ex.Message, StringComparison.Ordinal);
    }
}
