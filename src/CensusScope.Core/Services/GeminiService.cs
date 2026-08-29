using System.Text.Json;
using CensusScope.Core.Http;
using CensusScope.Core.Models;

namespace CensusScope.Core.Services;

/// <summary>
/// Thin client for the Gemini generateContent API. Translates natural-language questions
/// into <see cref="NlIntent"/> values (structured output) and summarizes result tables.
/// Gemini never produces demographic figures itself — all numbers come from the providers.
/// </summary>
public sealed class GeminiService(ApiClient http, AppSettings settings)
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    /// <summary>
    /// System prompt for intent extraction. The dataset/level options from the active
    /// catalog are appended at request time so the model can only pick real values.
    /// </summary>
    private const string SystemPrompt =
        "You are a translator that converts a user's demographic-data question into a structured query. " +
        "You NEVER produce population numbers or statistics yourself; you only select query parameters.\n" +
        "Rules:\n" +
        "- country: \"US\" or \"CA\", whichever country the question is about.\n" +
        "- mode: \"profile\" when the user asks about one named area; \"compare\" when the user asks for a " +
        "ranking or comparison of all areas at a level.\n" +
        "- datasetKey and geoLevel: choose STRICTLY from the provided options; never invent values.\n" +
        "- geoName: the specific area named by the user; null in compare mode unless the comparison is " +
        "within a named area.\n" +
        "- parentGeoName: the containing state/province when the user names or implies one " +
        "(e.g. \"San Francisco County, California\" -> parentGeoName \"California\"); otherwise null.\n" +
        "- explanation: one short sentence, in the SAME LANGUAGE as the user's question, describing your interpretation.";

    /// <summary>Response schema forcing the model to emit exactly one <see cref="NlIntent"/> JSON object.</summary>
    private static readonly object IntentSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            country = new { type = "STRING", @enum = new[] { "US", "CA" } },
            mode = new { type = "STRING", @enum = new[] { "profile", "compare" } },
            datasetKey = new { type = "STRING" },
            geoLevel = new { type = "STRING" },
            geoName = new { type = "STRING", nullable = true },
            parentGeoName = new { type = "STRING", nullable = true },
            explanation = new { type = "STRING" },
        },
        required = new[] { "country", "mode", "datasetKey", "geoLevel", "explanation" },
    };

    private static readonly JsonSerializerOptions IntentReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>True when a Gemini API key has been configured in settings.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.GeminiApiKey);

    /// <summary>
    /// Parses a natural-language question into a structured <see cref="NlIntent"/> using
    /// Gemini structured output (temperature 0).
    /// </summary>
    /// <param name="userText">The user's question, in any language.</param>
    /// <param name="catalogContext">Plain-text listing of the available datasets and geographic levels.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NlIntent> ParseIntentAsync(string userText, string catalogContext, CancellationToken ct = default)
    {
        var body = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = SystemPrompt + "\n\nAVAILABLE OPTIONS:\n" + catalogContext } },
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = userText } } },
            },
            generationConfig = new
            {
                temperature = 0.0,
                responseMimeType = "application/json",
                responseSchema = IntentSchema,
            },
        };

        var text = await GenerateContentAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NlIntent>(text, IntentReadOptions)
            ?? throw new InvalidOperationException("Gemini intent JSON deserialized to null: " + Snippet(text));
    }

    /// <summary>
    /// Produces a short natural-language answer (2–4 sentences) grounded strictly in the
    /// numbers of the provided result table.
    /// </summary>
    /// <param name="userQuestion">The original question, used to match language and focus.</param>
    /// <param name="tableText">Plain-text rendering of the result table, including any notes.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> SummarizeAsync(string userQuestion, string tableText, CancellationToken ct = default)
    {
        var prompt =
            "Answer the user's question in 2-4 sentences, in the same language as the question. " +
            "Use ONLY numbers that are present in the table below; never invent or extrapolate figures. " +
            "If the table notes rounding or margins of error, briefly mention that caveat.\n\n" +
            "QUESTION:\n" + userQuestion + "\n\n" +
            "TABLE:\n" + tableText;

        var body = new
        {
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } },
            },
            generationConfig = new { temperature = 0.3 },
        };

        var text = await GenerateContentAsync(body, ct).ConfigureAwait(false);
        return text.Trim();
    }

    /// <summary>
    /// Sends one generateContent request and returns the text of the first candidate part.
    /// Throws <see cref="InvalidOperationException"/> when the prompt was blocked or the
    /// response carries no usable text.
    /// </summary>
    private async Task<string> GenerateContentAsync(object body, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Gemini API key is not configured (Settings > Gemini API key).");

        var url = BaseUrl + settings.GeminiModel + ":generateContent";
        var headers = new Dictionary<string, string> { ["x-goog-api-key"] = settings.GeminiApiKey! };
        var response = await http.PostJsonAsync(url, JsonSerializer.Serialize(body), headers, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out var reason))
            throw new InvalidOperationException(
                "Gemini blocked the prompt (" + reason.GetString() + "): " + Snippet(response));

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini returned no candidates: " + Snippet(response));

        if (!candidates[0].TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array ||
            parts.GetArrayLength() == 0 ||
            !parts[0].TryGetProperty("text", out var textElement) ||
            textElement.GetString() is not { } text)
            throw new InvalidOperationException("Gemini response contained no text part: " + Snippet(response));

        return text;
    }

    private static string Snippet(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
