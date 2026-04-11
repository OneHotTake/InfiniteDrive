using System;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Handles user Save/Unsave/Block actions.
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
        /// Saves an item.
        /// </summary>
        public async Task SaveItemAsync(string itemId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SavedService] Saving item {ItemId}", itemId);

            var item = await _db.GetMediaItemAsync(itemId, ct);
            if (item == null)
            {
                _logger.LogWarning("[SavedService] Item {ItemId} not found", itemId);
                return;
            }

            item.Saved = true;
            item.SavedAt = DateTimeOffset.UtcNow;
            item.SavedBy = "user";
            item.SaveReason = SaveReason.Explicit;
            item.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.UpsertMediaItemAsync(item, ct);
            await LogPipelineEvent(item.Id, "Save", PipelineTrigger.UserSave, true, null, ct);

            _logger.LogDebug("[SavedService] Item {ItemId} saved successfully", itemId);
        }

        /// <summary>
        /// Unsaves an item.
        /// CRITICAL: Sets saved=false only, never sets Deleted directly.
        /// The removal pipeline evaluates grace period on next run.
        /// </summary>
        public async Task UnsaveItemAsync(string itemId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SavedService] Unsaving item {ItemId}", itemId);

            var item = await _db.GetMediaItemAsync(itemId, ct);
            if (item == null)
            {
                _logger.LogWarning("[SavedService] Item {ItemId} not found", itemId);
                return;
            }

            item.Saved = false;
            item.SaveReason = null;
            item.SavedAt = null;
            item.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.UpsertMediaItemAsync(item, ct);
            await LogPipelineEvent(item.Id, "Unsave", PipelineTrigger.UserRemove, true, null, ct);

            _logger.LogDebug("[SavedService] Item {ItemId} unsaved successfully", itemId);
        }

        /// <summary>
        /// Blocks an item.
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
        /// Unblocks an item.
        /// CRITICAL: Sets blocked=false only, never sets Deleted directly.
        /// Re-enters pipeline on next sync if any source claims it.
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
