using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Notifications;
using MediaBrowser.Model.Tasks;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Marvin: unified 4-phase pipeline orchestrator.
    /// Phase 1 — Sync (CatalogSyncTask): fetch manifests → deduplicate → upsert to DB.
    /// Phase 2 — Populate (RefreshTask): collect queued items → write .strm → write NFO hints.
    /// Phase 3 — Resolve (RefreshTask): enrich metadata → notify Emby → verify items.
    /// Phase 4 — Repair: validate system state, orphan cleanup, token renewal, enrichment trickle.
    /// </summary>
    public class MarvinTask : IScheduledTask
    {
        // ── Constants ────────────────────────────────────────────────────────────

        private const string TaskName     = "InfiniteDrive Marvin";
        private const string TaskKey      = "InfiniteDriveMarvin";
        private const string TaskCategory = "InfiniteDrive";

        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ILogger<MarvinTask> _logger;
        private readonly ILibraryManager       _libraryManager;
        private readonly ILogManager           _logManager;

        private static readonly SemaphoreSlim _runningGate = new(1, 1);

        // ── Constructor ──────────────────────────────────────────────────────────

        public MarvinTask(
            ILogManager logManager,
            ILibraryManager libraryManager)
        {
            _logger         = new EmbyLoggerAdapter<MarvinTask>(logManager.GetLogger("InfiniteDrive"));
            _libraryManager = libraryManager;
            _logManager     = logManager;
        }

        // ── IScheduledTask ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Unified pipeline: Sync catalogs → Populate .strm files → Resolve metadata → Repair library.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromMinutes(10).Ticks,
                }
            };

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Concurrency guard — skip if another instance is already running
            if (!_runningGate.Wait(0))
            {
                _logger.LogInformation("[InfiniteDrive] MarvinTask already running, skipping");
                return;
            }

            try
            {
                // Acquire global sync lock to prevent conflicts with other sync operations
                await Plugin.SyncLock.WaitAsync(cancellationToken);
                try
                {
                    await ExecuteInternalAsync(cancellationToken, progress);
                }
                finally
                {
                    Plugin.SyncLock.Release();
                }
            }
            finally
            {
                _runningGate.Release();
            }
        }

        // ── Internal execution: 4-phase pipeline ──────────────────────────────

        private async Task ExecuteInternalAsync(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("[InfiniteDrive] MarvinTask started (4-phase pipeline)");
            var pipelineSw = System.Diagnostics.Stopwatch.StartNew();

            // Sprint 311: Restore primary provider if it's back up
            _ = TryRestorePrimaryAsync();

            try
            {
                // ── Phase 1+2: Sync + Populate concurrently (0-55%) ──────────
                Plugin.Pipeline.SetPhase("Marvin", "Sync+Populate");
                progress?.Report(0.0);
                _logger.LogInformation("[InfiniteDrive] Marvin Phase 1+2: Concurrent Sync & Populate");
#pragma warning disable CS0618 // RefreshTask is obsolete but still functional
                var refreshWorker = new RefreshTask(_logManager, _libraryManager);
#pragma warning restore CS0618

                var allWrittenItems = new List<CatalogItem>();
                var syncSw = System.Diagnostics.Stopwatch.StartNew();

#pragma warning disable CS0618 // CatalogSyncTask is obsolete but still functional
                var syncProgress = new Progress<double>(p => progress?.Report(p * 0.35));
                var syncTask = new CatalogSyncTask(_libraryManager, _logManager)
                    .RunSyncAsync(cancellationToken, syncProgress);
#pragma warning restore CS0618

                // While sync runs, poll DB every 5s for newly Queued items
                while (!syncTask.IsCompleted)
                {
                    await Task.WhenAny(syncTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));

                    var batch = await refreshWorker.RunPopulateAsync(cancellationToken, new Progress<double>());
                    if (batch.Count > 0)
                    {
                        allWrittenItems.AddRange(batch);
                        _logger.LogInformation("[Marvin] Inline populate wrote {Count} items while syncing", batch.Count);
                    }
                }

                // Re-await to propagate any sync exceptions
                await syncTask;
                _logger.LogDebug("[Marvin] Phase 1 (Sync) completed in {Ms}ms", syncSw.ElapsedMilliseconds);

                progress?.Report(0.35);

                // Final populate pass after sync completes
                var finalBatch = await refreshWorker.RunPopulateAsync(
                    cancellationToken, new Progress<double>(p => progress?.Report(0.35 + p * 0.20)));
                if (finalBatch.Count > 0)
                    allWrittenItems.AddRange(finalBatch);

                _logger.LogInformation(
                    "[Marvin] Phase 1+2 completed in {Ms}ms — {Count} total items populated",
                    syncSw.ElapsedMilliseconds, allWrittenItems.Count);

                progress?.Report(0.55);

                // ── Phase 3: Resolve (55-80%) ────────────────────────────────
                Plugin.Pipeline.SetPhase("Marvin", "Resolve");
                _logger.LogInformation("[InfiniteDrive] Marvin Phase 3: Resolve");

                var phaseSw = System.Diagnostics.Stopwatch.StartNew();
                var resolveProgress = new Progress<double>(p => progress?.Report(0.55 + p * 0.25));
#pragma warning disable CS0618
                await refreshWorker.RunResolveAsync(cancellationToken, resolveProgress, allWrittenItems);
#pragma warning restore CS0618
                _logger.LogDebug("[Marvin] Phase 3 (Resolve) completed in {Ms}ms", phaseSw.ElapsedMilliseconds);

                progress?.Report(0.80);

                // ── Phase 4: Repair (80-100%) ────────────────────────────────
                Plugin.Pipeline.SetPhase("Marvin", "Repair");
                _logger.LogInformation("[InfiniteDrive] Marvin Phase 4: Repair");

                // Sprint 401/403: Log system state but NEVER skip.
                var stateService = Plugin.Instance?.SystemStateService;
                if (stateService != null)
                {
                    var state = await stateService.GetStateAsync(cancellationToken);
                    _logger.LogInformation("[State] Marvin proceeding — state={State}, desc={Desc}", state.State, state.Description);
                }

                phaseSw.Restart();
                await ValidationPassAsync(cancellationToken);
                _logger.LogDebug("[Marvin] Phase 4a (Validation) completed in {Ms}ms", phaseSw.ElapsedMilliseconds);

                phaseSw.Restart();
                await EnrichmentTrickleAsync(cancellationToken);
                _logger.LogDebug("[Marvin] Phase 4b (Enrichment) completed in {Ms}ms", phaseSw.ElapsedMilliseconds);

                phaseSw.Restart();
                await TokenRenewalAsync(cancellationToken);
                _logger.LogDebug("[Marvin] Phase 4c (TokenRenewal) completed in {Ms}ms", phaseSw.ElapsedMilliseconds);
                progress?.Report(0.90);

                // Sprint 530: removed SaveMaintenancePassAsync (user_item_saves deprecated)

                // Persist last run time
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync(
                    "last_marvin_run_time",
                    DateTime.UtcNow.ToString("o"),
                    cancellationToken);

                // Persist enrichment counts
                await PersistEnrichmentCountsAsync(cancellationToken);

                progress?.Report(1.0);
                pipelineSw.Stop();
                _logger.LogInformation("[InfiniteDrive] MarvinTask completed successfully in {Ms}ms (4-phase pipeline)", pipelineSw.ElapsedMilliseconds);

                _ = NotificationService.NotifyAsync(
                    "MarvinComplete",
                    "Marvin pipeline complete",
                    $"4-phase pipeline finished in {pipelineSw.ElapsedMilliseconds}ms — {allWrittenItems.Count} items populated",
                    cancellationToken: cancellationToken);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] MarvinTask failed");
                _ = NotificationService.NotifyAsync(
                    "MarvinFailed",
                    "Marvin pipeline error",
                    $"Pipeline failed: {ex.Message}",
                    NotificationLevel.Error,
                    cancellationToken: CancellationToken.None);
                throw;
            }
            finally
            {
                Plugin.Pipeline.Clear();
            }
        }

        // ── Phase 4 sub-operations ────────────────────────────────────────────

        // ── Sprint 311: Primary provider health restore ──────────────────────────

        private async Task TryRestorePrimaryAsync()
        {
            var state = Plugin.Instance?.ActiveProviderState;
            if (state == null || state.Current != Models.ActiveProvider.Secondary)
                return;

            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
                return;

            try
            {
                var (baseUrl, _, _) = Services.AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                if (string.IsNullOrWhiteSpace(baseUrl)) return;

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await http.GetAsync(baseUrl);

                if (response.IsSuccessStatusCode)
                {
                    state.Current = Models.ActiveProvider.Primary;
                    _logger.LogInformation("[Failover] Primary restored");

                    try
                    {
                        await Plugin.Instance!.DatabaseManager.SetActiveProviderAsync("Primary");
                    }
                    catch { /* best effort */ }
                }
            }
            catch
            {
                // Primary still down — no action needed
            }
        }

        private async Task ValidationPassAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            // Load all active catalog items
            var activeItems = await db.GetActiveCatalogItemsAsync();

            // Check .strm file integrity for Ready/Written/Notified items
            foreach (var item in activeItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item.ItemState == ItemState.Ready ||
                    item.ItemState == ItemState.Written ||
                    item.ItemState == ItemState.Notified)
                {
                    if (!string.IsNullOrEmpty(item.StrmPath))
                    {
                        if (!Directory.Exists(item.StrmPath))
                        {
                            item.ItemState = ItemState.Queued;
                            item.UpdatedAt = DateTime.UtcNow.ToString("o");
                            await db.UpsertCatalogItemAsync(item, cancellationToken);
                            _logger.LogWarning(
                                "[InfiniteDrive] Integrity fail: {AioId} .strm folder missing, reset to Queued",
                                item.AioId);
                        }
                    }
                }
                else if (item.ItemState == ItemState.Retired)
                {
                    if (!string.IsNullOrEmpty(item.LocalPath) && !File.Exists(item.LocalPath))
                    {
                        item.ItemState = ItemState.Queued;
                        item.UpdatedAt = DateTime.UtcNow.ToString("o");
                        await db.UpsertCatalogItemAsync(item, cancellationToken);
                        _logger.LogInformation(
                            "[InfiniteDrive] Resurrection: {AioId} real file missing, reset to Queued",
                            item.AioId);
                    }
                }
            }

            await CleanupOrphanFilesAsync(cancellationToken);
        }

        private async Task CleanupOrphanFilesAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;
            var config = Plugin.Instance!.Configuration;

            var activeStrmPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeItems = await db.GetActiveCatalogItemsAsync();
            foreach (var item in activeItems)
            {
                if (!string.IsNullOrEmpty(item.StrmPath))
                    activeStrmPaths.Add(item.StrmPath!);
            }

            var orphanedCount = 0;
            var libraryPaths = new[] { config.SyncPathMovies, config.SyncPathShows, config.SyncPathAnime };

            foreach (var libPath in libraryPaths)
            {
                if (string.IsNullOrEmpty(libPath) || !Directory.Exists(libPath))
                    continue;

                var strmFiles = Directory.GetFiles(libPath, "*.strm", SearchOption.AllDirectories);

                bool IsOrphan(string filePath)
                {
                    // Walk up from the .strm file's directory to the library root
                    // checking if any ancestor matches an active StrmPath (series root or movie folder)
                    var dir = Path.GetDirectoryName(filePath);
                    while (!string.IsNullOrEmpty(dir) && dir.Length >= libPath.Length)
                    {
                        if (activeStrmPaths.Contains(dir))
                            return false;
                        dir = Path.GetDirectoryName(dir);
                    }
                    return true;
                }

                var orphanedFiles = strmFiles.Where(IsOrphan);

                foreach (var orphanFile in orphanedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        File.Delete(orphanFile);
                        _logger.LogDebug("[InfiniteDrive] Deleted orphan .strm: {Path}", orphanFile);
                        orphanedCount++;

                        var parentDir = Path.GetDirectoryName(orphanFile);
                        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        {
                            try { Directory.Delete(parentDir); }
                            catch { /* Folder may not be empty */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[InfiniteDrive] Failed to delete orphan file: {Path}", orphanFile);
                    }
                }
            }

            if (orphanedCount > 0)
                _logger.LogInformation("[InfiniteDrive] Cleanup: Deleted {Count} orphan files", orphanedCount);
        }

        private async Task EnrichmentTrickleAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            var needsEnrichQuery = @"
                SELECT id, aio_id, title, year, retry_count, next_retry_at FROM catalog_items
                WHERE enrichment_status = 'NeedsEnrich'
                AND (next_retry_at IS NULL OR next_retry_at <= unixepoch('now'))
                AND removed_at IS NULL
                ORDER BY
                    CASE
                        WHEN (aio_id IS NULL OR aio_id = '')
                         AND (tmdb_id IS NULL OR tmdb_id = '') THEN 0
                        ELSE 1
                    END ASC,
                    added_at ASC
                LIMIT 42;";

            var needsEnrichItems = await db.QueryListAsync<EnrichmentRequest>(
                needsEnrichQuery,
                cmd => { },
                row => new EnrichmentRequest
                {
                    Id = row.GetString(0),
                    AioId = row.IsDBNull(1) ? null : row.GetString(1),
                    Title = row.GetString(2),
                    Year = row.IsDBNull(3) ? (int?)null : row.GetInt(3),
                    RetryCount = row.GetInt(4),
                    NextRetryAt = row.IsDBNull(5) ? (long?)null : row.GetInt64(5)
                });

            if (!needsEnrichItems.Any())
                return;

            var aioClient = new AioMetadataClient(Plugin.Instance!.Configuration, _logger);
            aioClient.Cooldown = Plugin.Instance?.CooldownGate;

            var result = await MetadataEnrichmentService.EnrichBatchAsync(
                needsEnrichItems,
                (req, ct) => aioClient.FetchAsync(req.AioId, req.Year, ct),
                db, _logger, cancellationToken);

            _logger.LogInformation(
                "[InfiniteDrive] Enrichment: {Total} items, {Enriched} enriched, {Blocked} blocked, {Skipped} skipped",
                needsEnrichItems.Count, result.EnrichedCount, result.BlockedCount, result.SkippedCount);

            if (result.EnrichedCount > 0)
            {
                await RefreshEnrichedItemsAsync(
                    needsEnrichItems.Take(result.EnrichedCount).Select(r => r.AioId).ToList(),
                    cancellationToken);
            }
        }

        private async Task TokenRenewalAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            var expiringItems = await db.GetCatalogItemsWithExpiringTokensAsync(
                int.MaxValue, cancellationToken);

            if (!expiringItems.Any())
                return;

            // Version-slot token renewal disabled — slot infrastructure removed.
            return;
        }

        private async Task PersistEnrichmentCountsAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            var blockedCount = await db.GetBlockedCountAsync(cancellationToken);
            var needsEnrichCount = await db.GetNeedsEnrichCountAsync(cancellationToken);

            await db.PersistMetadataAsync("blocked_enrichment_count", blockedCount.ToString(), cancellationToken);
            await db.PersistMetadataAsync("needs_enrich_count", needsEnrichCount.ToString(), cancellationToken);
        }

        private async Task RefreshEnrichedItemsAsync(List<string?> enrichedAioIds, CancellationToken cancellationToken)
        {
            var providerManager = Plugin.Instance?.ProviderManager;
            var fileSystem      = Plugin.Instance?.FileSystem;

            if (providerManager == null || fileSystem == null)
            {
                _logger.LogDebug("[InfiniteDrive] IProviderManager/IFileSystem not available, falling back to library scan");
                try { _libraryManager.QueueLibraryScan(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[InfiniteDrive] Enrichment: Failed to trigger library scan fallback"); }
                return;
            }

            var db = Plugin.Instance!.DatabaseManager;
            var refreshed = 0;

            foreach (var aioId in enrichedAioIds)
            {
                if (string.IsNullOrEmpty(aioId)) continue;
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var catalogItem = await db.GetCatalogItemByAioIdAsync(aioId);
                    if (catalogItem?.StrmPath == null) continue;

                    // Find the Emby item by its .strm folder path
                    var embyItem = _libraryManager.FindByPath(catalogItem.StrmPath, false);
                    if (embyItem == null)
                    {
                        // Also try as directory (for series)
                        embyItem = _libraryManager.FindByPath(catalogItem.StrmPath, true);
                    }

                    if (embyItem != null)
                    {
                        var options = new MetadataRefreshOptions(fileSystem);
                        providerManager.QueueRefresh(embyItem.InternalId, options, RefreshPriority.Low);
                        refreshed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[InfiniteDrive] Enrichment: Failed to refresh {AioId}", aioId);
                }
            }

            if (refreshed > 0)
            {
                _logger.LogInformation("[InfiniteDrive] Enrichment: Targeted refresh queued for {Count} items", refreshed);
            }
            else
            {
                // Fallback: no items found in Emby yet, do a full scan
                _logger.LogDebug("[InfiniteDrive] Enrichment: No Emby items found for targeted refresh, falling back to library scan");
                try { _libraryManager.QueueLibraryScan(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[InfiniteDrive] Enrichment: Failed to trigger library scan fallback"); }
            }
        }

        private static string GetEmbyBaseUrl(PluginConfiguration config)
        {
            if (!string.IsNullOrEmpty(config.EmbyBaseUrl))
                return config.EmbyBaseUrl.TrimEnd('/');
            return "http://localhost:8096";
        }

    }
}
