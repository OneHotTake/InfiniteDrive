using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Resolve endpoint: queries AIOStreams with SEL, caches the playback URL,
    /// and returns a 302 redirect to the binary proxy (/Stream).
    /// </summary>
    public class ResolverService : IService, IRequiresRequest
    {
        private readonly ILogger<ResolverService> _logger;
        private readonly ResolverHealthTracker _healthTracker;
        private readonly RateLimiter _rateLimiter;
        private readonly IAuthorizationContext? _authCtx;

        // ── SEL expressions per quality tier ─────────────────────────────────

        internal static readonly Dictionary<string, string> TierToSel = new()
        {
            // 4K tiers
            ["4k_remux_hdr"] = "slice(resolution(visualTag(streams,'DV','HDR','HDR10+','Remux'),'2160p'),0,1)",
            ["4k_51"]        = "slice(resolution(streams,'2160p'),0,1)",
            ["4k_any"]       = "slice(resolution(streams,'2160p'),0,1)",
            // 1080p tiers
            ["1080p_atmos"]  = "slice(resolution(visualTag(streams,'Atmos','TrueHD'),'1080p'),0,1)",
            ["1080p_51"]     = "slice(resolution(streams,'1080p'),0,1)",
            ["1080p_any"]    = "slice(resolution(streams,'1080p'),0,1)",
            // Lower tiers
            ["720p"]         = "slice(resolution(streams,'720p'),0,1)",
            ["sd"]           = "slice(resolution(streams,'480p','unknown'),0,1)",
            // Legacy keys (kept for .strm files written by older versions)
            ["4k_hdr"]       = "slice(resolution(visualTag(streams,'DV','HDR','HDR10+'),'2160p'),0,1)",
            ["4k_sdr"]       = "slice(resolution(streams,'2160p'),0,1)",
            ["hd_broad"]     = "slice(resolution(streams,'1080p'),0,1)",
            ["sd_broad"]     = "slice(resolution(streams,'720p','480p'),0,1)",
            ["best_available"] = "slice(resolution(visualTag(streams,'DV','HDR','HDR10+'),'2160p'),0,1)",
            ["4k_dv"]        = "slice(resolution(visualTag(streams,'DV'),'2160p'),0,1)",
            ["hd_efficient"] = "slice(resolution(streams,'1080p'),0,1)",
            ["compact"]      = "slice(resolution(streams,'720p'),0,1)",
        };

        /// Map from UI display names (ContentControlsTabView.AllQualityTiers) to tier keys.
        internal static readonly Dictionary<string, string> UiTierNameToKey = new(StringComparer.OrdinalIgnoreCase)
        {
            ["4K REMUX / HDR / Atmos"] = "4k_remux_hdr",
            ["4K 5.1 / DTS"]          = "4k_51",
            ["4K (any)"]              = "4k_any",
            ["1080p Atmos / TrueHD"]  = "1080p_atmos",
            ["1080p 5.1"]             = "1080p_51",
            ["1080p (any)"]           = "1080p_any",
            ["720p"]                  = "720p",
            ["SD / Unknown / Low-bandwidth"] = "sd",
        };

        private const string AnySel = "slice(streams,0,1)";

        internal static readonly Dictionary<string, string[]> TierFallbacks = new()
        {
            // 4K tiers fall back to lower resolutions
            ["4k_remux_hdr"] = new[] { "4k_remux_hdr", "4k_51", "4k_any", "1080p_atmos", "1080p_51", "1080p_any", "720p", "sd" },
            ["4k_51"]        = new[] { "4k_51", "4k_any", "1080p_51", "1080p_any", "720p", "sd" },
            ["4k_any"]       = new[] { "4k_any", "1080p_any", "720p", "sd" },
            // 1080p tiers
            ["1080p_atmos"]  = new[] { "1080p_atmos", "1080p_51", "1080p_any", "720p", "sd" },
            ["1080p_51"]     = new[] { "1080p_51", "1080p_any", "720p", "sd" },
            ["1080p_any"]    = new[] { "1080p_any", "720p", "sd" },
            // Lower tiers
            ["720p"]         = new[] { "720p", "sd" },
            ["sd"]           = new[] { "sd" },
            // Legacy keys — map to equivalent fallback chains
            ["4k_hdr"]       = new[] { "4k_remux_hdr", "4k_51", "4k_any", "1080p_any", "720p", "sd" },
            ["4k_sdr"]       = new[] { "4k_any", "1080p_any", "720p", "sd" },
            ["hd_broad"]     = new[] { "1080p_any", "720p", "sd" },
            ["sd_broad"]     = new[] { "720p", "sd" },
            ["best_available"] = new[] { "4k_remux_hdr", "4k_51", "4k_any", "1080p_atmos", "1080p_51", "1080p_any", "720p", "sd" },
            ["4k_dv"]        = new[] { "4k_remux_hdr", "4k_51", "4k_any", "1080p_any", "720p", "sd" },
            ["hd_efficient"] = new[] { "1080p_any", "720p", "sd" },
            ["compact"]      = new[] { "720p", "sd" },
        };

        // ── Constructor ──────────────────────────────────────────────────────

        public ResolverService(ILogManager logManager, IAuthorizationContext? authCtx = null)
        {
            _logger = new EmbyLoggerAdapter<ResolverService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
            _healthTracker = Plugin.Instance?.ResolverHealthTracker
                ?? new ResolverHealthTracker(_logger);
            _rateLimiter = new RateLimiter(
                new EmbyLoggerAdapter<RateLimiter>(logManager.GetLogger("InfiniteDrive")),
                Array.Empty<string>());
        }

        public IRequest Request { get; set; } = null!;

        private PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // ── Main handler ─────────────────────────────────────────────────────

        public async Task<object> Get(ResolverRequest req)
        {
            // Rate limit
            var clientIp = RateLimiter.GetClientIp(Request);
            var rateLimitResult = _rateLimiter.CheckResolveLimit(clientIp);
            if (rateLimitResult != null)
                return rateLimitResult;

            // Validate input
            if (string.IsNullOrEmpty(req.Id) || string.IsNullOrEmpty(req.Quality))
                return Error(ResolverError.InvalidToken);

            // Normalize unknown quality keys to a sensible default
            if (!TierToSel.ContainsKey(req.Quality))
            {
                _logger.LogDebug("[InfiniteDrive][Resolve] Unknown quality '{Quality}', defaulting to 4k_any", req.Quality);
                req.Quality = "4k_any";
            }

            if (string.IsNullOrEmpty(Config.PluginSecret))
                return Error(ResolverError.AllResolversDown);

            if (!PlaybackTokenService.ValidateStreamToken(req.Token, Config.PluginSecret))
            {
                _logger.LogWarning("[InfiniteDrive][Resolve] Invalid or expired resolve token");
                return Error(ResolverError.InvalidToken);
            }

            // Cache-first: return cached URL as 302 redirect
            var db = Plugin.Instance?.DatabaseManager;
            var cached = await TryGetCachedUrlAsync(db, req);
            if (cached != null)
                return cached;

            // No cache — live resolve via AIOStreams
            _logger.LogInformation("[Resolve] No cache for {Id}:{Quality}, falling back to live resolve",
                req.Id, req.Quality);
            var resolved = await ResolveWithFallbackAsync(req);
            if (resolved != null)
            {
                // Cache for next time
                _ = CacheResolvedUrlAsync(db, req, resolved);
                return RedirectToStream(resolved.PlaybackUrl);
            }

            return Error(ResolverError.NoStreamsExist);
        }

        // ── SEL fallback resolution ──────────────────────────────────────────

        /// <summary>
        /// Resolves a single stream by walking the SEL fallback chain.
        /// Each tier is one AIOStreams call; stops on first hit.
        /// Falls back to "any" if all tiers miss.
        /// </summary>
        private async Task<ResolvedStream?> ResolveWithFallbackAsync(ResolverRequest req)
        {
            var providers = ProviderHelper.GetProviders(Config);
            if (providers.Count == 0)
            {
                _logger.LogError("[InfiniteDrive][Resolve] No providers configured");
                return null;
            }

            // Build the chain: requested tier → fallback tiers → any
            var chain = TierFallbacks.TryGetValue(req.Quality, out var tiers)
                ? tiers.ToList() : new List<string> { req.Quality };

            // When UseRemuxForAutoSelection is false, skip REMUX tiers from the chain
            if (!Config.UseRemuxForAutoSelection)
                chain = chain.Where(t => !t.Contains("remux", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var tier in chain)
            {
                var sel = TierToSel[tier];
                var result = await TryProvidersAsync(providers, req, sel);
                if (result != null)
                {
                    LogResolution(req, tier, result);
                    return result;
                }
            }

            // Last resort: any stream
            var anyResult = await TryProvidersAsync(providers, req, AnySel);
            if (anyResult != null)
            {
                LogResolution(req, "any", anyResult);
                return anyResult;
            }

            _logger.LogError("[InfiniteDrive][Resolve] No streams found for {MediaType}:{Id} (tried {Count} tiers)",
                req.IdType, req.Id, chain.Count + 1);
            return null;
        }

        /// <summary>
        /// Tries each configured AIOStreams provider with the given SEL expression.
        /// Returns the first stream found, or null.
        /// </summary>
        private async Task<ResolvedStream?> TryProvidersAsync(
            List<ProviderInfo> providers, ResolverRequest req, string sel)
        {
            foreach (var provider in providers)
            {
                if (_healthTracker.ShouldSkip(provider.DisplayName))
                {
                    _logger.LogDebug("[InfiniteDrive][Resolve] Skipping {Name} — circuit open", provider.DisplayName);
                    continue;
                }

                try
                {
                    using var client = new AioStreamsClient(
                        provider.Url, provider.Uuid, provider.Token, _logger);

                    AioStreamsStreamResponse? response;

                    if (req.IdType == "series" && req.Season.HasValue && req.Episode.HasValue)
                        response = await client.GetSeriesStreamsAsync(req.Id, req.Season.Value, req.Episode.Value, sel: sel);
                    else
                        response = await client.GetMovieStreamsAsync(req.Id, sel: sel);

                    var streams = response?.Streams;
                    if (streams != null && streams.Count > 0)
                    {
                        var stream = streams[0];
                        _healthTracker.RecordSuccess(provider.DisplayName);

                        return new ResolvedStream
                        {
                            PlaybackUrl = stream.Url ?? string.Empty,
                            FileName = stream.BehaviorHints?.Filename,
                            ProviderName = provider.DisplayName
                        };
                    }
                }
                catch (Exception ex)
                {
                    _healthTracker.RecordFailure(provider.DisplayName);
                    _logger.LogError(ex, "[InfiniteDrive][Resolve] Provider {Name} failed for {Id}",
                        provider.DisplayName, req.Id);

                    if (ex.GetType().Name.Contains("RateLimit"))
                        return null;
                }
            }

            return null;
        }

        private void LogResolution(ResolverRequest req, string servedTier, ResolvedStream result)
        {
            var mediaLabel = req.IdType == "series"
                ? $"series:{req.Id}:S{req.Season}E{req.Episode}"
                : $"movie:{req.Id}";

            if (servedTier != req.Quality)
            {
                _logger.LogWarning(
                    "QUALITY_FALLBACK: Requested '{Requested}' but served '{Served}' for {Media}. " +
                    "Filename: \"{Filename}\"",
                    req.Quality, servedTier, mediaLabel,
                    result.FileName ?? "unknown");
            }
            else
            {
                _logger.LogInformation(
                    "Serving {Tier} for {Media}", servedTier, mediaLabel);
            }
        }

        // ── Cache ────────────────────────────────────────────────────────────

        /// <summary>
        /// Looks up a cached AIOStreams playback URL. If found, returns a 302
        /// redirect to the binary proxy. Returns null on miss.
        /// Cache is forever — URLs are validated at stream time, not resolve time.
        /// </summary>
        private async Task<object?> TryGetCachedUrlAsync(
            Data.DatabaseManager? db, ResolverRequest req)
        {
            // Try stream_candidates first (populated by live resolve)
            var candidate = await TryGetFromStreamCandidatesAsync(db, req);
            if (candidate != null)
                return candidate;

            // Fallback to cached_streams (populated by pre-cache task)
            return await TryGetFromPreCacheAsync(req);
        }

        private async Task<object?> TryGetFromStreamCandidatesAsync(
            Data.DatabaseManager? db, ResolverRequest req)
        {
            if (db == null) return null;

            try
            {
                var candidates = await db.GetStreamCandidatesAsync(
                    req.Id, req.Season, req.Episode);

                var validCandidates = candidates?
                    .Where(c => c.Status == "valid" && !string.IsNullOrEmpty(c.Url))
                    .ToList();

                // Respect REMUX setting — same filter as SelectBest
                if (!Config.UseRemuxForAutoSelection && validCandidates != null)
                    validCandidates = validCandidates
                        .Where(c => !StreamHelpers.IsRemuxFile(c.FileName)).ToList();

                if (validCandidates == null || validCandidates.Count == 0)
                    return null;

                // Filter by quality tier fallback chain
                var matched = FilterByQualityTier(validCandidates, req.Quality);
                if (matched == null || matched.Count == 0)
                    return null;

                var top = PreferLanguageMatch(matched);
                if (top == null)
                    return null;

                var mediaLabel = req.IdType == "series"
                    ? $"series:{req.Id}:S{req.Season}E{req.Episode}"
                    : $"movie:{req.Id}";

                _logger.LogInformation(
                    "[Resolve] Stream-candidates hit for {Media} — age: {Age}",
                    mediaLabel, FormatAge(top.ResolvedAt));

                return RedirectToStream(top.Url);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] stream_candidates lookup failed for {Id}", req.Id);
                return null;
            }
        }

        /// <summary>
        /// Fallback: reads cached_streams (pre-cache table) and returns rank-0
        /// from the scoring service, matching the user's quality preferences.
        /// </summary>
        private async Task<object?> TryGetFromPreCacheAsync(ResolverRequest req)
        {
            try
            {
                var streamCache = Plugin.Instance?.StreamCacheService;
                if (streamCache == null) return null;

                var entry = await streamCache.GetByImdbAsync(req.Id, req.Season, req.Episode);
                if (entry == null || string.IsNullOrEmpty(entry.VariantsJson)) return null;

                var variants = System.Text.Json.JsonSerializer
                    .Deserialize<List<StreamVariant>>(entry.VariantsJson);
                if (variants == null || variants.Count == 0) return null;

                // Convert variants to StreamCandidates for scoring
                var candidates = variants
                    .Where(v => !string.IsNullOrEmpty(v.Url))
                    .Select((v, i) => new StreamCandidate
                    {
                        ImdbId = req.Id,
                        Season = req.Season,
                        Episode = req.Episode,
                        Rank = i,
                        Url = v.Url ?? "",
                        FileName = v.FileName,
                        QualityTier = v.QualityTier,
                        Status = "valid",
                        FileSize = v.SizeBytes,
                    })
                    .ToList();

                // REMUX filter
                if (!Config.UseRemuxForAutoSelection)
                    candidates = candidates
                        .Where(c => !StreamHelpers.IsRemuxFile(c.FileName)).ToList();

                if (candidates.Count == 0) return null;

                // Use scoring service to pick the best one (respects DefaultSlotKey)
                var scoring = new Scoring.StreamScoringService(_logger, Config);
                var best = scoring.SelectBest(candidates);
                if (best.Count == 0) return null;

                var top = best[0];
                var mediaLabel = req.IdType == "series"
                    ? $"series:{req.Id}:S{req.Season}E{req.Episode}"
                    : $"movie:{req.Id}";

                _logger.LogInformation(
                    "[Resolve] Pre-cache hit for {Media} — {Name} (age: {Age})",
                    mediaLabel, top.FileName ?? "unknown", FormatAge(entry.CachedAt));

                return RedirectToStream(top.Url);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] Pre-cache lookup failed for {Id}", req.Id);
                return null;
            }
        }

        /// <summary>
        /// Filters cached candidates by matching their QualityTier against the
        /// requested tier's fallback chain. Returns candidates ordered by tier preference.
        /// </summary>
        private List<StreamCandidate>? FilterByQualityTier(List<StreamCandidate> candidates, string? requestedQuality)
        {
            if (string.IsNullOrEmpty(requestedQuality) || !TierFallbacks.TryGetValue(requestedQuality, out var tiers))
                return candidates; // No quality filter — return all

            // Build a resolution set from the fallback chain tiers
            var resolutionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tier in tiers)
            {
                resolutionSet.Add(tier);
                // Also add the resolution label (e.g. "1080p_any" → "1080p")
                var res = tier.Split('_')[0];
                if (!string.IsNullOrEmpty(res))
                    resolutionSet.Add(res);
            }

            var matched = candidates
                .Where(c => !string.IsNullOrEmpty(c.QualityTier) &&
                            resolutionSet.Contains(c.QualityTier))
                .ToList();

            return matched.Count > 0 ? matched : null;
        }

        private StreamCandidate PreferLanguageMatch(List<StreamCandidate> candidates)
        {
            if (candidates.Count == 1)
                return candidates[0];

            string? userLang = null;
            try
            {
                if (_authCtx != null && Request != null)
                {
                    var authInfo = _authCtx.GetAuthorizationInfo(Request);
                    var user = authInfo?.User;
                    userLang = user?.PreferredMetadataLanguage;
                }
            }
            catch { /* non-critical */ }

            if (string.IsNullOrEmpty(userLang))
                userLang = Config.MetadataLanguage;

            if (string.IsNullOrEmpty(userLang))
                return candidates[0];

            // Prefer first candidate whose Languages contains user's preferred language
            var match = candidates.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.Languages) &&
                c.Languages.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Any(l => string.Equals(l.Trim(), userLang, StringComparison.OrdinalIgnoreCase)));

            if (match != null)
            {
                _logger.LogDebug("[Resolve] Preferring language-matched candidate ({Lang}) for {Id}",
                    userLang, match.ImdbId);
                return match;
            }

            return candidates[0];
        }

        /// <summary>
        /// Writes the resolved stream URL to cache. Fire-and-forget.
        /// Stores a single candidate (rank 0) — the one we resolved via SEL.
        /// </summary>
        private async Task CacheResolvedUrlAsync(
            Data.DatabaseManager? db, ResolverRequest req, ResolvedStream resolved)
        {
            if (db == null) return;

            try
            {
                var now = DateTime.UtcNow;

                var entry = new ResolutionEntry
                {
                    ImdbId = req.Id,
                    Season = req.Season,
                    Episode = req.Episode,
                    StreamUrl = resolved.PlaybackUrl,
                    QualityTier = req.Quality,
                    FileName = resolved.FileName,
                    Status = "valid",
                    ResolvedAt = now.ToString("o"),
                    ExpiresAt = now.AddYears(1).ToString("o"), // effectively forever
                    ResolutionTier = "sel"
                };

                var candidate = new StreamCandidate
                {
                    ImdbId = req.Id,
                    Season = req.Season,
                    Episode = req.Episode,
                    Rank = 0,
                    ProviderKey = resolved.ProviderName?.ToLowerInvariant() ?? "unknown",
                    StreamType = "debrid",
                    Url = resolved.PlaybackUrl,
                    QualityTier = req.Quality,
                    FileName = resolved.FileName,
                    Status = "valid",
                    ResolvedAt = now.ToString("o"),
                    ExpiresAt = now.AddYears(1).ToString("o")
                };

                await db.UpsertResolutionResultAsync(entry, new List<StreamCandidate> { candidate });
                _logger.LogDebug("[Resolve] Cached URL for {Id}:{Quality}", req.Id, req.Quality);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] Cache write failed for {Id} (non-fatal)", req.Id);
            }
        }

        // ── Redirect helper ──────────────────────────────────────────────────

        /// <summary>
        /// Returns a 302 redirect to /InfiniteDrive/Stream with the upstream URL signed.
        /// ffprobe and ffmpeg follow HTTP redirects natively.
        /// </summary>
        private object RedirectToStream(string upstreamUrl)
        {
            Request.Response.StatusCode = 302;
            Request.Response.AddHeader("Location", upstreamUrl);

            return Array.Empty<byte>();
        }

        // ── Result model ─────────────────────────────────────────────────────

        private class ResolvedStream
        {
            public string PlaybackUrl { get; set; } = string.Empty;
            public string? FileName { get; set; }
            public string? ProviderName { get; set; }
        }

        // ── Utility ──────────────────────────────────────────────────────────

        private static string FormatAge(string? isoTimestamp)
        {
            if (string.IsNullOrEmpty(isoTimestamp)) return "unknown";
            if (!DateTime.TryParse(isoTimestamp, out var resolvedAt)) return "unknown";
            var age = DateTime.UtcNow - resolvedAt;
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
            return $"{(int)age.TotalDays}d";
        }

        // ── Error helper ─────────────────────────────────────────────────────

        private static object Error(ResolverError error)
        {
            return new
            {
                StatusCode = error switch
                {
                    ResolverError.InvalidToken => 401,
                    ResolverError.NoStreamsExist or ResolverError.QualityMismatch => 404,
                    ResolverError.PrimaryResolverDown or ResolverError.AllResolversDown or ResolverError.RateLimited => 503,
                    _ => 500
                },
                ErrorCode = error.ToString().ToLowerInvariant(),
                ErrorMessage = error switch
                {
                    ResolverError.InvalidToken => ResolverErrorMessages.InvalidToken,
                    ResolverError.NoStreamsExist => ResolverErrorMessages.NoStreamsExist,
                    ResolverError.QualityMismatch => ResolverErrorMessages.QualityMismatch,
                    ResolverError.PrimaryResolverDown => ResolverErrorMessages.PrimaryResolverDown,
                    ResolverError.AllResolversDown => ResolverErrorMessages.AllResolversDown,
                    ResolverError.RateLimited => ResolverErrorMessages.RateLimited,
                    _ => "Unknown error"
                }
            };
        }
    }
}
