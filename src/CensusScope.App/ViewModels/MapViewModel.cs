using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CensusScope.Core.Geo;
using CensusScope.Core.Models;
using CensusScope.Core.Providers;
using CensusScope.Core.Services;

namespace CensusScope.App.ViewModels;

/// <summary>
/// View model for the choropleth map tab: tracks whether the current country/level/dataset
/// selection is mappable, offers the dataset's mappable variables, and renders by joining
/// downloaded boundaries with classified data into a payload the MapLibre page consumes.
/// </summary>
public partial class MapViewModel : ObservableObject
{
    /// <summary>Fill color used for areas that have no data value.</summary>
    private const string NoDataColor = "#d9d9d9";

    /// <summary>ColorBrewer YlGnBu ramps by class count (sequential, color-blind safe).</summary>
    private static readonly IReadOnlyDictionary<int, string[]> YlGnBuRamps = new Dictionary<int, string[]>
    {
        [1] = ["#41b6c4"],
        [2] = ["#edf8b1", "#2c7fb8"],
        [3] = ["#edf8b1", "#7fcdbb", "#2c7fb8"],
        [4] = ["#ffffcc", "#a1dab4", "#41b6c4", "#225ea8"],
        [5] = ["#ffffcc", "#a1dab4", "#41b6c4", "#2c7fb8", "#253494"],
    };

    private readonly BoundaryService _boundaries;
    private readonly MapService _mapService;

    /// <summary>Cancels the previous render when a new one starts.</summary>
    private CancellationTokenSource? _renderCts;

    // Current selection context, pushed in by MainViewModel via UpdateContext.
    private IDemographicProvider? _provider;
    private DatasetDef? _dataset;
    private GeoLevelDef? _level;
    private GeoUnit? _ancestor1;

    /// <summary>Set once when the WebView2 engine fails to initialize; the map stays
    /// unavailable for the whole session with this reason.</summary>
    private string? _engineFailure;

    /// <summary>
    /// Permanently disables the map surface (e.g. the WebView2 runtime is missing or failed
    /// to initialize). The overlay shows the reason and Render can no longer run.
    /// </summary>
    public void MarkEngineUnavailable(string reason)
    {
        _engineFailure = reason;
        _renderCts?.Cancel();
        MapVariables.Clear();
        SelectedMapVariable = null;
        MapUnavailableReason = reason;
        MapAvailable = false;
        RenderMapCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Creates the view model. Matches the construction in App.xaml.cs.</summary>
    public MapViewModel(BoundaryService boundaries, MapService mapService)
    {
        _boundaries = boundaries;
        _mapService = mapService;
    }

    // ---------------------------------------------------------------- state

    /// <summary>Mappable variables of the selected dataset (empty when the map is unavailable).</summary>
    public ObservableCollection<MapVariableDef> MapVariables { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderMapCommand))]
    private MapVariableDef? _selectedMapVariable;

    [ObservableProperty]
    private string _mapStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderMapCommand))]
    [NotifyPropertyChangedFor(nameof(MapUnavailable))]
    private bool _mapAvailable;

    [ObservableProperty]
    private string _mapUnavailableReason = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderMapCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRenderCommand))]
    private bool _isMapBusy;

    /// <summary>Inverse of <see cref="MapAvailable"/>; the overlay in the map tab binds
    /// its visibility to this so the reason text covers the map when it cannot render.</summary>
    public bool MapUnavailable => !MapAvailable;

    /// <summary>
    /// Join-gap note from the last render ("N areas had no boundary/data match."), or null
    /// when every area matched. The window re-appends it to the "Rendered N areas." status
    /// posted back by the map page.
    /// </summary>
    public string? JoinNote { get; private set; }

    /// <summary>Raised after current.json is written; the argument is its full path.
    /// The window reacts by posting a reload message into the WebView2 page.</summary>
    public event Action<string>? PayloadReady;

    // -------------------------------------------------------------- context

    /// <summary>
    /// Called by <see cref="MainViewModel"/> whenever the country, level, dataset, or
    /// first-level ancestor selection changes. Recomputes availability and the variable list
    /// (preserving the current variable selection by key when possible).
    /// </summary>
    public void UpdateContext(IDemographicProvider? provider, DatasetDef? dataset, GeoLevelDef? level, GeoUnit? ancestor1)
    {
        // A real context change makes any in-flight render stale — cancel it so its payload
        // and status cannot be attributed to the new selection. (MainViewModel re-pushes the
        // unchanged context from several call sites; those must not abort a render.)
        var changed = !ReferenceEquals(provider, _provider) || !ReferenceEquals(dataset, _dataset)
            || !ReferenceEquals(level, _level) || !ReferenceEquals(ancestor1, _ancestor1);
        if (changed)
            _renderCts?.Cancel();

        _provider = provider;
        _dataset = dataset;
        _level = level;
        _ancestor1 = ancestor1;

        if (_engineFailure is not null)
        {
            MapVariables.Clear();
            SelectedMapVariable = null;
            MapUnavailableReason = _engineFailure;
            MapAvailable = false;
            return;
        }

        var levelSupported = provider is not null && level is not null
            && ((provider.CountryCode == "US" && level.Code is "state" or "county")
                || (provider.CountryCode == "CA" && level.Code is "PR" or "CD"));
        var supported = levelSupported && dataset is not null && dataset.MapVariables.Count > 0;

        if (supported)
        {
            MapUnavailableReason = "";
            var keepKey = SelectedMapVariable?.Key;
            MapVariables.Clear();
            foreach (var variable in dataset!.MapVariables) MapVariables.Add(variable);
            SelectedMapVariable = MapVariables.FirstOrDefault(v => v.Key == keepKey)
                ?? MapVariables.FirstOrDefault();
        }
        else
        {
            MapVariables.Clear();
            SelectedMapVariable = null;
            MapUnavailableReason = levelSupported
                ? "This dataset has no mappable variables."
                : "Map view supports State and County levels (US) and Province and Census division levels (Canada).";
        }

        MapAvailable = supported;
        RenderMapCommand.NotifyCanExecuteChanged();
    }

    // -------------------------------------------------------------- rendering

    private bool CanRenderMap() => MapAvailable && SelectedMapVariable is not null && !IsMapBusy;

    /// <summary>Cancels the in-flight map render (boundary download + data fetch).</summary>
    [RelayCommand(CanExecute = nameof(IsMapBusy))]
    private void CancelRender()
    {
        _renderCts?.Cancel();
        MapStatus = "Cancelling…";
    }

    /// <summary>Downloads boundaries, classifies the data, writes current.json,
    /// and signals the map page to reload it.</summary>
    [RelayCommand(CanExecute = nameof(CanRenderMap))]
    private async Task RenderMapAsync()
    {
        // Capture the whole context up front: UpdateContext may mutate these fields while
        // this render awaits, and a mixed old/new render must be impossible.
        var provider = _provider;
        var dataset = _dataset;
        var level = _level;
        var ancestor1 = _ancestor1;
        var mapVariable = SelectedMapVariable;
        if (provider is null || dataset is null || level is null || mapVariable is null) return;

        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        IsMapBusy = true;
        JoinNote = null;
        try
        {
            var progress = new Progress<string>(s => MapStatus = s);

            MapStatus = "Loading boundaries…";
            var boundarySet = await _boundaries.GetBoundariesAsync(provider.CountryCode, level.Code, progress, ct);

            var landKm2ById = new Dictionary<string, double?>(StringComparer.Ordinal);
            foreach (var area in boundarySet.Areas) landKm2ById[area.Id] = area.LandKm2;

            // County/CD maps are scoped to the selected top-level ancestor; state/PR maps
            // always cover the whole country.
            var parentId = level.Code is "county" or "CD" ? ancestor1?.Id : null;

            // The boundary file is national: when scoped to a parent, drop out-of-scope
            // features so the view fits the parent and areas outside it are not drawn
            // (and not misreported as join gaps). In-scope areas WITHOUT data still render
            // gray — suppression inside the parent must stay visible.
            if (parentId is not null)
                boundarySet = ScopeToParent(boundarySet, parentId, provider.CountryCode);

            var mapData = await _mapService.BuildAsync(
                provider, dataset, mapVariable, level.Code, parentId, landKm2ById, progress, ct);
            ct.ThrowIfCancellationRequested();

            var path = await WritePayloadAsync(boundarySet, mapData, ct);

            var matched = CountMatches(boundarySet, mapData, out var mismatched);
            MapStatus = "Map data ready — " + matched + " areas shaded.";
            if (mismatched > 0)
            {
                JoinNote = mismatched + " areas had no boundary/data match.";
                MapStatus += " " + JoinNote;
            }

            PayloadReady?.Invoke(path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            MapStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            MapStatus = ex.Message;
        }
        finally
        {
            IsMapBusy = false;
        }
    }

    /// <summary>True when the boundary id lies under the given top-level parent.</summary>
    private static bool IsInScope(string boundaryId, string parentId, string countryCode) =>
        countryCode == "US"
            // US: child GEOID is the 2-digit state FIPS plus its own code.
            ? boundaryId.StartsWith(parentId, StringComparison.Ordinal)
            // CA: DGUID = vintage(4)+type(1)+schema(4)+code; the child's first two code
            // digits are the parent province's 2-digit code.
            : parentId.Length >= 11 && boundaryId.Length >= 11
                && string.CompareOrdinal(boundaryId, 9, parentId, 9, 2) == 0;

    /// <summary>Returns a copy of the boundary set containing only features under the parent.</summary>
    private static BoundarySet ScopeToParent(BoundarySet set, string parentId, string countryCode)
    {
        var keptAreas = set.Areas.Where(a => IsInScope(a.Id, parentId, countryCode)).ToList();
        var keptIds = new HashSet<string>(keptAreas.Select(a => a.Id), StringComparer.Ordinal);

        using var doc = JsonDocument.Parse(set.GeoJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");
            foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                if (feature.TryGetProperty("properties", out var props)
                    && props.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && keptIds.Contains(id.GetString()!))
                {
                    feature.WriteTo(writer);
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new BoundarySet
        {
            GeoJson = System.Text.Encoding.UTF8.GetString(buffer.ToArray()),
            Areas = keptAreas,
            Attribution = set.Attribution,
        };
    }

    /// <summary>
    /// Counts boundary ids that have a data row (returned) and the join gaps in either
    /// direction (out): data rows without a boundary plus boundaries without a data row.
    /// </summary>
    private static int CountMatches(BoundarySet boundarySet, MapData mapData, out int mismatched)
    {
        var boundaryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var area in boundarySet.Areas) boundaryIds.Add(area.Id);

        var matchedIds = new HashSet<string>(StringComparer.Ordinal);
        var dataWithoutBoundary = 0;
        foreach (var area in mapData.Areas)
        {
            if (boundaryIds.Contains(area.Id)) matchedIds.Add(area.Id);
            else dataWithoutBoundary++;
        }

        mismatched = dataWithoutBoundary + (boundaryIds.Count - matchedIds.Count);
        return matchedIds.Count;
    }

    /// <summary>Composes and writes the payload consumed by map.html, returning its path.</summary>
    private static async Task<string> WritePayloadAsync(BoundarySet boundarySet, MapData mapData, CancellationToken ct)
    {
        var boundaryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var area in boundarySet.Areas) boundaryIds.Add(area.Id);

        // Values and display strings are written only for ids that actually have a boundary;
        // features whose id is absent from "values" render in the no-data color.
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        var displays = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var area in mapData.Areas)
        {
            if (!boundaryIds.Contains(area.Id)) continue;
            values[area.Id] = area.Value is { } v && double.IsFinite(v) ? v : null;
            displays[area.Id] = area.Display;
        }

        var colors = ColorsFor(mapData.ClassLabels.Count);

        // Legend counts must describe what is actually drawn: recount classes over the
        // joined values (data rows without a boundary are excluded), and no-data over the
        // boundary features, so the legend never disagrees with the picture on a join gap.
        var joinedCounts = new int[mapData.ClassLabels.Count];
        var joinedWithValue = 0;
        foreach (var value in values.Values)
        {
            if (value is not { } v) continue;
            joinedWithValue++;
            var index = 0;
            while (index < mapData.Breaks.Count && v > mapData.Breaks[index])
                index++;
            if (index < joinedCounts.Length) joinedCounts[index]++;
        }
        var drawnNoData = boundaryIds.Count - joinedWithValue;

        var classes = new List<Dictionary<string, object?>>();
        for (var i = 0; i < mapData.ClassLabels.Count; i++)
        {
            classes.Add(new Dictionary<string, object?>
            {
                ["color"] = i < colors.Length ? colors[i] : NoDataColor,
                ["label"] = mapData.ClassLabels[i],
                ["count"] = joinedCounts[i],
            });
        }

        using var geoDoc = JsonDocument.Parse(boundarySet.GeoJson);
        var payload = new Dictionary<string, object?>
        {
            ["geojson"] = geoDoc.RootElement.Clone(),
            ["values"] = values,
            ["displays"] = displays,
            ["breaks"] = mapData.Breaks,
            ["colors"] = colors,
            ["noDataColor"] = NoDataColor,
            ["legend"] = new Dictionary<string, object?>
            {
                ["title"] = mapData.Title,
                ["universe"] = mapData.Universe,
                ["source"] = mapData.Source,
                ["attribution"] = boundarySet.Attribution,
                ["method"] = mapData.MethodNote,
                ["classes"] = classes,
                ["noDataCount"] = drawnNoData,
            },
        };

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CensusScope", "map");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "current.json");

        // Atomic replace: the map page fetches this file on a reload message, so it must
        // never observe a half-written payload.
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(payload), ct);
        File.Move(tmpPath, path, overwrite: true);
        return path;
    }

    /// <summary>YlGnBu ramp for <paramref name="classCount"/> classes (the last color is
    /// repeated in the never-expected case of more than five classes).</summary>
    private static string[] ColorsFor(int classCount)
    {
        var ramp = YlGnBuRamps[Math.Clamp(classCount, 1, 5)];
        return classCount <= 5
            ? ramp
            : [.. ramp, .. Enumerable.Repeat(ramp[^1], classCount - 5)];
    }
}
