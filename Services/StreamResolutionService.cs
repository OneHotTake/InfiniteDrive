using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Stream resolution service with cache-first fallback hierarchy.
    /// Implements: cache primary → cache secondary → live resolution.
    /// </summary>
    public class StreamResolutionService
    {
        private readonly DatabaseManager _db;
        private readonly StreamResolver _resolver;
        private readonly StreamCache _cache;
        private readonly StreamProbeService _probe;
        private readonly ILogger<StreamResolutionService> _logger;

        public StreamResolutionService(
            DatabaseManager db,
            StreamResolver resolver,
            StreamCache cache,
            StreamProbeService probe,
            ILogger<StreamResolutionService> logger)
        {
            _db = db;
            _resolver = resolver;
            _cache = cache;
            _probe = probe;
            _logger = logger;
        }

        /// <summary>
        /// Gets a playable stream URL for a media item.
        /// Implements ranked fallback hierarchy: cache primary → cache secondary → live resolution.
        /// </summary>
        /// <param name="mediaId">The MediaId in format "{PrimaryIdType}:{PrimaryIdValue}".</param>
        /// <param name="pluginSecret">Plugin secret for URL signing.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Signed stream URL, or null if no stream available.</returns>
        public async Task<string?> GetStreamUrlAsync(
            string mediaId,
            string pluginSecret,
            CancellationToken ct = default)
        {
            // RANKED FALLBACK HIERARCHY (try in order):

            // 1. Try primary cached URL
            var cachedUrl = await _cache.GetPrimaryAsync(mediaId, ct);
            if (cachedUrl != null)
            {
                _logger.LogDebug("[StreamResolution] Cache primary hit for {MediaId}", mediaId);
                return PlaybackTokenService.Sign(cachedUrl, pluginSecret);
            }

            // 2. Try secondary cached URL
            var cachedUrl2 = await _cache.GetSecondaryAsync(mediaId, ct);
            if (cachedUrl2 != null)
            {
                _logger.LogDebug("[StreamResolution] Cache secondary hit for {MediaId}", mediaId);
                return PlaybackTokenService.Sign(cachedUrl2, pluginSecret);
            }

            // 3. Live resolution from AIOStreams
            _logger.LogInformation("[StreamResolution] Cache miss for {MediaId}, resolving live...", mediaId);

            // Parse mediaId to get MediaItem
            var item = await ParseMediaIdAsync(mediaId, ct);
            if (item == null)
            {
                _logger.LogWarning("[StreamResolution] Media item not found for {MediaId}", mediaId);
                return null;
            }

            var streams = await _resolver.ResolveStreamsAsync(item, ct);

            if (streams == null || streams.Count == 0)
            {
                _logger.LogWarning("[StreamResolution] No streams found for {MediaId}", mediaId);
                return null;
            }

            // 4. Probe top 3 candidates before serving (Sprint 159)
            // Probe the highest-ranked streams to verify availability before serving to user.
            // Cache hits (steps 1-3) are served immediately without probing.
            var probeBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeBudgetCts.CancelAfter(1500); // 1.5s total budget
            var bestStream = await ProbeTopCandidatesAsync(streams, mediaId, probeBudgetCts.Token);

            if (bestStream == null && streams.Count > 0)
            if (bestStream == null && streams.Count > 0)
            {
                bestStream = streams[0]; // Fallback to first if rank not assigned
            }

            if (bestStream == null)
            {
                _logger.LogWarning("[StreamResolution] No valid stream candidate for {MediaId}", mediaId);
                return null;
            }

            _logger.LogInformation("[StreamResolution] Selected stream: {Quality} from {Provider}",
                bestStream.QualityTier ?? "unknown", bestStream.ProviderKey);

            // 5. Return signed URL (DO NOT cache here - ItemPipelineService caches)
            return PlaybackTokenService.Sign(bestStream.Url, pluginSecret);
        }

        /// <summary>
        /// Parses a media ID string and fetches the corresponding MediaItem from database.
        /// </summary>
        private async Task<MediaItem?> ParseMediaIdAsync(string mediaId, CancellationToken ct)
        {
            // MediaId format: "{PrimaryIdType}:{PrimaryIdValue}"
            var parts = mediaId.Split(':');
            if (parts.Length != 2)
            {
                _logger.LogWarning("[StreamResolution] Invalid mediaId format: {MediaId}", mediaId);
                return null;
            }

            var idType = parts[0];
            var idValue = parts[1];

            // Parse MediaIdType from string
            MediaIdType mediaIdType = MediaIdTypeExtensions.Parse(idType);

            // Fetch from database
            return await _db.FindMediaItemByProviderIdAsync(idType, idValue, ct);
        }

        /// <summary>
        /// Probes the top 3 ranked stream candidates to verify availability.
        /// Returns the first candidate that responds successfully, or falls back to rank-0
        /// if all probes fail within the budget.
        /// </summary>
        /// <param name="streams">Ranked list of stream candidates (rank 0 is best).</param>
        /// <param name="mediaId">Media ID for logging.</param>
        /// <param name="ct">Cancellation token with 1.5s budget.</param>
        /// <returns>Best available stream, or null if no valid stream.</returns>
        private async Task<Models.StreamCandidate?> ProbeTopCandidatesAsync(
            List<Models.StreamCandidate> streams,
            string mediaId,
            CancellationToken ct)
        {
            var candidates = streams.OrderBy(s => s.Rank).Take(3).ToList();
            var failureReasons = new List<string>();

            _logger.LogDebug("[StreamResolution] Probing {Count} candidates for {MediaId}",
                candidates.Count, mediaId);

            foreach (var candidate in candidates)
            {
                try
                {
                    var result = await _probe.ProbeAsync(candidate.Url, ct);

                    if (result.Ok)
                    {
                        _logger.LogInformation("[StreamResolution] Probe OK for rank {Rank}: {Url}",
                            candidate.Rank, candidate.Url);
                        return candidate;
                    }

                    _logger.LogDebug("[StreamResolution] Probe failed for rank {Rank}: {Url} — {Reason}",
                        candidate.Rank, candidate.Url, result.Reason);
                    failureReasons.Add($"rank {candidate.Rank}: {result.Reason}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Our timeout expired
                    _logger.LogDebug("[StreamResolution] Probe timeout for rank {Rank}", candidate.Rank);
                    failureReasons.Add($"rank {candidate.Rank}: timeout");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StreamResolution] Probe error for rank {Rank}", candidate.Rank);
                    failureReasons.Add($"rank {candidate.Rank}: error");
                }
            }

            // All probes failed — serve rank-0 as best-effort
            _logger.LogWarning("[StreamResolution] Best-effort fallback for {MediaId}: " +
                "all {Count} probes failed. Serving rank-0 ({Url}). Failures: {Reasons}",
                mediaId, candidates.Count, streams.FirstOrDefault()?.Url,
                string.Join(", ", failureReasons));

            return streams.FirstOrDefault(s => s.Rank == 0) ?? streams.FirstOrDefault();
        }
    }
}
