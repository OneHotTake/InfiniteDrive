using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using EmbyStreams.Repositories.Interfaces;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Repositories
{
    /// <summary>
    /// Catalog repository implementation.
    /// Provides CRUD operations for catalog_items table via DatabaseManager.
    /// Part of DatabaseManager split (Sprint 104D-01).
    /// </summary>
    public class CatalogRepository : ICatalogRepository
    {
        private readonly DatabaseManager _db;
        private readonly ILogger<CatalogRepository> _logger;

        public CatalogRepository(DatabaseManager db, ILogManager logManager)
        {
            _db = db;
            _logger = new EmbyLoggerAdapter<CatalogRepository>(logManager.GetLogger("EmbyStreams"));
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<CatalogItem>> GetAllAsync(CancellationToken ct = default)
        {
            try
            {
                var items = await _db.GetActiveCatalogItemsAsync();
                _logger.LogDebug("[CatalogRepository] Retrieved {Count} active catalog items", items.Count);
                return items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogRepository] Failed to get all catalog items");
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<CatalogItem?> GetByIdAsync(string imdbId, CancellationToken ct = default)
        {
            try
            {
                var item = await _db.GetCatalogItemByImdbIdAsync(imdbId);
                if (item != null)
                {
                    _logger.LogDebug("[CatalogRepository] Found catalog item for {ImdbId}", imdbId);
                }
                return item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogRepository] Failed to get catalog item for {ImdbId}", imdbId);
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task UpsertAsync(CatalogItem item, CancellationToken ct = default)
        {
            try
            {
                await _db.UpsertCatalogItemAsync(item, ct);
                _logger.LogDebug("[CatalogRepository] Upserted catalog item {ImdbId}", item.ImdbId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogRepository] Failed to upsert catalog item {ImdbId}", item.ImdbId);
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task DeleteAsync(string imdbId, CancellationToken ct = default)
        {
            try
            {
                // ICatalogRepository.DeleteAsync only takes imdbId, but DatabaseManager requires source too
                // For now, we'll soft-delete all catalog items with this imdbId
                // This is a temporary adaptation until the interface or DatabaseManager is updated
                var existing = await _db.GetCatalogItemByImdbIdAsync(imdbId);
                if (existing != null)
                {
                    await _db.MarkCatalogItemRemovedAsync(imdbId, existing.Source, ct);
                    _logger.LogDebug("[CatalogRepository] Soft-deleted catalog item {ImdbId}", imdbId);
                }
                else
                {
                    _logger.LogWarning("[CatalogRepository] Catalog item not found for deletion: {ImdbId}", imdbId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogRepository] Failed to delete catalog item {ImdbId}", imdbId);
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<CatalogItem>> GetBySourceAsync(
            string sourceId,
            CancellationToken ct = default)
        {
            try
            {
                var items = await _db.GetCatalogItemsBySourceAsync(sourceId);
                _logger.LogDebug("[CatalogRepository] Retrieved {Count} catalog items for source {Source}",
                    items.Count, sourceId);
                return items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CatalogRepository] Failed to get catalog items for source {Source}", sourceId);
                throw;
            }
        }
    }
}
