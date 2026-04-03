using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    // ── Request DTO ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Request object for <c>GET /EmbyStreams/Stream/{ProxyId}</c>.
    /// The ProxyId is a short-lived token created by <see cref="PlaybackService"/>.
    /// </summary>
    [Route("/EmbyStreams/Stream/{ProxyId}", "GET,HEAD", Summary = "Passthrough proxy for a resolved stream URL")]
    public class StreamProxyRequest : IReturn<object>
    {
        /// <summary>32-char hex token identifying the proxy session.</summary>
        [ApiMember(Name = "ProxyId", Description = "Proxy session token", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string ProxyId { get; set; } = string.Empty;
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Passthrough proxy that streams bytes from a Real-Debrid (or other debrid)
    /// CDN URL to the requesting Emby client.
    ///
    /// Features:
    /// <list type="bullet">
    ///   <item>Range request support — seeks in large video files work correctly</item>
    ///   <item>Automatic fallback on 403 / 404 / 410 upstream responses</item>
    ///   <item>HEAD request support (Emby clients probe content-length before seeking)</item>
    ///   <item>Transparent header forwarding (Content-Type, Content-Length, etc.)</item>
    /// </list>
    ///
    /// Proxy sessions are stored in <see cref="ProxySessionStore"/> with a 4-hour TTL.
    /// Each proxied stream uses ~256 KB RAM (buffer size).
    /// </summary>
    public class StreamProxyService : IService, IRequiresRequest
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string UserAgent = "EmbyStreams/1.0.0";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<StreamProxyService> _logger;

        // ── IRequiresRequest ─────────────────────────────────────────────────────

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="loggerFactory"/> automatically.
        /// </summary>
        public StreamProxyService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<StreamProxyService>(logManager.GetLogger("EmbyStreams"));
        }

        // ── IService ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles <c>GET /EmbyStreams/Stream/{ProxyId}</c>.
        /// Streams the upstream content to the Emby client with Range support.
        /// </summary>
        public Task<object> Get(StreamProxyRequest req)
            => HandleAsync(req, isHead: false);

        /// <summary>
        /// Handles <c>HEAD /EmbyStreams/Stream/{ProxyId}</c>.
        /// Returns headers without body (used by clients for content-length probing).
        /// </summary>
        public Task<object> Head(StreamProxyRequest req)
            => HandleAsync(req, isHead: true);

        // ── Private: main handler ────────────────────────────────────────────────

        private async Task<object> HandleAsync(StreamProxyRequest req, bool isHead)
        {
            // 1. Look up session
            var session = ProxySessionStore.TryGet(req.ProxyId);
            if (session == null)
            {
                _logger.LogWarning("[EmbyStreams] Proxy token {Token} not found or expired", req.ProxyId);
                Request.Response.StatusCode = (int)HttpStatusCode.Gone; // 410
                return null!;
            }

            // 2. For GET: redirect client directly to the first live upstream URL.
            //    For HEAD: probe upstream to obtain content-length/type then forward.
            //    IResponse in Emby 4.10 does not expose an OutputStream, so body
            //    streaming is replaced by a redirect to the Real-Debrid URL; the
            //    client's own Range header is forwarded by the CDN natively.
            foreach (var url in new[] { session.StreamUrl, session.Fallback1, session.Fallback2 })
            {
                if (string.IsNullOrEmpty(url)) continue;

                var alive = await HeadCheckAsync(url);
                if (alive == null)
                {
                    _logger.LogDebug("[EmbyStreams] Upstream dead for {Url} — trying fallback", ShortenUrl(url));
                    if (!string.IsNullOrEmpty(session.TorrentHash))
                        _ = Plugin.Instance?.DatabaseManager?.InvalidateByTorrentHashAsync(session.TorrentHash);
                    continue;
                }

                if (isHead)
                {
                    // Forward content headers so clients can seek before buffering
                    var ct = alive.Value.ContentType ?? "video/mp4";
                    Request.Response.ContentType = ct;
                    Request.Response.AddHeader("Content-Type", ct);
                    if (alive.Value.ContentLength.HasValue)
                        Request.Response.AddHeader("Content-Length", alive.Value.ContentLength.Value.ToString());
                    Request.Response.AddHeader("Accept-Ranges", "bytes");
                }
                else
                {
                    // GET: redirect client to the CDN URL; range requests work natively
                    _logger.LogDebug("[EmbyStreams] Proxy redirect → {Url}", ShortenUrl(url));
                    Request.Response.Redirect(url);
                }
                return null!;
            }

            // All URLs failed
            _logger.LogWarning(
                "[EmbyStreams] All proxy URLs dead for session {Token} ({Imdb})",
                req.ProxyId, session.ImdbId);

            Request.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            return null!;
        }

        // ── Private: range probe ─────────────────────────────────────────────────

        /// <summary>
        /// R1/P1: Validates a stream URL using a <c>GET Range: bytes=0-0</c> probe
        /// rather than plain HEAD.
        ///
        /// Real-Debrid CDN URLs (and ElfHosted proxy URLs with embedded auth tokens)
        /// may return HTTP 200 to a HEAD request even after the URL has expired, but
        /// correctly return 401/403 for any actual GET request.  A zero-byte range
        /// probe is cheap (≤1 KB round-trip), catches auth failures that HEAD misses,
        /// and is universally supported by major CDNs (200, 206, or 416 all confirm
        /// the resource is reachable).
        ///
        /// ContentType is extracted from the probe response for HEAD pass-through.
        /// ContentLength is not reliably available from a range response (it returns
        /// the range size, not the file size), so callers receive null — Emby clients
        /// handle a missing Content-Length gracefully.
        /// </summary>
        private async Task<(string? ContentType, long? ContentLength)?> HeadCheckAsync(string url)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                http.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                // Zero-byte range probe: GET bytes=0-0.  Discard body immediately.
                var msg = new HttpRequestMessage(HttpMethod.Get, url);
                msg.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

                using var resp = await http.SendAsync(
                    msg, HttpCompletionOption.ResponseHeadersRead);

                var code = (int)resp.StatusCode;

                if (code == 401 || code == 403 || code == 404 || code == 410)
                    return null;

                // 200 (range ignored by server), 206 (partial — ideal), 416 (range
                // not satisfiable, but the resource exists) all mean the URL is alive.
                if (code != 200 && code != 206 && code != 416)
                    return null;

                var contentType = resp.Content?.Headers?.ContentType?.ToString();
                // Content-Length from a Range response reflects the range size (1 byte),
                // not the full file size — omit it rather than mislead clients.
                return (contentType, null);
            }
            catch
            {
                return null;
            }
        }

        // ── Private: helpers ─────────────────────────────────────────────────────

        private static string ShortenUrl(string url)
            => url.Length > 60 ? url.Substring(0, 60) + "…" : url;
    }
}
