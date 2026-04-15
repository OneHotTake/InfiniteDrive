using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Manages item removal with grace period.
    /// Respects Coalition rule and user overrides.
    /// </summary>
    public class RemovalService
    {
        private readonly DatabaseManager _db;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<RemovalService> _logger;
        private readonly PluginConfiguration _config;

        // Grace period configuration
        private readonly TimeSpan _gracePeriod = TimeSpan.FromDays(7);

        public RemovalService(
            DatabaseManager db,
            ILibraryManager libraryManager,
            ILogger<RemovalService> logger,
            PluginConfiguration config)
        {
            _db = db;
            _libraryManager = libraryManager;
            _logger = logger;
            _config = config;
        }

        /// <summary>
        /// Marks an item for removal by starting the grace period.
        /// </summary>
        public async Task<RemovalResult> MarkForRemovalAsync(
            string itemId,
            CancellationToken ct = default)
        {
            // Check if item exists
            var item = await _db.GetMediaItemAsync(itemId, ct);
            if (item == null)
            {
                return RemovalResult.Failure("Item not found");
            }

            // Check if item is saved (boolean column, NOT status enum)
            if (item.Saved)
            {
                _logger.LogInformation("[RemovalService] Item {ItemId} is Saved, cannot remove", itemId);
                return RemovalResult.Failure("Item is saved by user");
            }

            // Check if item is blocked (boolean column, NOT status enum)
            if (item.Blocked)
            {
                _logger.LogInformation("[RemovalService] Item {ItemId} is Blocked, cannot remove", itemId);
                return RemovalResult.Failure("Item is blocked by user");
            }

            // Check coalition rule: does item have enabled source?
            // CRITICAL: This MUST be a single JOIN query
            var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(itemId, ct);

            if (hasEnabledSource)
            {
                _logger.LogInformation("[RemovalService] Item {ItemId} has enabled source, cannot remove", itemId);
                return RemovalResult.Failure("Item has enabled source");
            }

            // Start grace period (do NOT set status = Deleted)
            // Per v3.3 spec §10.3: Removal pipeline handles grace period
            item.GraceStartedAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.UpsertMediaItemAsync(item, ct);

            _logger.LogInformation("[RemovalService] Item {ItemId} started grace period", itemId);

            return RemovalResult.Success($"Item grace period started: {item.Title}");
        }

        /// <summary>
        /// Removes an item if the grace period has expired.
        /// </summary>
        public async Task<RemovalResult> RemoveItemAsync(
            string itemId,
            CancellationToken ct = default)
        {
            // Check if item exists
            var item = await _db.GetMediaItemAsync(itemId, ct);
            if (item == null)
            {
                return RemovalResult.Failure("Item not found");
            }

            // Check grace period: is it safe to remove?
            if (!await IsGracePeriodExpiredAsync(item, ct))
            {
                _logger.LogWarning("[RemovalService] Item {ItemId} grace period not expired, cannot remove yet", itemId);
                return RemovalResult.Failure($"Grace period not expired until {item.GraceStartedAt?.Add(_gracePeriod)}");
            }

            // Check coalition rule: does item have enabled source?
            // CRITICAL: This MUST be a single JOIN query
            var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(itemId, ct);

            if (hasEnabledSource || item.Saved || item.Blocked)
            {
                // Item should not be removed, cancel grace period
                item.GraceStartedAt = null;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.UpsertMediaItemAsync(item, ct);
                _logger.LogInformation("[RemovalService] Item {ItemId} removal cancelled (coalition rule), grace cleared", itemId);
                return RemovalResult.Success($"Removal cancelled: {item.Title}");
            }

            // Remove .strm file
            RemoveStrmFileAsync(item);

            // Remove from Emby library
            await RemoveFromEmbyAsync(item, ct);

            // Update status to Deleted (NOT Removed - that status doesn't exist)
            item.Status = ItemStatus.Deleted;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.UpsertMediaItemAsync(item, ct);

            _logger.LogInformation("[RemovalService] Item {ItemId} removed from library", itemId);

            return RemovalResult.Success($"Item removed: {item.Title}");
        }

        /// <summary>
        /// Checks if the grace period has expired for an item.
        /// </summary>
        private async Task<bool> IsGracePeriodExpiredAsync(MediaItem item, CancellationToken ct)
        {
            if (!item.GraceStartedAt.HasValue)
            {
                // No grace period started, item can be removed
                return true;
            }

            var graceEnd = item.GraceStartedAt.Value.Add(_gracePeriod);
            var isExpired = DateTimeOffset.UtcNow > graceEnd;

            _logger.LogDebug("[RemovalService] Item {ItemId} grace period: started={Started}, ends={Ends}, expired={IsExpired}",
                item.Id, item.GraceStartedAt, graceEnd, isExpired);

            return await Task.FromResult(isExpired);
        }

        /// <summary>
        /// Deletes the .strm file (movies) or entire series folder (series/anime).
        /// Note: Emby library item removal is handled separately.
        /// </summary>
        private void RemoveStrmFileAsync(MediaItem item)
        {
            var strmPath = GetStrmPath(item);
            if (string.IsNullOrEmpty(strmPath))
                return;

            // ── Sprint 222: Full series folder removal ────────────────────────
            var isSeries = string.Equals(item.MediaType, "series", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase);

            if (isSeries && Directory.Exists(strmPath))
            {
                try
                {
                    // Delete entire series folder (all seasons, all .strm + .nfo)
                    Directory.Delete(strmPath, recursive: true);
                    _logger.LogInformation("[RemovalService] Deleted series folder: {Path}", strmPath);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RemovalService] Failed to delete series folder for item {ItemId}", item.Id);
                }
            }

            // ── Movie: single .strm file + version variants ───────────────────
            StrmWriterService.DeleteWithVersions(strmPath);
            _logger.LogDebug("[RemovalService] Deleted .strm + versions: {Path}", strmPath);
        }

        /// <summary>
        /// Removes an item from the Emby library.
        /// </summary>
        private async Task RemoveFromEmbyAsync(MediaItem item, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(item.EmbyItemId))
            {
                _logger.LogWarning("[RemovalService] Item {ItemId} has no EmbyItemId", item.Id);
                return;
            }

            var embyItemId = Guid.Parse(item.EmbyItemId);
            var baseItem = _libraryManager.GetItemById(embyItemId);

            if (baseItem == null)
            {
                _logger.LogWarning("[RemovalService] Emby item not found: {EmbyItemId}", embyItemId);
                return;
            }

            // Note: ILibraryManager does not provide DeleteItemAsync directly
            // The .strm file deletion is sufficient - Emby will automatically
            // remove the library item during the next library scan
            _logger.LogDebug("[RemovalService] Emby removal handled by .strm deletion for item {EmbyItemId}", embyItemId);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Gets the path to the .strm file for an item.
        /// CRITICAL: Resolve to three separate library paths
        /// Per v3.3 spec §4.1: Three separate Emby libraries
        /// </summary>
        private string GetStrmPath(MediaItem item)
        {
            var mediaType = item.MediaType ?? "movie";

            // Resolve subdirectory based on media type
            var subDir = mediaType switch
            {
                "movie" => _config.SyncPathMovies,
                "series" => _config.SyncPathShows,
                // For anime, check if primary ID is AniList/AniDB
                _ when IsAnimeMediaId(item) => _config.EnableAnimeLibrary ? _config.SyncPathAnime : _config.SyncPathShows,
                _ => _config.SyncPathMovies
            };

            return Path.Combine(subDir, $"{item.Id}.strm");
        }

        /// <summary>
        /// Checks if an item uses anime-specific media IDs.
        /// </summary>
        private bool IsAnimeMediaId(MediaItem item)
        {
            // Check if primary ID type is anime-specific
            return item.PrimaryIdType.ToString().ToLowerInvariant() switch
            {
                "anilist" => true,
                "anidb" => true,
                "kitsu" => true,
                _ => false
            };
        }
    }
}
