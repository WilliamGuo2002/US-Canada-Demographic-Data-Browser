using System.Text.Json;
using System.Text.Json.Serialization;

namespace CensusScope.Core.Services;

/// <summary>Persisted user settings (APPDATA/CensusScope/settings.json). API keys never live in code.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("censusApiKey")] public string? CensusApiKey { get; set; }
    [JsonPropertyName("geminiApiKey")] public string? GeminiApiKey { get; set; }
    [JsonPropertyName("geminiModel")] public string GeminiModel { get; set; } = "gemini-2.5-flash-lite";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CensusScope", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // corrupt settings file - start fresh rather than crash at startup
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, WriteOptions));
    }
}
