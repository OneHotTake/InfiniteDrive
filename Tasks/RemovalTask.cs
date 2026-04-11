using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Scheduled task for removal pipeline processing.
    /// Processes expired grace period items.
    /// </summary>
    public class RemovalTask : IScheduledTask
    {
        private readonly RemovalPipeline _pipeline;
        private readonly ILogger<RemovalTask> _logger;

        public RemovalTask(
            RemovalPipeline pipeline,
            ILogger<RemovalTask> logger)
        {
            _pipeline = pipeline;
            _logger = logger;
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
            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(0);

                _logger.LogInformation("[RemovalTask] Starting removal pipeline...");

                // Process expired grace period items
                var result = await _pipeline.ProcessExpiredGraceItemsAsync(cancellationToken);
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
