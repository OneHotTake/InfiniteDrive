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
            _healthTracker = new ResolverHealthTracker(_logger);
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

            // 2. Resolve stream from AIOStreams
            // Sprint 302-01: Check circuit breaker before attempting resolution
            const string resolverName = "aiostreams_primary";
            if (_healthTracker.ShouldSkip(resolverName))
            {
                _logger.LogWarning(
                    "[InfiniteDrive][Resolve] Skipping {Resolver} - circuit is open",
                    resolverName);
                return Error(ResolverError.PrimaryResolverDown);
            }

            var (streams, resolverError) = await ResolveStreamsAsync(req);
            if (resolverError.HasValue)
            {
                // Upstream error (rate limit, connection failure, etc.)
                if (resolverError == ResolverError.PrimaryResolverDown)
                {
                    _healthTracker.RecordFailure(resolverName);
                }
                return Error(resolverError.Value);
            }

            // Record success for circuit breaker
            _healthTracker.RecordSuccess(resolverName);

            if (streams == null || streams.Count == 0)
            {
                _logger.LogWarning("[InfiniteDrive][Resolve] No streams found for {Id}", req.Id);
                return Error(ResolverError.NoStreamsExist);
            }

            // 3. Filter by quality tier with fallback chain
            var (filtered, usedFallback) = FilterStreamsWithFallback(streams, req.Quality, req.Id);
            if (filtered.Count == 0)
            {
                _logger.LogWarning("[InfiniteDrive][Resolve] No streams found for {Id}", req.Id);
                return Error(ResolverError.QualityMismatch);
            }

            // 4. Probe top candidates and reorder — dead URLs sink to back of list.
            //    Emby client sees working streams first; dead ones remain as fallback.
            //    5s total budget shared across all probes (Sprint 302).
            filtered = await ProbeAndReorderAsync(filtered);

            // 5. Select top stream per source
            var variants = SelectTopStreams(filtered);

            // 5. Build M3U8 playlist with signed URLs
            var playlist = BuildPlaylist(variants, req.Quality);

            return new
            {
                ContentType = M3u8Builder.MimeType,
                Content = playlist,
                StatusCode = 200
            };
        }

        /// <summary>
        /// Resolves streams from AIOStreams API.
        /// Returns (streams, error) tuple where error is null on success.
        /// </summary>
        private async Task<(List<AioStreamsStream> streams, ResolverError? error)> ResolveStreamsAsync(ResolverRequest req)
        {
            try
            {
                using var client = new AioStreamsClient(Config, _logger);
                AioStreamsStreamResponse? response;

                if (req.IdType == "series" && req.Season.HasValue && req.Episode.HasValue)
                {
                    // Series episode request
                    response = await client.GetSeriesStreamsAsync(
                        req.Id,
                        req.Season.Value,
                        req.Episode.Value);
                }
                else
                {
                    // Movie request
                    response = await client.GetMovieStreamsAsync(req.Id);
                }

                var streams = response?.Streams ?? new List<AioStreamsStream>();
                return (streams, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive][Resolve] Failed to resolve streams for {Id}", req.Id);
                // Detect rate limit by exception type
                if (ex.GetType().Name.Contains("RateLimit"))
                {
                    _logger.LogWarning("[InfiniteDrive][Resolve] Rate limited for {Id}", req.Id);
                    return (new List<AioStreamsStream>(), ResolverError.RateLimited);
                }
                // Total resolution failure - all resolvers exhausted
                _logger.LogError("[InfiniteDrive][Resolve] TOTAL RESOLUTION FAILURE for {Id} - primary resolver unreachable, no secondary fallback available", req.Id);
                return (new List<AioStreamsStream>(), ResolverError.AllResolversDown);
            }
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
                    // Budget exhausted — give benefit of the doubt to remaining
                    dead.Add(stream);
                    break;
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
