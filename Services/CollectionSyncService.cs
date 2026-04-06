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
    /// Syncs sources to Emby BoxSets via BoxSetService.
    /// Note: Full item-to-collection sync requires SDK API investigation.
    /// </summary>
    public class CollectionSyncService
    {
        private readonly DatabaseManager _db;
        private readonly BoxSetService _boxSetService;
        private readonly ILogger<CollectionSyncService> _logger;

        public CollectionSyncService(
            DatabaseManager db,
            BoxSetService boxSetService,
            ILogger<CollectionSyncService> logger)
        {
            _db = db;
            _boxSetService = boxSetService;
            _logger = logger;
        }

        /// <summary>
        /// Syncs all ShowAsCollection sources to BoxSets.
        /// </summary>
        public async Task<CollectionSyncResult> SyncCollectionsAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("[CollectionSyncService] Starting collection sync");

            // Get sources with ShowAsCollection = true
            var collectionSources = await _db.GetSourcesWithShowAsCollectionAsync(ct);
            _logger.LogInformation("[CollectionSyncService] Found {Count} sources with ShowAsCollection", collectionSources.Count);

            var results = new List<CollectionResult>();

            foreach (var source in collectionSources)
            {
                var result = await SyncSourceCollectionAsync(source, ct);
                results.Add(result);
            }

            // Prune orphaned collections (source no longer has ShowAsCollection)
            // CRITICAL: Empty BoxSet, do NOT delete it
            await EmptyOrphanedCollectionsAsync(collectionSources, ct);

            _logger.LogInformation("[CollectionSyncService] Collection sync complete: {Success}/{Total} collections synced",
                results.Count(r => r.IsSuccess), results.Count);

            var totalItems = results.Where(r => r.ItemCount > 0).Sum(r => r.ItemCount);

            return new CollectionSyncResult(
                results.Count,
                results.Count(r => r.IsSuccess),
                totalItems,
                results
            );
        }

        /// <summary>
        /// Syncs a single source's collection.
        /// </summary>
        private async Task<CollectionResult> SyncSourceCollectionAsync(
            Source source,
            CancellationToken ct)
        {
            try
            {
                _logger.LogDebug("[CollectionSyncService] Syncing collection for source {Name}", source.Name);

                // Find or create BoxSet
                var boxSet = _boxSetService.FindOrCreateBoxSet(source.Name);
                if (boxSet == null)
                {
                    return CollectionResult.Failure(source.Name, 0, "Failed to create/find BoxSet");
                }

                // Get items in this source
                var items = await _db.FindMediaItemsBySourceAsync(source.Id, ct);

                // TODO: Sync items to BoxSet
                // Full implementation requires SDK API investigation for BoxSet item management
                var syncedCount = items.Count(i => !string.IsNullOrEmpty(i.EmbyItemId));

                // Update collection metadata
                var collection = new Collection
                {
                    SourceId = source.Id,
                    Name = source.Name,
                    EmbyCollectionId = boxSet.Id.ToString(),
                    CollectionName = source.CollectionName, // Override name from source
                    Enabled = true,
                    LastSyncedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await _db.UpsertCollectionAsync(collection, ct);

                _logger.LogInformation("[CollectionSyncService] Synced collection '{Name}': {Count} items (BoxSet created/updated, item sync pending SDK API investigation)", source.Name, syncedCount);

                return CollectionResult.Success(source.Name, syncedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CollectionSyncService] Failed to sync collection for source {SourceName}", source.Name);
                return CollectionResult.Failure(source.Name, 0, ex.Message);
            }
        }

        /// <summary>
        /// Empties orphaned collections (source no longer has ShowAsCollection = true).
        /// </summary>
        private async Task EmptyOrphanedCollectionsAsync(
            List<Source> activeSources,
            CancellationToken ct)
        {
            var activeSourceIds = activeSources.Select(s => s.Id).ToHashSet();
            var allCollections = await _db.GetAllCollectionsListAsync(ct);

            foreach (var collection in allCollections)
            {
                if (!string.IsNullOrEmpty(collection.SourceId) && !activeSourceIds.Contains(collection.SourceId))
                {
                    _logger.LogInformation("[CollectionSyncService] Emptying orphaned collection: {Name}", collection.Name);

                    // CRITICAL: Empty BoxSet, do NOT delete it
                    // This preserves the BoxSet structure for manual user edits
                    if (!string.IsNullOrEmpty(collection.EmbyCollectionId))
                    {
                        var boxSetId = Guid.Parse(collection.EmbyCollectionId!);
                        await _boxSetService.EmptyBoxSetAsync(boxSetId, ct);
                    }

                    // Delete collection metadata (not the BoxSet itself)
                    await _db.DeleteCollectionAsync(collection.Id, ct);
                }
            }
        }
    }

    /// <summary>
    /// Result of a single collection sync operation.
    /// </summary>
    public record CollectionResult(
        string SourceName,
        bool IsSuccess,
        int ItemCount,
        string? Message = null
    )
    {
        public static CollectionResult Success(string sourceName, int count) =>
            new(sourceName, true, count);

        public static CollectionResult Failure(string sourceName, int count, string message) =>
            new(sourceName, false, count, message);
    }

    /// <summary>
    /// Summary of collection sync operation.
    /// </summary>
    public record CollectionSyncResult(
        int TotalProcessed,
        int SuccessCount,
        int TotalItemsSynced,
        List<CollectionResult> Results
    );
}
