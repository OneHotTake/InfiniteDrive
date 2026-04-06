using System;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Tasks
{
    /// <summary>
    /// Scheduled task for syncing collections.
    /// </summary>
    public class CollectionTask : IScheduledTask
    {
        private readonly CollectionSyncService _service;
        private readonly ILogger<CollectionTask> _logger;

        public CollectionTask(
            CollectionSyncService service,
            ILogger<CollectionTask> logger)
        {
            _service = service;
            _logger = logger;
        }

        public string Name => "EmbyStreams Collection Sync";
        public string Key => "embystreams_collections";
        public string Description => "Syncs sources with ShowAsCollection to Emby BoxSets";
        public string Category => "EmbyStreams";

        /// <summary>
        /// Returns default triggers for this task (runs every 1 hour).
        /// </summary>
        public System.Collections.Generic.IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            };
        }

        /// <summary>
        /// Executes the collection sync task.
        /// </summary>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(0);

                _logger.LogInformation("[CollectionTask] Starting collection sync task...");

                var result = await _service.SyncCollectionsAsync(cancellationToken);
                progress?.Report(100);

                _logger.LogInformation(
                    "[CollectionTask] Collection sync complete: {Success}/{Total} collections synced, {ItemCount} items",
                    result.SuccessCount, result.TotalProcessed, result.TotalItemsSynced);
            }
            finally
            {
                Plugin.SyncLock.Release();
            }
        }
    }
}
