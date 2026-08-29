using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using CensusScope.Core.Services;

namespace CensusScope.App.Views;

/// <summary>Modal dialog for editing API keys and the Gemini model name.</summary>
public partial class SettingsWindow : Window
{
    private const string DefaultGeminiModel = "gemini-2.5-flash-lite";

    private readonly AppSettings _settings;

    /// <summary>Creates the dialog pre-filled from <paramref name="settings"/>.</summary>
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        CensusKeyBox.Text = settings.CensusApiKey ?? "";
        GeminiKeyBox.Text = settings.GeminiApiKey ?? "";
        GeminiModelBox.Text = settings.GeminiModel;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _settings.CensusApiKey = NullIfBlank(CensusKeyBox.Text);
        _settings.GeminiApiKey = NullIfBlank(GeminiKeyBox.Text);
        _settings.GeminiModel = NullIfBlank(GeminiModelBox.Text) ?? DefaultGeminiModel;
        _settings.Save();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private static string? NullIfBlank(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
