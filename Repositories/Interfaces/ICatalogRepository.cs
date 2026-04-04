using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Models;

namespace EmbyStreams.Repositories.Interfaces
{
    /// <summary>
    /// Repository interface for catalog item persistence.
    /// Provides CRUD operations and querying for catalog_items table.
    /// Implemented by CatalogRepository (SQLite-backed).
    /// </summary>
    public interface ICatalogRepository
    {
        /// <summary>
        /// Returns all active (non-removed) catalog items.
        /// </summary>
        Task<IEnumerable<CatalogItem>> GetAllAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns first active catalog item with specified IMDB ID, or null.
        /// </summary>
        Task<CatalogItem?> GetByIdAsync(string imdbId, CancellationToken ct = default);

        /// <summary>
        /// Inserts or updates a catalog item.
        /// UNIQUE constraint on (imdb_id, source) drives upsert behavior.
        /// </summary>
        Task UpsertAsync(CatalogItem item, CancellationToken ct = default);

        /// <summary>
        /// Soft-deletes an item by setting removed_at to now.
        /// Row is never physically deleted (audit trail).
        /// </summary>
        Task DeleteAsync(string imdbId, CancellationToken ct = default);

        /// <summary>
        /// Returns all active catalog items from a specific source.
        /// </summary>
        Task<IEnumerable<CatalogItem>> GetBySourceAsync(
            string sourceId,
            CancellationToken ct = default);
    }
}
