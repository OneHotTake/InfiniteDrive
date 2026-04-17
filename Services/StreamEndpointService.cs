using System;
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
    [Route("/InfiniteDrive/Stream", "GET", Summary = "Validates signed URL and redirects to upstream CDN")]
    [Unauthenticated]
    public class StreamEndpointRequest : IReturn<object>
    {
        /// <summary>
        /// Signed upstream URL to redirect to.
        /// Format: {url}|{timestamp}|{signature}
        /// </summary>
        public string? Url { get; set; }
    }

    /// <summary>
    /// Stream endpoint: validates the signed URL and returns a 302 redirect
    /// to the upstream CDN. Emby/ffmpeg follow HTTP redirects natively and
    /// download directly from the CDN, supporting Range headers for seeking.
    /// Since ffmpeg runs on the same server as Emby, the CDN sees the server
    /// IP regardless — no separate binary proxy needed.
    /// TODO: Add binary proxy mode for remote clients (real-debrid multi-IP
    /// protection). Requires IHttpResultFactory.GetStaticResult with StreamHandler.
    /// </summary>
    public class StreamEndpointService : IService, IRequiresRequest
    {
        private readonly ILogger<StreamEndpointService> _logger;
        private readonly RateLimiter _rateLimiter;

        public StreamEndpointService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<StreamEndpointService>(logManager.GetLogger("InfiniteDrive"));
            _rateLimiter = new RateLimiter(
                new EmbyLoggerAdapter<RateLimiter>(logManager.GetLogger("InfiniteDrive")),
                Array.Empty<string>());
        }

        private PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        public IRequest Request { get; set; } = null!;

        // ── Main handler ─────────────────────────────────────────────────────

        public Task<object> Get(StreamEndpointRequest req)
        {
            // Rate limit
            var clientIp = RateLimiter.GetClientIp(Request);
            var rateLimitResult = _rateLimiter.CheckStreamLimit(clientIp);
            if (rateLimitResult != null)
                return Task.FromResult(rateLimitResult);

            // Validate PluginSecret
            if (string.IsNullOrEmpty(Config.PluginSecret))
                return Task.FromResult(Error(503, "plugin_not_initialized", "Plugin not initialized"));

            // Decode signed URL
            if (string.IsNullOrEmpty(req.Url))
                return Task.FromResult(Error(400, "bad_request", "url parameter is required"));

            // Emby's framework does partial URL-decoding of query params
            // (decodes %2F, %3A etc.) but leaves %7C (pipe) encoded.
            // Fully decode so the pipe-delimiter split in Verify() works.
            string signedUrl = Uri.UnescapeDataString(req.Url);

            _logger.LogInformation("[Stream] Received signed URL length: {Len}, last 80: ...{Tail}",
                signedUrl.Length,
                signedUrl.Length > 80 ? signedUrl[^80..] : signedUrl);

            if (!PlaybackTokenService.Verify(signedUrl, Config.PluginSecret))
            {
                _logger.LogWarning("[Stream] Signature verification FAILED");
                return Task.FromResult(Error(401, "unauthorized", "Invalid or expired signature"));
            }

            // Extract upstream URL: {url}|{timestamp}|{signature}
            var parts = signedUrl.Split('|');
            if (parts.Length != 3)
                return Task.FromResult(Error(400, "bad_request", "Invalid signed URL format"));

            string upstreamUrl = parts[0];

            if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
                return Task.FromResult(Error(400, "bad_request", "Invalid upstream URL"));

            // 302 redirect to upstream CDN — Emby/ffmpeg follow redirects natively
            _logger.LogInformation("[Stream] Redirecting to upstream CDN: {Host}{Path}",
                uri.Host, uri.AbsolutePath);

            Request.Response.StatusCode = 302;
            Request.Response.AddHeader("Location", upstreamUrl);

            return Task.FromResult<object>(Array.Empty<byte>());
        }

        // ── Error helper ────────────────────────────────────────────────────

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
