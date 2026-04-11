using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Handles per-user Save/Unsave and global Block/Unblock actions.
    /// </summary>
    public class SavedService
    {
        private readonly ILogger<SavedService> _logger;
        private readonly DatabaseManager _db;

        public SavedService(ILogger<SavedService> logger, DatabaseManager db)
        {
            _logger = logger;
            _db = db;
        }

        /// <summary>
        /// Saves an item for a specific user.
        /// </summary>
        public async Task SaveItemAsync(string itemId, string userId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SavedService] User {UserId} saving item {ItemId}", userId, itemId);

            var item = await _db.GetMediaItemAsync(itemId, ct);
            if (item == null)
            {
                _logger.LogWarning("[SavedService] Item {ItemId} not found", itemId);
                return;
            }

            await _db.UpsertUserSaveAsync(userId, itemId, "explicit", null, ct);
            await _db.SyncGlobalSavedFlagAsync(itemId, ct);
            await LogPipelineEvent(item.Id, "Save", PipelineTrigger.UserSave, true, null, ct);

            _logger.LogDebug("[SavedService] Item {ItemId} saved for user {UserId}", itemId, userId);
        }

        /// <summary>
        /// Unsaves an item for a specific user.
        /// If no users remain with a save, the global saved flag drops to 0.
        /// </summary>
        public async Task UnsaveItemAsync(string itemId, string userId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SavedService] User {UserId} unsaving item {ItemId}", userId, itemId);

            var item = await _db.GetMediaItemAsync(itemId, ct);
            if (item == null)
            {
                _logger.LogWarning("[SavedService] Item {ItemId} not found", itemId);
                return;
            }

            await _db.DeleteUserSaveAsync(userId, itemId, ct);
            await _db.SyncGlobalSavedFlagAsync(itemId, ct);
            await LogPipelineEvent(item.Id, "Unsave", PipelineTrigger.UserRemove, true, null, ct);

            _logger.LogDebug("[SavedService] Item {ItemId} unsaved for user {UserId}", itemId, userId);
        }

        /// <summary>
        /// Checks if a specific user has saved an item.
        /// </summary>
        public Task<bool> IsItemSavedByUserAsync(string itemId, string userId, CancellationToken ct = default)
        {
            return _db.HasUserSaveAsync(userId, itemId, ct);
        }

        /// <summary>
        /// Gets all saved items for a specific user.
        /// </summary>
        public Task<List<MediaItem>> GetUserSavedItemsAsync(string userId, CancellationToken ct = default)
        {
            return _db.GetSavedItemsByUserAsync(userId, ct);
        }

        /// <summary>
        /// Blocks an item (global).
        /// </summary>
        public async Task BlockItemAsync(string itemId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SavedService] Blocking item {ItemId}", itemId);

            var item = await _db.GetMediaItemAsync(itemId, ct);
            if (item == null)
            {
                _logger.LogWarning("[SavedService] Item {ItemId} not found", itemId);
                return;
            }

            item.Blocked = true;
            item.BlockedAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.UpsertMediaItemAsync(item, ct);
            await LogPipelineEvent(item.Id, "Block", PipelineTrigger.UserBlock, true, null, ct);

            _logger.LogDebug("[SavedService] Item {ItemId} blocked successfully", itemId);
        }

        /// <summary>
        /// Unblocks an item (global).
        /// </summary>
        public async Task UnblockItemAsync(string itemId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SavedService] Unblocking item {ItemId}", itemId);

            var item = await _db.GetMediaItemAsync(itemId, ct);
            if (item == null)
            {
                _logger.LogWarning("[SavedService] Item {ItemId} not found", itemId);
                return;
            }

            item.Blocked = false;
            item.BlockedAt = null;
            item.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.UpsertMediaItemAsync(item, ct);
            await LogPipelineEvent(item.Id, "Unblock", PipelineTrigger.UserRemove, true, null, ct);

            _logger.LogDebug("[SavedService] Item {ItemId} unblocked successfully", itemId);
        }

        private Task LogPipelineEvent(string itemId, string phase, PipelineTrigger trigger, bool success, string? error, CancellationToken ct)
        {
            var item = _db.GetMediaItemAsync(itemId, ct).GetAwaiter().GetResult();
            if (item == null) return Task.CompletedTask;

            return _db.LogPipelineEventAsync(
                item.PrimaryId.Value,
                item.PrimaryId.Type.ToString(),
                item.MediaType,
                trigger.ToString(),
                null,
                item.Status.ToString(),
                success,
                error,
                ct);
        }
    }
}
