using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CensusScope.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace CensusScope.App.Views;

/// <summary>Main application window: hosts the query builder, the results grid,
/// the choropleth map tab, and the plain-language query panel.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>Creates the window bound to <paramref name="vm"/>. Matches the call in App.xaml.cs.</summary>
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.ResultColumnsChanged += OnResultColumnsChanged;
        ResultsGrid.AutoGeneratingColumn += OnAutoGeneratingColumn;
        InitializeMapAsync();
    }

    /// <summary>Grid metadata that cannot be data-bound: sorting is only meaningful
    /// when the cells are plain numbers (Compare mode).</summary>
    private void OnResultColumnsChanged()
    {
        ResultsGrid.CanUserSortColumns = _vm.LastResultNumeric;
    }

    /// <summary>
    /// Auto-generated columns bind by DataTable column NAME ("C0".."Cn" — real headers would
    /// break binding paths), so the human-readable header is applied here from the view model.
    /// </summary>
    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var index = -1;
        if (e.PropertyName is { Length: > 1 } name && name[0] == 'C'
            && int.TryParse(name[1..], out var parsed))
        {
            index = parsed;
        }

        var headers = _vm.ResultHeaders;
        e.Column.Header = headers is not null && index >= 0 && index < headers.Length
            ? headers[index]
            : e.PropertyName;

        if (e.Column is DataGridTextColumn textColumn && e.PropertyType == typeof(double))
        {
            // Data columns carry their value format (C1 -> ResultFormats[0], ...), so a
            // median income sorts numerically AND still reads as "$91,000", not "91000".
            var formats = _vm.ResultFormats;
            var format = formats is not null && index >= 1 && index - 1 < formats.Length
                ? formats[index - 1]
                : null;
            if (textColumn.Binding is Binding binding)
                binding.StringFormat = format switch
                {
                    "currency" => "$#,0",
                    // '%' in a numeric format string multiplies by 100; quote it for a literal.
                    "percent" => "#,0.#'%'",
                    "decimal" => "#,0.0",
                    _ => "#,0.##",
                };
            textColumn.ElementStyle = (Style)FindResource("RightCell");
        }

        if (index == 0)
            e.Column.MinWidth = 240;
    }

    /// <summary>
    /// Double-clicking the status line opens the full text in a copyable dialog — error
    /// messages (key problems, HTTP failures) are longer than one truncated line.
    /// </summary>
    private void OnStatusTextMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || string.IsNullOrWhiteSpace(_vm.StatusText))
            return;

        var textBox = new TextBox
        {
            Text = _vm.StatusText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        new Window
        {
            Title = "Status detail",
            Owner = this,
            Width = 560,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = textBox,
        }.ShowDialog();
    }

    // ------------------------------------------------------------------- map

    /// <summary>
    /// Initializes the WebView2 map surface: creates the environment with an app-local user
    /// data folder, maps the offline map page and the payload folder onto virtual host names
    /// (keeping everything same-machine, no live web content), wires the JS-to-C# message
    /// channel, and navigates to the map page. Any failure (e.g. the WebView2 runtime is not
    /// installed) is reported in the map status — the rest of the app keeps working.
    /// </summary>
    private async void InitializeMapAsync()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(localAppData, "CensusScope", "webview2"));
            await MapWeb.EnsureCoreWebView2Async(environment);

            var mapPageDir = Path.Combine(AppContext.BaseDirectory, "Map");
            var payloadDir = Path.Combine(localAppData, "CensusScope", "map");
            Directory.CreateDirectory(payloadDir);

            var core = MapWeb.CoreWebView2!;
            core.SetVirtualHostNameToFolderMapping(
                "censusscope-app.example", mapPageDir, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(
                "censusscope-data.example", payloadDir, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnMapWebMessage;

            _vm.Map.PayloadReady += _ => Dispatcher.Invoke(() =>
                MapWeb.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"reload\"}"));

            core.Navigate("https://censusscope-app.example/map.html");
        }
        catch (Exception ex)
        {
            // Latch the failure: a transient status line would be overwritten by the next
            // context update, and Render would then report success against a blank surface.
            _vm.Map.MarkEngineUnavailable(
                "The map engine (WebView2) could not start: " + ex.Message
                + " The rest of the application works normally.");
            _vm.Map.MapStatus = "Map engine unavailable.";
        }
    }

    /// <summary>Handles messages posted by map.html: area clicks, render confirmations,
    /// and recoverable page-side errors.</summary>
    private void OnMapWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp))
            {
                return;
            }

            switch (typeProp.GetString())
            {
                case "click":
                    if (root.TryGetProperty("name", out var nameProp)
                        && nameProp.GetString() is { Length: > 0 } areaName)
                    {
                        _vm.StatusText = areaName;
                    }
                    break;

                case "rendered":
                    var areas = root.TryGetProperty("areas", out var areasProp)
                        && areasProp.TryGetInt32(out var n) ? n : 0;
                    var status = "Rendered " + areas + " areas.";
                    if (!string.IsNullOrEmpty(_vm.Map.JoinNote))
                        status += " " + _vm.Map.JoinNote;
                    _vm.Map.MapStatus = status;
                    break;

                case "error":
                    _vm.Map.MapStatus = root.TryGetProperty("message", out var messageProp)
                        ? messageProp.GetString() ?? "Map error."
                        : "Map error.";
                    break;
            }
        }
        catch (JsonException)
        {
            // A malformed message from the page is ignored rather than crashing the app.
        }
    }
}
