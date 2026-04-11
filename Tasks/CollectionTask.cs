using System;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Scheduled task for syncing collections.
    /// </summary>
    public class CollectionTask : IScheduledTask
    {
        private readonly ILogManager _logManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;
        private readonly ILogger<CollectionTask> _logger;

        public CollectionTask(
            ILogManager logManager,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager)
        {
            _logManager = logManager;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
            _logger = new EmbyLoggerAdapter<CollectionTask>(logManager.GetLogger("InfiniteDrive"));
        }

        public string Name => "InfiniteDrive Collection Sync";
        public string Key => "embystreams_collections";
        public string Description => "Syncs sources with ShowAsCollection to Emby BoxSets";
        public string Category => "InfiniteDrive";

        public System.Collections.Generic.IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[CollectionTask] DatabaseManager not ready — skipping");
                return;
            }

            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(0);
                _logger.LogInformation("[CollectionTask] Starting collection sync task...");

                var boxSetService = new BoxSetService(
                    _collectionManager,
                    _libraryManager,
                    new EmbyLoggerAdapter<BoxSetService>(_logManager.GetLogger("InfiniteDrive")));
                var service = new CollectionSyncService(
                    db,
                    boxSetService,
                    new EmbyLoggerAdapter<CollectionSyncService>(_logManager.GetLogger("InfiniteDrive")));

                var result = await service.SyncCollectionsAsync(cancellationToken);
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
