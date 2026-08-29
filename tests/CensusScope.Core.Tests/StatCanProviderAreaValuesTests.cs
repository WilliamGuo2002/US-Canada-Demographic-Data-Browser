// Offline tests for StatCanProvider.GetAreaValuesAsync — the map layer's bulk fetch.
//
// The stub answers two kinds of URLs: the SDMX data endpoint (long-format CSV whose header
// copies the real service's column layout, see Fixtures/toronto_tier1.csv) and the CL_GEO_*
// codelist endpoint (SDMX-ML; the PR list is the real fixture, the CD list is synthesized).
// The tests validate the empty-REF_AREA key, the Canada-row exclusion, STATISTIC preference,
// DGUID prefix filtering under a parent, and null handling for missing/suppressed values.

using CensusScope.Core.Http;
using CensusScope.Core.Providers;
using CensusScope.Core.Services;

namespace CensusScope.Core.Tests;

public class StatCanProviderAreaValuesTests
{
    // DGUIDs used throughout (PR ones exist in Fixtures/CL_GEO_PR.xml).
    private const string CanadaDguid = "2021A000011124";
    private const string OntarioDguid = "2021A000235";
    private const string QuebecDguid = "2021A000224";
    private const string TorontoCdDguid = "2021A00033520";   // CD in Ontario (code 3520)
    private const string MontrealCdDguid = "2021A00032466";  // CD in Quebec  (code 2466)

    /// <summary>Header copied from the real SDMX CSV shape (see Fixtures/toronto_tier1.csv).</summary>
    private const string CsvHeader =
        "DATAFLOW,FREQ,TIME_PERIOD,REF_AREA,GENDER,CHARACTERISTIC,STATISTIC,OBS_VALUE,DECIMALS," +
        "FLAG,TOPIC,NOTE,RELEASE_DATE,GEO_LEVEL,ALT_GEO_CODE,GEO_DESC,PROV_TERR,DATA_QUALITY_FLAG," +
        "TNR_LF,TNR_SF,CI_LOW,CI_HIGH";

    /// <summary>One observation row in the same column order as <see cref="CsvHeader"/>.</summary>
    private static string Row(string flow, string refArea, string characteristic, string statistic, string obsValue) =>
        $"STC_CP:{flow}(1.3),A5,2021,{refArea},1,{characteristic},{statistic},{obsValue},0,,1,,2022-02-09,1,,{refArea},,00000,4.7,3.7,,";

    private static string Csv(params string[] rows) => CsvHeader + "\n" + string.Join("\n", rows) + "\n";

    /// <summary>Minimal synthesized CL_GEO_CD codelist covering the two CD DGUIDs above.</summary>
    private const string CdCodelistXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Structure>
          <Structures>
            <Codelists>
              <Codelist id="CL_GEO_CD">
                <Code id="2021A00033520">
                  <Name xml:lang="en">Toronto</Name>
                  <Description xml:lang="en">Toronto [Census division], Ontario</Description>
                </Code>
                <Code id="2021A00032466">
                  <Name xml:lang="en">Montreal</Name>
                  <Description xml:lang="en">Montreal [Census division], Quebec</Description>
                </Code>
              </Codelist>
            </Codelists>
          </Structures>
        </Structure>
        """;

    private static string PrCodelistXml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CL_GEO_PR.xml"));

    private static string TempCacheDir() =>
        Path.Combine(Path.GetTempPath(), "gw-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>Provider whose stub serves the codelists plus a caller-supplied CSV per data request.</summary>
    private static (StatCanProvider Provider, StubHttpHandler Stub) NewProvider(Func<string, string> csvForDataUrl)
    {
        var stub = new StubHttpHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/codelist/STC_CP/CL_GEO_PR/", StringComparison.Ordinal))
                return StubHttpHandler.Json(PrCodelistXml);
            if (url.Contains("/codelist/STC_CP/CL_GEO_CD/", StringComparison.Ordinal))
                return StubHttpHandler.Json(CdCodelistXml);
            if (url.Contains("/data/STC_CP,", StringComparison.Ordinal))
                return StubHttpHandler.Json(csvForDataUrl(url));
            throw new InvalidOperationException("Unexpected URL requested: " + url);
        });
        var provider = new StatCanProvider(
            new ApiClient(TempCacheDir(), stub, TimeSpan.FromMilliseconds(1)),
            CatalogService.Load("ca"));
        return (provider, stub);
    }

    // ---- 1. provinces: empty REF_AREA key, Canada exclusion, STATISTIC preference ------------

    [Fact]
    public async Task Provinces_EmptyRefAreaKey_ExcludesCanadaRow_PrefersCountsOverRates()
    {
        var (provider, stub) = NewProvider(_ => Csv(
            // The whole-country row must never become a mappable area.
            Row("DF_PR", CanadaDguid, "1", "1", "36991981"),
            // Ontario: the Rates row (STATISTIC 4) arrives FIRST, then Counts (1) — 1 must win.
            Row("DF_PR", OntarioDguid, "1", "4", "100.0"),
            Row("DF_PR", OntarioDguid, "1", "1", "14223942"),
            Row("DF_PR", OntarioDguid, "6", "1", "15.9"),
            // Quebec: Counts first, Rates second — 1 must be kept.
            Row("DF_PR", QuebecDguid, "1", "1", "8501833"),
            Row("DF_PR", QuebecDguid, "1", "4", "99.9"),
            // Quebec's "6" observation is suppressed (empty OBS_VALUE) -> null.
            Row("DF_PR", QuebecDguid, "6", "1", "")));

        var areas = await provider.GetAreaValuesAsync("PR", null, ["1", "6"]);

        // The data request left the REF_AREA key position empty ("/A5..{gender}..." pattern).
        var dataUrl = Assert.Single(stub.RequestedUrls, u => u.Contains("/data/", StringComparison.Ordinal));
        Assert.Contains("/data/STC_CP,DF_PR/A5..1.1+6.?format=csv", dataUrl, StringComparison.Ordinal);
        // The name lookup went to the PR codelist.
        Assert.Contains(stub.RequestedUrls,
            u => u.Contains("/codelist/STC_CP/CL_GEO_PR/latest", StringComparison.Ordinal));

        // Canada is excluded; the two provinces come back sorted by name with codelist names.
        Assert.Equal(2, areas.Count);
        Assert.Equal(["Ontario", "Quebec"], areas.Select(a => a.Name));
        Assert.Equal([OntarioDguid, QuebecDguid], areas.Select(a => a.GeoId));

        // STATISTIC 1 (Counts) beat STATISTIC 4 (Rates) regardless of row order.
        var ontario = areas[0];
        Assert.Equal(14223942, ontario.Values["1"]);
        Assert.Equal(15.9, ontario.Values["6"]);

        var quebec = areas[1];
        Assert.Equal(8501833, quebec.Values["1"]);
        Assert.Null(quebec.Values["6"]); // suppressed (empty OBS_VALUE)
    }

    // ---- 2. census divisions under a province: DGUID prefix filtering ------------------------

    [Fact]
    public async Task CdsUnderProvince_KeepsOnlyChildrenByDguidPrefix()
    {
        var (provider, stub) = NewProvider(_ => Csv(
            Row("DF_CD", CanadaDguid, "1", "1", "36991981"),
            Row("DF_CD", TorontoCdDguid, "1", "1", "2794356"),    // Ontario child (35 prefix)
            Row("DF_CD", MontrealCdDguid, "1", "1", "2004265"))); // Quebec child -> filtered out

        var areas = await provider.GetAreaValuesAsync("CD", OntarioDguid, ["1"]);

        // CD dataflow, empty REF_AREA segment, CD codelist for names.
        var dataUrl = Assert.Single(stub.RequestedUrls, u => u.Contains("/data/", StringComparison.Ordinal));
        Assert.Contains("/data/STC_CP,DF_CD/A5..1.1.?format=csv", dataUrl, StringComparison.Ordinal);
        Assert.Contains(stub.RequestedUrls,
            u => u.Contains("/codelist/STC_CP/CL_GEO_CD/latest", StringComparison.Ordinal));

        // Only the Ontario CD survives the parent prefix filter; its name comes from the
        // codelist description with the " [type], province" tail stripped.
        var toronto = Assert.Single(areas);
        Assert.Equal(TorontoCdDguid, toronto.GeoId);
        Assert.Equal("Toronto", toronto.Name);
        Assert.Equal(2794356, toronto.Values["1"]);
    }

    // ---- 3. a variable with no observation at all --------------------------------------------

    [Fact]
    public async Task VariableWithNoObservation_IsNullRatherThanMissing()
    {
        var (provider, _) = NewProvider(_ => Csv(
            Row("DF_PR", OntarioDguid, "1", "1", "14223942")));
        // Variable "6" never appears in the response.

        var areas = await provider.GetAreaValuesAsync("PR", null, ["1", "6"]);

        var ontario = Assert.Single(areas);
        Assert.True(ontario.Values.ContainsKey("6"), "every requested code must be present per area");
        Assert.Null(ontario.Values["6"]);
        Assert.Equal(14223942, ontario.Values["1"]);
    }

    // ---- 4. guard clause ---------------------------------------------------------------------

    [Fact]
    public async Task NoVariableCodes_ThrowsWithoutRequesting()
    {
        var (provider, stub) = NewProvider(_ => Csv());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetAreaValuesAsync("PR", null, []));

        Assert.Empty(stub.RequestedUrls);
    }
}
