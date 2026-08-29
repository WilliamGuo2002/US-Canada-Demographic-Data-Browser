// Offline tests for GeminiService.
//
// The Gemini integration has never been executed against the live API — no key has ever been
// available — so the single largest risk is that the JSON body System.Text.Json actually emits
// does not match Google's generateContent wire format (camelCase keys, the `enum` schema keyword,
// no `additionalProperties`, the key in the x-goog-api-key header rather than the query string).
//
// These tests pin the serialized request body against Google's documented contract by capturing
// it through an injected HttpMessageHandler and re-parsing it with JsonDocument. A serialization
// regression — a renamed anonymous-type member, an `@enum` leaking through as "@enum", a schema
// property silently dropped — fails here, offline, without an API key or a network call.

using System.Text.Json;
using CensusScope.Core.Http;
using CensusScope.Core.Services;

namespace CensusScope.Core.Tests;

public class GeminiServiceTests
{
    private const string TestKey = "AIzaSyTEST-not-a-real-key-000000000000";
    private const string CatalogContext = "datasets: income_households, population_total; levels: CD, CSD";
    private const string UserText = "Quel est le revenu median des divisions de recensement en Ontario?";

    /// <summary>A realistic Gemini envelope whose single candidate part carries <paramref name="text"/>.</summary>
    private static string Envelope(string text) =>
        JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new { role = "model", parts = new[] { new { text } } },
                    finishReason = "STOP",
                },
            },
            usageMetadata = new { promptTokenCount = 120, candidatesTokenCount = 40 },
        });

    private const string IntentJson =
        """{"country":"CA","mode":"compare","datasetKey":"income_households","geoLevel":"CD","geoName":null,"parentGeoName":"Ontario","explanation":"Comparaison des revenus par division de recensement en Ontario."}""";

    private static string TempCacheDir() =>
        Path.Combine(Path.GetTempPath(), "gw-gem-" + Guid.NewGuid().ToString("N"));

    private static GeminiService Service(StubHttpHandler handler, AppSettings? settings = null) =>
        new(new ApiClient(TempCacheDir(), handler, TimeSpan.FromMilliseconds(1)),
            settings ?? new AppSettings { GeminiApiKey = TestKey });

    private static StubHttpHandler Responds(string body) =>
        new(_ => StubHttpHandler.Json(body));

    /// <summary>Runs one successful ParseIntentAsync and returns the captured request body.</summary>
    private static async Task<(StubHttpHandler Handler, string Body)> CaptureIntentRequestAsync()
    {
        var handler = Responds(Envelope(IntentJson));
        await Service(handler).ParseIntentAsync(UserText, CatalogContext);
        Assert.Single(handler.RequestBodies);
        return (handler, handler.RequestBodies[0]);
    }

    private static async Task<(StubHttpHandler Handler, string Body)> CaptureSummarizeRequestAsync(
        string question, string table)
    {
        var handler = Responds(Envelope("Le revenu median le plus eleve est celui de Toronto."));
        await Service(handler).SummarizeAsync(question, table);
        Assert.Single(handler.RequestBodies);
        return (handler, handler.RequestBodies[0]);
    }

    // ---- 1. Request body wire format --------------------------------------------------------

    [Fact]
    public async Task IntentRequest_SystemInstruction_CarriesTheCatalogContext()
    {
        var (_, body) = await CaptureIntentRequestAsync();
        using var doc = JsonDocument.Parse(body);

        var text = doc.RootElement
            .GetProperty("systemInstruction")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        Assert.NotNull(text);
        Assert.Contains(CatalogContext, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntentRequest_Contents_CarryTheUserQuestion()
    {
        var (_, body) = await CaptureIntentRequestAsync();
        using var doc = JsonDocument.Parse(body);

        var contents = doc.RootElement.GetProperty("contents");
        Assert.Equal(JsonValueKind.Array, contents.ValueKind);

        var text = contents[0].GetProperty("parts")[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Contains(UserText, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntentRequest_GenerationConfig_RequestsJsonMimeType()
    {
        var (_, body) = await CaptureIntentRequestAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(
            "application/json",
            doc.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
    }

    [Fact]
    public async Task IntentRequest_Temperature_IsZeroForDeterministicParsing()
    {
        var (_, body) = await CaptureIntentRequestAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(
            0.0,
            doc.RootElement.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task IntentRequest_ResponseSchema_IsAnObjectType()
    {
        var (_, body) = await CaptureIntentRequestAsync();
        using var doc = JsonDocument.Parse(body);

        var type = doc.RootElement
            .GetProperty("generationConfig")
            .GetProperty("responseSchema")
            .GetProperty("type")
            .GetString();

        Assert.NotNull(type);
        Assert.Equal("OBJECT", type, ignoreCase: true);
    }

    [Fact]
    public async Task IntentRequest_ResponseSchema_DeclaresEveryIntentProperty()
    {
        var (_, body) = await CaptureIntentRequestAsync();
        using var doc = JsonDocument.Parse(body);

        var properties = doc.RootElement
            .GetProperty("generationConfig")
            .GetProperty("responseSchema")
            .GetProperty("properties");

        foreach (var name in new[]
                 {
                     "country", "mode", "datasetKey", "geoLevel",
                     "geoName", "parentGeoName", "explanation",
                 })
        {
            Assert.True(
                properties.TryGetProperty(name, out _),
                "responseSchema.properties is missing '" + name + "'");
        }
    }

    [Fact]
    public async Task IntentRequest_ResponseSchema_RequiresTheNonNullableFields()
    {
        var (_, body) = await CaptureIntentRequestAsync();
        using var doc = JsonDocument.Parse(body);

        var required = doc.RootElement
            .GetProperty("generationConfig")
            .GetProperty("responseSchema")
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        foreach (var name in new[] { "country", "mode", "datasetKey", "geoLevel" })
            Assert.Contains(name, required);
    }

    // C# needs `@enum` to escape the keyword, but the JSON key Google expects is `enum`. If the
    // escape ever leaked into the serialized name the schema would be silently ignored.
    [Fact]
    public async Task IntentRequest_ResponseSchema_EmitsEnumKeyWithoutTheCSharpEscape()
    {
        var (_, body) = await CaptureIntentRequestAsync();

        Assert.Contains("\"enum\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@enum", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);
        var properties = doc.RootElement
            .GetProperty("generationConfig")
            .GetProperty("responseSchema")
            .GetProperty("properties");

        var countries = properties.GetProperty("country").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "US", "CA" }, countries);

        var modes = properties.GetProperty("mode").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.NotEmpty(modes);
    }

    // Google's OpenAPI schema subset does not accept `additionalProperties`; a body carrying it
    // is rejected with a 400 at request time.
    [Fact]
    public async Task IntentRequest_NeverEmitsAdditionalProperties()
    {
        var (_, body) = await CaptureIntentRequestAsync();

        Assert.DoesNotContain("additionalProperties", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 2. API key transport ---------------------------------------------------------------

    [Fact]
    public async Task ApiKey_TravelsInTheHeader_AndNeverInTheUrl()
    {
        // StubHttpHandler records URLs and bodies but not headers, so the responder inspects
        // the live request message.
        var apiKeyHeaders = new List<string?>();
        var handler = new StubHttpHandler(req =>
        {
            apiKeyHeaders.Add(
                req.Headers.TryGetValues("x-goog-api-key", out var values)
                    ? string.Join(",", values)
                    : null);
            return StubHttpHandler.Json(Envelope(IntentJson));
        });

        await Service(handler).ParseIntentAsync(UserText, CatalogContext);

        var url = Assert.Single(handler.RequestedUrls);
        Assert.DoesNotContain(TestKey, url, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", url, StringComparison.OrdinalIgnoreCase);

        var sent = Assert.Single(apiKeyHeaders);
        Assert.Equal(TestKey, sent);
    }

    // ---- 3. Model in the URL ----------------------------------------------------------------

    [Fact]
    public async Task RequestUrl_TargetsTheConfiguredModelsGenerateContentEndpoint()
    {
        const string model = "gemini-2.5-pro-exp";
        var handler = Responds(Envelope(IntentJson));
        var service = Service(handler, new AppSettings { GeminiApiKey = TestKey, GeminiModel = model });

        await service.ParseIntentAsync(UserText, CatalogContext);

        var url = Assert.Single(handler.RequestedUrls);
        Assert.Contains("/models/" + model + ":generateContent", url, StringComparison.Ordinal);
    }

    // ---- 4. Happy-path deserialization ------------------------------------------------------

    // The model's answer arrives as a JSON *string* inside the envelope, so the service has to
    // deserialize twice. This proves the inner pass maps every field, nulls included.
    [Fact]
    public async Task ParseIntentAsync_MapsEveryFieldFromTheNestedJsonString()
    {
        var handler = Responds(Envelope(IntentJson));

        var intent = await Service(handler).ParseIntentAsync(UserText, CatalogContext);

        Assert.Equal("CA", intent.Country);
        Assert.Equal("compare", intent.Mode);
        Assert.Equal("income_households", intent.DatasetKey);
        Assert.Equal("CD", intent.GeoLevel);
        Assert.Null(intent.GeoName);
        Assert.Equal("Ontario", intent.ParentGeoName);
        Assert.Equal("Comparaison des revenus par division de recensement en Ontario.", intent.Explanation);
    }

    // ---- 5. Not configured ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutAnApiKey_IsNotConfigured_AndParseThrowsWithoutAnyHttpCall(string? key)
    {
        var handler = Responds(Envelope(IntentJson));
        var service = Service(handler, new AppSettings { GeminiApiKey = key });

        Assert.False(service.IsConfigured);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ParseIntentAsync(UserText, CatalogContext));

        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.RequestedUrls);
    }

    // ---- 6. Safety block / empty candidates --------------------------------------------------

    [Fact]
    public async Task BlockedPrompt_ThrowsAnInformativeException()
    {
        var handler = Responds("""{"promptFeedback":{"blockReason":"SAFETY"}}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(handler).ParseIntentAsync(UserText, CatalogContext));

        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SAFETY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyCandidatesArray_ThrowsAnInformativeException()
    {
        var handler = Responds("""{"candidates":[]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(handler).ParseIntentAsync(UserText, CatalogContext));

        Assert.Contains("candidates", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 7. Missing text part ----------------------------------------------------------------

    // A malformed candidate must not surface as IndexOutOfRange/NullReference from the parser.
    [Fact]
    public async Task CandidateWithNoParts_ThrowsInvalidOperation_NotAnIndexOrNullError()
    {
        var handler = Responds("""{"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"MAX_TOKENS"}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(handler).ParseIntentAsync(UserText, CatalogContext));

        Assert.Contains("no text part", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CandidateWithNoContent_ThrowsInvalidOperation_NotAnIndexOrNullError()
    {
        var handler = Responds("""{"candidates":[{"finishReason":"SAFETY"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(handler).ParseIntentAsync(UserText, CatalogContext));
    }

    // ---- 8. SummarizeAsync -------------------------------------------------------------------

    [Fact]
    public async Task SummarizeAsync_PromptCarriesBothTheQuestionAndTheTable()
    {
        const string question = "Which census division has the highest median income?";
        const string table = "Toronto | 89,000\nOttawa | 91,500\n(note: margins of error apply)";

        var (_, body) = await CaptureSummarizeRequestAsync(question, table);
        using var doc = JsonDocument.Parse(body);

        var text = doc.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[0].GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.Contains(question, text, StringComparison.Ordinal);
        Assert.Contains(table, text, StringComparison.Ordinal);
    }

    // Gemini must never invent figures: every number in the answer has to come from the
    // provider-sourced table, so the prompt says so explicitly.
    [Fact]
    public async Task SummarizeAsync_PromptInstructsGroundingInTheSuppliedNumbers()
    {
        var (_, body) = await CaptureSummarizeRequestAsync("q", "t");
        using var doc = JsonDocument.Parse(body);

        var text = doc.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[0].GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.Contains(
            "Use ONLY numbers that are present in the table below",
            text,
            StringComparison.Ordinal);
        Assert.Contains("never invent or extrapolate figures", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummarizeAsync_SendsFreeFormText_NotAStructuredOutputRequest()
    {
        var (_, body) = await CaptureSummarizeRequestAsync("q", "t");
        using var doc = JsonDocument.Parse(body);

        var config = doc.RootElement.GetProperty("generationConfig");
        Assert.False(config.TryGetProperty("responseMimeType", out _));
        Assert.False(config.TryGetProperty("responseSchema", out _));
        Assert.False(doc.RootElement.TryGetProperty("systemInstruction", out _));
    }

    [Fact]
    public async Task SummarizeAsync_ReturnsTheModelsTextTrimmed()
    {
        var handler = Responds(Envelope("\n  Toronto leads at 89,000. \n"));

        var summary = await Service(handler).SummarizeAsync("q", "t");

        Assert.Equal("Toronto leads at 89,000.", summary);
    }
}
