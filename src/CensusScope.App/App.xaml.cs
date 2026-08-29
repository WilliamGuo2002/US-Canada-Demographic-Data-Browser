using System.Windows;
using CensusScope.App.Demo;
using CensusScope.App.ViewModels;
using CensusScope.App.Views;
using CensusScope.Core.Geo;
using CensusScope.Core.Http;
using CensusScope.Core.Providers;
using CensusScope.Core.Services;

namespace CensusScope.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --demo runs the app with no API keys: Census and Gemini responses are canned,
        // Statistics Canada still comes from the live service. See DemoHttpHandler.
        var demo = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        // The user's real settings always back the Settings dialog. Demo mode gives the
        // PROVIDERS a throwaway copy with placeholder keys (they refuse to run without one)
        // so that saving from the dialog can never overwrite real keys with "demo".
        var settings = AppSettings.Load();
        var providerSettings = settings;
        var http = new ApiClient();

        if (demo)
        {
            http = new ApiClient(handler: new DemoHttpHandler());
            providerSettings = new AppSettings
            {
                CensusApiKey = "demo",
                GeminiApiKey = "demo",
                GeminiModel = settings.GeminiModel,
            };
        }

        IDemographicProvider[] providers;
        try
        {
            providers =
            [
                new UsCensusProvider(http, providerSettings, CatalogService.Load("us")),
                new StatCanProvider(http, CatalogService.Load("ca")),
            ];
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to load dimension catalogs:\n\n" + ex.Message,
                "CensusScope", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var gemini = new GeminiService(http, providerSettings);
        // Boundary downloads are keyless public data; in demo mode the demo handler passes
        // non-Census/non-Gemini hosts through to the network, so the same client works.
        var map = new MapViewModel(new BoundaryService(http), new MapService());
        var vm = new MainViewModel(providers, settings, gemini, map) { IsDemoMode = demo };
        new MainWindow(vm).Show();
    }
}
