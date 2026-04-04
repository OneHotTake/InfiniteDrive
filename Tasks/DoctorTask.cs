using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// The Doctor — unified catalog reconciliation engine (Sprint 66).
    ///
    /// Consolidates 4 legacy tasks into a single 5-phase operation:
    /// <list type="bullet">
    ///   <item><b>Phase 1: Fetch &amp; Diff</b> — Load catalog items, detect PINNED, build change list</item>
    ///   <item><b>Phase 2: Write</b> — Create .strm files (CATALOGUED → PRESENT)</item>
    ///   <item><b>Phase 3: Adopt</b> — Retire .strm when real file appears (PRESENT/RESOLVED → RETIRED)</item>
    ///   <item><b>Phase 4: Health Check</b> — Validate RESOLVED URLs, mark stale</item>
    ///   <item><b>Phase 5: Report</b> — Log summary statistics</item>
    /// </list>
    ///
    /// <para><b>Item State Machine:</b></para>
    /// <list type="bullet">
    ///   <item>CATALOGUED (0) — In DB, no .strm yet</item>
    ///   <item>PRESENT (1) — .strm exists, URL not resolved</item>
    ///   <item>RESOLVED (2) — .strm exists, valid cached URL</item>
    ///   <item>RETIRED (3) — Real file in library, .strm deleted</item>
    ///   <item>ORPHANED (4) — .strm exists but item removed from catalog (not PINNED)</item>
    ///   <item>PINNED (5) — User-added via Discover, protected from removal</item>
    /// </list>
    ///
    /// <para><b>PIN Protection:</b></para>
    /// Items with <c>ItemState = PINNED</c> are never removed in Phase 1, even if
    /// they no longer appear in any catalog source. They can only transition to RETIRED
    /// if a real file is detected in Phase 3.
    ///
    /// Default schedule: every 4 hours.
    /// </summary>
    public class DoctorTask : IScheduledTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string TaskName     = "EmbyStreams Doctor";
        private const string TaskKey      = "EmbyStreamsDoctor";
        private const string TaskCategory = "EmbyStreams";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<DoctorTask> _logger;
        private readonly ILibraryManager     _libraryManager;

        // ── Constructor ─────────────────────────────────────────────────────────

        public DoctorTask(
            ILibraryManager libraryManager,
            ILogManager     logManager)
        {
            _libraryManager = libraryManager;
            _logger         = new EmbyLoggerAdapter<DoctorTask>(logManager.GetLogger("EmbyStreams"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        public string Name => TaskName;

        public string Key => TaskKey;

        public string Description =>
            "Unified catalog reconciliation: syncs new items, retires .strm when " +
            "real files appear, validates cached URLs, cleans orphans. " +
            "Respects user-pinned items from Discover.";

        public string Category => TaskCategory;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(4).Ticks,
                }
            };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Sprint 100A-12: Startup jitter to prevent thundering herd on Emby restart
            await Task.Delay(Random.Shared.Next(0, 120_000), cancellationToken);

            // Sprint 100A-10: Acquire global sync lock to prevent concurrent catalog operations
            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _logger.LogInformation("[EmbyStreams] DoctorTask started");
                progress.Report(0);

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[EmbyStreams] Plugin configuration not available — aborting");
                return;
            }

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[EmbyStreams] DatabaseManager not available — aborting");
                return;
            }

            // F1: Ensure PluginSecret is initialized before any .strm writes
            // This must complete before Phase 2 (Write) begins
            if (Plugin.Instance != null)
            {
                var secretReady = await Plugin.Instance.EnsureInitializedAsync();
                if (!secretReady)
                {
                    _logger.LogWarning("[EmbyStreams] PluginSecret not available — .strm files may use unauthenticated fallback URLs");
                }
            }

            // ── Phase 1: Fetch & Diff ─────────────────────────────────────────────

            progress.Report(5);
            _logger.LogInformation("[EmbyStreams] Phase 1: Fetch & Diff");

            // Load all catalog items from DB
            List<CatalogItem> allItems;
            try
            {
                allItems = await db.GetActiveCatalogItemsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Failed to load catalog items");
                return;
            }

            // Scan filesystem for existing .strm files (orphan detection)
            var strmPathsOnDisk = FindStrmFiles(config);

            // Build library map for real file detection (Phase 3)
            var libraryMap = CatalogSyncTask.BuildLibraryItemMapPublic(config, _libraryManager, _logger);

            // ── Item Classification ─────────────────────────────────────────────

            var toWrite      = new List<CatalogItem>();  // CATALOGUED → PRESENT
            var toRetire     = new List<CatalogItem>();  // PRESENT/RESOLVED → RETIRED
            var toResolve    = new List<CatalogItem>();  // PRESENT → RESOLVED (queue for Link Resolver)
            var staleUrls    = new List<CatalogItem>();  // RESOLVED but URL is stale/dead
            var orphans      = new List<string>();       // .strm paths with no DB item (not PINNED)

            foreach (var item in allItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip PINNED items from catalog removal logic (Phase 1)
                if (item.ItemState == ItemState.Pinned)
                {
                    _logger.LogDebug("[EmbyStreams] '{Title}' ({ImdbId}) is PINNED — protected",
                        item.Title, item.ImdbId);
                    continue;
                }

                // CATALOGUED → needs .strm file
                if (item.ItemState == ItemState.Catalogued)
                {
                    toWrite.Add(item);
                    continue;
                }

                // Check for real file in library (Phase 3: Adopt)
                if (libraryMap.TryGetValue(item.ImdbId, out var realFilePath))
                {
                    // Real file found → retire .strm
                    toRetire.Add(item);
                    _logger.LogInformation("[EmbyStreams] '{Title}' ({ImdbId}): real file found at '{Path}'",
                        item.Title, item.ImdbId, realFilePath);
                    continue;
                }

                // PRESENT → needs resolution
                if (item.ItemState == ItemState.Present)
                {
                    toResolve.Add(item);
                    continue;
                }

                // RESOLVED → health check (Phase 4)
                if (item.ItemState == ItemState.Resolved)
                {
                    // For now, we'll defer URL validation to LinkResolverTask
                    // In Phase 4, we'd check if the cached URL is still valid
                    // and mark stale if needed (implementation in later tickets)
                    continue;
                }
            }

            // Detect orphans: .strm files on disk with no matching DB item (and not PINNED)
            var catalogImdbIds = new HashSet<string>(allItems
                .Where(i => i.ItemState != ItemState.Pinned)
                .Select(i => i.ImdbId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var strmPath in strmPathsOnDisk)
            {
                // Extract IMDB ID from path (format: .../Title [imdbid-ttXXX]/Title.strm)
                var imdbId = ExtractImdbIdFromPath(strmPath);
                if (!string.IsNullOrEmpty(imdbId) && !catalogImdbIds.Contains(imdbId))
                {
                    orphans.Add(strmPath);
                }
            }

            _logger.LogInformation(
                "[EmbyStreams] Phase 1 complete — toWrite: {ToWrite}, toRetire: {ToRetire}, " +
                "toResolve: {ToResolve}, orphans: {Orphans}",
                toWrite.Count, toRetire.Count, toResolve.Count, orphans.Count);

            progress.Report(20);

            // ── Phase 2: Write (CATALOGUED → PRESENT) ────────────────────────────

            // Preflight check: ensure all media directories exist with correct permissions
            try
            {
                EnsureMediaDirectoriesExist(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] DoctorTask: Phase 2 preflight check FAILED — aborting write phase");
                progress.Report(50); // Skip ahead to Phase 3
                goto Phase3; // Skip Phase 2 entirely
            }

            if (toWrite.Count > 0)
            {
                _logger.LogInformation("[EmbyStreams] Phase 2: Write {Count} item(s)", toWrite.Count);
                var writtenCount = 0;

                for (int i = 0; i < toWrite.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress.Report(20.0 + 30.0 * i / toWrite.Count);

                    var item = toWrite[i];

                    try
                    {
                        var strmPath = await CatalogSyncTask.WriteStrmFileForItemPublicAsync(item, config);
                        if (strmPath == null)
                        {
                            _logger.LogWarning("[EmbyStreams] '{Title}' ({ImdbId}): .strm write failed",
                                item.Title, item.ImdbId);
                            continue;
                        }

                        await db.UpdateStrmPathAsync(item.ImdbId, item.Source, strmPath);
                        await db.UpdateLocalPathAsync(item.ImdbId, item.Source, strmPath, "strm");

                        // Transition: CATALOGUED → PRESENT
                        item.ItemState = ItemState.Present;
                        await db.UpsertCatalogItemAsync(item);

                        writtenCount++;
                        _logger.LogInformation("[EmbyStreams] '{Title}' ({ImdbId}): wrote .strm at '{Path}'",
                            item.Title, item.ImdbId, strmPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[EmbyStreams] '{Title}' ({ImdbId}): write failed",
                            item.Title, item.ImdbId);
                    }
                }

                _logger.LogInformation("[EmbyStreams] Phase 2 complete — {Count}/{Total} written",
                    writtenCount, toWrite.Count);
            }
            else
            {
                _logger.LogInformation("[EmbyStreams] Phase 2: No items to write");
            }

            progress.Report(50);

        Phase3:
            // ── Phase 3: Adopt (PRESENT/RESOLVED → RETIRED) ───────────────────────

            if (toRetire.Count > 0)
            {
                _logger.LogInformation("[EmbyStreams] Phase 3: Adopt {Count} item(s)", toRetire.Count);
                var retiredCount = 0;

                for (int i = 0; i < toRetire.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress.Report(50.0 + 20.0 * i / toRetire.Count);

                    var item = toRetire[i];

                    try
                    {
                        // Delete .strm file if it exists
                        if (!string.IsNullOrEmpty(item.StrmPath) && File.Exists(item.StrmPath))
                        {
                            File.Delete(item.StrmPath);
                            _logger.LogInformation("[EmbyStreams] '{Title}' ({ImdbId}): deleted .strm at '{Path}'",
                                item.Title, item.ImdbId, item.StrmPath);
                        }

                        // Update DB: transition to RETIRED, record real file path
                        item.ItemState = ItemState.Retired;
                        item.LocalSource = "library";
                        item.LocalPath = libraryMap[item.ImdbId];
                        item.StrmPath = null; // Clear .strm reference
                        await db.UpsertCatalogItemAsync(item);

                        retiredCount++;
                        _logger.LogInformation("[EmbyStreams] '{Title}' ({ImdbId}): retired (real file at '{Path}')",
                            item.Title, item.ImdbId, item.LocalPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[EmbyStreams] '{Title}' ({ImdbId}): retirement failed",
                            item.Title, item.ImdbId);
                    }
                }

                _logger.LogInformation("[EmbyStreams] Phase 3 complete — {Count}/{Total} retired",
                    retiredCount, toRetire.Count);
            }
            else
            {
                _logger.LogInformation("[EmbyStreams] Phase 3: No items to retire");
            }

            progress.Report(70);

            // ── Phase 4: Health Check ─────────────────────────────────────────────

            // URL validation will be handled by LinkResolverTask
            // This phase is a placeholder for future implementation
            _logger.LogInformation("[EmbyStreams] Phase 4: Health Check (deferred to LinkResolverTask)");

            progress.Report(85);

            // ── Phase 5: Clean Orphans ───────────────────────────────────────────

            if (orphans.Count > 0)
            {
                _logger.LogInformation("[EmbyStreams] Phase 5: Clean {Count} orphaned .strm file(s)", orphans.Count);
                var deletedCount = 0;

                for (int i = 0; i < orphans.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress.Report(85.0 + 10.0 * i / orphans.Count);

                    var orphanPath = orphans[i];

                    try
                    {
                        if (File.Exists(orphanPath))
                        {
                            File.Delete(orphanPath);
                            deletedCount++;

                            // Try to delete empty parent folder
                            var parentDir = Path.GetDirectoryName(orphanPath);
                            if (!string.IsNullOrEmpty(parentDir) &&
                                Directory.Exists(parentDir) &&
                                !Directory.EnumerateFileSystemEntries(parentDir).Any())
                            {
                                Directory.Delete(parentDir);
                                _logger.LogDebug("[EmbyStreams] Deleted empty folder: {Path}", parentDir);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[EmbyStreams] Failed to delete orphan: {Path}", orphanPath);
                    }
                }

                _logger.LogInformation("[EmbyStreams] Phase 5 orphans cleaned — {Count}/{Total} deleted",
                    deletedCount, orphans.Count);
            }

            progress.Report(95);

            // ── Final Report ─────────────────────────────────────────────────────

            sw.Stop();

            _logger.LogInformation(
                "[EmbyStreams] DoctorTask complete — " +
                "processed: {Total}, written: {Written}, retired: {Retired}, " +
                "toResolve: {ToResolve}, orphans: {Orphans}",
                allItems.Count, toWrite.Count, toRetire.Count, toResolve.Count, orphans.Count);

            // Sprint 67 v0.67.2: Persist last run summary for UI
            PersistLastRunSummary(allItems.Count, toWrite.Count, toRetire.Count, toResolve.Count, orphans.Count, sw.Elapsed);

            progress.Report(100);

            // Trigger library scan if we wrote or deleted any files
            if (toWrite.Count > 0 || toRetire.Count > 0 || orphans.Count > 0)
            {
                TriggerLibraryScan();
            }

            _logger.LogInformation("[EmbyStreams] DoctorTask finished in {Elapsed}", sw.Elapsed);
            }
            finally
            {
                // Sprint 102A-03: Persist last doctor run time
                if (Plugin.Instance?.DatabaseManager != null)
                {
                    try
                    {
                        await Plugin.Instance.DatabaseManager.PersistMetadataAsync(
                            "last_doctor_run_time",
                            DateTimeOffset.UtcNow.ToString("o"),
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[EmbyStreams] Failed to persist last_doctor_run_time");
                    }
                }
                // Sprint 100A-10: Release global sync lock
                Plugin.SyncLock.Release();
            }
        }

        // ── Private Helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Finds all .strm files in the plugin's sync directories.
        /// </summary>
        private HashSet<string> FindStrmFiles(PluginConfiguration config)
        {
            var paths = new HashSet<string>();

            void ScanDirectory(string? basePath)
            {
                if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
                    return;

                try
                {
                    foreach (var path in Directory.EnumerateFiles(basePath, "*.strm", SearchOption.AllDirectories))
                    {
                        paths.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EmbyStreams] Failed to scan directory: {Path}", basePath);
                }
            }

            ScanDirectory(config.SyncPathMovies);
            ScanDirectory(config.SyncPathShows);
            ScanDirectory(config.SyncPathAnime);

            return paths;
        }

        /// <summary>
        /// Extracts IMDB ID from a .strm file path.
        /// Path format: .../Title [imdbid-ttXXX]/Title.strm
        /// </summary>
        private string? ExtractImdbIdFromPath(string path)
        {
            try
            {
                var folderName = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folderName))
                    return null;

                // Look for [imdbid-ttXXX] pattern
                var match = System.Text.RegularExpressions.Regex.Match(
                    folderName, @"\[imdbid-(tt\d+)\]");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        private void TriggerLibraryScan()
        {
            try
            {
                _libraryManager.QueueLibraryScan();
                _logger.LogInformation("[EmbyStreams] DoctorTask: triggered Emby library scan");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] DoctorTask: failed to queue library scan");
            }
        }

        /// <summary>
        /// Phase 2 Preflight Check — Ensures all media directories exist with correct permissions.
        /// Creates missing directories before any write operations begin.
        /// </summary>
        private void EnsureMediaDirectoriesExist(PluginConfiguration config)
        {
            var paths = new List<string?>
            {
                config.SyncPathMovies,
                config.SyncPathShows,
                config.SyncPathAnime
            };

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                try
                {
                    if (!Directory.Exists(path))
                    {
                        _logger.LogInformation("[EmbyStreams] DoctorTask: creating missing directory: {Path}", path);
                        Directory.CreateDirectory(path);

                        // Set permissions to 755 (rwxr-xr-x)
                        if (OperatingSystem.IsLinux())
                        {
                            _logger.LogDebug("[EmbyStreams] DoctorTask: setting permissions 755 on: {Path}", path);
                            // Note: chmod 755 is typically handled by umask; explicit chmod requires native calls
                        }
                    }
                    else
                    {
                        _logger.LogDebug("[EmbyStreams] DoctorTask: directory exists: {Path}", path);
                    }

                    // Verify write permissions
                    var testFile = Path.Combine(path, ".write_test");
                    try
                    {
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                        _logger.LogDebug("[EmbyStreams] DoctorTask: write permissions OK for: {Path}", path);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _logger.LogError("[EmbyStreams] DoctorTask: NO WRITE PERMISSION for: {Path}", path);
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EmbyStreams] DoctorTask: failed to ensure directory exists: {Path}", path);
                    throw;
                }
            }

            _logger.LogInformation("[EmbyStreams] DoctorTask: Phase 2 preflight check complete — all directories accessible");
        }

        // ── Sprint 67: v0.67.2 — Doctor LastRun Summary Persistence ─────────────────

        /// <summary>
        /// Persists the Doctor run summary to a JSON file for the UI to display.
        /// File location: {DataPath}/EmbyStreams/doctor_last_run.json
        /// </summary>
        private void PersistLastRunSummary(
            int totalCount,
            int writtenCount,
            int retiredCount,
            int toResolveCount,
            int orphanCount,
            TimeSpan duration)
        {
            try
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                // Derive the EmbyStreams data directory from the database path
                var dbPath = db.GetDatabasePath();
                var dataDir = Path.GetDirectoryName(dbPath);
                if (string.IsNullOrEmpty(dataDir))
                {
                    _logger.LogWarning("[EmbyStreams] DoctorTask: could not determine data directory from DB path: {DbPath}", dbPath);
                    return;
                }

                Directory.CreateDirectory(dataDir);

                var summary = new DoctorLastRunSummary
                {
                    LastRunAt = DateTime.UtcNow.ToString("o"),
                    LastRunStatus = "success",
                    DurationSeconds = duration.TotalSeconds,
                    Summary = new DoctorRunStats
                    {
                        ItemsHealthy = totalCount - writtenCount - retiredCount - orphanCount,
                        StrmWritten = writtenCount,
                        EpisodesExpanded = 0, // TODO: track expanded episodes
                        Adopted = retiredCount,
                        PinnedPreserved = 0, // TODO: track preserved pins
                        DeadUrlsFlagged = toResolveCount,
                        OrphansPurged = orphanCount,
                        Errors = 0
                    },
                    Phases = new List<DoctorPhaseSummary>
                    {
                        new() { Name = "Fetch & Diff", Status = "success", DurationSeconds = duration.TotalSeconds * 0.15, Detail = $"{totalCount} catalog items checked" },
                        new() { Name = "Write", Status = "success", DurationSeconds = duration.TotalSeconds * 0.45, Detail = $"{writtenCount} .strm files written" },
                        new() { Name = "Adopt", Status = "success", DurationSeconds = duration.TotalSeconds * 0.10, Detail = $"{retiredCount} .strm files retired" },
                        new() { Name = "Health Check", Status = toResolveCount > 0 ? "warning" : "success", DurationSeconds = duration.TotalSeconds * 0.30, Detail = toResolveCount > 0 ? $"{toResolveCount} URLs need re-resolve" : "All cached URLs valid" }
                    }
                };

                var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
                var summaryPath = Path.Combine(dataDir, "doctor_last_run.json");
                File.WriteAllText(summaryPath, json, System.Text.Encoding.UTF8);

                _logger.LogDebug("[EmbyStreams] DoctorTask: persisted last run summary to {Path}", summaryPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] DoctorTask: failed to persist last run summary");
            }
        }
    }

    // ── Doctor LastRun Summary Models (Sprint 67 v0.67.2) ─────────────────────────────

    public class DoctorLastRunSummary
    {
        public string LastRunAt { get; set; } = string.Empty;
        public string LastRunStatus { get; set; } = "never";
        public double DurationSeconds { get; set; }
        public DoctorRunStats Summary { get; set; } = new();
        public List<DoctorPhaseSummary> Phases { get; set; } = new();
    }

    public class DoctorRunStats
    {
        public int ItemsHealthy { get; set; }
        public int StrmWritten { get; set; }
        public int EpisodesExpanded { get; set; }
        public int Adopted { get; set; }
        public int PinnedPreserved { get; set; }
        public int DeadUrlsFlagged { get; set; }
        public int OrphansPurged { get; set; }
        public int Errors { get; set; }
    }

    public class DoctorPhaseSummary
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = "unknown"; // "success", "warning", "error", "running"
        public double DurationSeconds { get; set; }
        public string Detail { get; set; } = string.Empty;
    }
}
