using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CensusScope.Core.Http;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CensusScope.Core.Geo;

/// <summary>
/// Downloads national boundary geometry, converts it to a compact WGS84 GeoJSON
/// FeatureCollection, and caches the result on disk so the pipeline runs once.
/// <para>
/// US boundaries come from the Census Bureau 2024 cartographic boundary shapefiles
/// (EPSG:4269 geographic degrees, treated as WGS84-compatible for web maps — no
/// reprojection). Canadian boundaries come from the Statistics Canada 2021 cartographic
/// boundary Esri REST service, which outputs WGS84 GeoJSON directly.
/// </para>
/// </summary>
public sealed class BoundaryService
{
    private const string UsStateZipUrl = "https://www2.census.gov/geo/tiger/GENZ2024/shp/cb_2024_us_state_20m.zip";
    private const string UsCountyZipUrl = "https://www2.census.gov/geo/tiger/GENZ2024/shp/cb_2024_us_county_20m.zip";
    private const string CaMapServerBase = "https://geo.statcan.gc.ca/geo_wa/rest/services/2021/Cartographic_boundary_files/MapServer";

    /// <summary>
    /// Member polygons of a MultiPolygon whose exterior-ring bbox area (square degrees) falls
    /// below this are dropped (largest member always kept). The Canadian files are dominated by
    /// thousands of tiny coastal island rings that are invisible at national zoom; filtering them
    /// shrinks the 14-16 MB payloads to a few MB with no visible change.
    /// </summary>
    private const double MinMemberBboxAreaSqDeg = 0.002;

    /// <summary>Coordinate output precision; 4 decimals ≈ 11 m at the equator, plenty for national/regional zoom.</summary>
    private const int CoordinateDecimals = 4;

    private static readonly TimeSpan RawDownloadTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan FinalGeoJsonTtl = TimeSpan.FromDays(90);

    private readonly ApiClient _http;
    private readonly string _cacheDir;

    static BoundaryService()
    {
        // The shapefile DBF reader resolves legacy code pages; the provider must be registered
        // once per process before any DBF is opened.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Creates the service.</summary>
    /// <param name="http">HTTP client used for the raw downloads (zip / GeoJSON).</param>
    /// <param name="cacheDir">Directory for the final GeoJSON cache; defaults to %LOCALAPPDATA%\CensusScope\geo.</param>
    public BoundaryService(ApiClient http, string? cacheDir = null)
    {
        _http = http;
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CensusScope", "geo");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Returns the boundary layer for one (country, level) combination, running the full
    /// download/convert pipeline on first use and serving a 90-day on-disk cache afterwards.
    /// </summary>
    /// <param name="countryCode">"US" or "CA".</param>
    /// <param name="levelCode">US: "state" or "county"; CA: "PR" or "CD".</param>
    /// <param name="progress">Receives coarse stage messages ("Downloading boundaries...", ...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException">The (country, level) pair has no boundary source.</exception>
    public async Task<BoundarySet> GetBoundariesAsync(
        string countryCode, string levelCode, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var attribution = (countryCode, levelCode) switch
        {
            ("US", "state" or "county") => "Boundaries: U.S. Census Bureau cartographic boundary files (2024)",
            ("CA", "PR" or "CD") => "Boundaries: Statistics Canada, 2021 Census cartographic boundary files",
            _ => throw new NotSupportedException(
                $"No boundary source for country '{countryCode}', level '{levelCode}'. " +
                "Supported combinations: (US, state), (US, county), (CA, PR), (CA, CD)."),
        };

        var finalPath = Path.Combine(_cacheDir, countryCode + "-" + levelCode + ".geojson");
        if (File.Exists(finalPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(finalPath) < FinalGeoJsonTtl)
        {
            progress?.Report("Loading cached boundaries...");
            try
            {
                var cached = await File.ReadAllTextAsync(finalPath, ct).ConfigureAwait(false);
                return new BoundarySet { GeoJson = cached, Areas = ReadAreas(cached), Attribution = attribution };
            }
            catch (JsonException)
            {
                // Corrupt cache file - fall through and rebuild it.
            }
        }

        var features = countryCode == "US"
            ? await LoadUsAsync(levelCode, progress, ct).ConfigureAwait(false)
            : await LoadCaAsync(levelCode, progress, ct).ConfigureAwait(false);

        progress?.Report("Simplifying geometry...");
        foreach (var feature in features)
        {
            ct.ThrowIfCancellationRequested();
            FilterMicroRings(feature);
        }

        progress?.Report("Converting to GeoJSON...");
        var geoJson = WriteFeatureCollection(features);

        progress?.Report("Saving boundary cache...");
        try { await File.WriteAllTextAsync(finalPath, geoJson, ct).ConfigureAwait(false); }
        catch (IOException) { /* cache write failure is non-fatal */ }

        return new BoundarySet
        {
            GeoJson = geoJson,
            Areas = features.Select(f => new BoundaryArea(f.Id, f.Name, f.LandKm2)).ToList(),
            Attribution = attribution,
        };
    }

    // ---------------------------------------------------------------- US path

    private async Task<List<FeatureData>> LoadUsAsync(string levelCode, IProgress<string>? progress, CancellationToken ct)
    {
        var url = levelCode switch
        {
            "state" => UsStateZipUrl,
            "county" => UsCountyZipUrl,
            _ => throw new NotSupportedException("Unsupported US level: " + levelCode),
        };

        progress?.Report("Downloading boundaries...");
        var zipBytes = await _http.GetBytesAsync(url, RawDownloadTtl, ct).ConfigureAwait(false);

        progress?.Report("Reading shapefile...");
        var tempDir = Path.Combine(Path.GetTempPath(), "censusscope-shp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using (var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                    if (entry.Name.Length > 0)
                        entry.ExtractToFile(Path.Combine(tempDir, entry.Name), overwrite: true);
            }

            var shpPath = Directory.EnumerateFiles(tempDir, "*.shp").FirstOrDefault()
                ?? throw new InvalidOperationException("The boundary archive contains no .shp file: " + url);

            var features = new List<FeatureData>();
            using var reader = new ShapefileDataReader(shpPath, GeometryFactory.Default);
            var header = reader.DbaseHeader;

            // Column 0 of the data reader is the geometry; DBF attributes start at 1.
            int geoidOrdinal = -1, nameOrdinal = -1, alandOrdinal = -1;
            for (var i = 0; i < header.NumFields; i++)
            {
                switch (header.Fields[i].Name)
                {
                    case "GEOID": geoidOrdinal = i + 1; break;
                    case "NAME": nameOrdinal = i + 1; break;
                    case "ALAND": alandOrdinal = i + 1; break;
                }
            }
            if (geoidOrdinal < 0 || nameOrdinal < 0 || alandOrdinal < 0)
                throw new InvalidOperationException("Shapefile is missing an expected attribute (GEOID/NAME/ALAND): " + url);

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = (Convert.ToString(reader.GetValue(geoidOrdinal), CultureInfo.InvariantCulture) ?? "").Trim();
                var name = (Convert.ToString(reader.GetValue(nameOrdinal), CultureInfo.InvariantCulture) ?? "").Trim();
                var landM2 = Convert.ToDouble(reader.GetValue(alandOrdinal), CultureInfo.InvariantCulture);
                var geometry = reader.Geometry;

                // Alaska crosses the antimeridian (Aleutians beyond -180 appear near +180);
                // shifting those longitudes by -360 renders the state as one piece west of the mainland.
                if (id.StartsWith("02", StringComparison.Ordinal))
                    ShiftPositiveLongitudes(geometry);

                features.Add(new FeatureData
                {
                    Id = id,
                    Name = name,
                    LandKm2 = Math.Round(landM2 / 1_000_000.0, 2),
                    Polygons = ToPolygonLists(geometry),
                });
            }
            return features;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ShiftPositiveLongitudes(Geometry geometry)
    {
        var changed = false;
        foreach (var c in geometry.Coordinates)
        {
            if (c.X > 0)
            {
                c.X -= 360;
                changed = true;
            }
        }
        if (changed)
            geometry.GeometryChanged();
    }

    private static List<List<List<double[]>>> ToPolygonLists(Geometry geometry) => geometry switch
    {
        Polygon p => [ToRings(p)],
        MultiPolygon mp => Enumerable.Range(0, mp.NumGeometries)
            .Select(i => ToRings((Polygon)mp.GetGeometryN(i)))
            .ToList(),
        _ => throw new InvalidOperationException("Unexpected geometry type in boundary file: " + geometry.GeometryType),
    };

    private static List<List<double[]>> ToRings(Polygon polygon)
    {
        var rings = new List<List<double[]>>(1 + polygon.NumInteriorRings) { ToRing(polygon.ExteriorRing) };
        for (var i = 0; i < polygon.NumInteriorRings; i++)
            rings.Add(ToRing(polygon.GetInteriorRingN(i)));
        return rings;
    }

    private static List<double[]> ToRing(LineString ring)
    {
        var seq = ring.CoordinateSequence;
        var points = new List<double[]>(seq.Count);
        for (var i = 0; i < seq.Count; i++)
            points.Add([seq.GetX(i), seq.GetY(i)]);
        return points;
    }

    // ---------------------------------------------------------------- CA path

    private async Task<List<FeatureData>> LoadCaAsync(string levelCode, IProgress<string>? progress, CancellationToken ct)
    {
        var (layer, nameField) = levelCode switch
        {
            "PR" => (0, "PRENAME"),
            "CD" => (4, "CDNAME"),
            _ => throw new NotSupportedException("Unsupported CA level: " + levelCode),
        };
        var url = $"{CaMapServerBase}/{layer}/query?where=1%3D1&outFields=DGUID,{nameField},LANDAREA"
                  + "&outSR=4326&geometryPrecision=4&maxAllowableOffset=0.01&f=geojson";

        progress?.Report("Downloading boundaries...");
        var json = await _http.GetStringAsync(url, RawDownloadTtl, ct).ConfigureAwait(false);

        progress?.Report("Parsing boundaries...");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Esri returns HTTP 200 with an error object instead of an error status code. The
        // transport cache cannot see that, so evict the entry or the error would be served
        // for the full raw-download TTL.
        if (root.TryGetProperty("error", out var error))
        {
            _http.InvalidateCached(url);
            var message = error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException("Boundary service returned an error: " + message);
        }

        if (!root.TryGetProperty("features", out var featureArray) || featureArray.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Boundary response contains no feature array.");

        var features = new List<FeatureData>();
        foreach (var feature in featureArray.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
                continue;

            var props = feature.GetProperty("properties");
            double? landKm2 = props.TryGetProperty("LANDAREA", out var land) && land.ValueKind == JsonValueKind.Number
                ? Math.Round(land.GetDouble(), 2)
                : null;

            features.Add(new FeatureData
            {
                Id = props.GetProperty("DGUID").GetString() ?? "",
                Name = props.GetProperty(nameField).GetString() ?? "",
                LandKm2 = landKm2,
                Polygons = ParseGeoJsonPolygons(geometry),
            });
        }
        return features;
    }

    private static List<List<List<double[]>>> ParseGeoJsonPolygons(JsonElement geometry)
    {
        var type = geometry.GetProperty("type").GetString();
        var coordinates = geometry.GetProperty("coordinates");
        return type switch
        {
            "Polygon" => [ParseRings(coordinates)],
            "MultiPolygon" => coordinates.EnumerateArray().Select(ParseRings).ToList(),
            _ => throw new InvalidOperationException("Unexpected GeoJSON geometry type: " + type),
        };
    }

    private static List<List<double[]>> ParseRings(JsonElement polygon) =>
        polygon.EnumerateArray()
            .Select(ring => ring.EnumerateArray()
                .Select(pos => new[] { pos[0].GetDouble(), pos[1].GetDouble() })
                .ToList())
            .ToList();

    // ------------------------------------------------------- shared pipeline

    /// <summary>
    /// Drops member polygons of a multi-part feature whose exterior-ring bbox area is below
    /// <see cref="MinMemberBboxAreaSqDeg"/> square degrees. The largest member is always kept
    /// so no feature disappears; single-polygon features are kept whole.
    /// </summary>
    private static void FilterMicroRings(FeatureData feature)
    {
        if (feature.Polygons.Count <= 1)
            return;

        var areas = new double[feature.Polygons.Count];
        var largestIndex = 0;
        for (var i = 0; i < feature.Polygons.Count; i++)
        {
            areas[i] = BboxArea(feature.Polygons[i][0]);
            if (areas[i] > areas[largestIndex])
                largestIndex = i;
        }

        var kept = new List<List<List<double[]>>>();
        for (var i = 0; i < feature.Polygons.Count; i++)
            if (i == largestIndex || areas[i] >= MinMemberBboxAreaSqDeg)
                kept.Add(feature.Polygons[i]);
        feature.Polygons = kept;
    }

    private static double BboxArea(List<double[]> ring)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in ring)
        {
            if (p[0] < minX) minX = p[0];
            if (p[0] > maxX) maxX = p[0];
            if (p[1] < minY) minY = p[1];
            if (p[1] > maxY) maxY = p[1];
        }
        return (maxX - minX) * (maxY - minY);
    }

    private static string WriteFeatureCollection(List<FeatureData> features)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");
            foreach (var feature in features)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");

                writer.WriteStartObject("properties");
                writer.WriteString("id", feature.Id);
                writer.WriteString("name", feature.Name);
                if (feature.LandKm2 is { } km2) writer.WriteNumber("landKm2", km2);
                else writer.WriteNull("landKm2");
                writer.WriteEndObject();

                writer.WriteStartObject("geometry");
                if (feature.Polygons.Count == 1)
                {
                    writer.WriteString("type", "Polygon");
                    writer.WriteStartArray("coordinates");
                    WriteRings(writer, feature.Polygons[0]);
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString("type", "MultiPolygon");
                    writer.WriteStartArray("coordinates");
                    foreach (var polygon in feature.Polygons)
                    {
                        writer.WriteStartArray();
                        WriteRings(writer, polygon);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteRings(Utf8JsonWriter writer, List<List<double[]>> rings)
    {
        foreach (var ring in rings)
        {
            writer.WriteStartArray();
            foreach (var pos in ring)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(pos[0], CoordinateDecimals));
                writer.WriteNumberValue(Math.Round(pos[1], CoordinateDecimals));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
    }

    /// <summary>Rebuilds the area list from a cached FeatureCollection produced by <see cref="WriteFeatureCollection"/>.</summary>
    private static IReadOnlyList<BoundaryArea> ReadAreas(string geoJson)
    {
        using var doc = JsonDocument.Parse(geoJson);
        var areas = new List<BoundaryArea>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            var land = props.GetProperty("landKm2");
            areas.Add(new BoundaryArea(
                props.GetProperty("id").GetString() ?? "",
                props.GetProperty("name").GetString() ?? "",
                land.ValueKind == JsonValueKind.Number ? land.GetDouble() : null));
        }
        return areas;
    }

    /// <summary>
    /// Intermediate feature: polygon members, each a list of rings, each ring a list of
    /// [lon, lat] positions. A plain nested-list shape shared by both source paths so the
    /// micro-ring filter and the writer are written once.
    /// </summary>
    private sealed class FeatureData
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public double? LandKm2 { get; init; }
        public required List<List<List<double[]>>> Polygons { get; set; }
    }
}
