using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Orchestrates the full sync pipeline: fetch → filter → diff → process items.
    /// </summary>
    public class SyncTask : IScheduledTask
    {
        private readonly ManifestFetcher _fetcher;
        private readonly ManifestFilter _filter;
        private readonly ManifestDiff _diff;
        private readonly ItemPipelineService _pipeline;
        private readonly ILogger<SyncTask> _logger;

        public SyncTask(
            ManifestFetcher fetcher,
            ManifestFilter filter,
            ManifestDiff diff,
            ItemPipelineService pipeline,
            ILogger<SyncTask> logger)
        {
            _fetcher = fetcher;
            _filter = filter;
            _diff = diff;
            _pipeline = pipeline;
            _logger = logger;
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        public string Name => "InfiniteDrive Sync";

        public string Key => "embystreams_sync";

        public string Description => "Syncs manifest entries to library";

        public string Category => "InfiniteDrive";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Run every 6 hours
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(0);

                // Step 1: Fetch manifest
                _logger.LogInformation("[SyncTask] Fetching manifest...");
                var manifest = await _fetcher.FetchAllManifestsAsync(cancellationToken);
                progress?.Report(20);

                // Step 2: Filter entries (blocked first, then your files, then release gate)
                _logger.LogInformation("[SyncTask] Filtering {Count} entries...", manifest.Count);
                var filtered = await _filter.FilterEntriesAsync(manifest, SourceType.Aio, cancellationToken);
                progress?.Report(40);

                // Step 3: Diff vs database
                _logger.LogInformation("[SyncTask] Diffing manifest vs database...");
                var diff = await _diff.DiffAsync(filtered, cancellationToken);
                progress?.Report(60);

                // Step 4: Process new items
                _logger.LogInformation("[SyncTask] Processing {Count} new items...", diff.NewItems.Count);
                var processedCount = 0;
                foreach (var entry in diff.NewItems)
                {
                    var item = CreateMediaItem(entry);
                    await _pipeline.ProcessItemAsync(item, PipelineTrigger.Sync, cancellationToken);
                    processedCount++;

                    // Report progress for each 10 items
                    if (processedCount % 10 == 0)
                    {
                        var itemProgress = 60 + (double)processedCount / diff.NewItems.Count * 20;
                        progress?.Report(Math.Min(itemProgress, 80));
                    }
                }
                progress?.Report(80);

                // Step 5: Handle removed items
                _logger.LogInformation("[SyncTask] Handling {Count} removed items...", diff.RemovedItems.Count);
                foreach (var item in diff.RemovedItems)
                {
                    await HandleRemovedItemAsync(item, cancellationToken);
                }
                progress?.Report(100);

                _logger.LogInformation("[SyncTask] Sync complete");
            }
            finally
            {
                Plugin.SyncLock.Release();
            }
        }

        /// <summary>
        /// Creates a MediaItem from a ManifestEntry.
        /// </summary>
        private MediaItem CreateMediaItem(ManifestEntry entry)
        {
            var mediaId = MediaId.Parse(entry.Id);

            return new MediaItem
            {
                PrimaryId = mediaId,
                Title = entry.Name,
                Year = entry.Year,
                MediaType = entry.Type,
                Status = ItemStatus.Known,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Handles an item that was removed from the manifest.
        /// </summary>
        private async Task HandleRemovedItemAsync(MediaItem item, CancellationToken ct)
        {
            // Check if item is saved or has enabled source (coalition rule)
            var db = Plugin.Instance?.DatabaseManager;
            var hasEnabledSource = db != null && await db.ItemHasEnabledSourceAsync(item.Id, ct);

            if (item.Saved || hasEnabledSource)
            {
                // Keep item - start grace period for potential removal
                _logger.LogInformation("[SyncTask] Starting grace period for removed item: {Id}", item.Id);
                item.GraceStartedAt = DateTimeOffset.UtcNow;
                item.UpdatedAt = DateTimeOffset.UtcNow;

                if (Plugin.Instance?.DatabaseManager != null)
                {
                    await Plugin.Instance.DatabaseManager.UpsertMediaItemAsync(item, ct);
                }
            }
            else
            {
                // Remove item from library
                RemoveFromLibraryAsync(item);
            }
        }

        /// <summary>
        /// Removes an item from the library (deletes .strm file).
        /// </summary>
        private void RemoveFromLibraryAsync(MediaItem item)
        {
            _logger.LogInformation("[SyncTask] Removing item from library: {Id}", item.Id);

            // Delete .strm file if exists
            if (!string.IsNullOrEmpty(item.StrmPath) && System.IO.File.Exists(item.StrmPath))
            {
                try
                {
                    System.IO.File.Delete(item.StrmPath);
                    _logger.LogDebug("[SyncTask] Deleted .strm file: {Path}", item.StrmPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SyncTask] Failed to delete .strm file: {Path}", item.StrmPath);
                }
            }

            // TODO: Trigger Emby library refresh to update the UI
            // This would typically be done via ILibraryManager via item deletion
        }
    }
}
