using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Scheduled task for "Your Files" reconciliation.
    /// Scans library, matches items, resolves conflicts.
    /// </summary>
    public class YourFilesTask : IScheduledTask
    {
        private readonly YourFilesScanner _scanner;
        private readonly YourFilesMatcher _matcher;
        private readonly YourFilesConflictResolver _resolver;
        private readonly ILogger<YourFilesTask> _logger;

        public YourFilesTask(
            YourFilesScanner scanner,
            YourFilesMatcher matcher,
            YourFilesConflictResolver resolver,
            ILogger<YourFilesTask> logger)
        {
            _scanner = scanner;
            _matcher = matcher;
            _resolver = resolver;
            _logger = logger;
        }

        public string Name => "InfiniteDrive Your Files Reconciler";
        public string Key => "embystreams_yourfiles";
        public string Description => "Reconciles 'Your Files' with InfiniteDrive items";
        public string Category => "InfiniteDrive";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(6).Ticks
                }
            };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(0);

                _logger.LogInformation("[YourFilesTask] Starting Your Files reconciliation...");

                // Step 1: Scan library
                var yourFilesItems = await _scanner.ScanAsync(cancellationToken);
                progress?.Report(25);

                // Step 2: Match against media_item_ids
                var matches = await _matcher.MatchAsync(yourFilesItems, cancellationToken);
                progress?.Report(50);

                // Step 3: Resolve conflicts
                var resolutions = new List<ConflictResolution>();
                foreach (var match in matches)
                {
                    var resolution = await _resolver.ResolveAsync(match, cancellationToken);
                    resolutions.Add(resolution);
                }
                progress?.Report(75);

                // Step 4: Report results
                var summary = new YourFilesSummary(
                    yourFilesItems.Count,
                    matches.Count,
                    resolutions.Count(r => r == ConflictResolution.KeepBlocked),
                    resolutions.Count(r => r == ConflictResolution.SupersededWithEnabledSource),
                    resolutions.Count(r => r == ConflictResolution.SupersededWithoutEnabledSource),
                    resolutions.Count(r => r == ConflictResolution.SupersededConflict)
                );
                progress?.Report(100);

                _logger.LogInformation(
                    "[YourFilesTask] Your Files reconciliation complete: " +
                    "Scanned={TotalScanned}, " +
                    "Matches={TotalMatches}, " +
                    "KeptBlocked={KeptBlocked}, " +
                    "SupersededWithEnabledSource={SupersededWithEnabledSource}, " +
                    "SupersededWithoutEnabledSource={SupersededWithoutEnabledSource}, " +
                    "SupersededConflict={SupersededConflict}",
                    summary.TotalScanned,
                    summary.TotalMatches,
                    summary.KeptBlocked,
                    summary.SupersededWithEnabledSource,
                    summary.SupersededWithoutEnabledSource,
                    summary.SupersededConflict);
            }
            finally
            {
                Plugin.SyncLock.Release();
            }
        }
    }

    /// <summary>
    /// Summary of "Your Files" reconciliation.
    /// </summary>
    public record YourFilesSummary(
        int TotalScanned,
        int TotalMatches,
        int KeptBlocked,
        int SupersededWithEnabledSource,
        int SupersededWithoutEnabledSource,
        int SupersededConflict
    );
}
