using System.Net;
using System.Net.Http;
using System.Text;

namespace CensusScope.App.Demo;

/// <summary>
/// Lets the application run without any API keys (<c>--demo</c> switch).
/// </summary>
/// <remarks>
/// Only the two services that require a key are intercepted: the U.S. Census API and Gemini.
/// Statistics Canada needs no key, so Canadian requests pass through to the real service and
/// demo mode shows genuine published figures for Canada.
/// <para>
/// Requests still flow through the real <c>ApiClient</c> and the real providers, so demo mode
/// exercises the production URL assembly, chunking, parsing and rendering code — only the
/// network response is substituted. U.S. values are SYNTHETIC placeholders shaped per the
/// documented Census contract; the UI labels them so they cannot be mistaken for real
/// statistics.
/// </para>
/// </remarks>
public sealed class DemoHttpHandler : DelegatingHandler
{
    /// <summary>Creates the handler, passing non-intercepted traffic to the real network.</summary>
    public DemoHttpHandler()
        : base(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        })
    {
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";

        if (url.Contains("api.census.gov", StringComparison.OrdinalIgnoreCase))
            return Ok(DemoData.Census(url), "application/json");

        if (url.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Ok(DemoData.Gemini(body), "application/json");
        }

        // Statistics Canada and anything else: real network, real data.
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpResponseMessage Ok(string body, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}
