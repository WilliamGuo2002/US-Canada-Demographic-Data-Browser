using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CensusScope.Core.Http;

/// <summary>
/// An HTTP failure carrying the status code and redirect target, so a provider can translate
/// a service-specific condition into an actionable message (e.g. the Census Bureau redirects
/// key errors to missing_key.html / invalid_key.html instead of returning 401).
/// </summary>
public sealed class ApiHttpException(string message, HttpStatusCode status, Uri? location)
    : HttpRequestException(message)
{
    /// <summary>Status code of the failing response.</summary>
    public HttpStatusCode Status { get; } = status;

    /// <summary>The Location header target when <see cref="Status"/> is a redirect, else null.</summary>
    public Uri? Location { get; } = location;
}

/// <summary>HTTP helper with exponential-backoff retry and an on-disk response cache.</summary>
public sealed partial class ApiClient
{
    /// <summary>Shared production client; a per-instance one is used only when a handler is injected.</summary>
    private static readonly HttpClient SharedClient = CreateClient(null);

    private readonly HttpClient _client;
    private readonly string _cacheDir;
    private readonly TimeSpan _retryBackoff;

    /// <summary>
    /// Creates the client.
    /// </summary>
    /// <param name="cacheDir">Response cache directory; defaults to %LOCALAPPDATA%\CensusScope\cache.</param>
    /// <param name="handler">
    /// Test/offline seam. When supplied, requests go through this handler instead of the network,
    /// which lets the provider code (URL assembly, chunking, parsing, retry, redaction) run
    /// end-to-end without an API key. Production passes null.
    /// </param>
    /// <param name="retryBackoff">Base retry delay; shortened by tests. Defaults to 2 seconds.</param>
    public ApiClient(string? cacheDir = null, HttpMessageHandler? handler = null, TimeSpan? retryBackoff = null)
    {
        _client = handler is null ? SharedClient : CreateClient(handler);
        _retryBackoff = retryBackoff ?? TimeSpan.FromSeconds(2);
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CensusScope", "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var c = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Both APIs return data directly; a redirect means something is wrong (the Census
            // API redirects key errors to an HTML page, which would otherwise be parsed as JSON).
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CensusScope/1.0");
        return c;
    }

    /// <summary>GET a URL as text. When cacheTtl is set, a fresh-enough cached copy is returned instead of hitting the network.</summary>
    public async Task<string> GetStringAsync(string url, TimeSpan? cacheTtl = null, CancellationToken ct = default)
    {
        string? cachePath = null;
        if (cacheTtl is { } ttl)
        {
            cachePath = Path.Combine(_cacheDir, Sha256(url) + ".cache");
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < ttl)
                return await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
        }

        var body = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);

        if (cachePath is not null)
            await WriteCacheAtomicAsync(cachePath, writer => File.WriteAllTextAsync(writer, body, CancellationToken.None))
                .ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// Removes the cached response for a URL, so the next call re-fetches. Used when a
    /// caller discovers a cached body is unusable (e.g. an upstream service returned an
    /// error payload with HTTP 200, which the transport layer cannot detect).
    /// </summary>
    public void InvalidateCached(string url)
    {
        foreach (var extension in new[] { ".cache", ".bin" })
        {
            try { File.Delete(Path.Combine(_cacheDir, Sha256(url) + extension)); }
            catch (IOException) { /* nothing to invalidate */ }
        }
    }

    /// <summary>
    /// Writes a cache file via a temp file + atomic rename, deliberately NOT observing the
    /// caller's cancellation: a half-written cache entry would be served for the full TTL.
    /// Failures are swallowed — caching is an optimization, never a correctness requirement.
    /// </summary>
    private static async Task WriteCacheAtomicAsync(string cachePath, Func<string, Task> writeTo)
    {
        var tmpPath = cachePath + ".tmp";
        try
        {
            await writeTo(tmpPath).ConfigureAwait(false);
            File.Move(tmpPath, cachePath, overwrite: true);
        }
        catch (IOException)
        {
            try { File.Delete(tmpPath); } catch (IOException) { }
        }
    }

    /// <summary>GET a URL as raw bytes. When cacheTtl is set, a fresh-enough cached copy is returned instead of hitting the network.</summary>
    public async Task<byte[]> GetBytesAsync(string url, TimeSpan? cacheTtl = null, CancellationToken ct = default)
    {
        string? cachePath = null;
        if (cacheTtl is { } ttl)
        {
            cachePath = Path.Combine(_cacheDir, Sha256(url) + ".bin");
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < ttl)
                return await File.ReadAllBytesAsync(cachePath, ct).ConfigureAwait(false);
        }

        // Same retry loop as SendWithRetryAsync, but reading the body as bytes.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                if (!retryable || attempt == maxAttempts)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        var location = resp.Headers.Location;
                        var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        throw new ApiHttpException(
                            "HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase + " for "
                            + Redact(req.RequestUri)
                            + (location is null ? "" : " -> " + location)
                            + (errBody.Length == 0 ? "" : "\n" + Truncate(errBody, 500)),
                            resp.StatusCode, location);
                    }
                    var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (cachePath is not null)
                        await WriteCacheAtomicAsync(cachePath, writer => File.WriteAllBytesAsync(writer, body, CancellationToken.None))
                            .ConfigureAwait(false);
                    return body;
                }
            }
            catch (HttpRequestException ex) when (ex is not ApiHttpException && attempt < maxAttempts)
            {
                // transient network failure - fall through to backoff
            }
            catch (TaskCanceledException) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                // HttpClient timeout (not a user cancellation) - retry
            }
            await Task.Delay(_retryBackoff * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
        }
    }

    public Task<string> PostJsonAsync(string url, string jsonBody, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            if (headers is not null)
                foreach (var (k, v) in headers)
                    req.Headers.TryAddWithoutValidation(k, v);
            return req;
        }, ct);

    private async Task<string> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = requestFactory();
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                if (!retryable || attempt == maxAttempts)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        var location = resp.Headers.Location;
                        var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        throw new ApiHttpException(
                            "HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase + " for "
                            + Redact(req.RequestUri)
                            + (location is null ? "" : " -> " + location)
                            + (errBody.Length == 0 ? "" : "\n" + Truncate(errBody, 500)),
                            resp.StatusCode, location);
                    }
                    return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
            }
            // ApiHttpException is raised inside the try above for a decided, non-retryable
            // failure; since it derives from HttpRequestException it must be excluded here,
            // or a 302/4xx would be re-sent until the attempts run out.
            catch (HttpRequestException ex) when (ex is not ApiHttpException && attempt < maxAttempts)
            {
                // transient network failure - fall through to backoff
            }
            catch (TaskCanceledException) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                // HttpClient timeout (not a user cancellation) - retry
            }
            await Task.Delay(_retryBackoff * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Strips credential-bearing query parameters from a URL before it reaches an error
    /// message. The Census API takes its key in the query string, and error messages are
    /// surfaced in the UI status bar — an unredacted URL would disclose the user's key.
    /// </summary>
    private static string Redact(Uri? uri)
    {
        if (uri is null) return "(unknown URL)";
        var url = uri.ToString();
        return SecretParam().Replace(url, "$1=***");
    }

    [GeneratedRegex(@"(?<=[?&])(key|api_key|apikey|token|access_token)=[^&]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretParam();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32].ToLowerInvariant();
}
