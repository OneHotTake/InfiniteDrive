using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Orchestrates the full sync pipeline: fetch → filter → diff → process items.
    /// </summary>
    public class SyncTask : IScheduledTask
    {
        private readonly ILogManager _logManager;
        private readonly ILogger<SyncTask> _logger;

        public SyncTask(ILogManager logManager)
        {
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<SyncTask>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        public string Name => "InfiniteDrive Sync";

        public string Key => "embystreams_sync";

        public string Description => "Syncs manifest entries to library";

        public string Category => "InfiniteDrive";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[SyncTask] DatabaseManager not ready — skipping");
                return;
            }
            var config = Plugin.Instance!.Configuration;
            var log = _logManager;

            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(0);

                var aioClient = new AioStreamsClient(config, new EmbyLoggerAdapter<AioStreamsClient>(log.GetLogger("InfiniteDrive")));
                var releaseGate = new DigitalReleaseGateService(new HttpClient(), new EmbyLoggerAdapter<DigitalReleaseGateService>(log.GetLogger("InfiniteDrive")));
                var cinemetaProvider = new CinemetaProvider(new EmbyLoggerAdapter<CinemetaProvider>(log.GetLogger("InfiniteDrive")));
                var metadataHydrator = new MetadataHydrator(new EmbyLoggerAdapter<MetadataHydrator>(log.GetLogger("InfiniteDrive")), cinemetaProvider);
                var streamResolver = new StreamResolver(new EmbyLoggerAdapter<StreamResolver>(log.GetLogger("InfiniteDrive")), aioClient);
                var fetcher = new ManifestFetcher(aioClient, db, new EmbyLoggerAdapter<ManifestFetcher>(log.GetLogger("InfiniteDrive")));
                var filter = new ManifestFilter(releaseGate, db, new EmbyLoggerAdapter<ManifestFilter>(log.GetLogger("InfiniteDrive")));
                var diff = new ManifestDiff(db, new EmbyLoggerAdapter<ManifestDiff>(log.GetLogger("InfiniteDrive")));
                var pipeline = new ItemPipelineService(new EmbyLoggerAdapter<ItemPipelineService>(log.GetLogger("InfiniteDrive")), log, db, streamResolver, metadataHydrator, releaseGate);

                // Step 1: Fetch manifest
                _logger.LogInformation("[SyncTask] Fetching manifest...");
                var manifest = await fetcher.FetchAllManifestsAsync(cancellationToken);
                progress?.Report(20);

                // Step 2: Filter entries (blocked first, then your files, then release gate)
                _logger.LogInformation("[SyncTask] Filtering {Count} entries...", manifest.Count);
                var filtered = await filter.FilterEntriesAsync(manifest, SourceType.Aio, cancellationToken);
                progress?.Report(40);

                // Step 3: Diff vs database
                _logger.LogInformation("[SyncTask] Diffing manifest vs database...");
                var diffResult = await diff.DiffAsync(filtered, cancellationToken);
                progress?.Report(60);

                // Step 4: Process new items
                _logger.LogInformation("[SyncTask] Processing {Count} new items...", diffResult.NewItems.Count);
                var processedCount = 0;
                foreach (var entry in diffResult.NewItems)
                {
                    var item = CreateMediaItem(entry);
                    await pipeline.ProcessItemAsync(item, PipelineTrigger.Sync, cancellationToken);
                    processedCount++;

                    if (processedCount % 10 == 0)
                    {
                        var itemProgress = 60 + (double)processedCount / diffResult.NewItems.Count * 20;
                        progress?.Report(Math.Min(itemProgress, 80));
                    }
                }
                progress?.Report(80);

                // Step 5: Handle removed items
                _logger.LogInformation("[SyncTask] Handling {Count} removed items...", diffResult.RemovedItems.Count);
                foreach (var item in diffResult.RemovedItems)
                {
                    await HandleRemovedItemAsync(item, db, cancellationToken);
                }
                progress?.Report(100);

                _logger.LogInformation("[SyncTask] Sync complete");
            }
            finally
            {
                Plugin.SyncLock.Release();
            }
        }

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

        private async Task HandleRemovedItemAsync(MediaItem item, DatabaseManager db, CancellationToken ct)
        {
            var hasEnabledSource = await db.ItemHasEnabledSourceAsync(item.Id, ct);

            if (item.Saved || hasEnabledSource)
            {
                _logger.LogInformation("[SyncTask] Starting grace period for removed item: {Id}", item.Id);
                item.GraceStartedAt = DateTimeOffset.UtcNow;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await db.UpsertMediaItemAsync(item, ct);
            }
            else
            {
                _logger.LogInformation("[SyncTask] Removing item from library: {Id}", item.Id);
                if (!string.IsNullOrEmpty(item.StrmPath) && System.IO.File.Exists(item.StrmPath))
                {
                    try { System.IO.File.Delete(item.StrmPath); }
                    catch (Exception ex) { _logger.LogError(ex, "[SyncTask] Failed to delete .strm file: {Path}", item.StrmPath); }
                }
            }
        }
    }
}
