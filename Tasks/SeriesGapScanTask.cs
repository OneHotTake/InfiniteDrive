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
    /// Scheduled task that runs <see cref="SeriesGapDetector"/> for all indexed series.
    /// Detects missing episodes by comparing Emby's canonical season/episode map
    /// against our .strm coverage.
    /// Default schedule: every 12 hours.
    /// </summary>
    [Obsolete("Superseded by catalog-first episode sync (Sprint 222)")]
    public class SeriesGapScanTask : IScheduledTask
    {
        private const string TaskName     = "InfiniteDrive: Series Gap Scan";
        private const string TaskKey      = "InfiniteDriveSeriesGapScan";
        private const string TaskCategory = "InfiniteDrive";

        private readonly ILogger<SeriesGapScanTask> _logger;

        public SeriesGapScanTask(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<SeriesGapScanTask>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        public string Name => TaskName;
        public string Key => TaskKey;
        public string Description => "Detects missing episodes in indexed series by querying Emby TV endpoints.";
        public string Category => TaskCategory;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(12).Ticks,
                }
            };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("[InfiniteDrive] SeriesGapScanTask started");
            progress.Report(0);

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[InfiniteDrive] DatabaseManager not available — aborting gap scan");
                return;
            }

            var tvClient = new EmbyTvApiClient(_logger);
            var detector = new SeriesGapDetector(tvClient, db, _logger);

            await detector.ScanAllAsync(progress, cancellationToken);

            _logger.LogInformation("[InfiniteDrive] SeriesGapScanTask complete");
        }
    }
}
