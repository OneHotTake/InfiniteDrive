using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Resolve endpoint service for AIOStreams stream resolution.
    /// Queries AIOStreams API, filters by quality tier, and returns M3U8 manifest.
    /// </summary>
    public class ResolverService : IService, IRequiresRequest
    {
        private readonly ILogger<ResolverService> _logger;
        private readonly PluginConfiguration _config;
        private readonly AioStreamsClient _aioClient;
        private readonly M3u8Builder _m3u8Builder;

        public ResolverService(
            ILogManager logManager,
            PluginConfiguration config,
            AioStreamsClient aioClient)
        {
            _logger = new EmbyLoggerAdapter<ResolverService>(logManager.GetLogger("EmbyStreams"));
            _config = config;
            _aioClient = aioClient;
            _m3u8Builder = new M3u8Builder();
        }

        /// <inheritdoc/>
        public IRequest Request { get; set; }

        /// <inheritdoc/>
        public IResponse Response => Request?.Response;

        /// <summary>
        /// Handles GET /EmbyStreams/Resolve endpoint.
        /// </summary>
        public async Task<object> Get(ResolverRequest req)
        {
            // 1. Validate input
            if (string.IsNullOrEmpty(req.Id))
            {
                return Error(400, "bad_request", "id parameter is required");
            }

            if (string.IsNullOrEmpty(req.Quality))
            {
                return Error(400, "bad_request", "quality parameter is required");
            }

            // Validate quality tier
            if (!M3u8Builder.TierMetadata.ContainsKey(req.Quality))
            {
                return Error(400, "bad_request", $"invalid quality tier: {req.Quality}");
            }

            // 2. Resolve stream from AIOStreams
            var streams = await ResolveStreamsAsync(req);
            if (streams == null || streams.Count == 0)
            {
                _logger.LogWarning("[EmbyStreams][Resolve] No streams found for {Id}", req.Id);
                return Error(404, "not_found", "No streams available");
            }

            // 3. Filter and select streams
            var filtered = FilterStreamsByTier(streams, req.Quality);
            if (filtered.Count == 0)
            {
                _logger.LogWarning("[EmbyStreams][Resolve] No streams match tier {Quality} for {Id}", req.Quality, req.Id);
                return Error(404, "not_found", $"No streams available for quality {req.Quality}");
            }

            // 4. Select top stream per source
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
        /// </summary>
        private async Task<List<AioStreamsStream>> ResolveStreamsAsync(ResolverRequest req)
        {
            try
            {
                AioStreamsStreamResponse? response;

                if (req.IdType == "series" && req.Season.HasValue && req.Episode.HasValue)
                {
                    // Series episode request
                    response = await _aioClient.GetSeriesStreamsAsync(
                        req.Id,
                        req.Season.Value,
                        req.Episode.Value);
                }
                else
                {
                    // Movie request
                    response = await _aioClient.GetMovieStreamsAsync(req.Id);
                }

                return response?.Streams ?? new List<AioStreamsStream>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams][Resolve] Failed to resolve streams for {Id}", req.Id);
                return new List<AioStreamsStream>();
            }
        }

        /// <summary>
        /// Filters streams by quality tier.
        /// </summary>
        private List<AioStreamsStream> FilterStreamsByTier(List<AioStreamsStream> streams, string requestedTier)
        {
            return streams
                .Where(s =>
                {
                    var tier = M3u8Builder.MapStreamToTier(s);
                    return tier == requestedTier;
                })
                .ToList();
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
                    var signedUrl = StreamUrlSigner.Sign(s.Url!, _config.PluginSecret, 1);
                    var proxyUrl = $"{_config.EmbyBaseUrl.TrimEnd('/')}/EmbyStreams/stream?url={Uri.EscapeDataString(signedUrl)}";

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

            return _m3u8Builder.CreateVariantPlaylist(_config.EmbyBaseUrl, quality, variants);
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
        /// Error response helper.
        /// </summary>
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
