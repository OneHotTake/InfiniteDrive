using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Services;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Scheduled task that repairs missing episode .strm files.
    /// Runs every 6 hours, offset from SeriesGapScanTask's 12h schedule.
    /// </summary>
    [Obsolete("Superseded by catalog-first episode sync (Sprint 222)")]
    public class SeriesGapRepairTask : IScheduledTask
    {
        private const string TaskName     = "InfiniteDrive: Series Gap Repair";
        private const string TaskKey      = "InfiniteDriveSeriesGapRepair";
        private const string TaskCategory = "InfiniteDrive";

        private readonly ILogger<SeriesGapRepairTask> _logger;

        public SeriesGapRepairTask(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<SeriesGapRepairTask>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        public string Name => TaskName;
        public string Key => TaskKey;
        public string Description => "Writes missing .strm files for series episodes detected by the gap scanner.";
        public string Category => TaskCategory;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(6).Ticks,
                }
            };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("[InfiniteDrive] SeriesGapRepairTask started");
            progress.Report(0);

            var db = Plugin.Instance?.DatabaseManager;
            var strmWriter = Plugin.Instance?.StrmWriterService;
            if (db == null || strmWriter == null)
            {
                _logger.LogWarning("[InfiniteDrive] Dependencies not available — aborting gap repair");
                return;
            }

            var service = new SeriesGapRepairService(db, strmWriter, _logger);
            var result = await service.RepairSeriesGapsAsync(50, cancellationToken);

            _logger.LogInformation(
                "[InfiniteDrive] SeriesGapRepairTask complete: {Written} episodes written",
                result.EpisodesWritten);
        }
    }
}
