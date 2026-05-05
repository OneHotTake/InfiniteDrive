using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Manages blocked items: prevents re-addition of items blocked by admin.
    /// Multi-ID matching (IMDB, TMDB, AniList) for robust blocking.
    /// </summary>
    public class BlockListService
    {
        private readonly DatabaseManager _db;
        private readonly ILogger<BlockListService> _logger;

        public BlockListService(DatabaseManager db, ILogger<BlockListService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Checks if an item is blocked by any of its IDs.
        /// Any non-null ID is checked; nulls are skipped.
        /// </summary>
        public Task<bool> IsBlockedAsync(string? aioId, string? tmdbId, string? anilistId)
        {
            return _db.IsBlockedAsync(aioId, tmdbId, anilistId);
        }

        /// <summary>
        /// Blocks an item. Sets blocked_at and blocked_by.
        /// </summary>
        public Task BlockItemAsync(
            string? aioId, string? tmdbId, string? anilistId,
            string title, string mediaType, Guid adminUserId)
        {
            _logger.LogInformation("[BlockList] Blocking '{Title}' ({MediaType}) by admin {AdminId}",
                title, mediaType, adminUserId);

            return _db.UpsertBlockedItemAsync(
                aioId, tmdbId, anilistId,
                title, mediaType, adminUserId.ToString());
        }

        /// <summary>
        /// Unblocks an item by row ID. Sets unblocked_at and unblocked_by.
        /// </summary>
        public Task UnblockItemAsync(long id, Guid adminUserId)
        {
            _logger.LogInformation("[BlockList] Unblocking item {Id} by admin {AdminId}",
                id, adminUserId);

            return _db.UnblockItemAsync(id, adminUserId.ToString());
        }

        /// <summary>
        /// Gets paginated blocked items for admin UI.
        /// </summary>
        public Task<List<BlockedItem>> GetBlockedItemsAsync(int skip, int limit)
        {
            return _db.GetBlockedItemsAsync(skip, limit);
        }
    }
}
