using CensusScope.Core.Http;
using CensusScope.Core.Models;
using CensusScope.Core.Providers;
using CensusScope.Core.Services;

namespace CensusScope.Core.Tests;

public class GeoResolverTests
{
    private static GeoUnit Unit(string name) => new(name, name, "test", null);

    [Fact]
    public void Normalize_StripsAccentsAndCase()
    {
        Assert.Equal(GeoResolver.Normalize("montreal"), GeoResolver.Normalize("Montréal"));
    }

    [Fact]
    public void Normalize_CollapsesInternalWhitespaceAndTrims()
    {
        Assert.Equal("new york", GeoResolver.Normalize("  New   York  "));
    }

    [Fact]
    public void Resolve_ExactMatch_BeatsPartialMatches()
    {
        var units = new[] { Unit("New York"), Unit("York"), Unit("East York") };

        var resolved = GeoResolver.Resolve(units, "york");

        Assert.NotNull(resolved);
        Assert.Equal("York", resolved.Name);
    }

    [Fact]
    public void Resolve_UniquePrefixMatch_Works()
    {
        var units = new[] { Unit("Toronto"), Unit("Ottawa"), Unit("Hamilton") };

        var resolved = GeoResolver.Resolve(units, "Tor");

        Assert.NotNull(resolved);
        Assert.Equal("Toronto", resolved.Name);
    }

    [Fact]
    public void Resolve_AmbiguousContains_PicksShortestName()
    {
        var units = new[] { Unit("Greater New York"), Unit("East New York") };

        var resolved = GeoResolver.Resolve(units, "new york");

        Assert.NotNull(resolved);
        Assert.Equal("East New York", resolved.Name);
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNull()
    {
        var units = new[] { Unit("Toronto"), Unit("Ottawa") };

        Assert.Null(GeoResolver.Resolve(units, "zzz"));
    }

    [Fact]
    public void Resolve_AccentInsensitive_MatchesAccentedUnit()
    {
        var units = new[] { Unit("Montréal"), Unit("Toronto") };

        var resolved = GeoResolver.Resolve(units, "montreal");

        Assert.NotNull(resolved);
        Assert.Equal("Montréal", resolved.Name);
    }
}

public class GeoResolverPunctuationTests
{
    [Fact]
    public void Normalize_FoldsSmartApostropheToAscii()
    {
        Assert.Equal(GeoResolver.Normalize("St. John's"), GeoResolver.Normalize("St. John’s"));
    }

    [Fact]
    public void Resolve_MatchesHyphenatedNameWhenTypedWithSpaces()
    {
        var units = new List<GeoUnit>
        {
            new("1", "Trois-Rivières", "CSD", null),
            new("2", "Montréal", "CSD", null),
        };
        var hit = GeoResolver.Resolve(units, "Trois Rivieres");
        Assert.NotNull(hit);
        Assert.Equal("1", hit!.Id);
    }
}

// ApiClient-level behaviour, exercised offline through an injected handler: these were the
// last tests in the suite that needed a live network connection.
public class ApiClientTests
{
    private const string Secret = "abcdef0123456789abcdef0123456789abcdef01";
    private const string CensusUrl =
        "https://api.census.gov/data/2024/acs/acs5?get=NAME&for=state:06&key=" + Secret;

    private static ApiClient NewClient(StubHttpHandler handler) =>
        new(Path.Combine(Path.GetTempPath(), "gw-api-" + Guid.NewGuid().ToString("N")),
            handler, TimeSpan.FromMilliseconds(1));

    // The Census API takes its key in the query string and error messages reach the UI status
    // bar, so the key must never survive into an exception message.
    [Fact]
    public async Task ErrorMessage_DoesNotContainTheApiKey()
    {
        var handler = new StubHttpHandler(_ =>
            StubHttpHandler.Redirect("https://api.census.gov/data/invalid_key.html"));

        var ex = await Assert.ThrowsAsync<ApiHttpException>(
            () => NewClient(handler).GetStringAsync(CensusUrl));

        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.Contains("key=***", ex.Message, StringComparison.Ordinal);
    }

    // ApiHttpException derives from HttpRequestException, so without an explicit exclusion the
    // retry loop swallows the hard failure it just raised and re-sends a decided 3xx/4xx.
    [Fact]
    public async Task NonRetryableFailure_IsSentExactlyOnce()
    {
        var handler = new StubHttpHandler(_ =>
            StubHttpHandler.Redirect("https://api.census.gov/data/invalid_key.html"));

        await Assert.ThrowsAsync<ApiHttpException>(() => NewClient(handler).GetStringAsync(CensusUrl));

        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task ServerError_IsRetriedUpToThreeTimes()
    {
        var handler = new StubHttpHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("busy"),
            });

        await Assert.ThrowsAsync<ApiHttpException>(() => NewClient(handler).GetStringAsync(CensusUrl));

        Assert.Equal(3, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task SuccessfulResponse_IsCachedAndNotRefetched()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""[["NAME"],["California"]]"""));
        var client = NewClient(handler);

        var first = await client.GetStringAsync(CensusUrl, TimeSpan.FromMinutes(5));
        var second = await client.GetStringAsync(CensusUrl, TimeSpan.FromMinutes(5));

        Assert.Equal(first, second);
        Assert.Single(handler.RequestedUrls);
    }
}
