using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Resolves playable streams from AIOStreams with quality ranking.
    /// </summary>
    public class StreamResolver
    {
        private readonly ILogger<StreamResolver> _logger;
        private readonly AioStreamsClient _client;

        public StreamResolver(ILogger<StreamResolver> logger, AioStreamsClient client)
        {
            _logger = logger;
            _client = client;
        }

        /// <summary>
        /// Resolves streams for a media item.
        /// </summary>
        public async Task<List<StreamCandidate>> ResolveStreamsAsync(
            MediaItem item,
            CancellationToken ct = default)
        {
            _logger.LogDebug("[StreamResolver] Resolving streams for {MediaId}", item.PrimaryId.ToString());

            try
            {
                var streams = new List<InfiniteDrive.Services.AioStreamsStream>();

                // Query AIOStreams based on media type
                if (item.MediaType.Equals("movie", StringComparison.OrdinalIgnoreCase))
                {
                    var response = await _client.GetMovieStreamsAsync(item.PrimaryId.Value, ct);
                    if (response?.Streams != null)
                        streams.AddRange(response.Streams);
                }
                else if (item.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase))
                {
                    // For series, we need to resolve each episode
                    // For now, just return empty list - will be implemented in sprint 112
                    _logger.LogDebug("[StreamResolver] Series resolution not yet implemented");
                    return new List<StreamCandidate>();
                }
                else
                {
                    _logger.LogDebug("[StreamResolver] Unsupported media type {Type}", item.MediaType);
                    return new List<StreamCandidate>();
                }

                // Convert to stream candidates
                var candidates = ConvertToCandidates(streams);

                // Rank by quality
                var ranked = RankStreams(candidates);

                _logger.LogInformation("[StreamResolver] Resolved {Count} stream candidates for {MediaId}",
                    ranked.Count, item.PrimaryId.ToString());

                return ranked;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StreamResolver] Failed to resolve streams for {MediaId}", item.PrimaryId.ToString());
                return new List<StreamCandidate>();
            }
        }

        /// <summary>
        /// Converts AIOStreams stream objects to StreamCandidate DTOs.
        /// </summary>
        private List<StreamCandidate> ConvertToCandidates(List<InfiniteDrive.Services.AioStreamsStream> streams)
        {
            return streams.Where(s => !string.IsNullOrEmpty(s.Url))
                .Select(s => new StreamCandidate
                {
                    Url = s.Url!,
                    QualityTier = ParseQuality(s),
                    FileName = s.BehaviorHints?.Filename,
                    FileSize = s.Size ?? s.BehaviorHints?.VideoSize,
                    BitrateKbps = s.Bitrate != null ? (int?)(s.Bitrate / 1000) : null,
                    IsCached = s.Service?.Cached ?? true,
                    InfoHash = s.InfoHash,
                    FileIdx = s.FileIdx,
                    StreamKey = BuildStreamKey(s),
                    BingeGroup = s.BehaviorHints?.BingeGroup,
                    ProviderKey = s.Service?.Id ?? "unknown",
                    StreamType = s.StreamType ?? "debrid"
                }).ToList();
        }

        /// <summary>
        /// Builds a stable stream key for deduplication.
        /// </summary>
        private string? BuildStreamKey(InfiniteDrive.Services.AioStreamsStream stream)
        {
            if (!string.IsNullOrEmpty(stream.InfoHash))
            {
                return $"{stream.InfoHash}:{stream.FileIdx ?? 0}";
            }
            return stream.Url;
        }

        /// <summary>
        /// Parses quality tier from AIOStreams stream.
        /// </summary>
        private string ParseQuality(InfiniteDrive.Services.AioStreamsStream stream)
        {
            // Check parsed file first
            if (stream.ParsedFile?.Quality != null)
                return stream.ParsedFile.Quality.ToLowerInvariant();

            // Parse from filename
            var filename = stream.BehaviorHints?.Filename ?? string.Empty;
            var lowerFilename = filename.ToLowerInvariant();

            if (lowerFilename.Contains("remux") || lowerFilename.Contains("2160p"))
                return "remux";
            if (lowerFilename.Contains("1080p"))
                return "1080p";
            if (lowerFilename.Contains("720p"))
                return "720p";
            if (lowerFilename.Contains("480p") || lowerFilename.Contains("576p"))
                return "480p";
            if (lowerFilename.Contains("720p") || lowerFilename.Contains("1080p") || lowerFilename.Contains("4k"))
                return "1080p"; // Default to 1080p if HD indicated but specific resolution unknown

            return "unknown";
        }

        /// <summary>
        /// Ranks streams by quality tier, then by size.
        /// </summary>
        private List<StreamCandidate> RankStreams(List<StreamCandidate> candidates)
        {
            var qualityOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["remux"] = 5,
                ["2160p"] = 5,
                ["1080p"] = 4,
                ["720p"] = 3,
                ["480p"] = 2,
                ["576p"] = 2,
                ["unknown"] = 1
            };

            var ranked = candidates.OrderByDescending(c => c.IsCached)
                .ThenByDescending(c => qualityOrder.GetValueOrDefault(c.QualityTier ?? "unknown", 0))
                .ThenByDescending(c => c.FileSize ?? 0)
                .ToList();

            // Assign ranks
            for (int i = 0; i < ranked.Count; i++)
                ranked[i].Rank = i;

            return ranked;
        }
    }
}
