using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Manages Emby BoxSet collections for sources.
    /// </summary>
    public class CollectionsService
    {
        private readonly ILogger<CollectionsService> _logger;
        private readonly DatabaseManager _db;
        private readonly ICollectionManager _collectionManager;

        public CollectionsService(ILogger<CollectionsService> logger, DatabaseManager db, ICollectionManager collectionManager)
        {
            _logger = logger;
            _db = db;
            _collectionManager = collectionManager;
        }

        /// <summary>
        /// Syncs collections for all sources with ShowAsCollection = true.
        /// </summary>
        public async Task SyncCollectionsAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("[CollectionsService] Starting collection sync");

            var collectionSources = await _db.GetSourcesWithShowAsCollectionAsync(ct);

            foreach (var source in collectionSources)
            {
                await SyncSourceCollectionAsync(source, ct);
            }

            _logger.LogInformation("[CollectionsService] Collection sync complete");
        }

        /// <summary>
        /// Syncs a single source's collection.
        /// </summary>
        private async Task SyncSourceCollectionAsync(Source source, CancellationToken ct)
        {
            _logger.LogDebug("[CollectionsService] Syncing collection for source {Name}", source.Name);

            var items = await _db.FindMediaItemsBySourceAsync(source.Id, ct);
            var existingCollection = await _db.GetCollectionBySourceIdAsync(source.Id, ct);

            if (items.Count == 0)
            {
                _logger.LogDebug("[CollectionsService] No items for source {Name}, skipping", source.Name);
                return;
            }

            // TODO: Implement Emby BoxSet creation and sync
            // Requires proper handling of CollectionCreationOptions and AddToCollection API
            // For now, just log the items that would be synced

            var embyItemIds = items
                .Where(i => !string.IsNullOrEmpty(i.EmbyItemId))
                .Select(i => i.EmbyItemId!)
                .ToList();

            _logger.LogDebug("[CollectionsService] Would sync {Count} items for source {Name}",
                embyItemIds.Count, source.Name);
        }
    }
}
