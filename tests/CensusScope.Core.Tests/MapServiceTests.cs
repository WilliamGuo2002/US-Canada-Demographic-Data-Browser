// Offline tests for MapService, the choropleth builder.
//
// The provider is a tiny hand-written fake returning canned AreaValue lists, so every test
// exercises MapService's own logic in isolation: indicator derivation (percent / direct /
// density with their null-honesty rules), quantile classification (breaks, tie dedup, class
// counts), and legend label formatting.

using CensusScope.Core.Models;
using CensusScope.Core.Providers;
using CensusScope.Core.Services;

namespace CensusScope.Core.Tests;

public class MapServiceTests
{
    // ---- fake provider ----------------------------------------------------------------------

    /// <summary>Canned-data IDemographicProvider: returns a fixed AreaValue list and records the request.</summary>
    private sealed class FakeProvider(IReadOnlyList<AreaValue> areaValues) : IDemographicProvider
    {
        public string CountryCode => "XX";

        public CountryCatalog Catalog { get; } = new()
        {
            CountryCode = "XX",
            CountryName = "Testland",
            SourceName = "Test Statistical Office",
        };

        public string? RequestedLevel { get; private set; }
        public string? RequestedParentId { get; private set; }
        public IReadOnlyList<string>? RequestedCodes { get; private set; }

        public Task<IReadOnlyList<AreaValue>> GetAreaValuesAsync(
            string levelCode, string? parentId, IReadOnlyList<string> variableCodes,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            RequestedLevel = levelCode;
            RequestedParentId = parentId;
            RequestedCodes = variableCodes;
            return Task.FromResult(areaValues);
        }

        public Task<IReadOnlyList<GeoUnit>> GetTopLevelUnitsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GeoUnit>> GetChildUnitsAsync(GeoUnit ancestor, string targetLevelCode, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<QueryResult> QueryAsync(DemographicQuery query, IProgress<string>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // ---- fixture helpers --------------------------------------------------------------------

    private static readonly DatasetDef TestDataset = new()
    {
        Key = "test_dataset",
        DisplayName = "Test dataset",
        SourceTable = "T0001",
        Universe = "Test universe",
    };

    private static AreaValue Area(string id, params (string Code, double? Value)[] values) =>
        new(id, "Area " + id, values.ToDictionary(v => v.Code, v => v.Value, StringComparer.Ordinal));

    /// <summary>Direct-kind areas over a single variable "V", one area per value (null = no data).</summary>
    private static List<AreaValue> DirectAreas(params double?[] values) =>
        values.Select((v, i) => Area("A" + i, ("V", v))).ToList();

    private static MapVariableDef DirectVar(string format = "decimal") => new()
    {
        Key = "direct_v",
        Label = "Direct value",
        Numerator = ["V"],
        Kind = "direct",
        Format = format,
    };

    private static Task<MapData> BuildAsync(
        IReadOnlyList<AreaValue> areas, MapVariableDef mapVar,
        IReadOnlyDictionary<string, double?>? landKm2ById = null)
    {
        var provider = new FakeProvider(areas);
        return new MapService().BuildAsync(provider, TestDataset, mapVar, "level1", null, landKm2ById);
    }

    // ---- 1. percent: sum of numerators over denominator, with honesty rules ------------------

    [Fact]
    public async Task Percent_SumsNumeratorsOverDenominator_AndSuppressesDishonestValues()
    {
        var mapVar = new MapVariableDef
        {
            Key = "pct_test",
            Label = "Share (%)",
            Numerator = ["N1", "N2"],
            Denominator = "D",
            Kind = "percent",
            Format = "percent",
        };
        var areas = new List<AreaValue>
        {
            Area("A", ("N1", 30), ("N2", 20), ("D", 200)),      // (30+20)/200*100 = 25
            Area("B", ("N1", null), ("N2", 20), ("D", 200)),    // one numerator missing -> null
            Area("C", ("N1", 30), ("N2", 20), ("D", null)),     // denominator missing -> null
            Area("D", ("N1", 30), ("N2", 20), ("D", 99)),       // denominator below 100 -> suppressed
            Area("E", ("N1", 25), ("N2", 25), ("D", 100)),      // denominator exactly 100 -> allowed
        };

        var provider = new FakeProvider(areas);
        var data = await new MapService().BuildAsync(provider, TestDataset, mapVar, "level1", "P1", null);

        // The provider was asked for numerators plus denominator in one request, at the right scope.
        Assert.Equal(["N1", "N2", "D"], provider.RequestedCodes);
        Assert.Equal("level1", provider.RequestedLevel);
        Assert.Equal("P1", provider.RequestedParentId);

        Assert.Equal(5, data.Areas.Count);
        var byId = data.Areas.ToDictionary(a => a.Id);
        Assert.Equal(25, byId["A"].Value);
        Assert.Equal("25%", byId["A"].Display);
        Assert.Null(byId["B"].Value);
        Assert.Null(byId["C"].Value);
        Assert.Null(byId["D"].Value);
        // The app's own small-denominator floor is disclosed in the display, so it can
        // never be mistaken for source-suppressed data.
        Assert.Equal("— (base under 100)", byId["D"].Display);
        Assert.Equal(50, byId["E"].Value);

        Assert.Equal(3, data.NoDataCount);
        Assert.Equal("Share (%)", data.VariableLabel);
        Assert.Equal("Share (%) — Test dataset", data.Title);
        Assert.Equal("Test universe", data.Universe);
        Assert.Equal("Test Statistical Office, T0001", data.Source);
        Assert.Equal("percent", data.Format);
    }

    [Fact]
    public async Task Percent_DenominatorAlreadyAmongNumerators_IsNotRequestedTwice()
    {
        var mapVar = new MapVariableDef
        {
            Key = "pct_self",
            Label = "Self share (%)",
            Numerator = ["D"],
            Denominator = "D",
            Kind = "percent",
            Format = "percent",
        };
        var provider = new FakeProvider([Area("A", ("D", 500))]);

        var data = await new MapService().BuildAsync(provider, TestDataset, mapVar, "level1", null, null);

        Assert.Equal(["D"], provider.RequestedCodes);
        Assert.Equal(100, data.Areas[0].Value);
    }

    // ---- 2. guard clauses --------------------------------------------------------------------

    [Fact]
    public async Task Percent_WithoutDenominator_Throws()
    {
        var mapVar = new MapVariableDef
        {
            Key = "bad_pct",
            Label = "Bad",
            Numerator = ["N1"],
            Denominator = null,
            Kind = "percent",
            Format = "percent",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync([], mapVar));
        Assert.Contains("bad_pct", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyNumerator_Throws()
    {
        var mapVar = new MapVariableDef { Key = "no_num", Label = "Bad", Numerator = [], Kind = "direct" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync([], mapVar));
        Assert.Contains("no_num", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownKind_Throws()
    {
        var mapVar = new MapVariableDef { Key = "weird", Label = "Bad", Numerator = ["V"], Kind = "ratio" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildAsync([Area("A", ("V", 1))], mapVar));
        Assert.Contains("ratio", ex.Message, StringComparison.Ordinal);
    }

    // ---- 3. direct passthrough ---------------------------------------------------------------

    [Fact]
    public async Task Direct_PassesValueThrough_IncludingNull()
    {
        var data = await BuildAsync(DirectAreas(42.5, null, 7), DirectVar());

        Assert.Equal([42.5, null, 7], data.Areas.Select(a => a.Value));
        Assert.Equal(1, data.NoDataCount);
        Assert.Equal("42.5", data.Areas[0].Display);
        Assert.Equal("—", data.Areas[1].Display);
    }

    // ---- 4. density --------------------------------------------------------------------------

    [Fact]
    public async Task Density_DividesByLandArea_NullWhenLandMissingOrNonPositive()
    {
        var mapVar = new MapVariableDef
        {
            Key = "density",
            Label = "Density (per km²)",
            Numerator = ["POP"],
            Kind = "density",
            Format = "decimal",
        };
        var areas = new List<AreaValue>
        {
            Area("A", ("POP", 1000)),   // land 100 -> 10
            Area("B", ("POP", 1000)),   // absent from the lookup -> null
            Area("C", ("POP", 1000)),   // null land -> null
            Area("D", ("POP", 1000)),   // zero land -> null (no division by zero)
            Area("E", ("POP", null)),   // no population -> null
        };
        var land = new Dictionary<string, double?>
        {
            ["A"] = 100,
            ["C"] = null,
            ["D"] = 0,
            ["E"] = 100,
        };

        var data = await BuildAsync(areas, mapVar, land);

        Assert.Equal([10, null, null, null, null], data.Areas.Select(a => a.Value));
        Assert.Equal(4, data.NoDataCount);
    }

    [Fact]
    public async Task Density_NullLookupDictionary_YieldsAllNullWithoutCrashing()
    {
        var mapVar = new MapVariableDef
        {
            Key = "density",
            Label = "Density",
            Numerator = ["POP"],
            Kind = "density",
            Format = "decimal",
        };

        var data = await BuildAsync([Area("A", ("POP", 1000))], mapVar, landKm2ById: null);

        Assert.Null(data.Areas[0].Value);
        Assert.Equal(1, data.NoDataCount);
    }

    // ---- 5. quantile classification ----------------------------------------------------------

    [Fact]
    public async Task Quantiles_TenDistinctValues_FiveAscendingClassesCoveringAllAreas()
    {
        var data = await BuildAsync(DirectAreas(10, 20, 30, 40, 50, 60, 70, 80, 90, 100), DirectVar());

        // Four inner breaks -> five classes, strictly ascending, strictly inside the value range.
        Assert.Equal(4, data.Breaks.Count);
        double[] expectedBreaks = [28, 46, 64, 82]; // R type-7 quantiles at p = .2, .4, .6, .8
        for (var i = 0; i < expectedBreaks.Length; i++)
            Assert.Equal(expectedBreaks[i], data.Breaks[i], precision: 9);
        for (var i = 1; i < data.Breaks.Count; i++)
            Assert.True(data.Breaks[i] > data.Breaks[i - 1], "breaks must be strictly ascending");
        Assert.All(data.Breaks, b => Assert.InRange(b, 10, 99.999999));

        Assert.Equal(5, data.ClassLabels.Count);
        Assert.Equal(5, data.ClassCounts.Count);
        Assert.Equal([2, 2, 2, 2, 2], data.ClassCounts);
        Assert.Equal(10, data.ClassCounts.Sum()); // every non-null area landed in exactly one class
        Assert.Equal(0, data.NoDataCount);
        Assert.Equal("Quantile classification, 5 classes", data.MethodNote);
    }

    [Fact]
    public async Task Quantiles_HeavyTies_DedupesBreaksAndCollapsesClasses()
    {
        // Eight areas share one value; only three distinct values exist.
        var data = await BuildAsync(DirectAreas(5, 5, 5, 5, 5, 5, 5, 5, 7, 9), DirectVar());

        Assert.Equal([5], data.Breaks); // both raw tercile breaks equal 5 -> deduped to one
        Assert.Equal(2, data.ClassLabels.Count);
        Assert.True(data.ClassLabels.Count <= 5);
        Assert.Equal([8, 2], data.ClassCounts);
        Assert.Equal(10, data.ClassCounts.Sum());
        Assert.Equal("Quantile classification, 2 classes", data.MethodNote);
    }

    [Fact]
    public async Task Quantiles_AllValuesEqual_SingleClassNoBreaks()
    {
        var data = await BuildAsync(DirectAreas(3, 3, 3, 3), DirectVar());

        Assert.Empty(data.Breaks);
        Assert.Single(data.ClassLabels);
        Assert.Equal([4], data.ClassCounts);
        Assert.Equal("Quantile classification, 1 class", data.MethodNote);
        Assert.Equal("3.0", data.ClassLabels[0]); // degenerate class: one value, no "lo – hi" range
    }

    [Fact]
    public async Task Quantiles_AllNullValues_EmptySchemeAndCorrectNoDataCount()
    {
        var data = await BuildAsync(DirectAreas(null, null, null), DirectVar());

        Assert.Equal(3, data.Areas.Count);
        Assert.All(data.Areas, a => Assert.Null(a.Value));
        Assert.All(data.Areas, a => Assert.Equal("—", a.Display));
        Assert.Empty(data.Breaks);
        Assert.Empty(data.ClassLabels);
        Assert.Empty(data.ClassCounts);
        Assert.Equal(3, data.NoDataCount);
        Assert.Equal("Quantile classification, 0 classes", data.MethodNote);
    }

    [Fact]
    public async Task Quantiles_MixedNullAndValues_CountsSumToNonNullCount()
    {
        var data = await BuildAsync(DirectAreas(1, null, 2, null, 3, 4, 5, 6, null), DirectVar());

        Assert.Equal(3, data.NoDataCount);
        Assert.Equal(6, data.ClassCounts.Sum()); // only the six non-null areas are classified
    }

    // ---- 6. legend label formatting ----------------------------------------------------------

    [Fact]
    public async Task ClassLabels_PercentFormat_EveryLabelCarriesPercentSign()
    {
        var mapVar = new MapVariableDef
        {
            Key = "pct",
            Label = "Share (%)",
            Numerator = ["N"],
            Denominator = "D",
            Kind = "percent",
            Format = "percent",
        };
        var areas = Enumerable.Range(1, 10)
            .Select(i => Area("A" + i, ("N", i * 10.0), ("D", 200.0)))
            .ToList();

        var data = await BuildAsync(areas, mapVar);

        Assert.Equal(5, data.ClassLabels.Count);
        Assert.All(data.ClassLabels, l => Assert.Contains("%", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClassLabels_CurrencyFormat_EveryLabelCarriesDollarSign()
    {
        var data = await BuildAsync(
            DirectAreas(15000, 27000, 39000, 48000, 55000, 63000, 72000, 84000, 91000, 105000),
            DirectVar(format: "currency"));

        Assert.Equal(5, data.ClassLabels.Count);
        Assert.All(data.ClassLabels, l => Assert.Contains("$", l, StringComparison.Ordinal));
        // Range labels are "lo – hi" with both ends formatted.
        Assert.Contains(" – ", data.ClassLabels[0], StringComparison.Ordinal);
    }
}

public class QuantileEmptyClassRegressionTests
{
    // Duplicate-heavy data whose interpolated quantile lands in an empty gap used to
    // produce a legend class containing no areas (e.g. StatCan pop_change rounded to
    // one decimal). Breaks must only close classes that hold data.
    [Fact]
    public async Task DuplicateHeavyValues_ProduceNoEmptyClasses()
    {
        var provider = new FakeProvider(
        [
            new AreaValue("A", "A", new Dictionary<string, double?> { ["v"] = -1.5 }),
            new AreaValue("B", "B", new Dictionary<string, double?> { ["v"] = -1.5 }),
            new AreaValue("C", "C", new Dictionary<string, double?> { ["v"] = -1.5 }),
            new AreaValue("D", "D", new Dictionary<string, double?> { ["v"] = 0.3 }),
            new AreaValue("E", "E", new Dictionary<string, double?> { ["v"] = 2.1 }),
        ]);
        var mapVar = new MapVariableDef { Key = "k", Label = "L", Numerator = ["v"], Kind = "direct", Format = "percent" };

        var data = await new MapService().BuildAsync(
            provider, new DatasetDef { Key = "d", DisplayName = "D", Universe = "U", SourceTable = "T" },
            mapVar, "PR", null, null);

        Assert.DoesNotContain(0, data.ClassCounts);
        Assert.Equal(data.ClassCounts.Count, data.ClassLabels.Count);
        Assert.Equal(5, data.ClassCounts.Sum());
    }

    private sealed class FakeProvider(IReadOnlyList<AreaValue> areas) : IDemographicProvider
    {
        public string CountryCode => "CA";
        public CountryCatalog Catalog { get; } = new() { SourceName = "Test source" };
        public Task<IReadOnlyList<GeoUnit>> GetTopLevelUnitsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GeoUnit>> GetChildUnitsAsync(GeoUnit ancestor, string targetLevelCode, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QueryResult> QueryAsync(DemographicQuery query, IProgress<string>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AreaValue>> GetAreaValuesAsync(string levelCode, string? parentId, IReadOnlyList<string> variableCodes, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(areas);
    }
}
