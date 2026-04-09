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
        private readonly ILogger<StreamResolutionService> _logger;

        public StreamResolutionService(
            DatabaseManager db,
            StreamResolver resolver,
            StreamCache cache,
            ILogger<StreamResolutionService> logger)
        {
            _db = db;
            _resolver = resolver;
            _cache = cache;
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

            // 4. Pick best stream (already ranked by StreamResolver - rank 0 is best)
            var bestStream = streams.FirstOrDefault(s => s.Rank == 0);
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
    }
}
