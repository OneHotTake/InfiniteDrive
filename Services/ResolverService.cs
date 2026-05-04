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
    public partial class ResolverService : IService, IRequiresRequest
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
                    using var client = AioStreamsClientFactory.CreateForProvider(provider, _logger);

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
                        continue;
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

        // ── Cache operations → ResolverService.Cache.cs

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
