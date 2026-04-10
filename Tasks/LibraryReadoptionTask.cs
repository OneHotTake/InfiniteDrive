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
    /// Scheduled task that detects catalog items currently managed by the plugin
    /// as .strm files (<c>local_source='strm'</c>) for which the user has since
    /// acquired a real media file in their Emby library.
    ///
    /// <para><b>DEPRECATED (Sprint 66):</b> This task has been consolidated into
    /// <see cref="DoctorTask"/>. Use the Doctor task instead for all catalog
    /// reconciliation operations.</para>
    ///
    /// When a match is found the task:
    /// <list type="number">
    ///   <item>Updates <c>local_source='library'</c> and records the real file path.</item>
    ///   <item>Optionally deletes the .strm file and its parent folder (if empty)
    ///         to prevent Emby from showing the title twice.</item>
    /// </list>
    ///
    /// This is the complement to <see cref="FileResurrectionTask"/>:
    /// <list type="bullet">
    ///   <item><see cref="FileResurrectionTask"/> — real file gone → rebuild .strm</item>
    ///   <item><see cref="LibraryReadoptionTask"/> — real file appeared → retire .strm</item>
    /// </list>
    ///
    /// Default schedule: every 6 hours.
    /// </summary>
    [Obsolete("Use DoctorTask instead (Sprint 66)")]
    public class LibraryReadoptionTask : IScheduledTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string TaskName     = "EmbyStreams Library Re-adoption";
        private const string TaskKey      = "EmbyStreamsLibraryReadoption";
        private const string TaskCategory = "EmbyStreams";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<LibraryReadoptionTask> _logger;
        private readonly ILibraryManager                _libraryManager;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="libraryManager"/> and
        /// <paramref name="loggerFactory"/> automatically.
        /// </summary>
        public LibraryReadoptionTask(
            ILibraryManager  libraryManager,
            ILogManager      logManager)
        {
            _libraryManager = libraryManager;
            _logger         = new EmbyLoggerAdapter<LibraryReadoptionTask>(logManager.GetLogger("EmbyStreams"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Checks plugin-managed .strm items against the Emby library. " +
            "When the user acquires a real media file for a streamed title, " +
            "retires the .strm to prevent duplicate entries.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks,
                }
            };
        //
        // NOTE (Sprint 152): Primary re-adoption mechanism is LibraryPostScanReadoptionService
        // (ILibraryPostScanTask), which fires immediately after every Emby library scan.
        // This scheduled task is the safety net for environments where scans are infrequent
        // or where file system notifications are unreliable (NFS, remote mounts, etc.).
        //

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("[EmbyStreams] LibraryReadoptionTask started");
            progress.Report(0);

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[EmbyStreams] Plugin configuration not available — aborting re-adoption check");
                return;
            }

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[EmbyStreams] DatabaseManager not available — aborting re-adoption check");
                return;
            }

            // 1. Load all items currently managed as .strm files.
            List<CatalogItem> strmItems;
            try
            {
                strmItems = await db.GetItemsByLocalSourceAsync("strm");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Failed to query .strm-tracked items");
                return;
            }

            if (strmItems.Count == 0)
            {
                _logger.LogInformation("[EmbyStreams] No .strm-tracked items to check — re-adoption pass skipped");
                progress.Report(100);
                return;
            }

            _logger.LogInformation(
                "[EmbyStreams] Re-adoption check: {Count} .strm item(s) to compare against library",
                strmItems.Count);

            progress.Report(10);

            // 2. Build the Emby library map (IMDB ID → real file path), excluding
            //    the plugin's own sync directories so we never match our own .strm files.
            var libraryMap = CatalogSyncTask.BuildLibraryItemMapPublic(config, _libraryManager, _logger);

            _logger.LogInformation(
                "[EmbyStreams] Library map built: {Count} external item(s) found",
                libraryMap.Count);

            progress.Report(20);

            var checkedCount  = 0;
            var adoptedCount  = 0;
            var deletedCount  = 0;
            var failedCount   = 0;

            for (int i = 0; i < strmItems.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(20.0 + 75.0 * i / strmItems.Count);

                var item = strmItems[i];
                checkedCount++;

                // Check if this IMDB ID now exists in the user's real library.
                if (!libraryMap.TryGetValue(item.ImdbId, out var realPath))
                    continue;

                // Item has been acquired — switch tracking to the real file.
                adoptedCount++;
                _logger.LogInformation(
                    "[EmbyStreams] '{Title}' ({ImdbId}): real copy found at '{RealPath}' — retiring .strm",
                    item.Title, item.ImdbId, realPath);

                try
                {
                    // Update the database record before touching the filesystem so
                    // a crash mid-operation leaves us in a consistent state.
                    await db.UpdateLocalPathAsync(item.ImdbId, item.Source, realPath, "library");

                    // Optionally remove the .strm file to avoid Emby showing duplicates.
                    if (config.DeleteStrmOnReadoption)
                    {
                        var strmDeleted = TryDeleteStrm(item.LocalPath);
                        if (strmDeleted) deletedCount++;
                    }

                    _logger.LogInformation(
                        "[EmbyStreams] '{Title}' ({ImdbId}): re-adopted → '{RealPath}'",
                        item.Title, item.ImdbId, realPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[EmbyStreams] '{Title}' ({ImdbId}): re-adoption update failed",
                        item.Title, item.ImdbId);
                    failedCount++;
                }
            }

            progress.Report(100);

            _logger.LogInformation(
                "[EmbyStreams] LibraryReadoptionTask complete — " +
                "checked: {Checked}, adopted: {Adopted}, .strm deleted: {Deleted}, failed: {Failed}",
                checkedCount, adoptedCount, deletedCount, failedCount);

            // Trigger a library scan so Emby picks up the removed .strm entries.
            if (adoptedCount > 0)
                TriggerLibraryScan();

            await Task.CompletedTask;
        }

        // ── Private ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes the .strm file at <paramref name="strmPath"/> and, if the
        /// parent directory is now empty, removes that directory too.
        /// Returns true if the file was deleted successfully (or already absent).
        /// </summary>
        private bool TryDeleteStrm(string? strmPath)
        {
            if (string.IsNullOrEmpty(strmPath))
                return false;

            try
            {
                if (File.Exists(strmPath))
                {
                    File.Delete(strmPath);
                    _logger.LogDebug("[EmbyStreams] Deleted .strm: {Path}", strmPath);
                }

                // Remove the containing folder if it's now empty (e.g. movie folder
                // that only contained the single .strm file).
                var dir = Path.GetDirectoryName(strmPath);
                if (!string.IsNullOrEmpty(dir)
                    && Directory.Exists(dir)
                    && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    _logger.LogDebug("[EmbyStreams] Removed empty folder: {Dir}", dir);

                    // Also remove the grandparent folder if it's empty (series season dir).
                    var parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parent)
                        && Directory.Exists(parent)
                        && Directory.GetFileSystemEntries(parent).Length == 0)
                    {
                        Directory.Delete(parent);
                        _logger.LogDebug("[EmbyStreams] Removed empty folder: {Dir}", parent);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Could not delete .strm at {Path}", strmPath);
                return false;
            }
        }

        private void TriggerLibraryScan()
        {
            try
            {
                _libraryManager.QueueLibraryScan();
                _logger.LogInformation("[EmbyStreams] LibraryReadoptionTask: triggered Emby library scan");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] LibraryReadoptionTask: failed to queue library scan");
            }
        }
    }
}
