using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    // ── Request / Response DTOs ──────────────────────────────────────────────────

    /// <summary>
    /// Request object for the <c>GET /InfiniteDrive/TestFailover</c> dry-run endpoint.
    /// </summary>
    [Route("/InfiniteDrive/TestFailover", "GET",
        Summary = "Dry-run the full failover chain for a given IMDB ID without serving a stream")]
    public class TestFailoverRequest : IReturn<object>
    {
        /// <summary>IMDB ID to probe, e.g. <c>tt0111161</c>.</summary>
        [ApiMember(Name = "imdb", Description = "IMDB ID to test", IsRequired = false,
                   DataType = "string", ParameterType = "query")]
        public string Imdb { get; set; } = "tt0111161"; // Default: Shawshank Redemption
    }

    /// <summary>
    /// Result for one failover layer in the <c>TestFailover</c> dry-run.
    /// </summary>
    public class FailoverLayerResult
    {
        /// <summary>
        /// Machine-readable outcome.
        /// Values: <c>ok</c>, <c>skipped</c>, <c>timeout</c>, <c>error</c>,
        ///         <c>no_streams</c>, <c>no_hash</c>, <c>no_key</c>, <c>not_cached</c>.
        /// </summary>
        public string Status { get; set; } = "skipped";

        /// <summary>Human-readable detail for the status badge.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Round-trip latency in milliseconds, or 0 if no network call was made.</summary>
        public int LatencyMs { get; set; }
    }

    /// <summary>
    /// Full response from the <c>TestFailover</c> dry-run.
    /// </summary>
    public class TestFailoverResponse
    {
        /// <summary>IMDB ID that was tested.</summary>
        public string Imdb { get; set; } = string.Empty;

        /// <summary>Layer 1 result: primary AIOStreams instance.</summary>
        public FailoverLayerResult Layer1 { get; set; } = new FailoverLayerResult();

        /// <summary>Layer 2 result: AIOStreams fallback instances.</summary>
        public FailoverLayerResult Layer2 { get; set; } = new FailoverLayerResult();

        /// <summary>Layer 3 result: direct debrid provider APIs.</summary>
        public FailoverLayerResult Layer3 { get; set; } = new FailoverLayerResult();

        /// <summary>One-line plain-English summary of the overall result.</summary>
        public string Summary { get; set; } = string.Empty;
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles <c>GET /InfiniteDrive/TestFailover</c>.
    ///
    /// <para>
    /// Dry-runs the complete resilience chain for a given IMDB ID:
    /// <list type="number">
    ///   <item>Layer 1 — primary AIOStreams only (no fallbacks)</item>
    ///   <item>Layer 2 — each configured fallback AIOStreams URL</item>
    ///   <item>Layer 3 — direct debrid instant-availability check using stored torrent hashes</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// This endpoint performs <em>read-only probes</em>.  It does not serve a stream,
    /// does not modify any cache entries, and does not trigger any downloads.
    /// </para>
    /// </summary>
    public class TestFailoverService : IService, IRequiresRequest
    {
        // Timeout used for each layer's AIOStreams probe — short to keep the UI responsive.
        private const int ProbeTimeoutMs = 5_000;

        private readonly ILogger<TestFailoverService> _logger;
        private readonly IAuthorizationContext        _authCtx;

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        /// <summary>Emby injects <paramref name="logManager"/> and <paramref name="authCtx"/> automatically.</summary>
        public TestFailoverService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<TestFailoverService>(
                logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles the dry-run request.</summary>
        public async Task<object> Get(TestFailoverRequest req)
        {
            // SEC-3: Admin-only — this endpoint makes real outbound network calls and
            // exposes the full failover configuration (fallback URLs, debrid availability).
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var config = Plugin.Instance?.Configuration;
            var db     = Plugin.Instance?.DatabaseManager;

            if (config == null || db == null)
                return new TestFailoverResponse { Summary = "Plugin not initialised." };

            // SEC-2: Validate IMDB ID format before making any outbound network calls.
            // Fall back to Shawshank if the parameter is blank.
            var rawImdb = string.IsNullOrWhiteSpace(req.Imdb) ? "tt0111161" : req.Imdb.Trim();
            if (!IsValidImdbId(rawImdb))
                return new TestFailoverResponse
                {
                    Imdb    = rawImdb,
                    Summary = $"Invalid IMDB ID '{rawImdb}'. Expected format: tt1234567",
                };

            var imdb = rawImdb;
            var resp = new TestFailoverResponse { Imdb = imdb };

            // ── Layer 1: Primary AIOStreams ────────────────────────────────────────
            resp.Layer1 = await ProbeAioStreamsInstanceAsync(imdb, config, primaryOnly: true);

            // ── Layer 2: Fallback AIOStreams instances ─────────────────────────────
            resp.Layer2 = await ProbeFallbacksAsync(imdb, config);

            // ── Layer 3: Direct debrid instant availability ────────────────────────
            resp.Layer3 = await ProbeDebridDirectAsync(imdb, config, db);

            // ── Summary ───────────────────────────────────────────────────────────
            resp.Summary = BuildSummary(resp);
            return resp;
        }

        // ── Layer probes ─────────────────────────────────────────────────────────

        /// <summary>
        /// Probes the primary AIOStreams instance with a short timeout.
        /// </summary>
        private async Task<FailoverLayerResult> ProbeAioStreamsInstanceAsync(
            string imdb, PluginConfiguration config, bool primaryOnly)
        {
            // Get the primary manifest URL
            if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
                return new FailoverLayerResult
                {
                    Status  = "skipped",
                    Message = "No primary manifest URL configured.",
                };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var cts = new CancellationTokenSource(ProbeTimeoutMs);
            try
            {
                using var client = new AioStreamsClient(config, _logger);
                var response = await client.GetMovieStreamsAsync(imdb, cts.Token);

                sw.Stop();
                if (response?.Streams == null || response.Streams.Count == 0)
                    return new FailoverLayerResult
                    {
                        Status    = "no_streams",
                        Message   = "AIOStreams responded but returned no streams for this title.",
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                    };

                return new FailoverLayerResult
                {
                    Status    = "ok",
                    Message   = $"{response.Streams.Count} stream(s) returned.",
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
            catch (AioStreamsUnreachableException ex)
            {
                sw.Stop();
                return new FailoverLayerResult
                {
                    Status    = "timeout",
                    Message   = $"Primary AIOStreams unreachable: {ex.Message}",
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new FailoverLayerResult
                {
                    Status    = "timeout",
                    Message   = $"Primary AIOStreams did not respond within {ProbeTimeoutMs / 1000}s.",
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new FailoverLayerResult
                {
                    Status    = "error",
                    Message   = ex.Message,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
        }

        /// <summary>
        /// Probes the secondary AIOStreams provider if configured.
        /// Returns "skipped" if no secondary manifest URL is configured.
        /// </summary>
        private async Task<FailoverLayerResult> ProbeFallbacksAsync(
            string imdb, PluginConfiguration config)
        {
            // In v0.51+, only one fallback URL is supported (SecondaryManifestUrl)
            if (string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                return new FailoverLayerResult
                {
                    Status  = "skipped",
                    Message = "No secondary manifest URL configured.",
                };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(ProbeTimeoutMs);
                // Create a temporary config with only the secondary URL
                var tempConfig = new PluginConfiguration
                {
                    PrimaryManifestUrl = config.SecondaryManifestUrl,
                    SecondaryManifestUrl = string.Empty,
                    EmbyBaseUrl = config.EmbyBaseUrl,
                    EmbyApiKey = config.EmbyApiKey,
                };
                using var client = new AioStreamsClient(tempConfig, _logger);
                var response = await client.GetMovieStreamsAsync(imdb, cts.Token);
                sw.Stop();

                if (response?.Streams?.Count > 0)
                    return new FailoverLayerResult
                    {
                        Status    = "ok",
                        Message   = $"{response.Streams.Count} stream(s) returned.",
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                    };
                else
                    return new FailoverLayerResult
                    {
                        Status    = "no_streams",
                        Message   = "Secondary instance responded but returned no streams.",
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                    };
            }
            catch (AioStreamsUnreachableException ex)
            {
                sw.Stop();
                return new FailoverLayerResult
                {
                    Status    = "timeout",
                    Message   = $"Secondary instance unreachable: {ex.Message}",
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new FailoverLayerResult
                {
                    Status    = "timeout",
                    Message   = $"Secondary instance did not respond within {ProbeTimeoutMs / 1000}s.",
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new FailoverLayerResult
                {
                    Status    = "error",
                    Message   = ex.Message,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
        }

        /// <summary>
        /// Layer 3 was direct debrid instant-availability, removed in v0.51+.
        /// Always returns <c>no_key</c> — AIOStreams is the only resolution path.
        /// </summary>
        private Task<FailoverLayerResult> ProbeDebridDirectAsync(
            string imdb, PluginConfiguration config, DatabaseManager db)
        {
            return Task.FromResult(new FailoverLayerResult
            {
                Status  = "no_key",
                Message = "Direct debrid fallback was removed in v0.51+. AIOStreams handles all resolution.",
            });
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string BuildSummary(TestFailoverResponse r)
        {
            if (r.Layer1.Status == "ok")
                return $"Layer 1 ✅ — Primary AIOStreams is healthy. {r.Layer1.Message}";

            if (r.Layer2.Status == "ok")
                return "Layer 2 🛡️ — Primary AIOStreams is down but at least one fallback is healthy.";

            if (r.Layer3.Status == "ok")
                return "Layer 3 🔑 — Direct debrid would serve this title.";

            return "⚠️ All AIOStreams layers failed — check connection settings.";
        }


        /// <summary>
        /// Parses a Stremio manifest URL into (baseUrl, uuid, token) components.
        /// Handles both authenticated (<c>{base}/stremio/{uuid}/{token}/manifest.json</c>)
        /// and unauthenticated (<c>{base}/stremio/manifest.json</c>) forms.
        /// </summary>
        private static (string BaseUrl, string? Uuid, string? Token)
            ParseManifestUrl(string manifestUrl)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                manifestUrl.Trim(),
                @"^(https?://[^/]+(?::\d+)?)(?:/[^/]+)*/stremio/([^/]+)/([^/]+)/manifest\.json",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (m.Success)
                return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);

            // Unauthenticated form: {base}/stremio/manifest.json
            var m2 = System.Text.RegularExpressions.Regex.Match(
                manifestUrl.Trim(),
                @"^(https?://[^/]+(?::\d+)?)(?:/[^/]+)*/stremio/manifest\.json",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return m2.Success
                ? (m2.Groups[1].Value, null, null)
                : (manifestUrl.TrimEnd('/'), null, null);
        }

        private static string ShortenUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.Host + (uri.Port != 80 && uri.Port != 443 ? ":" + uri.Port : string.Empty);
            }
            catch { return url.Length > 30 ? url.Substring(0, 30) + "…" : url; }
        }

        /// <summary>
        /// SEC-2: Validates an IMDB ID string.
        /// Must start with "tt" followed by 1–8 decimal digits only.
        /// </summary>
        private static bool IsValidImdbId(string imdb)
        {
            if (imdb.Length < 3 || imdb.Length > 10) return false;
            if (!imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 2; i < imdb.Length; i++)
                if (!char.IsDigit(imdb[i])) return false;
            return true;
        }
    }
}
