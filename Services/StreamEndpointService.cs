using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    // ── Request DTO ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stream endpoint request model.
    /// </summary>
    [Route("/InfiniteDrive/Stream", "GET", Summary = "Proxies HLS manifests and redirects to CDN")]
    [Unauthenticated]
    public class StreamEndpointRequest : IReturn<object>
    {
        /// <summary>
        /// Short-lived stream token for authenticating requests.
        /// Format: {url}|{timestamp}|{signature}
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Optional upstream URL to proxy directly without token validation.
        /// Used for CDN redirection or testing.
        /// </summary>
        public string? Url { get; set; }
    }

    /// <summary>
    /// Proxy endpoint for HLS manifests and stream content.
    /// Rewrites relative URLs in HLS manifests to /InfiniteDrive/Stream
    /// with individual signed tokens (1-hour expiry).
    /// </summary>
    public class StreamEndpointService : IService, IRequiresRequest
    {
        private readonly ILogger<StreamEndpointService> _logger;
        private readonly RateLimiter _rateLimiter;

        public StreamEndpointService(ILogManager logManager, ILogger<RateLimiter> rateLimiterLogger)
        {
            _logger = new EmbyLoggerAdapter<StreamEndpointService>(logManager.GetLogger("InfiniteDrive"));
            _rateLimiter = new RateLimiter(rateLimiterLogger, Array.Empty<string>());
        }

        private PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        /// <inheritdoc/>
        public IResponse Response => Request?.Response!;

        // ── Main handler ─────────────────────────────────────────────────────

        /// <summary>
        /// Handles GET/HEAD for /InfiniteDrive/Stream endpoint.
        /// </summary>
        public async Task<object> Get(StreamEndpointRequest req)
        {
            return await HandleAsync(req, false);
        }

        /// <summary>
        /// Handles HEAD request.
        /// </summary>
        public async Task<object> Head(StreamEndpointRequest req)
        {
            return await HandleAsync(req, true);
        }

        /// <summary>
        /// Main request handler.
        /// </summary>
        private async Task<object> HandleAsync(StreamEndpointRequest req, bool isHead)
        {
            // Sprint 302-05: Rate limit check
            var clientIp = RateLimiter.GetClientIp(Request);
            var rateLimitResult = _rateLimiter.CheckStreamLimit(clientIp);
            if (rateLimitResult != null)
                return rateLimitResult;

            // 1. Validate PluginSecret
            if (string.IsNullOrEmpty(Config.PluginSecret))
            {
                return Error(503, "plugin_not_initialized", "Plugin not initialized");
            }

            // 2. Decode and validate signed URL
            if (string.IsNullOrEmpty(req.Url))
            {
                return Error(400, "bad_request", "url parameter is required");
            }

            string signedUrl;
            try { signedUrl = Uri.UnescapeDataString(req.Url); }
            catch { return Error(400, "bad_request", "Invalid URL encoding"); }

            // Validate signature and extract upstream URL
            if (!PlaybackTokenService.Verify(signedUrl, Config.PluginSecret))
            {
                _logger.LogWarning("[InfiniteDrive][Stream] Invalid or expired stream signature");
                return Error(401, "unauthorized", "Invalid or expired signature");
            }

            // Extract upstream URL from signed format: {url}|{timestamp}|{signature}
            var parts = signedUrl.Split('|');
            if (parts.Length != 3)
            {
                return Error(400, "bad_request", "Invalid signed URL format");
            }

            string upstreamUrl = parts[0];

            if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return Error(400, "bad_request", "Invalid upstream URL");
            }

            // 3. Determine content type (M3U8 playlist vs binary stream)
            bool isM3U8 = IsM3U8Request(upstreamUrl);

            if (isM3U8)
            {
                // 4a. Fetch upstream M3U8 with retry on transient failures
                string upstreamContent = "";
                bool fetched = false;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(attempt == 0 ? 30 : 15) };
                        using var response = await httpClient.GetAsync(upstreamUrl);
                        response.EnsureSuccessStatusCode();
                        upstreamContent = await response.Content.ReadAsStringAsync();
                        fetched = true;
                        break;
                    }
                    catch (HttpRequestException ex) when (attempt == 0)
                    {
                        _logger.LogWarning(ex, "[Stream] M3U8 fetch attempt {Attempt} failed for {Url}, retrying", attempt + 1, upstreamUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Stream] M3U8 fetch failed for {Url}", upstreamUrl);
                    }
                }

                if (!fetched)
                {
                    _logger.LogWarning("[Stream] All M3U8 fetch attempts failed for {Url}", upstreamUrl);
                    return Error(502, "upstream_unavailable", "Stream temporarily unavailable - try again");
                }

                // 4b. Rewrite HLS URLs and return
                string? upstreamBaseUrl = GetBaseUrl(upstreamUrl);
                string rewritten = RewriteHlsUrls(upstreamContent, upstreamBaseUrl, Config.EmbyBaseUrl, Config.PluginSecret);

                return new
                {
                    ContentType = "application/vnd.apple.mpegurl",
                    Content = System.Text.Encoding.UTF8.GetBytes(rewritten),
                    StatusCode = 200
                };
            }

            // 5. Binary streaming — proxy through server to hide client IP from debrid providers
            _logger.LogInformation("[InfiniteDrive][Stream] Proxying binary stream: " + upstreamUrl);
            try
            {
                var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

                // Forward Range header for seeking support
                var rangeHeader = Request.Headers["Range"];
                if (!string.IsNullOrEmpty(rangeHeader))
                    httpClient.DefaultRequestHeaders.Add("Range", rangeHeader);

                var upstreamResp = await httpClient.GetAsync(upstreamUrl, HttpCompletionOption.ResponseHeadersRead);

                if (!upstreamResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Stream] Upstream returned {Status} for {Url}", (int)upstreamResp.StatusCode, upstreamUrl);
                    upstreamResp.Dispose();
                    httpClient.Dispose();
                    return Error(502, "upstream_error", $"Upstream returned {(int)upstreamResp.StatusCode}");
                }

                var contentType = upstreamResp.Content.Headers.ContentType?.MediaType ?? "video/mp4";
                var contentLength = upstreamResp.Content.Headers.ContentLength;

                Request.Response.ContentType = contentType;
                Request.Response.StatusCode = (int)upstreamResp.StatusCode;

                if (contentLength.HasValue)
                    Request.Response.AddHeader("Content-Length", contentLength.Value.ToString());

                if (upstreamResp.Content.Headers.ContentRange != null)
                    Request.Response.AddHeader("Content-Range", upstreamResp.Content.Headers.ContentRange.ToString());

                var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync();

                // Wrap stream so HttpClient/HttpResponseMessage are disposed after streaming completes
                return new ProxyStream(httpClient, upstreamResp, upstreamStream);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Stream] Binary proxy failed for {Url}", upstreamUrl);
                return Error(502, "upstream_unavailable", "Stream temporarily unavailable - try again");
            }
        }

        // ── HLS manifest rewriting ───────────────────────────────────────

        /// <summary>
        /// Rewrites relative URLs in HLS manifests to /InfiniteDrive/Stream
        /// Each rewritten URL gets a fresh short-lived stream token.
        /// </summary>
        private string RewriteHlsUrls(string hlsContent, string? upstreamBaseUrl, string embyBaseUrl, string secret)
        {
            if (string.IsNullOrEmpty(hlsContent)) return hlsContent;

            var baseUri = upstreamBaseUrl != null
                ? new Uri(upstreamBaseUrl)
                : null;

            // Match URLs that are NOT already absolute http(s) or comment lines
            var lines = hlsContent.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim('\r');
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                // Already absolute URL — rewrite to proxy through us
                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = BuildProxyUrl(embyBaseUrl, secret, line);
                    continue;
                }

                // Relative URL — resolve against upstream base
                if (baseUri != null)
                {
                    try
                    {
                        var absolute = new Uri(baseUri, line).ToString();
                        lines[i] = BuildProxyUrl(embyBaseUrl, secret, absolute);
                    }
                    catch
                    {
                        // If resolution fails, leave as-is
                    }
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Builds a proxy URL through /InfiniteDrive/Stream with a signed token.
        /// Format: {embyBaseUrl}/InfiniteDrive/Stream?url={signedUrl}
        /// where signedUrl = {url}|{timestamp}|{signature}
        /// </summary>
        private static string BuildProxyUrl(string embyBaseUrl, string secret, string upstreamUrl)
        {
            // Sign the upstream URL with timestamp and HMAC signature
            var signedUrl = PlaybackTokenService.Sign(upstreamUrl, secret, 1); // 1 hour expiry

            return $"{embyBaseUrl.TrimEnd('/')}/InfiniteDrive/Stream?url={Uri.EscapeDataString(signedUrl)}";
        }

        /// <summary>
        /// Determines if request is for an M3U8 playlist or binary stream.
        /// </summary>
        private static bool IsM3U8Request(string url)
        {
            return url.Contains(".m3u8") ||
                   url.EndsWith(".ts") || url.EndsWith(".mpd") ||
                   url.EndsWith(".webm");
        }

        /// <summary>
        /// Extracts base URL from an upstream URL for resolving relative paths.
        /// Returns null if URL is already absolute.
        /// </summary>
        private static string? GetBaseUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Host}";
            }
            return null;
        }

        /// <summary>
        /// Error response helper.
        /// </summary>
        private static object Error(int statusCode, string errorCode, string message)
        {
            return new
            {
                StatusCode = statusCode,
                ErrorCode = errorCode,
                ErrorMessage = message
            };
        }

        /// <summary>
        /// Wrapper stream that owns the HttpClient and HttpResponseMessage.
        /// Ensures they are disposed after the framework finishes reading the stream.
        /// </summary>
        private sealed class ProxyStream : Stream
        {
            private readonly HttpClient _client;
            private readonly HttpResponseMessage _response;
            private readonly Stream _inner;
            private bool _disposed;

            public ProxyStream(HttpClient client, HttpResponseMessage response, Stream inner)
            {
                _client = client;
                _response = response;
                _inner = inner;
            }

            public override bool CanRead => !_disposed && _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
                => _inner.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    try { _inner.Dispose(); } catch { }
                    try { _response.Dispose(); } catch { }
                    try { _client.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }
        }
    }
}
