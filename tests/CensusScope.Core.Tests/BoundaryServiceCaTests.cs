// Offline tests for BoundaryService's Canadian path, driven through the public
// GetBoundariesAsync seam with a stubbed HTTP handler serving synthetic Esri GeoJSON.
//
// This exercises the private micro-ring filter and the GeoJSON property normalization
// (id/name/landKm2, coordinate rounding, Polygon/MultiPolygon rewriting) end-to-end without
// the network. The US path is NOT covered here: it requires a zipped shapefile fixture,
// which has no small synthetic form worth maintaining.

using System.Text.Json;
using CensusScope.Core.Geo;
using CensusScope.Core.Http;

namespace CensusScope.Core.Tests;

public class BoundaryServiceCaTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "gw-test-" + Guid.NewGuid().ToString("N"));

    private static (BoundaryService Service, StubHttpHandler Stub) NewService(string responseBody)
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(responseBody));
        var service = new BoundaryService(
            new ApiClient(TempDir(), stub, TimeSpan.FromMilliseconds(1)),
            cacheDir: TempDir());
        return (service, stub);
    }

    /// <summary>A closed square ring of the given side length (5 positions, first == last).</summary>
    private static string Ring(double x, double y, double side) =>
        $"[[{F(x)},{F(y)}],[{F(x + side)},{F(y)}],[{F(x + side)},{F(y + side)}],[{F(x)},{F(y + side)}],[{F(x)},{F(y)}]]";

    private static string F(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Feature 1: MultiPolygon with a large member (1° x 1° bbox) and a micro member
    //            (0.01° x 0.01° = 0.0001 sq deg < the 0.002 threshold) -> micro dropped,
    //            and the survivor is rewritten as a plain Polygon.
    // Feature 2: single Polygon with high-precision coordinates -> rounded to 4 decimals,
    //            LANDAREA null -> landKm2 null.
    // Feature 3: MultiPolygon of two micro members -> the larger one is kept anyway
    //            (a feature must never disappear).
    private static readonly string EsriGeoJson = $$$"""
        {"type":"FeatureCollection","features":[
          {"type":"Feature",
           "properties":{"DGUID":"2021A000235","PRENAME":"Ontario","LANDAREA":908699.33},
           "geometry":{"type":"MultiPolygon","coordinates":[
             [{{{Ring(-80, 43, 1.0)}}}],
             [{{{Ring(-81, 45, 0.01)}}}]]}},
          {"type":"Feature",
           "properties":{"DGUID":"2021A000224","PRENAME":"Quebec","LANDAREA":null},
           "geometry":{"type":"Polygon","coordinates":[
             [[-71.123456,46.987654],[-70.111111,46.0],[-71.0,47.0],[-71.123456,46.987654]]]}},
          {"type":"Feature",
           "properties":{"DGUID":"2021A000211","PRENAME":"Prince Edward Island","LANDAREA":5681.18},
           "geometry":{"type":"MultiPolygon","coordinates":[
             [{{{Ring(-63, 46, 0.02)}}}],
             [{{{Ring(-64, 46, 0.01)}}}]]}}
        ]}
        """;

    [Fact]
    public async Task CaProvinces_FiltersMicroRings_NormalizesProperties_RoundsCoordinates()
    {
        var (service, stub) = NewService(EsriGeoJson);

        var set = await service.GetBoundariesAsync("CA", "PR");

        // The Esri query targets the PR layer (0) with the PR name field.
        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("/MapServer/0/query", url, StringComparison.Ordinal);
        Assert.Contains("PRENAME", url, StringComparison.Ordinal);
        Assert.Contains("f=geojson", url, StringComparison.Ordinal);

        Assert.Equal("Boundaries: Statistics Canada, 2021 Census cartographic boundary files", set.Attribution);

        // Area list: one entry per feature, in file order, LANDAREA carried or null.
        Assert.Equal(3, set.Areas.Count);
        Assert.Equal(["2021A000235", "2021A000224", "2021A000211"], set.Areas.Select(a => a.Id));
        Assert.Equal(["Ontario", "Quebec", "Prince Edward Island"], set.Areas.Select(a => a.Name));
        Assert.Equal(908699.33, set.Areas[0].LandKm2);
        Assert.Null(set.Areas[1].LandKm2);

        using var doc = JsonDocument.Parse(set.GeoJson);
        var features = doc.RootElement.GetProperty("features").EnumerateArray().ToList();
        Assert.Equal(3, features.Count);

        // Normalized properties: id / name / landKm2 (null written explicitly).
        var ontarioProps = features[0].GetProperty("properties");
        Assert.Equal("2021A000235", ontarioProps.GetProperty("id").GetString());
        Assert.Equal("Ontario", ontarioProps.GetProperty("name").GetString());
        Assert.Equal(908699.33, ontarioProps.GetProperty("landKm2").GetDouble());
        Assert.Equal(JsonValueKind.Null, features[1].GetProperty("properties").GetProperty("landKm2").ValueKind);

        // Ontario: the micro member (0.0001 sq deg bbox) was dropped and the remaining single
        // member was rewritten as a Polygon, keeping only the 5-point large ring.
        var ontarioGeom = features[0].GetProperty("geometry");
        Assert.Equal("Polygon", ontarioGeom.GetProperty("type").GetString());
        var ontarioRings = ontarioGeom.GetProperty("coordinates");
        Assert.Equal(1, ontarioRings.GetArrayLength());
        Assert.Equal(5, ontarioRings[0].GetArrayLength());
        Assert.Equal(-80, ontarioRings[0][0][0].GetDouble());

        // Quebec: coordinates rounded to 4 decimals.
        var quebecRing = features[1].GetProperty("geometry").GetProperty("coordinates")[0];
        Assert.Equal(-71.1235, quebecRing[0][0].GetDouble());
        Assert.Equal(46.9877, quebecRing[0][1].GetDouble());
        Assert.Equal(-70.1111, quebecRing[1][0].GetDouble());

        // PEI: both members are below the threshold, but the largest is always kept.
        var peiGeom = features[2].GetProperty("geometry");
        Assert.Equal("Polygon", peiGeom.GetProperty("type").GetString());
        Assert.Equal(-63, peiGeom.GetProperty("coordinates")[0][0][0].GetDouble());
    }

    [Fact]
    public async Task SecondCall_IsServedFromTheOnDiskCacheWithoutANewRequest()
    {
        var (service, stub) = NewService(EsriGeoJson);

        var first = await service.GetBoundariesAsync("CA", "PR");
        var second = await service.GetBoundariesAsync("CA", "PR");

        Assert.Single(stub.RequestedUrls); // the pipeline ran once; the rerun hit the geojson cache
        Assert.Equal(first.GeoJson, second.GeoJson);
        Assert.Equal(first.Areas.Select(a => a.Id), second.Areas.Select(a => a.Id));
    }

    [Fact]
    public async Task CaCensusDivisions_QueryTheCdLayerWithTheCdNameField()
    {
        var body = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature",
               "properties":{"DGUID":"2021A00033520","CDNAME":"Toronto","LANDAREA":630.2},
               "geometry":{"type":"Polygon","coordinates":[[[-79.6,43.6],[-79.1,43.6],[-79.1,43.9],[-79.6,43.6]]]}}
            ]}
            """;
        var (service, stub) = NewService(body);

        var set = await service.GetBoundariesAsync("CA", "CD");

        var url = Assert.Single(stub.RequestedUrls);
        Assert.Contains("/MapServer/4/query", url, StringComparison.Ordinal);
        Assert.Contains("CDNAME", url, StringComparison.Ordinal);
        var area = Assert.Single(set.Areas);
        Assert.Equal("2021A00033520", area.Id);
        Assert.Equal("Toronto", area.Name);
        Assert.Equal(630.2, area.LandKm2);
    }

    [Fact]
    public async Task EsriErrorObject_BecomesAnActionableException()
    {
        // Esri returns HTTP 200 with an error body instead of an error status.
        var (service, _) = NewService("""{"error":{"code":400,"message":"Invalid query parameters"}}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetBoundariesAsync("CA", "PR"));

        Assert.Contains("Invalid query parameters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedCombination_ThrowsWithoutRequesting()
    {
        var (service, stub) = NewService(EsriGeoJson);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetBoundariesAsync("CA", "state"));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetBoundariesAsync("FR", "region"));

        Assert.Empty(stub.RequestedUrls);
    }
}
