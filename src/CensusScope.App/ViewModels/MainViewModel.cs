using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CensusScope.Core.Models;
using CensusScope.Core.Parsing;
using CensusScope.Core.Providers;
using CensusScope.Core.Services;

namespace CensusScope.App.ViewModels;

/// <summary>
/// A selectable entry in an area picker: either a concrete <see cref="GeoUnit"/>,
/// or the "compare all" pseudo-entry (null <see cref="Unit"/>) that switches the query to Compare mode.
/// </summary>
public sealed class UnitChoice
{
    /// <summary>Creates a choice wrapping <paramref name="unit"/> (null for the compare-all entry).</summary>
    public UnitChoice(GeoUnit? unit, string display)
    {
        Unit = unit;
        Display = display;
    }

    /// <summary>The wrapped geographic unit, or null for the compare-all pseudo-entry.</summary>
    public GeoUnit? Unit { get; }

    /// <summary>Text shown in the combo box.</summary>
    public string Display { get; }

    /// <summary>True when this is the "compare all units of this level" pseudo-entry.</summary>
    public bool IsCompareAll => Unit is null;

    /// <inheritdoc/>
    public override string ToString() => Display;
}

/// <summary>
/// Main view model: drives country/level/dataset selection, the dynamic ancestor pickers
/// (derived entirely from the country catalog's level hierarchy — no hard-coded level names),
/// data loading into a <see cref="DataView"/>, and the Gemini plain-language query flow.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly GeminiService _gemini;

    /// <summary>Top-level units of the selected country (US states / Canadian provinces), cached per country.</summary>
    private IReadOnlyList<GeoUnit> _topUnits = [];

    /// <summary>Cancels the previous unit/ancestor refresh when a new one starts.</summary>
    private CancellationTokenSource? _refreshCts;

    /// <summary>The last successfully loaded result (used to build the Gemini summary context).</summary>
    private QueryResult? _lastResult;

    /// <summary>Nesting counter: while &gt; 0 a command orchestrates selection changes itself,
    /// so the selection-changed auto-handlers stay quiet and ordering stays deterministic.
    /// A counter (not a bool save/restore) stays correct under interleaved async flows.</summary>
    private int _suppressCount;

    private bool SuppressAutoHandlers => _suppressCount > 0;

    /// <summary>Nesting counter behind <see cref="IsBusy"/> so overlapping operations balance.</summary>
    private int _busyCount;

    /// <summary>Creates the view model. Matches the constructor call in App.xaml.cs.</summary>
    public MainViewModel(IDemographicProvider[] providers, AppSettings settings, GeminiService gemini, MapViewModel map)
    {
        Countries = providers;
        _settings = settings;
        _gemini = gemini;
        Map = map;
        SelectedCountry = providers.FirstOrDefault();
    }

    /// <summary>View model of the choropleth map tab; kept in sync via <see cref="UpdateMapContext"/>.</summary>
    public MapViewModel Map { get; }

    // ---------------------------------------------------------------- state

    /// <summary>All available country providers (one per country).</summary>
    public IReadOnlyList<IDemographicProvider> Countries { get; }

    /// <summary>
    /// True when the app was started with --demo: U.S. figures are synthetic placeholders and
    /// plain-language answers are canned. The UI shows a banner so demo numbers are never
    /// mistaken for published statistics.
    /// </summary>
    public bool IsDemoMode { get; init; }

    [ObservableProperty]
    private IDemographicProvider? _selectedCountry;

    /// <summary>Geographic levels of the selected country's catalog.</summary>
    public ObservableCollection<GeoLevelDef> Levels { get; } = [];

    [ObservableProperty]
    private GeoLevelDef? _selectedLevel;

    /// <summary>Datasets of the selected country's catalog.</summary>
    public ObservableCollection<DatasetDef> Datasets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadDataCommand))]
    private DatasetDef? _selectedDataset;

    /// <summary>Root-level ancestor choices (always the country's top-level units).</summary>
    public ObservableCollection<GeoUnit> Ancestor1Units { get; } = [];

    [ObservableProperty]
    private GeoUnit? _selectedAncestor1;

    [ObservableProperty]
    private bool _ancestor1Visible;

    [ObservableProperty]
    private string _ancestor1Label = "";

    /// <summary>Second-level ancestor choices ("All" plus intermediate units), shown for depth-2 levels.</summary>
    public ObservableCollection<UnitChoice> Ancestor2Units { get; } = [];

    [ObservableProperty]
    private UnitChoice? _selectedAncestor2;

    [ObservableProperty]
    private bool _ancestor2Visible;

    [ObservableProperty]
    private string _ancestor2Label = "";

    /// <summary>Area choices at the selected level: the compare-all entry plus concrete units.</summary>
    public ObservableCollection<UnitChoice> Units { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadDataCommand))]
    private UnitChoice? _selectedUnit;

    /// <summary>Filter text over the area list — large levels (US places, CA subdivisions)
    /// have thousands of entries, unusable without narrowing.</summary>
    [ObservableProperty]
    private string _unitFilter = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelOperationCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>Inverse of <see cref="IsBusy"/>; the selection controls bind IsEnabled to this
    /// so the user cannot change selections while an operation is orchestrating them.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>True when the selected country is the US and no Census API key is stored:
    /// drives the call-to-action banner telling the user where to paste the key.</summary>
    [ObservableProperty]
    private bool _censusKeyMissing;

    [ObservableProperty]
    private string _statusText = "Ready.";

    [ObservableProperty]
    private DataView? _resultView;

    /// <summary>Real column headers, index-aligned with the DataTable columns C0..Cn.</summary>
    [ObservableProperty]
    private string[]? _resultHeaders;

    /// <summary>Value format per DATA column (aligned with C1..Cn), from
    /// <see cref="QueryResult.ColumnFormats"/>; null when the result carries none.</summary>
    [ObservableProperty]
    private string[]? _resultFormats;

    [ObservableProperty]
    private bool _lastResultNumeric;

    [ObservableProperty]
    private string _resultTitle = "";

    [ObservableProperty]
    private string _resultSource = "";

    [ObservableProperty]
    private string _resultUniverse = "";

    [ObservableProperty]
    private string _resultNotes = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    private bool _hasResult;

    [ObservableProperty]
    private string _nlQuery = "";

    [ObservableProperty]
    private string _nlInterpretation = "";

    [ObservableProperty]
    private string _nlAnswer = "";

    /// <summary>Raised after <see cref="ResultView"/> and <see cref="ResultHeaders"/> are updated,
    /// so the window can rebuild grid metadata (headers, sorting).</summary>
    public event Action? ResultColumnsChanged;

    // --------------------------------------------------- selection handlers

    partial void OnSelectedCountryChanged(IDemographicProvider? value)
    {
        if (SuppressAutoHandlers || value is null) return;
        RunSafe(ResetForCountryAsync);
    }

    partial void OnSelectedLevelChanged(GeoLevelDef? value)
    {
        if (SuppressAutoHandlers || value is null) return;
        RunSafe(ConfigureLevelAsync);
    }

    partial void OnSelectedDatasetChanged(DatasetDef? value)
    {
        // Unconditional (no suppress guard): the map context is cheap to recompute and
        // must track programmatic dataset changes too.
        UpdateMapContext();
    }

    partial void OnSelectedAncestor1Changed(GeoUnit? value)
    {
        UpdateMapContext();
        if (SuppressAutoHandlers || value is null) return;
        RunSafe(ReloadAncestor2Async);
    }

    partial void OnSelectedAncestor2Changed(UnitChoice? value)
    {
        if (SuppressAutoHandlers || value is null) return;
        RunSafe(RefreshUnitsAsync);
    }

    /// <summary>Fire-and-forget wrapper for UI-triggered refreshes; never lets an exception
    /// escape onto the UI thread.</summary>
    private async void RunSafe(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer refresh
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    // ------------------------------------------------------ hierarchy logic

    /// <summary>
    /// Ancestor levels of <paramref name="level"/>, walking <see cref="GeoLevelDef.Parent"/>
    /// codes up to the root, returned top-down (root first). Depth = list length.
    /// </summary>
    private List<GeoLevelDef> AncestorChain(GeoLevelDef level)
    {
        var chain = new List<GeoLevelDef>();
        var levels = SelectedCountry?.Catalog.GeoLevels;
        if (levels is null) return chain;
        var parentCode = level.Parent;
        while (parentCode is not null)
        {
            var parent = levels.FirstOrDefault(l => l.Code == parentCode);
            if (parent is null) break;
            chain.Insert(0, parent);
            parentCode = parent.Parent;
        }
        return chain;
    }

    /// <summary>Cancels any in-flight refresh and returns a token for the new one.</summary>
    private CancellationToken NextRefreshToken()
    {
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        return _refreshCts.Token;
    }

    /// <summary>Rebuilds all catalog-driven state after the country changes:
    /// levels, datasets, cached top-level units, then the level configuration.</summary>
    internal async Task ResetForCountryAsync()
    {
        var provider = SelectedCountry;
        if (provider is null) return;
        var ct = NextRefreshToken();
        _suppressCount++;
        BeginBusy();
        UpdateCensusKeyBanner();
        try
        {
            Levels.Clear();
            foreach (var level in provider.Catalog.GeoLevels) Levels.Add(level);
            Datasets.Clear();
            foreach (var dataset in provider.Catalog.Datasets) Datasets.Add(dataset);
            SelectedLevel = Levels.FirstOrDefault();
            SelectedDataset = Datasets.FirstOrDefault();

            // Clear the previous country's geography up front so a failed load (e.g. missing
            // US Census key) leaves empty pickers, never a cross-country mixture.
            _topUnits = [];
            Ancestor1Units.Clear();
            SelectedAncestor1 = null;
            Ancestor2Units.Clear();
            SelectedAncestor2 = null;
            Units.Clear();
            SelectedUnit = null;

            StatusText = "Loading " + provider.Catalog.CountryName + " geography…";
            _topUnits = await provider.GetTopLevelUnitsAsync(ct);
            if (ct.IsCancellationRequested) return;

            await ConfigureLevelAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // superseded by a newer refresh (a genuine HTTP timeout falls through below)
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _suppressCount--;
            EndBusy();
            // Also covers the failure path (e.g. geography load failed): the map tab must
            // reflect the cleared selection rather than the previous country's state.
            UpdateMapContext();
        }
    }

    /// <summary>Pushes the current selection into the map view model.</summary>
    private void UpdateMapContext() =>
        Map.UpdateContext(SelectedCountry, SelectedDataset, SelectedLevel, SelectedAncestor1);

    /// <summary>Reconfigures the ancestor pickers for the selected level, then refreshes units.</summary>
    internal Task ConfigureLevelAsync() => ConfigureLevelAsync(NextRefreshToken());

    private async Task ConfigureLevelAsync(CancellationToken ct)
    {
        var level = SelectedLevel;
        if (SelectedCountry is null || level is null) return;
        var chain = AncestorChain(level);
        _suppressCount++;
        try
        {
            Ancestor1Visible = chain.Count >= 1;
            Ancestor2Visible = chain.Count >= 2;
            Ancestor1Label = chain.Count >= 1 ? chain[0].DisplayName : "";
            Ancestor2Label = chain.Count >= 2 ? chain[1].DisplayName + " (optional filter)" : "";

            if (chain.Count >= 1)
            {
                var keepId = SelectedAncestor1?.Id;
                Ancestor1Units.Clear();
                foreach (var unit in _topUnits) Ancestor1Units.Add(unit);
                SelectedAncestor1 = Ancestor1Units.FirstOrDefault(u => u.Id == keepId)
                    ?? Ancestor1Units.FirstOrDefault();
            }
            else
            {
                Ancestor1Units.Clear();
                SelectedAncestor1 = null;
            }

            if (chain.Count < 2)
            {
                Ancestor2Units.Clear();
                SelectedAncestor2 = null;
            }

            UpdateMapContext();
        }
        finally
        {
            _suppressCount--;
        }
        await ReloadAncestor2Async(ct);
    }

    /// <summary>Reloads the optional second-level ancestor list for the current
    /// <see cref="SelectedAncestor1"/> (when the level is deep enough), then refreshes units.</summary>
    internal Task ReloadAncestor2Async() => ReloadAncestor2Async(NextRefreshToken());

    private async Task ReloadAncestor2Async(CancellationToken ct)
    {
        var provider = SelectedCountry;
        var level = SelectedLevel;
        if (provider is null || level is null) return;
        var chain = AncestorChain(level);

        if (chain.Count >= 2 && SelectedAncestor1 is not null)
        {
            _suppressCount++;
            BeginBusy();
            try
            {
                StatusText = "Loading " + chain[1].DisplayName + " list…";
                var children = await provider.GetChildUnitsAsync(SelectedAncestor1, chain[1].Code, ct);
                if (ct.IsCancellationRequested) return;

                Ancestor2Units.Clear();
                Ancestor2Units.Add(new UnitChoice(null, "All"));
                foreach (var child in children) Ancestor2Units.Add(new UnitChoice(child, child.Name));
                SelectedAncestor2 = Ancestor2Units[0];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                return;
            }
            finally
            {
                _suppressCount--;
                EndBusy();
            }
        }

        await RefreshUnitsAsync(ct);
    }

    /// <summary>Reloads the area choices for the selected level under the effective ancestor.</summary>
    internal Task RefreshUnitsAsync() => RefreshUnitsAsync(NextRefreshToken());

    private async Task RefreshUnitsAsync(CancellationToken ct)
    {
        var provider = SelectedCountry;
        var level = SelectedLevel;
        if (provider is null || level is null) return;
        var chain = AncestorChain(level);
        _suppressCount++;
        BeginBusy();
        try
        {
            IReadOnlyList<GeoUnit> realUnits;
            if (chain.Count == 0)
            {
                realUnits = _topUnits;
            }
            else
            {
                var effectiveAncestor = SelectedAncestor2?.Unit ?? SelectedAncestor1;
                if (effectiveAncestor is null)
                {
                    realUnits = [];
                }
                else
                {
                    StatusText = "Loading " + level.DisplayName + " list…";
                    realUnits = await provider.GetChildUnitsAsync(effectiveAncestor, level.Code, ct);
                }
            }
            if (ct.IsCancellationRequested) return;

            _allUnitChoices = [new UnitChoice(null, "(Compare all — " + level.DisplayName + ")")];
            foreach (var unit in realUnits) _allUnitChoices.Add(new UnitChoice(unit, unit.Name));

            // A new unit list invalidates the old filter text; reset it with the handler
            // muted so ApplyUnitFilter below rebuilds exactly once.
            _resettingUnitFilter = true;
            UnitFilter = "";
            _resettingUnitFilter = false;
            ApplyUnitFilter(resetSelection: true);
            StatusText = "Ready.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // superseded by a newer refresh (a genuine HTTP timeout falls through below)
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _suppressCount--;
            EndBusy();
        }
    }

    /// <summary>Full unfiltered area choices for the current level (compare-all entry first).</summary>
    private List<UnitChoice> _allUnitChoices = [];

    /// <summary>True while the filter text is being reset programmatically, so the
    /// changed-handler does not re-apply against a list that is about to be rebuilt.</summary>
    private bool _resettingUnitFilter;

    partial void OnUnitFilterChanged(string value)
    {
        if (!_resettingUnitFilter)
            ApplyUnitFilter(resetSelection: false);
    }

    /// <summary>
    /// Rebuilds <see cref="Units"/> from the full list per the current filter text
    /// (accent- and case-insensitive substring match; the compare-all entry always stays).
    /// </summary>
    private void ApplyUnitFilter(bool resetSelection)
    {
        var needle = GeoResolver.Normalize(UnitFilter);
        var keep = SelectedUnit;

        Units.Clear();
        foreach (var choice in _allUnitChoices)
        {
            if (choice.IsCompareAll || needle.Length == 0
                || GeoResolver.Normalize(choice.Display).Contains(needle, StringComparison.Ordinal))
            {
                Units.Add(choice);
            }
        }

        if (!resetSelection && keep is not null && Units.Contains(keep))
            SelectedUnit = keep;
        else
            SelectedUnit = Units.FirstOrDefault(c => !c.IsCompareAll) ?? Units.FirstOrDefault();
    }

    // ---------------------------------------------------------- data loading

    private bool CanLoadData() => !IsBusy && SelectedDataset is not null && SelectedUnit is not null;

    /// <summary>Cancels the in-flight query or list refresh (the token also blocks a
    /// cancelled query's result from being published).</summary>
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelOperation()
    {
        _refreshCts?.Cancel();
        StatusText = "Cancelling…";
    }

    /// <summary>Loads the selected dataset for the selected area (or comparison) into the grid.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadData))]
    private Task LoadDataAsync() => LoadDataCoreAsync();

    /// <summary>Runs the query and publishes the result table. Returns true when a result was loaded.</summary>
    internal async Task<bool> LoadDataCoreAsync()
    {
        var provider = SelectedCountry;
        var dataset = SelectedDataset;
        var level = SelectedLevel;
        var unit = SelectedUnit;
        if (provider is null || dataset is null || level is null || unit is null)
        {
            StatusText = "Select a dataset and an area first.";
            return false;
        }

        // Reuse the refresh-token mechanism so any country/level/ancestor change both
        // cancels an in-flight load and blocks its stale result from being published.
        var ct = NextRefreshToken();
        BeginBusy();
        try
        {
            DemographicQuery query;
            if (unit.IsCompareAll)
            {
                var parent = SelectedAncestor2?.Unit ?? (Ancestor1Visible ? SelectedAncestor1 : null);
                query = new DemographicQuery(
                    provider.CountryCode, QueryMode.Compare, dataset.Key, level.Code,
                    GeoUnitId: null, ParentGeoId: parent?.Id);
            }
            else
            {
                query = new DemographicQuery(
                    provider.CountryCode, QueryMode.Profile, dataset.Key, level.Code,
                    GeoUnitId: unit.Unit!.Id, ParentGeoId: null);
            }

            var progress = new Progress<string>(s => StatusText = s);
            var result = await provider.QueryAsync(query, progress, ct);
            if (ct.IsCancellationRequested)
            {
                StatusText = "Cancelled.";
                return false;
            }
            _lastResult = result;

            var table = new DataTable();
            table.Columns.Add("C0", typeof(string));
            for (var i = 0; i < result.ColumnHeaders.Count; i++)
                table.Columns.Add("C" + (i + 1), result.NumericCells ? typeof(double) : typeof(string));

            foreach (var row in result.Rows)
            {
                var dataRow = table.NewRow();
                dataRow["C0"] = new string(' ', row.Indent * 3) + row.Label;
                for (var i = 0; i < row.Cells.Count && i < result.ColumnHeaders.Count; i++)
                {
                    var cell = row.Cells[i];
                    dataRow["C" + (i + 1)] = result.NumericCells
                        ? cell.Value is { } value ? (object)value : DBNull.Value
                        : cell.Display;
                }
                table.Rows.Add(dataRow);
            }

            // Headers must be in place before ResultView triggers column auto-generation.
            ResultHeaders = [query.Mode == QueryMode.Compare ? "Area" : "Category", .. result.ColumnHeaders];
            ResultFormats = result.ColumnFormats?.ToArray();
            LastResultNumeric = result.NumericCells;
            ResultTitle = result.Title;
            ResultSource = result.SourceAttribution;
            ResultUniverse = result.Universe;
            ResultNotes = result.Notes ?? "";
            ResultView = table.DefaultView;
            HasResult = true;
            ResultColumnsChanged?.Invoke();

            StatusText = result.Rows.Count + " rows · " + result.SourceAttribution;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusText = "Cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return false;
        }
        finally
        {
            EndBusy();
        }
    }

    private bool CanExportCsv() => !IsBusy && _lastResult is not null;

    /// <summary>Saves the loaded result as CSV: raw values, a separate margin-of-error column,
    /// the source code of every row, and the provenance block needed to cite the figures.</summary>
    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private void ExportCsv()
    {
        if (_lastResult is not { } result) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export result as CSV",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = SuggestFileName(result),
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // UTF-8 with BOM: without it Excel on Windows misreads accented place names
            // ("Montréal", "Trois-Rivières") as mojibake.
            File.WriteAllText(dialog.FileName, CsvWriter.Write(result), new UTF8Encoding(true));
            StatusText = "Exported " + result.Rows.Count + " rows to " + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusText = "Export failed: " + ex.Message;
        }
    }

    /// <summary>A filename derived from the result title, with characters Windows forbids removed.</summary>
    private static string SuggestFileName(QueryResult result)
    {
        var name = result.Title;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, ' ');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 100) name = name[..100].TrimEnd();
        return (name.Length == 0 ? "censusscope-export" : name) + ".csv";
    }

    // ------------------------------------------------- natural-language flow

    /// <summary>Interprets the plain-language question via Gemini, drives the selection
    /// state accordingly, loads the data, and summarizes the answer.</summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        var question = NlQuery.Trim();
        if (question.Length == 0) return;
        if (!_gemini.IsConfigured)
        {
            NlAnswer = "Gemini is not configured. Open Settings… and enter a Gemini API key "
                + "to use plain-language queries.";
            return;
        }

        var notes = new List<string>();
        _suppressCount++;
        BeginBusy();
        try
        {
            NlAnswer = "";
            NlInterpretation = "";
            StatusText = "Interpreting question…";
            var intent = await _gemini.ParseIntentAsync(question, BuildCatalogContext());
            NlInterpretation = (intent.Explanation ?? "").Trim()
                + " [" + (intent.Country ?? "?") + " · " + (intent.DatasetKey ?? "?")
                + " · " + (intent.GeoLevel ?? "?") + " · " + (intent.Mode ?? "?") + "]";

            // Country: switch provider and rebuild state deterministically.
            var provider = Countries.FirstOrDefault(p =>
                string.Equals(p.CountryCode, intent.Country, StringComparison.OrdinalIgnoreCase));
            if (provider is not null)
            {
                SelectedCountry = provider;
                await ResetForCountryAsync();
            }
            if (SelectedCountry is null)
            {
                NlAnswer = "No country is selected.";
                return;
            }

            // Dataset: match by key, otherwise keep the current one and note it.
            if (!string.IsNullOrWhiteSpace(intent.DatasetKey))
            {
                var dataset = Datasets.FirstOrDefault(d =>
                    string.Equals(d.Key, intent.DatasetKey, StringComparison.OrdinalIgnoreCase));
                if (dataset is not null)
                    SelectedDataset = dataset;
                else
                    notes.Add("Dataset '" + intent.DatasetKey + "' was not recognized; using '"
                        + (SelectedDataset?.DisplayName ?? "none") + "'.");
            }

            // Level: match by code and reconfigure the ancestor pickers.
            if (!string.IsNullOrWhiteSpace(intent.GeoLevel))
            {
                var level = Levels.FirstOrDefault(l =>
                    string.Equals(l.Code, intent.GeoLevel, StringComparison.OrdinalIgnoreCase));
                if (level is not null && !ReferenceEquals(level, SelectedLevel))
                {
                    SelectedLevel = level;
                    await ConfigureLevelAsync();
                }
            }
            if (SelectedLevel is null)
            {
                NlAnswer = "No geographic level is selected.";
                return;
            }

            var isCompare = string.Equals(intent.Mode, "compare", StringComparison.OrdinalIgnoreCase);
            var chain = AncestorChain(SelectedLevel);

            if (chain.Count == 0)
            {
                // Root level: the profile target resolves directly against the top-level units.
                if (isCompare)
                {
                    SelectedUnit = Units.FirstOrDefault(c => c.IsCompareAll) ?? SelectedUnit;
                }
                else if (!string.IsNullOrWhiteSpace(intent.GeoName))
                {
                    var target = GeoResolver.Resolve(_topUnits, intent.GeoName);
                    if (target is null)
                    {
                        NlAnswer = NotFoundMessage(intent.GeoName, _topUnits);
                        return;
                    }
                    SelectedUnit = Units.FirstOrDefault(c => c.Unit?.Id == target.Id) ?? SelectedUnit;
                }
                else
                {
                    NlAnswer = "The question did not name a specific area to profile.";
                    return;
                }
            }
            else
            {
                // Deeper levels: resolve the containing top-level ancestor first.
                var parentName = intent.ParentGeoName;
                if (string.IsNullOrWhiteSpace(parentName))
                {
                    if (isCompare)
                        parentName = intent.GeoName;
                    else if (intent.GeoName is { } geoName && geoName.Contains(','))
                        parentName = geoName[(geoName.LastIndexOf(',') + 1)..].Trim();
                }
                if (!string.IsNullOrWhiteSpace(parentName))
                {
                    var parent = GeoResolver.Resolve(_topUnits, parentName);
                    if (parent is null)
                    {
                        NlAnswer = NotFoundMessage(parentName, _topUnits);
                        return;
                    }
                    SelectedAncestor1 = Ancestor1Units.FirstOrDefault(u => u.Id == parent.Id)
                        ?? SelectedAncestor1;
                }

                // Rebuild the optional-filter list and units under the (possibly new) ancestor.
                await ReloadAncestor2Async();

                if (isCompare)
                {
                    SelectedUnit = Units.FirstOrDefault(c => c.IsCompareAll) ?? SelectedUnit;
                }
                else
                {
                    var targetName = intent.GeoName;
                    if (string.IsNullOrWhiteSpace(targetName))
                    {
                        NlAnswer = "The question did not name a specific area to profile.";
                        return;
                    }
                    var realUnits = Units.Where(c => c.Unit is not null).Select(c => c.Unit!).ToList();
                    var target = GeoResolver.Resolve(realUnits, targetName);
                    if (target is null && targetName.Contains(','))
                        target = GeoResolver.Resolve(realUnits, targetName[..targetName.LastIndexOf(',')].Trim());
                    if (target is null)
                    {
                        NlAnswer = NotFoundMessage(targetName, realUnits);
                        return;
                    }
                    SelectedUnit = Units.FirstOrDefault(c => c.Unit?.Id == target.Id) ?? SelectedUnit;
                }
            }

            // Load the data and summarize the result.
            var loaded = await LoadDataCoreAsync();
            if (loaded && _lastResult is { } result)
            {
                StatusText = "Summarizing…";
                var tableText = new StringBuilder();
                tableText.AppendLine(result.Title);
                tableText.AppendLine("Universe: " + result.Universe);
                tableText.AppendLine("Columns: " + string.Join(" | ", result.ColumnHeaders));
                foreach (var row in result.Rows.Take(80))
                    tableText.AppendLine(row.Label + ": " + string.Join(" | ", row.Cells.Select(c => c.Display)));

                var summary = await _gemini.SummarizeAsync(question, tableText.ToString());
                NlAnswer = notes.Count == 0 ? summary : string.Join(" ", notes) + "\n\n" + summary;
                StatusText = "Ready.";
            }
            else if (string.IsNullOrEmpty(NlAnswer))
            {
                NlAnswer = "The data request did not complete: " + StatusText;
            }
        }
        catch (Exception ex)
        {
            NlAnswer = "Error: " + ex.Message;
        }
        finally
        {
            _suppressCount--;
            EndBusy();
        }
    }

    private bool CanAsk() => !IsBusy;

    /// <summary>Compact catalog description handed to Gemini so it can map the question
    /// onto real country codes, level codes, and dataset keys.</summary>
    private string BuildCatalogContext()
    {
        var sb = new StringBuilder();
        foreach (var provider in Countries)
        {
            var catalog = provider.Catalog;
            sb.AppendLine("Country " + catalog.CountryCode + " (" + catalog.CountryName + "):");
            sb.AppendLine("  Levels: " + string.Join(", ",
                catalog.GeoLevels.Select(l => l.Code + ": " + l.DisplayName)));
            sb.AppendLine("  Datasets:");
            foreach (var dataset in catalog.Datasets)
                sb.AppendLine("    " + dataset.Key + ": " + dataset.DisplayName + " — " + dataset.Universe);
        }
        return sb.ToString();
    }

    private static string NotFoundMessage(string name, IReadOnlyList<GeoUnit> pool)
    {
        var candidates = GeoResolver.Candidates(pool, name);
        var closest = candidates.Count == 0
            ? "(no similar names)"
            : string.Join(", ", candidates.Select(c => c.Name));
        return "Could not find area '" + name + "'. Closest: " + closest;
    }

    // -------------------------------------------------------------- settings

    /// <summary>Opens the modal settings dialog.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settings)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (window.ShowDialog() == true)
        {
            StatusText = "Settings saved.";
            UpdateCensusKeyBanner();
            // A newly entered Census key should load the US geography right away.
            RunSafe(ResetForCountryAsync);
        }
    }

    /// <summary>Shows the key call-to-action only when U.S. data genuinely needs one
    /// (never in demo mode, where canned U.S. responses are served).</summary>
    private void UpdateCensusKeyBanner() =>
        CensusKeyMissing = !IsDemoMode
            && SelectedCountry?.CountryCode == "US"
            && string.IsNullOrWhiteSpace(_settings.CensusApiKey);

    // ------------------------------------------------------------------ busy

    private void BeginBusy()
    {
        if (++_busyCount == 1) IsBusy = true;
    }

    private void EndBusy()
    {
        if (--_busyCount == 0) IsBusy = false;
    }
}
