using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Scheduled task for removal pipeline processing.
    /// Processes expired grace period items.
    /// </summary>
    public class RemovalTask : IScheduledTask
    {
        private readonly ILogManager _logManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<RemovalTask> _logger;

        public RemovalTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            _logManager = logManager;
            _libraryManager = libraryManager;
            _logger = new EmbyLoggerAdapter<RemovalTask>(logManager.GetLogger("InfiniteDrive"));
        }

        public string Name => "InfiniteDrive Removal Cleanup";
        public string Key => "embystreams_removal";
        public string Description => "Processes expired grace period items for removal";
        public string Category => "InfiniteDrive";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(1).Ticks
                }
            };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[RemovalTask] DatabaseManager not ready — skipping");
                return;
            }

            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(0);
                _logger.LogInformation("[RemovalTask] Starting removal pipeline...");

                var removalService = new RemovalService(
                    db,
                    _libraryManager,
                    new EmbyLoggerAdapter<RemovalService>(_logManager.GetLogger("InfiniteDrive")),
                    Plugin.Instance!.Configuration);
                var pipeline = new RemovalPipeline(
                    removalService,
                    db,
                    new EmbyLoggerAdapter<RemovalPipeline>(_logManager.GetLogger("InfiniteDrive")));

                var result = await pipeline.ProcessExpiredGraceItemsAsync(cancellationToken);
                progress?.Report(100);

                _logger.LogInformation(
                    "[RemovalTask] Removal pipeline complete: {Total} processed, {Removed} removed, {Cancelled} cancelled, {Extended} still active",
                    result.TotalProcessed, result.RemovedCount, result.CancelledCount, result.ExtendedCount);
            }
            finally
            {
                Plugin.SyncLock.Release();
            }
        }
    }
}
