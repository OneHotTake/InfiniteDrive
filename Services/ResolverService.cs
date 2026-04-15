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
    /// Resolve endpoint service for AIOStreams stream resolution.
    /// Queries AIOStreams API, filters by quality tier, and returns M3U8 manifest.
    /// </summary>
    public class ResolverService : IService, IRequiresRequest
    {
        private readonly ILogger<ResolverService> _logger;
        private readonly M3u8Builder _m3u8Builder;
        private readonly ResolverHealthTracker _healthTracker;
        private readonly RateLimiter _rateLimiter;

        public ResolverService(ILogManager logManager, ILogger<RateLimiter> rateLimiterLogger)
        {
            _logger = new EmbyLoggerAdapter<ResolverService>(logManager.GetLogger("InfiniteDrive"));
            _m3u8Builder = new M3u8Builder();
            _healthTracker = Plugin.Instance?.ResolverHealthTracker
                ?? new ResolverHealthTracker(_logger);
            _rateLimiter = new RateLimiter(rateLimiterLogger, Array.Empty<string>());
        }

        public IRequest Request { get; set; } = null!;

        private PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        /// <summary>
        /// Handles GET /InfiniteDrive/Resolve endpoint.
        /// </summary>
        public async Task<object> Get(ResolverRequest req)
        {
            // Sprint 302-05: Rate limit check
            var clientIp = RateLimiter.GetClientIp(Request);
            var rateLimitResult = _rateLimiter.CheckResolveLimit(clientIp);
            if (rateLimitResult != null)
                return rateLimitResult;

            // 1. Validate input
            if (string.IsNullOrEmpty(req.Id))
            {
                return Error(ResolverError.InvalidToken); // 400 - bad request
            }

            if (string.IsNullOrEmpty(req.Quality))
            {
                return Error(ResolverError.InvalidToken); // 400 - bad request
            }

            // Validate quality tier
            if (!M3u8Builder.TierMetadata.ContainsKey(req.Quality))
            {
                return Error(ResolverError.InvalidToken); // 400 - bad request
            }

            // Validate resolve token (Sprint 140A-03)
            _logger.LogInformation("[Resolve] PluginSecret is empty: {IsEmpty}, length: {Length}",
                string.IsNullOrEmpty(Config.PluginSecret),
                Config.PluginSecret?.Length ?? 0);

            if (string.IsNullOrEmpty(Config.PluginSecret))
            {
                return Error(ResolverError.AllResolversDown); // 500 - server error
            }

            if (!PlaybackTokenService.ValidateStreamToken(req.Token, Config.PluginSecret))
            {
                _logger.LogWarning("[InfiniteDrive][Resolve] Invalid or expired resolve token");
                return Error(ResolverError.InvalidToken);
            }

            // 2. Cache-first: try cached candidates before hitting AIOStreams
            var db = Plugin.Instance?.DatabaseManager;
            var cachedPlaylist = await TryServeFromCacheAsync(db, req);
            if (cachedPlaylist != null)
                return cachedPlaylist;

            // 3. Cache miss — resolve live from AIOStreams (with provider fallback)
            var (streams, resolverError) = await ResolveStreamsAsync(req);
            if (resolverError.HasValue)
            {
                return Error(resolverError.Value);
            }

            if (streams == null || streams.Count == 0)
            {
                _logger.LogWarning("[InfiniteDrive][Resolve] No streams found for {Id}", req.Id);
                return Error(ResolverError.NoStreamsExist);
            }

            // 4. Filter by quality tier with fallback chain
            var (filtered, usedFallback) = FilterStreamsWithFallback(streams, req.Quality, req.Id);
            if (filtered.Count == 0)
            {
                _logger.LogWarning("[InfiniteDrive][Resolve] No streams found for {Id}", req.Id);
                return Error(ResolverError.QualityMismatch);
            }

            // 5. Probe top candidates and reorder — dead URLs sink to back of list.
            //    Emby client sees working streams first; dead ones remain as fallback.
            //    5s total budget shared across all probes (Sprint 302).
            filtered = await ProbeAndReorderAsync(filtered);

            // 6. Select top stream per source
            var variants = SelectTopStreams(filtered);

            // 7. Write live results to cache (fire-and-forget)
            _ = WriteToCacheAsync(db, req, variants);

            // 8. Build M3U8 playlist with signed URLs
            var playlist = BuildPlaylist(variants, req.Quality);

            return new
            {
                ContentType = M3u8Builder.MimeType,
                Content = playlist,
                StatusCode = 200
            };
        }

        /// <summary>
        /// Resolves streams from AIOStreams API with provider fallback (primary → secondary).
        /// Returns (streams, error) tuple where error is null on success.
        /// Sprint 310: Unifies with StreamResolutionHelper provider iteration pattern.
        /// </summary>
        private async Task<(List<AioStreamsStream> streams, ResolverError? error)> ResolveStreamsAsync(ResolverRequest req)
        {
            var providers = GetProvidersToTry();

            foreach (var provider in providers)
            {
                var providerKey = provider.DisplayName ?? "unknown";

                // Circuit breaker check
                if (_healthTracker.ShouldSkip(providerKey))
                {
                    _logger.LogDebug("[InfiniteDrive][Resolve] Skipping {Name} — circuit open", providerKey);
                    continue;
                }

                try
                {
                    using var client = new AioStreamsClient(
                        provider.Url, provider.Uuid, provider.Token, _logger);
                    AioStreamsStreamResponse? response;

                    if (req.IdType == "series" && req.Season.HasValue && req.Episode.HasValue)
                        response = await client.GetSeriesStreamsAsync(req.Id, req.Season.Value, req.Episode.Value);
                    else
                        response = await client.GetMovieStreamsAsync(req.Id);

                    var streams = response?.Streams ?? new List<AioStreamsStream>();
                    if (streams.Count > 0)
                    {
                        _healthTracker.RecordSuccess(providerKey);
                        return (streams, null);
                    }
                }
                catch (Exception ex)
                {
                    _healthTracker.RecordFailure(providerKey);
                    _logger.LogError(ex, "[InfiniteDrive][Resolve] Provider {Name} failed for {Id}", providerKey, req.Id);

                    if (ex.GetType().Name.Contains("RateLimit"))
                        return (new List<AioStreamsStream>(), ResolverError.RateLimited);
                }
            }

            _logger.LogError("[InfiniteDrive][Resolve] All providers exhausted for {Id}", req.Id);
            return (new List<AioStreamsStream>(), ResolverError.AllResolversDown);
        }

        /// <summary>
        /// Builds the ordered list of providers to try for stream resolution.
        /// </summary>
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

        /// <summary>
        /// Probes the top 3 stream URLs and reorders the list so live streams
        /// come first. Dead URLs sink to the back rather than being removed —
        /// the Emby client can still attempt them as a last resort.
        /// Total probe budget: 5s across all probes.
        /// </summary>
        private async Task<List<AioStreamsStream>> ProbeAndReorderAsync(
            List<AioStreamsStream> streams)
        {
            var probe = Plugin.Instance?.StreamProbeService;
            if (probe == null) return streams; // probe not available — pass through

            var toProbe = streams.Take(3).ToList();
            var rest    = streams.Skip(3).ToList();
            var live    = new List<AioStreamsStream>();
            var dead    = new List<AioStreamsStream>();

            using var cts = new System.Threading.CancellationTokenSource(5000);

            foreach (var stream in toProbe)
            {
                if (string.IsNullOrEmpty(stream.Url)) { dead.Add(stream); continue; }
                try
                {
                    var result = await probe.ProbeAsync(stream.Url, cts.Token);
                    if (result.Ok) live.Add(stream);
                    else           dead.Add(stream);
                }
                catch (OperationCanceledException)
                {
                    // Budget exhausted for this probe — move to next, don't kill remaining
                    dead.Add(stream);
                    continue;
                }
            }

            // live probed → dead probed → unprobed tail (untouched)
            return live.Concat(dead).Concat(rest).ToList();
        }

        /// <summary>
        /// Filters streams by quality tier with fallback chain: 4k_hdr → 4k_sdr → hd_broad → sd_broad → any
        /// Returns filtered streams and whether fallback was used.
        /// </summary>
        private (List<AioStreamsStream> streams, bool usedFallback) FilterStreamsWithFallback(
            List<AioStreamsStream> streams,
            string requestedTier,
            string mediaId)
        {
            var tierChain = new[] { "4k_hdr", "4k_sdr", "hd_broad", "sd_broad" };
            var requestedIndex = Array.IndexOf(tierChain, requestedTier);

            // Try requested tier first
            var filtered = streams
                .Where(s => M3u8Builder.MapStreamToTier(s) == requestedTier)
                .ToList();

            if (filtered.Count > 0)
            {
                return (filtered, false);
            }

            // Fallback down the chain
            for (int i = requestedIndex + 1; i < tierChain.Length; i++)
            {
                var fallbackTier = tierChain[i];
                var tierStreams = streams
                    .Where(s => M3u8Builder.MapStreamToTier(s) == fallbackTier)
                    .ToList();

                if (tierStreams.Count > 0)
                {
                    var tierMeta = M3u8Builder.TierMetadata.GetValueOrDefault(fallbackTier);
                    _logger.LogInformation("[InfiniteDrive][Resolve] Quality fallback for {MediaId}: {Requested} → {Actual} ({DisplayName})",
                        mediaId, requestedTier, fallbackTier, tierMeta?.DisplayName ?? fallbackTier);
                    return (tierStreams, true);
                }
            }

            // Final fallback: return all streams (best effort)
            _logger.LogWarning("[InfiniteDrive][Resolve] No quality matches for {MediaId}, returning all streams as fallback", mediaId);
            return (streams, true);
        }

        /// <summary>
        /// Selects top stream per source based on quality and availability.
        /// </summary>
        private List<AioStreamsStream> SelectTopStreams(List<AioStreamsStream> streams)
        {
            // Group by source (addon)
            var grouped = streams
                .GroupBy(s => M3u8Builder.GetSourceName(s))
                .Select(g => g.OrderByDescending(s => s.ParsedFile?.Resolution ?? string.Empty).FirstOrDefault())
                .Where(s => s != null)
                .Cast<AioStreamsStream>()
                .ToList();

            return grouped;
        }

        /// <summary>
        /// Builds M3U8 playlist from selected streams.
        /// </summary>
        private string BuildPlaylist(List<AioStreamsStream> streams, string quality)
        {
            var variants = streams
                .Where(s => !string.IsNullOrEmpty(s.Url))
                .Select(s =>
                {
                    // Sign stream URL
                    var signedUrl = PlaybackTokenService.Sign(s.Url!, Config.PluginSecret, 1);
                    var proxyUrl = $"{Config.EmbyBaseUrl.TrimEnd('/')}/InfiniteDrive/Stream?url={Uri.EscapeDataString(signedUrl)}";

                    // Get resolution from parsed metadata
                    var resolution = s.ParsedFile?.Resolution ?? string.Empty;
                    if (string.IsNullOrEmpty(resolution))
                    {
                        resolution = M3u8Builder.TierMetadata[quality].Resolution;
                    }

                    // Estimate bandwidth from resolution
                    var bandwidth = EstimateBandwidth(resolution);

                    // Build display name
                    var sourceName = M3u8Builder.GetSourceName(s);
                    var displayName = $"{sourceName} - {resolution}";

                    if (s.ParsedFile?.Quality != null)
                    {
                        displayName += $" [{s.ParsedFile.Quality}]";
                    }

                    return new M3U8Variant
                    {
                        DisplayName = displayName,
                        SourceName = sourceName,
                        Url = proxyUrl,
                        Bandwidth = bandwidth,
                        Resolution = resolution,
                        IsHevc = M3u8Builder.IsHevcStream(s),
                        IsHdr = M3u8Builder.MapStreamToTier(s) == "4k_hdr"
                    };
                })
                .ToList();

            return _m3u8Builder.CreateVariantPlaylist(Config.EmbyBaseUrl, quality, variants);
        }

        // ── Cache-first path ────────────────────────────────────────────────────

        /// <summary>
        /// Checks the resolution cache for valid candidates. If the top-ranked
        /// candidate probes alive, builds and returns an M3U8 playlist immediately
        /// without hitting AIOStreams. Returns null on cache miss or dead probe.
        /// </summary>
        private async Task<object?> TryServeFromCacheAsync(
            Data.DatabaseManager? db, ResolverRequest req)
        {
            if (db == null) return null;

            try
            {
                var candidates = await db.GetStreamCandidatesAsync(
                    req.Id, req.Season, req.Episode);

                if (candidates == null || candidates.Count == 0)
                    return null;

                // Filter to valid candidates only — the probe is the real validation
                var validCandidates = candidates
                    .Where(c => c.Status == "valid" && !string.IsNullOrEmpty(c.Url))
                    .OrderBy(c => c.Rank)
                    .ToList();

                if (validCandidates.Count == 0)
                    return null;

                // Probe ONLY the top-ranked candidate (2s budget)
                var top = validCandidates[0];
                var probe = Plugin.Instance?.StreamProbeService;
                if (probe != null)
                {
                    using var cts = new System.Threading.CancellationTokenSource(2000);
                    try
                    {
                        var result = await probe.ProbeAsync(top.Url, cts.Token);
                        if (!result.Ok)
                        {
                            _logger.LogDebug("[Resolve] Cache probe failed for {Url}, falling through to live", top.Url);
                            return null;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("[Resolve] Cache probe timed out for {Url}, falling through to live", top.Url);
                        return null;
                    }
                }

                // Probe passed — build M3U8 from cached candidates
                _logger.LogInformation(
                    "[Resolve] Cache hit for {Id} S{S}E{E} — serving {Count} cached candidates",
                    req.Id, req.Season, req.Episode, validCandidates.Count);

                var playlist = BuildPlaylistFromCandidates(validCandidates, req.Quality);
                return new
                {
                    ContentType = M3u8Builder.MimeType,
                    Content = playlist,
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] Cache lookup failed for {Id}, falling through to live", req.Id);
                return null;
            }
        }

        /// <summary>
        /// Builds an M3U8 playlist from cached StreamCandidate entries.
        /// </summary>
        private string BuildPlaylistFromCandidates(
            List<StreamCandidate> candidates, string quality)
        {
            var variants = candidates
                .Where(c => !string.IsNullOrEmpty(c.Url))
                .Select(c =>
                {
                    var signedUrl = PlaybackTokenService.Sign(c.Url!, Config.PluginSecret, 1);
                    var proxyUrl = $"{Config.EmbyBaseUrl.TrimEnd('/')}/InfiniteDrive/Stream?url={Uri.EscapeDataString(signedUrl)}";

                    var resolution = !string.IsNullOrEmpty(c.QualityTier)
                        ? MapTierToResolution(c.QualityTier)
                        : M3u8Builder.TierMetadata[quality].Resolution;

                    var bandwidth = EstimateBandwidth(resolution);
                    var displayName = $"{c.ProviderKey} - {resolution}";
                    if (!string.IsNullOrEmpty(c.FileName))
                        displayName += $" [{c.FileName}]";

                    return new M3U8Variant
                    {
                        DisplayName = displayName,
                        SourceName = c.ProviderKey,
                        Url = proxyUrl,
                        Bandwidth = bandwidth,
                        Resolution = resolution,
                        IsHevc = false,
                        IsHdr = c.QualityTier == "4k_hdr"
                    };
                })
                .ToList();

            return _m3u8Builder.CreateVariantPlaylist(Config.EmbyBaseUrl, quality, variants);
        }

        /// <summary>
        /// Maps a quality tier string from cache to a resolution label.
        /// </summary>
        private static string MapTierToResolution(string tier) => tier switch
        {
            "remux" or "2160p" or "4k" => "4K",
            "1080p" => "1080p",
            "720p" => "720p",
            "480p" => "480p",
            _ => "1080p"
        };

        /// <summary>
        /// Writes live resolution results to cache. Fire-and-forget — never
        /// blocks or fails the playback response.
        /// </summary>
        private async Task WriteToCacheAsync(
            Data.DatabaseManager? db, ResolverRequest req, List<AioStreamsStream> streams)
        {
            if (db == null || streams.Count == 0) return;

            try
            {
                var now = DateTime.UtcNow;
                var expiresAt = now.AddMinutes(Config.CacheLifetimeMinutes);

                var entry = new ResolutionEntry
                {
                    ImdbId = req.Id,
                    Season = req.Season,
                    Episode = req.Episode,
                    StreamUrl = streams[0].Url ?? string.Empty,
                    QualityTier = M3u8Builder.MapStreamToTier(streams[0]),
                    FileName = streams[0].BehaviorHints?.Filename,
                    Status = "valid",
                    ResolvedAt = now.ToString("o"),
                    ExpiresAt = expiresAt.ToString("o"),
                    ResolutionTier = "live"
                };

                var candidates = streams
                    .Where(s => !string.IsNullOrEmpty(s.Url))
                    .Select((s, i) => new StreamCandidate
                    {
                        ImdbId = req.Id,
                        Season = req.Season,
                        Episode = req.Episode,
                        Rank = i,
                        ProviderKey = M3u8Builder.GetSourceName(s).ToLowerInvariant(),
                        StreamType = s.ParsedFile != null ? "debrid" : "unknown",
                        Url = s.Url!,
                        QualityTier = M3u8Builder.MapStreamToTier(s),
                        FileName = s.BehaviorHints?.Filename,
                        Status = "valid",
                        ResolvedAt = now.ToString("o"),
                        ExpiresAt = expiresAt.ToString("o")
                    })
                    .ToList();

                await db.UpsertResolutionResultAsync(entry, candidates);
                _logger.LogDebug("[Resolve] Wrote {Count} candidates to cache for {Id}", candidates.Count, req.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] Cache write failed for {Id} (non-fatal)", req.Id);
            }
        }

        /// <summary>
        /// Estimates bandwidth from resolution.
        /// </summary>
        private static long EstimateBandwidth(string resolution)
        {
            return resolution.ToLowerInvariant() switch
            {
                var r when r.Contains("4k") || r.Contains("2160") => 15000000, // 15 Mbps
                var r when r.Contains("1080") => 8000000, // 8 Mbps
                var r when r.Contains("720") => 5000000, // 5 Mbps
                var r when r.Contains("480") => 3000000, // 3 Mbps
                _ => 2000000 // 2 Mbps default
            };
        }

        /// <summary>
        /// Error response helper with structured error codes.
        /// Maps ResolverError enum to HTTP status codes and messages.
        /// </summary>
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
