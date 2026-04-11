using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Resolves conflicts between "Your Files" items and InfiniteDrive items.
    /// Implements coalition rule: keeps available but supersedes stream.
    /// </summary>
    public class YourFilesConflictResolver
    {
        private readonly DatabaseManager _db;
        private readonly ILogger<YourFilesConflictResolver> _logger;
        private readonly ILibraryManager _libraryManager;

        public YourFilesConflictResolver(
            DatabaseManager db,
            ILogger<YourFilesConflictResolver> logger,
            ILibraryManager libraryManager)
        {
            _db = db;
            _logger = logger;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Resolves a "Your Files" match according to coalition rules.
        /// </summary>
        public async Task<ConflictResolution> ResolveAsync(
            YourFilesMatchResult match,
            CancellationToken ct = default)
        {
            var mediaItem = match.MediaItem;

            // Check current status: Blocked items are never superseded
            if (mediaItem.Blocked)
            {
                _logger.LogInformation("[YourFilesResolver] Item {ItemId} is Blocked, ignoring Your Files match", mediaItem.Id);
                return ConflictResolution.KeepBlocked;
            }

            // Check coalition rule: does item have enabled source?
            var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(mediaItem.Id, ct);

            if (hasEnabledSource)
            {
                // Coalition rule: keep available but supersede stream
                // Set superseded=true to indicate user's local file takes precedence
                await _db.SetSupersededAsync(mediaItem.Id, true, ct);

                // CRITICAL: If item is also Saved, mark as superseded_conflict
                // This signals admin review needed (user saved vs your files match)
                if (mediaItem.Saved)
                {
                    await _db.SetSupersededConflictAsync(mediaItem.Id, true, ct);
                    _logger.LogWarning("[YourFilesResolver] Item {ItemId} is Saved AND has enabled source, superseded_conflict=true", mediaItem.Id);
                    return ConflictResolution.SupersededConflict;
                }

                await _db.SetSupersededAtAsync(mediaItem.Id, DateTimeOffset.UtcNow, ct);
                _logger.LogInformation("[YourFilesResolver] Item {ItemId} has enabled source, superseded=true (Your Files match)", mediaItem.Id);
                return ConflictResolution.SupersededWithEnabledSource;
            }

            // No enabled source: supersede and remove .strm file
            await _db.SetSupersededAsync(mediaItem.Id, true, ct);
            await _db.SetSupersededAtAsync(mediaItem.Id, DateTimeOffset.UtcNow, ct);
            DeleteStrmFileAsync(mediaItem);

            _logger.LogInformation("[YourFilesResolver] Item {ItemId} matched Your Files, superseded=true and deleted .strm (no enabled source)", mediaItem.Id);

            return ConflictResolution.SupersededWithoutEnabledSource;
        }

        /// <summary>
        /// Deletes the .strm file.
        /// Note: Emby library item removal is handled by Emby when .strm is deleted.
        /// </summary>
        private void DeleteStrmFileAsync(MediaItem item)
        {
            if (string.IsNullOrEmpty(item.StrmPath) || !File.Exists(item.StrmPath))
            {
                return;
            }

            try
            {
                File.Delete(item.StrmPath);
                _logger.LogDebug("[YourFilesResolver] Deleted .strm file for superseded item: {Path}", item.StrmPath);

                // Note: Emby will automatically remove the library item when the .strm file is deleted
                // during the next library scan
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[YourFilesResolver] Failed to delete .strm file for superseded item {ItemId}", item.Id);
            }
        }
    }

    /// <summary>
    /// Resolution outcome for "Your Files" conflict.
    /// </summary>
    public enum ConflictResolution
    {
        /// <summary>Item is Blocked - never supersede user's explicit block</summary>
        KeepBlocked,

        /// <summary>Item has enabled source - supersede stream, keep item (coalition rule)</summary>
        SupersededWithEnabledSource,

        /// <summary>Item has no enabled source - supersede and delete .strm</summary>
        SupersededWithoutEnabledSource,

        /// <summary>Item is Saved AND has enabled source - conflict requiring admin review</summary>
        SupersededConflict
    }
}
