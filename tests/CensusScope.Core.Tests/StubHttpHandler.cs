using System.Net;
using System.Text;

namespace CensusScope.Core.Tests;

/// <summary>
/// Offline test seam for <see cref="CensusScope.Core.Http.ApiClient"/>: answers every request from a
/// caller-supplied function instead of the network, and records what was asked for so a test can
/// assert on the URLs the code under test assembled.
/// </summary>
/// <remarks>
/// The responder is invoked once per request and must return a FRESH <see cref="HttpResponseMessage"/>
/// each time — ApiClient disposes the response (and therefore its content stream) it receives.
/// </remarks>
public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly Lock _gate = new();

    /// <summary>Absolute URI of every request that reached this handler, in order.</summary>
    public List<string> RequestedUrls { get; } = [];

    /// <summary>Body of every POST that reached this handler, in order.</summary>
    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Method == HttpMethod.Post && request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            RequestedUrls.Add(request.RequestUri?.AbsoluteUri ?? "");
            if (body is not null)
                RequestBodies.Add(body);
        }

        var response = responder(request);
        response.RequestMessage ??= request;
        return response;
    }

    /// <summary>A 200 carrying <paramref name="body"/> as application/json.</summary>
    public static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// A 302 whose Location is <paramref name="location"/> — how the Census API answers a key
    /// problem (it redirects to an HTML page instead of returning 401).
    /// </summary>
    public static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(location) },
            Content = new StringContent("<html>redirecting</html>", Encoding.UTF8, "text/html"),
        };
}
