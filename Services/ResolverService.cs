using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Net;
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

        // ── SEL expressions per quality tier ─────────────────────────────────

        internal static readonly Dictionary<string, string> TierToSel = new()
        {
            ["4k_hdr"]   = "slice(resolution(visualTag(streams,'DV','HDR','HDR10+'),'2160p'),0,1)",
            ["4k_sdr"]   = "slice(resolution(streams,'2160p'),0,1)",
            ["hd_broad"] = "slice(resolution(streams,'1080p'),0,1)",
            ["sd_broad"] = "slice(resolution(streams,'720p','480p'),0,1)",
        };

        private const string AnySel = "slice(streams,0,1)";

        internal static readonly Dictionary<string, string[]> TierFallbacks = new()
        {
            ["4k_hdr"]   = new[] { "4k_hdr", "4k_sdr", "hd_broad", "sd_broad" },
            ["4k_sdr"]   = new[] { "4k_sdr", "hd_broad", "sd_broad" },
            ["hd_broad"] = new[] { "hd_broad", "sd_broad" },
            ["sd_broad"] = new[] { "sd_broad" },
        };

        // ── Constructor ──────────────────────────────────────────────────────

        public ResolverService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<ResolverService>(logManager.GetLogger("InfiniteDrive"));
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

            if (!TierToSel.ContainsKey(req.Quality))
                return Error(ResolverError.InvalidToken);

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

            // Cache miss — resolve via SEL fallback chain
            var resolved = await ResolveWithFallbackAsync(req);
            if (resolved == null)
                return Error(ResolverError.NoStreamsExist);

            // Cache the resolved URL (fire-and-forget)
            _ = CacheResolvedUrlAsync(db, req, resolved);

            // Binge prefetch: fire-and-forget resolve of next episode
            if (req.IdType == "series" && req.Season.HasValue && req.Episode.HasValue)
                _ = BingePrefetchService.PrefetchNextEpisodeAsync(
                    req.Id, req.Season.Value, req.Episode.Value,
                    _logger);

            // 302 redirect to binary proxy
            return RedirectToStream(resolved.PlaybackUrl);
        }

        // ── SEL fallback resolution ──────────────────────────────────────────

        /// <summary>
        /// Resolves a single stream by walking the SEL fallback chain.
        /// Each tier is one AIOStreams call; stops on first hit.
        /// Falls back to "any" if all tiers miss.
        /// </summary>
        private async Task<ResolvedStream?> ResolveWithFallbackAsync(ResolverRequest req)
        {
            var providers = GetProvidersToTry();
            if (providers.Count == 0)
            {
                _logger.LogError("[InfiniteDrive][Resolve] No providers configured");
                return null;
            }

            // Build the chain: requested tier → fallback tiers → any
            var chain = TierFallbacks.TryGetValue(req.Quality, out var tiers)
                ? tiers.ToList() : new List<string> { req.Quality };

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
            if (db == null) return null;

            try
            {
                var candidates = await db.GetStreamCandidatesAsync(
                    req.Id, req.Season, req.Episode);

                var top = candidates?
                    .FirstOrDefault(c => c.Status == "valid" && !string.IsNullOrEmpty(c.Url));

                if (top == null)
                    return null;

                var mediaLabel = req.IdType == "series"
                    ? $"series:{req.Id}:S{req.Season}E{req.Episode}"
                    : $"movie:{req.Id}";

                _logger.LogInformation(
                    "[Resolve] Cache hit for {Media} — serving cached URL (age: {Age})",
                    mediaLabel,
                    FormatAge(top.ResolvedAt));

                return RedirectToStream(top.Url);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] Cache lookup failed for {Id}, falling through to live", req.Id);
                return null;
            }
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
            var signed = PlaybackTokenService.Sign(upstreamUrl, Config.PluginSecret, 1);
            var proxyUrl = $"{Config.EmbyBaseUrl.TrimEnd('/')}/InfiniteDrive/Stream?url={Uri.EscapeDataString(signed)}";

            Request.Response.StatusCode = 302;
            Request.Response.AddHeader("Location", proxyUrl);

            // Return empty byte array — Emby's pipeline serializes this as the
            // response body while respecting the StatusCode and Location headers.
            return Array.Empty<byte>();
        }

        // ── Provider list ────────────────────────────────────────────────────

        private List<ProviderInfo> GetProvidersToTry()
        {
            var providers = new List<ProviderInfo>();

            if (!string.IsNullOrWhiteSpace(Config.PrimaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(Config.PrimaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    providers.Add(new ProviderInfo
                    {
                        DisplayName = "Primary",
                        Url = url,
                        Uuid = uuid ?? string.Empty,
                        Token = token ?? string.Empty
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(Config.SecondaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(Config.SecondaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    providers.Add(new ProviderInfo
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

        private class ProviderInfo
        {
            public string DisplayName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Uuid { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
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
