using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Manages Emby BoxSets via ICollectionManager API.
    /// </summary>
    public class BoxSetService
    {
        private readonly ICollectionManager _collectionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<BoxSetService> _logger;

        public BoxSetService(
            ICollectionManager collectionManager,
            ILibraryManager libraryManager,
            ILogger<BoxSetService> logger)
        {
            _collectionManager = collectionManager;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// Finds an existing BoxSet by name.
        /// </summary>
        public BoxSet? FindBoxSet(string name)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "BoxSet" },
                Name = name,
                Recursive = true
            };

            return _libraryManager.GetItemList(query)
                .OfType<BoxSet>()
                .FirstOrDefault();
        }

        /// <summary>
        /// Finds or creates a BoxSet by name.
        /// </summary>
        public async Task<BoxSet?> FindOrCreateBoxSetAsync(
            string name,
            CancellationToken ct = default)
        {
            var existing = FindBoxSet(name);
            if (existing != null)
            {
                _logger.LogDebug("[BoxSetService] Found existing BoxSet: {BoxSetId}", existing.Id);
                return existing;
            }

            // Create new BoxSet
            _logger.LogInformation("[BoxSetService] Creating new BoxSet: {Name}", name);
            return await CreateBoxSetAsync(name, ct);
        }

        /// <summary>
        /// Creates a new BoxSet.
        /// </summary>
        public async Task<BoxSet?> CreateBoxSetAsync(
            string name,
            CancellationToken ct = default)
        {
            try
            {
                // Emby SDK: Create collection via ICollectionManager
                // CRITICAL: IsLocked must be FALSE to allow AddToCollection/RemoveFromCollection
                var boxSet = await Task.Run(() =>
                {
                    return _collectionManager.CreateCollection(
                        new CollectionCreationOptions
                        {
                            Name = name,
                            IsLocked = false, // MUST be false to allow collection modifications
                            ItemIdList = Array.Empty<long>() // Uses long[] not Guid[]
                        });
                }, ct);

                _logger.LogInformation("[BoxSetService] Created BoxSet: {BoxSetId} - {Name}", boxSet.Id, name);

                return boxSet;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BoxSetService] Failed to create BoxSet: {Name}", name);
                return null;
            }
        }

        /// <summary>
        /// Adds an item to a BoxSet.
        /// </summary>
        public async Task AddItemToBoxSetAsync(
            Guid boxSetId,
            Guid itemId,
            CancellationToken ct = default)
        {
            try
            {
                var boxSet = _libraryManager.GetItemById(boxSetId) as BoxSet;
                if (boxSet == null)
                {
                    _logger.LogWarning("[BoxSetService] BoxSet not found: {BoxSetId}", boxSetId);
                    return;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    _logger.LogWarning("[BoxSetService] Item not found: {ItemId}", itemId);
                    return;
                }

                await Task.Run(() =>
                {
                    // AddToCollection uses internal long IDs, not Guids
                    _collectionManager.AddToCollection(boxSet.InternalId, new[] { item.InternalId });
                }, ct);

                _logger.LogDebug("[BoxSetService] Added item {ItemId} to BoxSet {BoxSetId}", itemId, boxSetId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BoxSetService] Failed to add item {ItemId} to BoxSet {BoxSetId}", itemId, boxSetId);
            }
        }

        /// <summary>
        /// Removes an item from a BoxSet.
        /// </summary>
        public async Task RemoveItemFromBoxSetAsync(
            Guid boxSetId,
            Guid itemId,
            CancellationToken ct = default)
        {
            try
            {
                var boxSet = _libraryManager.GetItemById(boxSetId) as BoxSet;
                if (boxSet == null)
                {
                    _logger.LogWarning("[BoxSetService] BoxSet not found: {BoxSetId}", boxSetId);
                    return;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    _logger.LogWarning("[BoxSetService] Item not found: {ItemId}", itemId);
                    return;
                }

                await Task.Run(() =>
                {
                    // CRITICAL: RemoveFromCollection requires BoxSet cast, not BaseItem
                    _collectionManager.RemoveFromCollection(boxSet, new[] { item.InternalId });
                }, ct);

                _logger.LogDebug("[BoxSetService] Removed item {ItemId} from BoxSet {BoxSetId}", itemId, boxSetId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BoxSetService] Failed to remove item {ItemId} from BoxSet {BoxSetId}", itemId, boxSetId);
            }
        }

        /// <summary>
        /// Empty a BoxSet by removing all items.
        /// NOTE: This is a placeholder implementation. Full implementation requires
        /// SDK API investigation to query BoxSet members efficiently.
        /// </summary>
        public async Task EmptyBoxSetAsync(
            Guid boxSetId,
            CancellationToken ct = default)
        {
            try
            {
                var boxSet = _libraryManager.GetItemById(boxSetId) as BoxSet;
                if (boxSet == null)
                {
                    _logger.LogWarning("[BoxSetService] BoxSet not found: {BoxSetId}", boxSetId);
                    return;
                }

                // TODO: Implement proper BoxSet member query and removal
                // Requires SDK API investigation to efficiently query BoxSet members
                // For now, we'll just delete and recreate the BoxSet to empty it
                _logger.LogWarning("[BoxSetService] EmptyBoxSetAsync is a placeholder - requires SDK API investigation");

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BoxSetService] Failed to empty BoxSet {BoxSetId}", boxSetId);
            }
        }
    }
}
