using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    // ── Request DTO ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Request for <c>GET /EmbyStreams/Stream</c> — the HMAC-signed public stream endpoint.
    ///
    /// This route has NO Emby authentication requirement.  Security is provided by the
    /// HMAC-SHA256 signature embedded in the URL via <see cref="StreamUrlSigner"/>.
    ///
    /// This allows bare HTTP clients (VLC, ffmpeg, Roku, Apple TV, web browsers)
    /// to play streams directly from .strm files without sending X-Emby-Token headers.
    /// </summary>
    [Route("/EmbyStreams/Stream", "GET",
        Summary = "Resolve and redirect to stream URL via HMAC-signed token (no Emby auth required)")]
    public class SignedStreamRequest : IReturn<object>
    {
        /// <summary>IMDB ID (e.g. tt0133093).</summary>
        [ApiMember(Name = "id", Description = "IMDB ID", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Id { get; set; } = string.Empty;

        /// <summary>Media type: "movie" or "series".</summary>
        [ApiMember(Name = "type", Description = "Media type (movie or series)", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Type { get; set; } = string.Empty;

        /// <summary>Unix timestamp after which the signature is invalid.</summary>
        [ApiMember(Name = "exp", Description = "Expiry timestamp (Unix seconds)", IsRequired = true, DataType = "long", ParameterType = "query")]
        public long Exp { get; set; }

        /// <summary>HMAC-SHA256 signature (lowercase hex).</summary>
        [ApiMember(Name = "sig", Description = "HMAC-SHA256 signature", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Sig { get; set; } = string.Empty;

        /// <summary>Season number for series (omit for movies).</summary>
        [ApiMember(Name = "season", Description = "Season number (series only)", DataType = "int", ParameterType = "query")]
        public int? Season { get; set; }

        /// <summary>Episode number for series (omit for movies).</summary>
        [ApiMember(Name = "episode", Description = "Episode number (series only)", DataType = "int", ParameterType = "query")]
        public int? Episode { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Public stream endpoint secured by HMAC-SHA256 signatures embedded in .strm files.
    ///
    /// This endpoint accepts requests WITHOUT Emby authentication. Security is provided by
    /// time-limited HMAC-SHA256 signatures computed from the PluginSecret.
    ///
    /// This allows:
    /// - Emby server itself to request stream URLs (server-to-server)
    /// - Non-Emby clients (VLC, ffmpeg, Roku, Apple TV) to play .strm files directly
    /// - Bare HTTP requests without X-Emby-Token headers
    ///
    /// Flow:
    ///   1. Parse and validate required parameters (id, type, exp, sig)
    ///   2. Validate HMAC signature and check expiry via <see cref="StreamUrlSigner"/>
    ///   3. Resolve the real stream URL via <see cref="StreamResolver"/>
    ///   4. HTTP 302 redirect to the resolved CDN/debrid URL
    ///
    /// On success:  HTTP 302 with Location header pointing to the real stream.
    /// On bad sig:  HTTP 403 with {"error": "invalid_signature"} or "signature_expired"
    /// On no stream: HTTP 502 with {"error": "no_stream_available"}
    /// </summary>
    [Unauthenticated]
    public class SignedStreamService : IService, IRequiresRequest
    {
        private readonly ILogger<SignedStreamService> _logger;
        private readonly DatabaseManager _db;

        public IRequest Request { get; set; } = null!;

        public SignedStreamService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<SignedStreamService>(logManager.GetLogger("EmbyStreams"));
            _db = Plugin.Instance?.DatabaseManager
                ?? throw new InvalidOperationException("DatabaseManager not initialized");
        }

        /// <summary>
        /// Handles <c>GET /EmbyStreams/Stream?id=tt...&amp;type=movie&amp;exp=...&amp;sig=...</c>.
        /// </summary>
        public async Task<object> Get(SignedStreamRequest req)
        {
            // Ensure PluginSecret is initialized before accessing Configuration
            Plugin.Instance?.EnsureInitialization();

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                Request.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return new { error = "plugin_not_initialized" };
            }

            // ── 1. Validate required parameters ────────────────────────────────

            if (string.IsNullOrWhiteSpace(req.Id)
                || string.IsNullOrWhiteSpace(req.Type)
                || string.IsNullOrWhiteSpace(req.Sig)
                || req.Exp == 0)
            {
                Request.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return new { error = "missing_parameters",
                             message = "id, type, exp, and sig are all required" };
            }

            // ── 2. Validate HMAC signature ──────────────────────────────────────

            var secret = config.PluginSecret;
            if (string.IsNullOrEmpty(secret))
            {
                _logger.LogError("[EmbyStreams][Stream] PluginSecret is not set — stream signing is non-functional");
                Request.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return new { error = "secret_not_configured",
                             message = "Plugin secret is not configured. Regenerate it from the plugin settings page." };
            }

            // Check expiry separately so we can give a useful error message
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (nowUnix > req.Exp)
            {
                _logger.LogWarning("[EmbyStreams][Stream] Expired signature for {Id} (exp={Exp}, now={Now})",
                    req.Id, req.Exp, nowUnix);
                Request.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return new { error = "signature_expired",
                             message = "The stream URL has expired. Re-sync the library to regenerate .strm files." };
            }

            if (!StreamUrlSigner.ValidateSignature(
                    req.Id, req.Type, req.Season, req.Episode, req.Exp, req.Sig, secret))
            {
                _logger.LogWarning("[EmbyStreams][Stream] Invalid signature for {Id}", req.Id);
                Request.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return new { error = "invalid_signature" };
            }

            _logger.LogDebug("[EmbyStreams][Stream] Valid signature — resolving {Id} ({Type}) S{Season}E{Episode}",
                req.Id, req.Type, req.Season ?? 0, req.Episode ?? 0);

            // ── 3. Resolve stream URL ───────────────────────────────────────────

            string? streamUrl = null;
            try
            {
                streamUrl = await StreamResolutionHelper.GetStreamUrlAsync(
                    req.Id,
                    req.Season,
                    req.Episode,
                    config,
                    _db,
                    _logger,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams][Stream] Resolution exception for {Id}", req.Id);
            }

            // ── 4. Handle resolution failure ────────────────────────────────────

            if (string.IsNullOrEmpty(streamUrl))
            {
                _logger.LogWarning("[EmbyStreams][Stream] No stream available for {Id}", req.Id);
                Request.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                return new { error = "no_stream_available", imdb = req.Id };
            }

            // ── 5. 302 Redirect to real CDN/debrid URL ──────────────────────────

            _logger.LogInformation("[EmbyStreams][Stream] Redirecting {Id} to resolved stream", req.Id);
            Request.Response.AddHeader("Cache-Control", "no-store");
            Request.Response.AddHeader("Access-Control-Allow-Origin", "*");
            Request.Response.Redirect(streamUrl);
            return null!;
        }
    }
}
