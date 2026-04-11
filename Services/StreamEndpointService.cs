using System;
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
    public class StreamEndpointRequest : IReturn<object>
    {
        /// <summary>
        /// Short-lived stream token for authenticating requests.
        /// Format: {quality}:{imdbId}:{exp}:{signature}
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
        private readonly PluginConfiguration _config;

        public StreamEndpointService(ILogManager logManager, PluginConfiguration config)
        {
            _logger = new EmbyLoggerAdapter<StreamEndpointService>(logManager.GetLogger("InfiniteDrive"));
            _config = config;
        }

        // ── IService ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => "InfiniteDrive Stream Endpoint";

        /// <inheritdoc/>
        public string Key => "InfiniteDriveStream";

        // ── IRequiresRequest ──────────────────────────────────────────────────

        /// <inheritdoc/>
        public IRequest Request { get; set; }

        /// <inheritdoc/>
        public IResponse Response => Request?.Response;

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
            // 1. Validate token
            if (string.IsNullOrEmpty(_config.PluginSecret))
            {
                return Error(500, "server_error", "Plugin not initialized");
            }

            if (!PlaybackTokenService.ValidateStreamToken(req.Token, _config.PluginSecret))
            {
                _logger.LogWarning("[InfiniteDrive][Stream] Invalid or expired stream token");
                return Error(401, "unauthorized", "Invalid or expired token");
            }

            // 2. Decode upstream URL
            if (string.IsNullOrEmpty(req.Url))
            {
                return Error(400, "bad_request", "url parameter is required");
            }

            string upstreamUrl;
            try { upstreamUrl = Uri.UnescapeDataString(req.Url); }
            catch { return Error(400, "bad_request", "Invalid URL encoding"); }

            if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return Error(400, "bad_request", "Invalid upstream URL");
            }

            // 3. Determine content type (M3U8 playlist vs binary stream)
            bool isM3U8 = IsM3U8Request(upstreamUrl);

            if (isM3U8)
            {
                // 4a. Fetch upstream M3U8
                string upstreamContent;
                try
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    using var response = await httpClient.GetAsync(upstreamUrl);
                    response.EnsureSuccessStatusCode();
                    upstreamContent = await response.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive][Stream] Failed to fetch upstream M3U8: " + upstreamUrl);
                    return Error(502, "upstream_error", "Failed to fetch upstream manifest");
                }

                // 4b. Rewrite HLS URLs and return
                string? upstreamBaseUrl = GetBaseUrl(upstreamUrl);
                string rewritten = RewriteHlsUrls(upstreamContent, upstreamBaseUrl, _config.EmbyBaseUrl, _config.PluginSecret);

                return new
                {
                    ContentType = "application/vnd.apple.mpegurl",
                    Content = System.Text.Encoding.UTF8.GetBytes(rewritten),
                    StatusCode = 200
                };
            }

            // 5. Binary streaming (redirect to CDN)
            _logger.LogInformation("[InfiniteDrive][Stream] Redirecting to CDN: " + upstreamUrl);
            Request.Response.Redirect(upstreamUrl);
            return new
            {
                ContentType = "application/vnd.apple.mpegurl",
                Content = Array.Empty<byte>(),
                StatusCode = 302
            };
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
        /// Format: {embyBaseUrl}/InfiniteDrive/stream?url={signedUrl}
        /// where signedUrl = {url}|{timestamp}|{signature}
        /// </summary>
        private static string BuildProxyUrl(string embyBaseUrl, string secret, string upstreamUrl)
        {
            // Sign the upstream URL with timestamp and HMAC signature
            var signedUrl = PlaybackTokenService.Sign(upstreamUrl, secret, 1); // 1 hour expiry

            return $"{embyBaseUrl.TrimEnd('/')}/InfiniteDrive/stream?url={Uri.EscapeDataString(signedUrl)}";
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
    }
}
