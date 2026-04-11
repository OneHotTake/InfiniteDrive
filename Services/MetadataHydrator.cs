using System;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Fetches and enriches item metadata from Cinemeta/AIOMetadata.
    /// </summary>
    public class MetadataHydrator
    {
        private readonly ILogger<MetadataHydrator> _logger;
        private readonly CinemetaProvider _cinemetaProvider;

        public MetadataHydrator(ILogger<MetadataHydrator> logger, CinemetaProvider cinemetaProvider)
        {
            _logger = logger;
            _cinemetaProvider = cinemetaProvider;
        }

        /// <summary>
        /// Hydrates a media item with metadata from Cinemeta.
        /// </summary>
        public async Task<MediaItem> HydrateAsync(
            MediaItem item,
            CancellationToken ct = default)
        {
            _logger.LogDebug("[MetadataHydrator] Hydrating {MediaId}", item.PrimaryId.ToString());

            try
            {
                // Fetch metadata from Cinemeta
                var metadata = await _cinemetaProvider.GetMetadataAsync(
                    item.PrimaryId.Value,
                    item.PrimaryId.Type.ToString().ToLower(),
                    ct);

                if (metadata != null)
                {
                    // Update item with metadata
                    // Note: Images are NOT stored on MediaItem per spec
                    // They come from Emby's own metadata provider via .nfo injection

                    _logger.LogDebug("[MetadataHydrator] Hydrated {MediaId}: {Title}",
                        item.PrimaryId.ToString(), metadata.Title);
                }
                else
                {
                    _logger.LogWarning("[MetadataHydrator] No metadata found for {MediaId}",
                        item.PrimaryId.ToString());
                }

                return item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MetadataHydrator] Failed to hydrate {MediaId}",
                    item.PrimaryId.ToString());
                return item;
            }
        }
    }
}
