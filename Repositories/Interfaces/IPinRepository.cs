using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyStreams.Repositories.Interfaces
{
    /// <summary>
    /// Repository interface for item pin/unpin operations.
    /// Provides PIN state management for catalog items.
    /// Implemented by CatalogRepository (delegates to DatabaseManager pin methods).
    /// PINNED items are protected from catalog removal (Doctor Phase 1).
    /// </summary>
    public interface IPinRepository
    {
        /// <summary>
        /// Checks if an item with the given IMDB ID is currently pinned.
        /// </summary>
        Task<bool> IsPinnedAsync(string imdbId, CancellationToken ct = default);

        /// <summary>
        /// Marks an item as pinned (ItemState = PINNED).
        /// Protected from catalog removal in Doctor Phase 1.
        /// </summary>
        Task PinAsync(string imdbId, CancellationToken ct = default);

        /// <summary>
        /// Removes pin state from an item (returns to catalog-driven state).
        /// Allows item to be removed when no longer in any catalog source.
        /// </summary>
        Task UnpinAsync(string imdbId, CancellationToken ct = default);

        /// <summary>
        /// Returns all IMDB IDs that are currently pinned.
        /// Used for orphan detection in Doctor Phase 1.
        /// </summary>
        Task<IEnumerable<string>> GetAllPinnedIdsAsync(CancellationToken ct = default);
    }
}
