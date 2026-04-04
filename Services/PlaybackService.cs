using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    // ── Request DTO ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Request object for the <c>GET /EmbyStreams/Play</c> endpoint.
    /// Emby clients request this URL as the media source URL from .strm files.
    /// Authentication is handled via Emby's [Authenticated] attribute and X-Emby-Token header.
    /// </summary>
    [Route("/EmbyStreams/Play", "GET", Summary = "Resolve and serve a media stream from AIOStreams/Real-Debrid")]
    public class PlayRequest : IReturn<object>
    {
        /// <summary>IMDB ID of the item to play, e.g. <c>tt1160419</c>.</summary>
        [ApiMember(Name = "imdb", Description = "IMDB ID (use either imdb or episode_id)", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string Imdb { get; set; } = string.Empty;

        /// <summary>Season number for series playback.  Omit for movies.</summary>
        [ApiMember(Name = "season", Description = "Season number (series only)", DataType = "int", ParameterType = "query")]
        public int? Season { get; set; }

        /// <summary>Episode number for series playback.  Omit for movies.</summary>
        [ApiMember(Name = "episode", Description = "Episode number (series only)", DataType = "int", ParameterType = "query")]
        public int? Episode { get; set; }

        /// <summary>Stremio episode ID in format "imdbId:season:episode". Alternative to imdb/season/episode.</summary>
        [ApiMember(Name = "episode_id", Description = "Stremio episode ID (imdbId:season:episode)", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? EpisodeId { get; set; }

        /// <summary>DEPRECATED: No longer used. Authentication is handled via Emby's [Authenticated] attribute.</summary>
        [ApiMember(Name = "apikey", Description = "[DEPRECATED - no longer used]", DataType = "string", ParameterType = "query")]
        public string? ApiKey { get; set; }
    }

    // ── Error response DTO ───────────────────────────────────────────────────────

    /// <summary>
    /// JSON body returned when the plugin cannot serve a stream.
    /// </summary>
    public class PlayErrorResponse
    {
        /// <summary>Machine-readable error code.</summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>Human-readable message suitable for display.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Suggested client retry delay in seconds.</summary>
        public int RetryAfterSeconds { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles every <c>GET /EmbyStreams/Play</c> request.
    ///
    /// Authentication is secured via Emby's [Authenticated] attribute which validates
    /// the X-Emby-Token header automatically before the handler executes.
    ///
    /// Decision tree (CLAUDE.md §Playback Decision Tree):
    /// <list type="number">
    ///   <item>SQLite lookup in <c>resolution_cache</c></item>
    ///   <item>Valid cache hit → serve via proxy or redirect (&lt;100ms)</item>
    ///   <item>Expired cache → HEAD-validate → try fallbacks → re-resolve if all fail</item>
    ///   <item>Cache miss → sync AIOStreams call (3s timeout) → cache + serve</item>
    ///   <item>All paths fail → HTTP 503 with JSON error body</item>
    /// </list>
    ///
    /// After every play event (success or failure) a <see cref="PlaybackEntry"/>
    /// is written to the log in a fire-and-forget background task.
    /// </summary>
    [Authenticated]
    public class PlaybackService : IService, IRequiresRequest
    {
        // ── Constants ───────────────────────────────────────────────────────────

        // Fallback used only when config is unavailable (should never happen in practice)
        private const int FallbackSyncResolveTimeoutMs = 30_000;

        // Number of resolution attempts before giving up and returning 503.
        private const int MaxResolutionAttempts = 3; // Don't Panic — we tried

        // Anti-intrusion: rate limiting
        private const int MaxPlayRequestsPerMinutePerIp = 60; // 1 req/sec per client is reasonable
        private const int MaxPlayRequestsPerMinutePerUser = 120; // User can play faster (queued playback)

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<PlaybackService> _logger;
        private readonly IAuthorizationContext _authContext;

        // In-memory rate limit tracking: Key = "ip:192.168.1.100" or "user:username"
        // Value = (count, timestamp) — reset every minute
        private static readonly Dictionary<string, (int count, DateTime resetTime)> RateLimitBucket
            = new Dictionary<string, (int count, DateTime resetTime)>();
        private static readonly object RateLimitLock = new object();

        // Lazy cleanup threshold for rate limit bucket (Sprint 104B-03)
        private static int _rateLimitAccessCount;
        private const int RateLimitCleanupThreshold = 100;

        // ── IRequiresRequest ─────────────────────────────────────────────────────

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects dependencies automatically.
        /// </summary>
        public PlaybackService(ILogManager logManager, IAuthorizationContext authContext)
        {
            _logger = new EmbyLoggerAdapter<PlaybackService>(logManager.GetLogger("EmbyStreams"));
            _authContext = authContext;
        }

        // ── IService ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles <c>GET /EmbyStreams/Play?imdb=tt...&amp;season=1&amp;episode=1</c>.
        /// </summary>
        public async Task<object> Get(PlayRequest req)
        {
            var startTime = DateTime.UtcNow;

            // ── 0. Validate ──────────────────────────────────────────────────────

            // ── Parse media identifier ────────────────────────────────────────────────
            string imdb;
            int? season = req.Season;
            int? episode = req.Episode;

            // Support episode_id parameter format: "imdbId:season:episode"
            if (!string.IsNullOrWhiteSpace(req.EpisodeId))
            {
                if (!string.IsNullOrWhiteSpace(req.Imdb))
                {
                    return Error400("bad_request", "Use either imdb parameter or episode_id, not both");
                }

                var parts = req.EpisodeId.Split(':');
                if (parts.Length != 3)
                    return Error400("bad_request", "Invalid episode_id format. Expected: imdbId:season:episode");

                imdb = parts[0];
                if (!int.TryParse(parts[1], out var seasonNum) || !int.TryParse(parts[2], out var epNum))
                {
                    return Error400("bad_request", "Season and episode must be numbers in episode_id");
                }
                season = seasonNum;
                episode = epNum;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(req.Imdb))
                    return Error400("bad_request", "imdb parameter is required (or use episode_id)");

                imdb = req.Imdb;
            }

            if (!IsValidImdbId(imdb))
                return Error400("bad_request", $"Invalid IMDB ID '{imdb}' — expected format: tt1234567");

            // Normalize request so all downstream code can use req.Imdb/Season/Episode uniformly
            req.Imdb    = imdb;
            req.Season  = season;
            req.Episode = episode;

            // ── Authorization via Emby Token ──────────────────────────────────────
            // The [Authenticated] attribute ensures the request has a valid X-Emby-Token.
            // Use IAuthorizationContext to extract user and verify library access.

            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return Error400("server_error", "Plugin not initialized");

            var authInfo = _authContext.GetAuthorizationInfo(Request);
            if (authInfo.User == null)
                return Error401("unauthorized", "Emby authentication required");

            _logger.LogDebug("[EmbyStreams] Request authenticated as user: {User}", authInfo.User.Name);

            // ── Anti-Intrusion: Rate Limiting ─────────────────────────────────────────
            var clientIp = ExtractClientIp();
            var userName = authInfo.User.Name ?? "anonymous";

            // Check rate limit for this IP
            if (!CheckRateLimit($"ip:{clientIp}", MaxPlayRequestsPerMinutePerIp))
            {
                _logger.LogWarning("[EmbyStreams] Rate limit exceeded for IP {Ip}", clientIp);
                return Error429("rate_limited", $"Too many requests from {clientIp}. Max {MaxPlayRequestsPerMinutePerIp} per minute.");
            }

            // Check rate limit for this user
            if (!CheckRateLimit($"user:{userName}", MaxPlayRequestsPerMinutePerUser))
            {
                _logger.LogWarning("[EmbyStreams] Rate limit exceeded for user {User}", userName);
                return Error429("rate_limited", $"Too many requests from user {userName}. Max {MaxPlayRequestsPerMinutePerUser} per minute.");
            }

            // P5: Season 0 / specials — AIOStreams has no S00 concept.
            // Treat as movie lookup by dropping the season/episode parameters so the
            // regular movie stream resolution path handles it gracefully.
            if (season.HasValue && season.Value == 0)
            {
                _logger.LogDebug(
                    "[EmbyStreams] Season 0 (specials) requested for {Imdb} — falling back to movie lookup",
                    imdb);
                season = null;
                episode = null;
                req.Season  = null;
                req.Episode = null;
            }

            // config already validated above
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
                return PanicRedirect("server_error", imdb, 30);

            var clientType = ExtractClientType();
            // Per-device compat key: type + IP so learning is not shared across devices
            var compatKey  = ExtractCompatKey();

            _logger.LogInformation(
                "[EmbyStreams] Play request: {Imdb} S{Season}E{Episode} client={Client}",
                imdb, season, episode, clientType);

            // ── 1. SQLite cache lookup ────────────────────────────────────────────

            var cached = await db.GetCachedStreamAsync(imdb, season, episode);

            _logger.LogInformation(
                "[DEBUG] Cache lookup for {Imdb} S{Season}E{Episode}: found={Found} status={Status} url_empty={UrlEmpty}",
                imdb, season, episode,
                cached != null, cached?.Status ?? "N/A",
                string.IsNullOrEmpty(cached?.StreamUrl));

            if (cached != null)
            {
                // T2/P2: a failed entry with an empty stream URL means AIOStreams explicitly
                // returned an empty streams list (not a network error).  Don't waste an
                // API call — we know there are no links right now; report it cleanly and
                // wait for the 1-hour short-TTL to expire before the resolver retries.
                if (cached.Status == "failed"
                    && string.IsNullOrEmpty(cached.StreamUrl)
                    && IsNotExpired(cached.ExpiresAt))
                {
                    _logger.LogDebug("[EmbyStreams] no_streams sentinel hit for {Imdb} (1h TTL not expired)", req.Imdb);
                    _ = LogFailureAsync(db, req, clientType, "no_streams (cached)");
                    return PanicRedirect("no_streams", req.Imdb, 3600);
                }

                // Load ranked candidates (Sprint 16+).  Falls back gracefully to
                // the flat ResolutionEntry fields on pre-V7 installs.
                var candidates = await db.GetStreamCandidatesAsync(
                    imdb, season, episode);

                // ── 2. Valid fresh cache hit — serve without probe ────────────────
                // R1: Proactive age-based validation gate.  Real-Debrid CDN URLs
                // expire server-side at ~4–6 h after issuance, independent of our
                // 6-hour cache TTL.  If the cached URL has consumed more than 70% of
                // its TTL (≈4.2 h for the default 6 h debrid policy) we range-probe
                // before serving to avoid silently redirecting to a dead CDN URL.
                if (cached.Status == "valid" && IsNotExpired(cached.ExpiresAt)
                    && !IsUrlAging(cached.ResolvedAt, config.CacheLifetimeMinutes))
                {
                    _logger.LogDebug("[EmbyStreams] Cache HIT for {Imdb}", req.Imdb);
                    _ = LogPlaybackAsync(db, req, cached, "cached", clientType, startTime);
                    _ = db.IncrementPlayCountAsync(imdb, season, episode);
                    return await ServeStream(cached, candidates, req, config, compatKey);
                }

                // ── 3. Expired, stale, or aging cache: range-probe ───────────────
                // "Aging" = valid + not-expired but > 70% of TTL consumed.
                // All three states go through HeadValidateAsync (range probe).
                var isAging = cached.Status == "valid" && IsNotExpired(cached.ExpiresAt);
                _logger.LogDebug(
                    isAging
                        ? "[EmbyStreams] Cache HIT but URL aging for {Imdb} — proactive range probe"
                        : "[EmbyStreams] Cache STALE for {Imdb} — validating",
                    req.Imdb);

                var urlToUse = await HeadValidateAsync(cached, candidates);
                if (urlToUse != null)
                {
                    // Probe succeeded — serve and queue background re-resolution
                    _ = QueueResolutionAsync(req, db, config, "tier1");
                    _ = LogPlaybackAsync(db, req, cached, "cached", clientType, startTime);
                    _ = db.IncrementPlayCountAsync(imdb, season, episode);
                    return await ServeStreamUrl(urlToUse, cached, candidates, req, config, compatKey);
                }

                // ── 3.5. Hash-based cross-provider failover ───────────────────────
                // Before resorting to a full sync resolve, try candidates with the same
                // hash from other providers (if available).
                if (!string.IsNullOrEmpty(cached.TorrentHash))
                {
                    var hashMatchUrl = GetCandidatesByHashExcludingProvider(
                        candidates, cached.TorrentHash, cached.StreamUrl);
                    if (!string.IsNullOrEmpty(hashMatchUrl))
                    {
                        _logger.LogInformation(
                            "[EmbyStreams] Hash match found from different provider for {Imdb} — trying that URL",
                            req.Imdb);
                        _ = LogPlaybackAsync(db, req, cached, "hash_match_fallback", clientType, startTime);
                        _ = db.IncrementPlayCountAsync(imdb, season, episode);
                        return await ServeStreamUrl(hashMatchUrl, cached, candidates, req, config, compatKey);
                    }
                }

                // All fallbacks failed — fall through to sync resolve
                _logger.LogWarning(
                    "[EmbyStreams] All cached URLs for {Imdb} returned 4xx — sync resolving",
                    req.Imdb);
            }

            // ── 4. Cache miss / all fallbacks dead — sync AIOStreams call ─────────

            _logger.LogInformation("[EmbyStreams] Cache MISS for {Imdb} — sync resolving", imdb);

            ResolutionEntry? resolved    = null;
            bool             aioDown     = false;

            try
            {
                // Create a temporary request for SyncResolveAsync
                var tempReq = new PlayRequest
                {
                    Imdb = imdb,
                    Season = season,
                    Episode = episode
                };
                // Single-flight: if another client is already resolving this episode,
                // await its result instead of firing a duplicate AIOStreams call.
                var flightKey = $"{req.Imdb}:{req.Season}:{req.Episode}";
                resolved = await SingleFlight<ResolutionEntry?>.RunAsync(
                    flightKey, () => SyncResolveAsync(tempReq, config, db));
                if (resolved == null)
                {
                    _logger.LogWarning(
                        "[DEBUG] SyncResolveAsync returned null for {Imdb} (AIOStreams has no streams)",
                        req.Imdb);
                }
                else
                {
                    _logger.LogInformation(
                        "[DEBUG] SyncResolveAsync SUCCESS for {Imdb}: url_empty={UrlEmpty} status={Status}",
                        req.Imdb, string.IsNullOrEmpty(resolved.StreamUrl), resolved.Status);
                }
            }
            catch (AioStreamsUnreachableException ex)
            {
                aioDown = true;
                _logger.LogWarning(
                    ex, "[DEBUG] AioStreamsUnreachableException for {Imdb}: {Message}",
                    req.Imdb, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "[DEBUG] Unexpected exception during SyncResolveAsync for {Imdb}",
                    req.Imdb);
                aioDown = true;
            }

            if (resolved != null)
            {
                // Read the candidates we just wrote so ServeStream can use them
                var resolvedCandidates = await db.GetStreamCandidatesAsync(
                    imdb, season, episode);
                _ = LogPlaybackAsync(db, req, resolved, "sync_resolve", clientType, startTime);
                _ = db.IncrementPlayCountAsync(imdb, season, episode);
                return await ServeStream(resolved, resolvedCandidates, req, config, compatKey);
            }

            // ── 5. Total failure ──────────────────────────────────────────────────

            _logger.LogWarning("[EmbyStreams] Could not resolve stream for {Imdb}", req.Imdb);
            _ = LogFailureAsync(db, req, clientType,
                aioDown ? "All configured AIOStreams providers unreachable"
                        : "AIOStreams returned no streams");
            return PanicRedirect("stream_unavailable", req.Imdb, 30);
        }

        // ── Private: stream serving ─────────────────────────────────────────────

        private Task<object> ServeStream(
            ResolutionEntry       entry,
            List<StreamCandidate> candidates,
            PlayRequest           req,
            PluginConfiguration   config,
            string                clientType)
        {
            var url = candidates.Count > 0 ? candidates[0].Url : entry.StreamUrl;
            return ServeStreamUrl(url, entry, candidates, req, config, clientType);
        }

        private async Task<object> ServeStreamUrl(
            string                url,
            ResolutionEntry       entry,
            List<StreamCandidate> candidates,
            PlayRequest           req,
            PluginConfiguration   config,
            string                clientType)
        {
            // Bitrate-aware quality routing using ranked candidates when available,
            // falling back to the legacy flat fallback fields on pre-V7 installs.
            url = await SelectBitrateAwareUrl(url, entry, candidates, clientType);

            var mode = await ChooseMode(config, clientType);

            if (mode == "redirect")
            {
                _logger.LogDebug("[EmbyStreams] Serving via redirect to {Url}", ShortenUrl(url));
                Request.Response.Redirect(url);
                return null!;
            }

            // proxy mode — create a session and redirect to the proxy endpoint.
            // Populate fallbacks from the ranked candidate list when available;
            // fall back to the legacy flat fields on pre-V7 installs.
            var fb1 = candidates.Count > 1 ? candidates[1].Url : entry.Fallback1;
            var fb2 = candidates.Count > 2 ? candidates[2].Url : entry.Fallback2;
            var primaryBitrate = candidates.Count > 0
                ? candidates[0].BitrateKbps
                : entry.FileBitrateKbps;
            var primaryQuality = candidates.Count > 0
                ? candidates[0].QualityTier
                : entry.QualityTier;

            var bingeGroup = candidates.Count > 0 ? candidates[0].BingeGroup : null;

            var session = new ProxySession
            {
                StreamUrl             = url,
                ImdbId                = req.Imdb,
                Season                = req.Season,
                Episode               = req.Episode,
                Fallback1             = fb1,
                Fallback2             = fb2,
                TorrentHash           = entry.TorrentHash,
                QualityTier           = primaryQuality,
                EstimatedBitrateKbps  = primaryBitrate
                                     ?? StreamHelpers.EstimateBitrateKbps(primaryQuality),
                BingeGroup            = bingeGroup,
            };
            var token   = ProxySessionStore.Create(session);
            var proxyUrl = $"{config.EmbyBaseUrl.TrimEnd('/')}/EmbyStreams/Stream/{token}";

            _logger.LogDebug("[EmbyStreams] Serving via proxy token {Token}", token);
            Request.Response.Redirect(proxyUrl);
            return null!;
        }

        // ── Private: mode selection ─────────────────────────────────────────────

        private async Task<string> ChooseMode(PluginConfiguration config, string clientType)
        {
            if (config.ProxyMode == "proxy")    return "proxy";
            if (config.ProxyMode == "redirect") return "redirect";

            // auto: check learned client compat; default to redirect on first play
            var compat = await (Plugin.Instance?.DatabaseManager
                ?.GetClientCompatAsync(clientType) ?? Task.FromResult<ClientCompatEntry?>(null));

            if (compat != null && compat.SupportsRedirect == 0)
                return "proxy";

            return "redirect"; // optimistic default
        }

        // ── Private: bitrate-aware URL selection ────────────────────────────────

        /// <summary>
        /// If the client has a learned <c>MaxSafeBitrate</c> ceiling, walks the ranked
        /// candidate list to find the first stream within budget.  Falls back to the
        /// legacy flat fields on pre-V7 installs where no candidates exist.
        /// Returns the original URL unchanged when no ceiling is known.
        /// </summary>
        private async Task<string> SelectBitrateAwareUrl(
            string                url,
            ResolutionEntry       entry,
            List<StreamCandidate> candidates,
            string                clientType)
        {
            var compat = await (Plugin.Instance?.DatabaseManager
                ?.GetClientCompatAsync(clientType) ?? Task.FromResult<ClientCompatEntry?>(null));

            if (compat?.MaxSafeBitrate == null)
                return url;

            int ceiling = compat.MaxSafeBitrate.Value;

            // Candidates path (Sprint 16+): walk ranked list for first URL within budget
            if (candidates.Count > 0)
            {
                foreach (var c in candidates)
                {
                    int bitrate = c.BitrateKbps
                               ?? StreamHelpers.EstimateBitrateKbps(c.QualityTier);
                    if (bitrate <= ceiling)
                    {
                        if (c.Rank > 0)
                            _logger.LogDebug(
                                "[EmbyStreams] Client {Client} ceiling {Ceil} kbps — demoting to rank {Rank} ({Provider})",
                                clientType, ceiling, c.Rank, c.ProviderKey);
                        return c.Url;
                    }
                }
                return candidates[0].Url; // all exceed ceiling — serve best available
            }

            // Legacy path (pre-V7 installs)
            int primaryBitrate = entry.FileBitrateKbps
                              ?? StreamHelpers.EstimateBitrateKbps(entry.QualityTier);

            if (primaryBitrate <= ceiling) return url;

            if (!string.IsNullOrEmpty(entry.Fallback1))
            {
                _logger.LogDebug(
                    "[EmbyStreams] Client {Client} ceiling {Ceil} kbps — demoting to fallback_1 (legacy)",
                    clientType, ceiling);
                return entry.Fallback1;
            }

            return !string.IsNullOrEmpty(entry.Fallback2) ? entry.Fallback2 : url;
        }

        // ── Private: range-probe validation ────────────────────────────────────

        /// <summary>
        /// P1: Validates cached URLs using a <c>GET Range: bytes=0-0</c> probe instead
        /// of HEAD.  Real-Debrid pre-signed CDN URLs return 200 for HEAD even when the
        /// token is expired, but return 401/403 for any actual GET request — so HEAD-only
        /// checks miss expired-token failures entirely.  A zero-byte range probe is cheap
        /// (≤1KB round-trip), correctly catches auth failures, and is widely supported by
        /// CDNs (200, 206, or even 416 all mean "the resource exists and I can reach it").
        ///
        /// Tries ranked candidates first, then falls back to legacy flat fields on
        /// pre-V7 DB installs.  Returns the first live URL, or null if all fail.
        /// </summary>
        private async Task<string?> HeadValidateAsync(
            ResolutionEntry       entry,
            List<StreamCandidate> candidates)
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(6)
            };

            // Prefer ranked candidates; fall back to legacy flat fields
            IEnumerable<string?> urlsToTry = candidates.Count > 0
                ? candidates.Select(c => (string?)c.Url)
                : new string?[] { entry.StreamUrl, entry.Fallback1, entry.Fallback2 };

            foreach (var url in urlsToTry)
            {
                if (string.IsNullOrEmpty(url)) continue;
                try
                {
                    // Range probe: GET bytes=0-0.  Discard the body immediately.
                    // Accept: 200 (server ignores range), 206 (partial content — ideal),
                    //         416 (range not satisfiable — file exists, range was bad).
                    // Reject: 401/403 (auth failed — token expired or revoked),
                    //         404/410 (resource gone), 5xx (server error).
                    var msg = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Get, url);
                    msg.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

                    using var resp = await http.SendAsync(
                        msg, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);

                    var code = (int)resp.StatusCode;
                    if (code == 200 || code == 206 || code == 416)
                        return url;

                    // 401/403 = auth failure — token expired.  Log and try next candidate.
                    if (code == 401 || code == 403)
                        _logger.LogDebug(
                            "[EmbyStreams] RangeProbe: {Url} returned {Code} — likely expired token, trying fallback",
                            ShortenUrl(url), code);
                }
                catch
                {
                    // timeout or network error — try next fallback
                }
            }

            return null;
        }

        // ── Private: sync resolution ────────────────────────────────────────────

        /// <summary>
        /// Calls AIOStreams synchronously on cache miss to resolve a stream URL.
        /// Timeout is read from <see cref="PluginConfiguration.SyncResolveTimeoutSeconds"/>
        /// (set from the AIOStreams manifest hint when available, otherwise user-configured,
        /// defaulting to 30 s — much better than the old hardcoded 3 s).
        /// Writes both the ranked candidates and the primary ResolutionEntry.
        /// </summary>
        private async Task<ResolutionEntry?> SyncResolveAsync(
            PlayRequest         req,
            PluginConfiguration config,
            DatabaseManager     db)
        {
            // Use the larger of the user-configured floor and the timeout discovered
            // from the AIOStreams manifest (behaviorHints.requestTimeout).  This ensures
            // the plugin never times out before AIOStreams has finished querying its addons.
            int configuredSecs  = config.SyncResolveTimeoutSeconds > 0
                ? config.SyncResolveTimeoutSeconds
                : FallbackSyncResolveTimeoutMs / 1000;
            int discoveredSecs  = config.AioStreamsDiscoveredTimeoutSeconds > 0
                ? config.AioStreamsDiscoveredTimeoutSeconds
                : 0;
            // P7: cap at 60 s so a misconfigured or enormous manifest-hinted value can never
            // hold a request thread indefinitely.  Most AIOStreams installs resolve in < 10 s.
            int timeoutMs = Math.Min(Math.Max(configuredSecs, discoveredSecs) * 1000, 60_000);

            using var cts = new CancellationTokenSource(timeoutMs);

            // Build list of providers to try (round-robin on configured manifest URLs)
            var providers = GetProvidersToTry(config);

            AioStreamsUnreachableException? lastUnreachable = null;

            foreach (var provider in providers)
            {
                try
                {
                    _logger.LogDebug("[EmbyStreams] Trying provider {Name} for {Imdb}",
                        provider.DisplayName ?? "unknown", req.Imdb);

                    using var client = new AioStreamsClient(
                        provider.Url, provider.Uuid, provider.Token, _logger);
                    AioStreamsStreamResponse? response;

                    if (req.Season.HasValue && req.Episode.HasValue)
                        response = await client.GetSeriesStreamsAsync(
                            req.Imdb, req.Season.Value, req.Episode.Value, cts.Token);
                    else
                        response = await client.GetMovieStreamsAsync(req.Imdb, cts.Token);

                    if (response?.Streams == null || response.Streams.Count == 0)
                        continue; // Try next provider

                    // Build ranked candidates and write to stream_candidates
                    var candidates = StreamHelpers.RankAndFilterStreams(
                        response, req.Imdb, req.Season, req.Episode,
                        config.ProviderPriorityOrder,
                        config.CandidatesPerProvider,
                        config.CacheLifetimeMinutes);

                    if (candidates.Count == 0) continue; // Try next provider

                    // Write resolution_cache + stream_candidates atomically in one transaction
                    var entry = BuildEntryFromCandidates(candidates, req, config, "tier0");
                    await db.UpsertResolutionResultAsync(entry, candidates);
                    await db.IncrementApiCallCountAsync();
                    return entry;
                }
                catch (AioStreamsUnreachableException ex)
                {
                    lastUnreachable = ex;
                    _logger.LogDebug("[EmbyStreams] Provider {Name} unreachable, trying next",
                        provider.DisplayName ?? "unknown");
                    // continue to next provider
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[EmbyStreams] Sync resolve timed out ({Timeout}s) for {Imdb}",
                        timeoutMs / 1000, req.Imdb);
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EmbyStreams] Sync resolve failed for {Imdb} with provider {Name}",
                        req.Imdb, provider.DisplayName ?? "unknown");
                    // continue to next provider
                }
            }

            // If we got here, all providers were either unreachable or returned no streams
            if (lastUnreachable != null)
                throw lastUnreachable; // All providers were unreachable

            return null; // All providers returned no streams
        }

        /// <summary>
        /// Simple provider holder for stream resolution attempts.
        /// </summary>
        private class AioProvider
        {
            public string DisplayName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Uuid { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
        }

        /// <summary>
        /// Builds the ordered list of AIOStreams providers to try for stream resolution.
        /// Uses PrimaryManifestUrl and SecondaryManifestUrl from simplified configuration.
        /// </summary>
        private static List<AioProvider> GetProvidersToTry(PluginConfiguration config)
        {
            var providers = new List<AioProvider>();

            // Parse primary manifest URL
            if (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    providers.Add(new AioProvider
                    {
                        DisplayName = "Primary",
                        Url = url,
                        Uuid = uuid ?? string.Empty,
                        Token = token ?? string.Empty
                    });
                }
            }

            // Parse secondary manifest URL if configured
            if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.SecondaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    providers.Add(new AioProvider
                    {
                        DisplayName = "Secondary",
                        Url = url,
                        Uuid = uuid ?? string.Empty,
                        Token = token ?? string.Empty
                    });
                }
            }

            return providers;
        }

        /// <summary>
        /// Builds a <see cref="ResolutionEntry"/> from the primary (rank=0) candidate.
        /// Populates flat fallback fields from ranks 1 and 2 for proxy backward compat.
        /// </summary>
        private static ResolutionEntry BuildEntryFromCandidates(
            List<StreamCandidate> candidates,
            PlayRequest           req,
            PluginConfiguration   config,
            string                tier)
        {
            var primary = candidates[0];
            var fb1     = candidates.Count > 1 ? candidates[1] : null;
            var fb2     = candidates.Count > 2 ? candidates[2] : null;

            return new ResolutionEntry
            {
                ImdbId          = req.Imdb,
                Season          = req.Season,
                Episode         = req.Episode,
                StreamUrl       = primary.Url,
                QualityTier     = primary.QualityTier,
                FileName        = primary.FileName,
                FileSize        = primary.FileSize,
                FileBitrateKbps = primary.BitrateKbps,
                Fallback1       = fb1?.Url,
                Fallback1Quality = fb1?.QualityTier,
                Fallback2       = fb2?.Url,
                Fallback2Quality = fb2?.QualityTier,
                TorrentHash     = null, // populated by season-pack detection in LinkResolverTask
                RdCached        = primary.IsCached ? 1 : 0,
                ResolutionTier  = tier,
                Status          = "valid",
                ExpiresAt       = primary.ExpiresAt,
            };
        }

        // ── Private: fire-and-forget tasks ──────────────────────────────────────

        private static async Task QueueResolutionAsync(
            PlayRequest req, DatabaseManager db, PluginConfiguration config, string tier)
        {
            // Mark for next LinkResolverTask run
            await db.MarkStreamStaleAsync(req.Imdb, req.Season, req.Episode);
        }

        private static async Task LogPlaybackAsync(
            DatabaseManager db,
            PlayRequest     req,
            ResolutionEntry entry,
            string          mode,
            string          clientType,
            DateTime        startTime)
        {
            var latencyMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
            await db.LogPlaybackAsync(new PlaybackEntry
            {
                ImdbId         = req.Imdb,
                Season         = req.Season,
                Episode        = req.Episode,
                ResolutionMode = mode,
                QualityServed  = entry.QualityTier,
                ClientType     = clientType,
                LatencyMs      = latencyMs,
            });
        }

        private static async Task LogFailureAsync(
            DatabaseManager db,
            PlayRequest     req,
            string          clientType,
            string          message)
        {
            await db.LogPlaybackAsync(new PlaybackEntry
            {
                ImdbId         = req.Imdb,
                Season         = req.Season,
                Episode        = req.Episode,
                ResolutionMode = "failed",
                ClientType     = clientType,
                ErrorMessage   = message,
            });
        }

        // ── Private: mapping ────────────────────────────────────────────────────

        /// <summary>
        /// Maps an AIOStreams stream response to a ranked candidate list and a primary
        /// <see cref="ResolutionEntry"/>.  Called by <see cref="Tasks.LinkResolverTask"/>
        /// which also needs the candidates for <c>stream_candidates</c> storage.
        /// Returns null when no playable stream can be extracted.
        /// </summary>
        public static (ResolutionEntry? Entry, List<StreamCandidate> Candidates)
            StreamResponseToEntryAndCandidates(
                AioStreamsStreamResponse response,
                PlayRequest              req,
                PluginConfiguration      config,
                string                   tier)
        {
            var candidates = StreamHelpers.RankAndFilterStreams(
                response, req.Imdb, req.Season, req.Episode,
                config.ProviderPriorityOrder,
                config.MaxFallbacksToStore,
                config.CacheLifetimeMinutes);

            if (candidates.Count == 0)
                return (null, candidates);

            // Preserve torrent hash from the original rank-0 stream for season-pack detection
            var primary   = response.Streams?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
            var entry     = BuildEntryFromCandidates(candidates, req, config, tier);
            entry.TorrentHash = primary?.InfoHash;

            return (entry, candidates);
        }

        // Keep the old single-return overload for callers that don't need candidates.
        // Delegates to the new method — no duplicate logic.
        /// <summary>
        /// Legacy overload: returns only the <see cref="ResolutionEntry"/>.
        /// New code should prefer <see cref="StreamResponseToEntryAndCandidates"/>.
        /// </summary>
        public static ResolutionEntry? StreamResponseToEntry(
            AioStreamsStreamResponse response,
            PlayRequest              req,
            PluginConfiguration      config,
            string                   tier)
        {
            var (entry, _) = StreamResponseToEntryAndCandidates(response, req, config, tier);
            return entry;
        }

        // ── Private: helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Searches the candidate list for a URL with the same info hash but from a
        /// different provider (to enable quick failover without re-calling AIOStreams).
        ///
        /// Returns the first matching candidate URL, or null if no match is found.
        /// This implements optimistic hash-based cross-provider failover: if all URLs
        /// from Provider A fail, we can immediately try Provider B's URL for the same
        /// content without waiting for a full sync resolve.
        /// </summary>
        private static string? GetCandidatesByHashExcludingProvider(
            List<StreamCandidate> candidates,
            string                targetHash,
            string                excludeUrl)
        {
            if (string.IsNullOrEmpty(targetHash)) return null;

            // Find a candidate with the same hash but different URL
            return candidates
                .FirstOrDefault(c =>
                    !string.IsNullOrEmpty(c.InfoHash)
                    && c.InfoHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(c.Url, excludeUrl, StringComparison.OrdinalIgnoreCase))
                ?.Url;
        }

        private string ExtractClientType()
        {
            var header = Request?.Headers?["X-Emby-Client"]
                      ?? Request?.Headers?["User-Agent"];
            return StreamHelpers.NormalizeClientType(header);
        }

        /// <summary>
        /// Builds the client compatibility key used as the primary key in <c>client_compat</c>.
        /// Combines the normalised client type with the client IP so that per-device learning
        /// is isolated — an Apple TV on one network no longer overwrites learning for a
        /// different Apple TV on another network.
        /// </summary>
        private string ExtractCompatKey()
        {
            var clientType = ExtractClientType();
            var ip = Request?.RemoteIp?.ToString() ?? string.Empty;
            return string.IsNullOrEmpty(ip) ? clientType : $"{clientType}:{ip}";
        }

        private static bool IsNotExpired(string expiresAt)
        {
            return DateTime.TryParse(expiresAt, out var dt)
                && DateTime.UtcNow < dt;
        }

        /// <summary>
        /// R1: Returns true when a cached URL has consumed more than 70% of its
        /// cache lifetime — the threshold at which a proactive range-probe is
        /// worthwhile.
        ///
        /// Rationale: Real-Debrid CDN URLs expire server-side at ~4–6 h after
        /// issuance.  With a 6 h (360 min) cache TTL the 70% gate fires at ≈252 min
        /// (4.2 h), giving a comfortable overlap with the RD expiry window.  URLs
        /// younger than this threshold are served immediately without an extra
        /// round-trip.  URLs older than the threshold are range-probed before the
        /// redirect is issued; if the probe fails the request falls through to
        /// <c>SyncResolveAsync</c> for a fresh URL.
        /// </summary>
        private static bool IsUrlAging(string resolvedAt, int cacheLifetimeMinutes)
        {
            if (cacheLifetimeMinutes <= 0) return false;
            if (!DateTime.TryParse(resolvedAt, out var resolved)) return false;
            var ageMinutes = (DateTime.UtcNow - resolved).TotalMinutes;
            return ageMinutes > cacheLifetimeMinutes * 0.7;
        }

        private static string ShortenUrl(string url)
            => url.Length > 60 ? url.Substring(0, 60) + "…" : url;

        private object Error400(string code, string message)
        {
            Request.Response.StatusCode = 400;
            return new PlayErrorResponse
            {
                Error             = code,
                Message           = message,
                RetryAfterSeconds = 0,
            };
        }

        private object Error401(string code, string message)
        {
            Request.Response.StatusCode = 401;
            return new PlayErrorResponse
            {
                Error             = code,
                Message           = message,
                RetryAfterSeconds = 0,
            };
        }

        /// <summary>HTTP 429 Too Many Requests (rate limited).</summary>
        private object Error429(string code, string message)
        {
            Request.Response.StatusCode = 429;
            return new PlayErrorResponse
            {
                Error             = code,
                Message           = message,
                RetryAfterSeconds = 60,
            };
        }

        /// <summary>
        /// Extracts the client IP address from the request.
        /// Checks X-Forwarded-For (proxy case) then falls back to RemoteIP.
        /// </summary>
        private string ExtractClientIp()
        {
            // Check for X-Forwarded-For header (common with reverse proxies)
            var forwarded = Request?.Headers?.Get("X-Forwarded-For");
            if (!string.IsNullOrEmpty(forwarded))
            {
                // Take first IP if comma-separated list
                var firstIp = forwarded.Split(',')[0].Trim();
                return firstIp;
            }

            return Request?.RemoteIp?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Checks if a request bucket (ip or user) has exceeded the rate limit.
        /// Returns true if within limit, false if exceeded.
        /// </summary>
        private bool CheckRateLimit(string bucketKey, int maxPerMinute)
        {
            lock (RateLimitLock)
            {
                var now = DateTime.UtcNow;

                // Lazy cleanup: purge old entries periodically (Sprint 104B-03)
                if (++_rateLimitAccessCount % RateLimitCleanupThreshold == 0)
                {
                    CleanupExpiredRateLimitEntries(now);
                }

                if (RateLimitBucket.TryGetValue(bucketKey, out var entry))
                {
                    // Check if we need to reset the bucket (1 minute elapsed)
                    if (now - entry.resetTime >= TimeSpan.FromMinutes(1))
                    {
                        // Reset the bucket
                        RateLimitBucket[bucketKey] = (1, now);
                        return true;
                    }

                    // Bucket hasn't expired, check count
                    if (entry.count >= maxPerMinute)
                    {
                        return false; // Rate limited
                    }

                    // Increment count
                    RateLimitBucket[bucketKey] = (entry.count + 1, entry.resetTime);
                    return true;
                }

                // First request for this bucket
                RateLimitBucket[bucketKey] = (1, now);
                return true;
            }
        }

        /// <summary>
        /// Redirects web-browser clients to the HHGTTG-styled Panic error page.
        /// Native Emby apps that follow the redirect and receive HTML will show their own
        /// error UI, which is no worse than a JSON 503 they'd ignore anyway.
        /// </summary>
        private object PanicRedirect(string code, string imdb, int retryAfter)
        {
            var config = Plugin.Instance?.Configuration;
            var baseUrl = config?.EmbyBaseUrl?.TrimEnd('/') ?? string.Empty;
            var url = string.IsNullOrEmpty(baseUrl)
                ? $"/EmbyStreams/Panic?reason={Uri.EscapeDataString(code)}&imdb={Uri.EscapeDataString(imdb)}&retry={retryAfter}"
                : $"{baseUrl}/EmbyStreams/Panic?reason={Uri.EscapeDataString(code)}&imdb={Uri.EscapeDataString(imdb)}&retry={retryAfter}";
            Request.Response.Redirect(url);
            return null!;
        }

        private static object Error503(string code, string message, int retryAfter)
        {
            // Fallback JSON response — used when Request context is unavailable.
            return new PlayErrorResponse
            {
                Error             = code,
                Message           = message,
                RetryAfterSeconds = retryAfter,
            };
        }

        private static bool IsValidImdbId(string imdb)
        {
            // Must start with "tt" followed by 1-8 digits
            if (imdb.Length < 3 || !imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                return false;
            for (int i = 2; i < imdb.Length; i++)
                if (!char.IsDigit(imdb[i])) return false;
            return imdb.Length <= 10; // tt + up to 8 digits
        }

        /// <summary>
        /// Removes old rate limit entries to prevent memory leaks (Sprint 104B-03).
        /// Entries older than 2 minutes are removed since rate limit reset is 1 minute.
        /// Called lazily every 100 accesses to avoid impacting performance.
        /// </summary>
        private static void CleanupExpiredRateLimitEntries(DateTime now)
        {
            var expiredKeys = new List<string>();
            var cutoff = now.AddMinutes(-2);

            foreach (var kvp in RateLimitBucket)
            {
                if (kvp.Value.resetTime < cutoff)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                RateLimitBucket.Remove(key);
            }
        }
    }
}
