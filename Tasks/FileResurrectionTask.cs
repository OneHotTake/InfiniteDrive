using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Tasks
{
    /// <summary>
    /// Scheduled task that detects catalog items previously recorded as real
    /// library files (<c>local_source='library'</c>) whose original file has
    /// since disappeared, and rebuilds them as plugin-managed .strm files.
    ///
    /// <para><b>DEPRECATED (Sprint 66):</b> This task has been consolidated into
    /// <see cref="DoctorTask"/>. Use the Doctor task instead for all catalog
    /// reconciliation operations.</para>
    ///
    /// This is the "file resurrection" safety net: if a user's local copy of a
    /// title is deleted, the drive goes offline, or a library re-scan removes
    /// it, the next resurrection pass transparently falls back to streaming via
    /// AIOStreams — without any manual intervention.
    ///
    /// File checks are purely filesystem-based (<see cref="File.Exists"/>) and
    /// never call any external API, so the task is cheap to run frequently.
    ///
    /// Default schedule: every 2 hours.
    /// </summary>
    [Obsolete("Use DoctorTask instead (Sprint 66)")]
    public class FileResurrectionTask : IScheduledTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string TaskName     = "EmbyStreams File Resurrection";
        private const string TaskKey      = "EmbyStreamsFileResurrection";
        private const string TaskCategory = "EmbyStreams";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<FileResurrectionTask> _logger;
        private readonly ILibraryManager               _libraryManager;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="libraryManager"/> and
        /// <paramref name="loggerFactory"/> automatically.
        /// </summary>
        public FileResurrectionTask(
            ILibraryManager  libraryManager,
            ILogManager      logManager)
        {
            _libraryManager = libraryManager;
            _logger         = new EmbyLoggerAdapter<FileResurrectionTask>(logManager.GetLogger("EmbyStreams"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Checks catalog items currently tracked as real library files. " +
            "If the original file is missing, rebuilds a streaming .strm as a fallback.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(2).Ticks,
                }
            };

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("[EmbyStreams] FileResurrectionTask started");
            progress.Report(0);

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[EmbyStreams] Plugin configuration not available — aborting resurrection check");
                return;
            }

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[EmbyStreams] DatabaseManager not available — aborting resurrection check");
                return;
            }

            // Load all items the plugin has handed off to the local library.
            List<CatalogItem> candidates;
            try
            {
                candidates = await db.GetItemsByLocalSourceAsync("library");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Failed to query library-tracked items");
                return;
            }

            if (candidates.Count == 0)
            {
                _logger.LogInformation("[EmbyStreams] No library-tracked items to check — resurrection pass skipped");
                progress.Report(100);
                return;
            }

            _logger.LogInformation(
                "[EmbyStreams] Resurrection check: {Count} library-tracked item(s) to verify", candidates.Count);

            var checkedCount     = 0;
            var missingCount     = 0;
            var resurrectedCount = 0;
            var failedCount      = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(10.0 + 85.0 * i / candidates.Count);

                var item = candidates[i];
                checkedCount++;

                // Items with no recorded path cannot be verified — skip silently.
                if (string.IsNullOrEmpty(item.LocalPath))
                    continue;

                // File still present — nothing to do.
                if (File.Exists(item.LocalPath))
                    continue;

                // ── File is missing — attempt resurrection ────────────────────

                missingCount++;
                _logger.LogInformation(
                    "[EmbyStreams] '{Title}' ({ImdbId}): library file gone at '{Path}' — writing .strm fallback",
                    item.Title, item.ImdbId, item.LocalPath);

                try
                {
                    var strmPath = await CatalogSyncTask.WriteStrmFileForItemPublicAsync(item, config);

                    if (strmPath == null)
                    {
                        _logger.LogWarning(
                            "[EmbyStreams] '{Title}' ({ImdbId}): .strm write returned null — " +
                            "SyncPath may not be configured",
                            item.Title, item.ImdbId);
                        failedCount++;
                        continue;
                    }

                    await db.UpdateStrmPathAsync(item.ImdbId, item.Source, strmPath);
                    await db.UpdateLocalPathAsync(item.ImdbId, item.Source, strmPath, "strm");
                    await db.IncrementResurrectionCountAsync(item.ImdbId, item.Source);

                    resurrectedCount++;
                    _logger.LogInformation(
                        "[EmbyStreams] '{Title}' ({ImdbId}): resurrected → '{StrmPath}'",
                        item.Title, item.ImdbId, strmPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[EmbyStreams] '{Title}' ({ImdbId}): resurrection failed",
                        item.Title, item.ImdbId);
                    failedCount++;
                }
            }

            progress.Report(100);

            _logger.LogInformation(
                "[EmbyStreams] FileResurrectionTask complete — " +
                "checked: {Checked}, missing: {Missing}, resurrected: {Resurrected}, failed: {Failed}",
                checkedCount, missingCount, resurrectedCount, failedCount);

            // Trigger a library scan so Emby picks up the newly written .strm files.
            if (resurrectedCount > 0)
                TriggerLibraryScan();
        }

        // ── Private ─────────────────────────────────────────────────────────────

        private void TriggerLibraryScan()
        {
            try
            {
                _libraryManager.QueueLibraryScan();
                _logger.LogInformation("[EmbyStreams] FileResurrectionTask: triggered Emby library scan");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] FileResurrectionTask: failed to queue library scan");
            }
        }
    }
}
