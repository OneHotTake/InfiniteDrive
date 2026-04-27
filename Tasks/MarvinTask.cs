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
using MediaBrowser.Model.Logging;
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
        private static bool _isColdStart = true;

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
                // Cold-start jitter: only the first invocation (Emby startup trigger) waits
                if (_isColdStart)
                {
                    _isColdStart = false;
                    await Task.Delay(Random.Shared.Next(0, 120_000), cancellationToken);
                }

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

            // Sprint 311: Restore primary provider if it's back up
            _ = TryRestorePrimaryAsync();

            try
            {
                // ── Phase 1: Sync (0-25%) ─────────────────────────────────────
                Plugin.Pipeline.SetPhase("Marvin", "Sync");
                progress?.Report(0.0);

                _logger.LogInformation("[InfiniteDrive] Marvin Phase 1: Sync");
#pragma warning disable CS0618 // CatalogSyncTask is obsolete but still functional
                var syncProgress = new Progress<double>(p => progress?.Report(p * 0.25));
                await new CatalogSyncTask(_libraryManager, _logManager)
                    .RunSyncAsync(cancellationToken, syncProgress);
#pragma warning restore CS0618

                progress?.Report(0.25);

                // ── Phase 2: Populate (25-55%) ───────────────────────────────
                Plugin.Pipeline.SetPhase("Marvin", "Populate");
                _logger.LogInformation("[InfiniteDrive] Marvin Phase 2: Populate");

                var populateProgress = new Progress<double>(p => progress?.Report(0.25 + p * 0.30));
#pragma warning disable CS0618 // RefreshTask is obsolete but still functional
                var refreshWorker = new RefreshTask(_logManager, _libraryManager);
                var writtenItems = await refreshWorker.RunPopulateAsync(cancellationToken, populateProgress);
#pragma warning restore CS0618

                progress?.Report(0.55);

                // ── Phase 3: Resolve (55-80%) ────────────────────────────────
                Plugin.Pipeline.SetPhase("Marvin", "Resolve");
                _logger.LogInformation("[InfiniteDrive] Marvin Phase 3: Resolve");

                var resolveProgress = new Progress<double>(p => progress?.Report(0.55 + p * 0.25));
#pragma warning disable CS0618
                await refreshWorker.RunResolveAsync(cancellationToken, resolveProgress, writtenItems);
#pragma warning restore CS0618

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

                await ValidationPassAsync(cancellationToken);
                progress?.Report(0.85);

                await EnrichmentTrickleAsync(cancellationToken);
                progress?.Report(0.90);

                await TokenRenewalAsync(cancellationToken);
                progress?.Report(0.95);

                await SaveMaintenancePassAsync(cancellationToken);

                // Persist last run time
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync(
                    "last_marvin_run_time",
                    DateTime.UtcNow.ToString("o"),
                    cancellationToken);

                // Persist enrichment counts
                await PersistEnrichmentCountsAsync(cancellationToken);

                progress?.Report(1.0);
                _logger.LogInformation("[InfiniteDrive] MarvinTask completed successfully (4-phase pipeline)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] MarvinTask failed");
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
                                "[InfiniteDrive] Integrity fail: {Imdb} .strm folder missing, reset to Queued",
                                item.ImdbId);
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
                            "[InfiniteDrive] Resurrection: {Imdb} real file missing, reset to Queued",
                            item.ImdbId);
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
                var orphanedFiles = strmFiles.Where(f => !activeStrmPaths.Contains(Path.GetDirectoryName(f) ?? ""));

                foreach (var orphanFile in orphanedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        File.Delete(orphanFile);
                        _logger.LogDebug("[InfiniteDrive] Deleted orphan .strm: {Path}", orphanFile);
                        orphanedCount++;

                        var nfoPath = Path.ChangeExtension(orphanFile, ".nfo");
                        if (File.Exists(nfoPath))
                            File.Delete(nfoPath);

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
                SELECT * FROM catalog_items
                WHERE nfo_status = 'NeedsEnrich'
                AND (next_retry_at IS NULL OR next_retry_at <= unixepoch('now'))
                AND removed_at IS NULL
                ORDER BY
                    CASE
                        WHEN (imdb_id IS NULL OR imdb_id = '')
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
                    ImdbId = row.IsDBNull(1) ? null : row.GetString(1),
                    Title = row.GetString(4),
                    Year = row.IsDBNull(5) ? (int?)null : row.GetInt(5),
                    RetryCount = row.GetInt(20),
                    NextRetryAt = row.IsDBNull(22) ? (long?)null : row.GetInt64(22)
                });

            if (!needsEnrichItems.Any())
                return;

            var aioClient = new AioMetadataClient(Plugin.Instance!.Configuration, _logger);
            aioClient.Cooldown = Plugin.Instance?.CooldownGate;

            var result = await MetadataEnrichmentService.EnrichBatchAsync(
                needsEnrichItems,
                (req, ct) => aioClient.FetchAsync(req.ImdbId, req.Year, ct),
                db, _logger, cancellationToken);

            _logger.LogInformation(
                "[InfiniteDrive] Enrichment: {Total} items, {Enriched} enriched, {Blocked} blocked, {Skipped} skipped",
                needsEnrichItems.Count, result.EnrichedCount, result.BlockedCount, result.SkippedCount);

            if (result.EnrichedCount > 0)
            {
                try
                {
                    _libraryManager.QueueLibraryScan();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Enrichment: Failed to trigger library scan");
                }
            }
        }

        private async Task TokenRenewalAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            var expiringItems = await db.GetCatalogItemsWithExpiringTokensAsync(
                int.MaxValue, cancellationToken);

            if (!expiringItems.Any())
                return;

            var renewedCount = 0;
            var slots = await Plugin.Instance!.VersionSlotRepository.GetEnabledSlotsAsync(cancellationToken);
            if (!slots.Any())
                return;

            var defaultSlot = slots.FirstOrDefault(s => s.IsDefault) ?? slots.First();
            var config = Plugin.Instance!.Configuration;
            var embyBaseUrl = GetEmbyBaseUrl(config);
            var materializer = new VersionMaterializer(_logger);

            foreach (var item in expiringItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(item.StrmPath) || string.IsNullOrEmpty(item.LocalPath))
                    continue;

                var folderPath = item.LocalPath!;
                var baseName = Path.GetFileNameWithoutExtension(folderPath);

                try
                {
                    foreach (var slot in slots)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var (strmUrl, expiresAtUnix) = materializer.BuildStrmUrlWithExpiry(
                            embyBaseUrl,
                            item.ImdbId,
                            slot.SlotKey,
                            "imdb",
                            null,
                            null);

                        var fileName = $"{baseName}{(slot.IsDefault ? "" : $"_{slot.SlotKey}")}.strm";
                        var fullPath = Path.Combine(folderPath, fileName);
                        var tmpPath = fullPath + ".tmp";

                        await File.WriteAllTextAsync(tmpPath, strmUrl, new UTF8Encoding(false));
                        File.Move(tmpPath, fullPath, overwrite: true);
                    }

                    item.StrmTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(config.SignatureValidityDays).ToUnixTimeSeconds();
                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await db.UpsertCatalogItemAsync(item, cancellationToken);

                    renewedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Token renewal failed for {Imdb}", item.ImdbId);
                }
            }

            if (renewedCount > 0)
                _logger.LogInformation("[InfiniteDrive] Token renewal: Renewed {Count} items", renewedCount);
        }

        private async Task SaveMaintenancePassAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            var orphans = await db.GetOrphanedUserSavesAsync(cancellationToken);
            var orphanCount = 0;
            foreach (var (saveId, userId, mediaItemId) in orphans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await db.DeleteUserSaveByIdAsync(saveId, cancellationToken);
                orphanCount++;
            }

            if (orphanCount > 0)
                _logger.LogInformation("[InfiniteDrive] Save maintenance: Removed {Count} orphaned saves", orphanCount);

            var savedItems = await db.GetItemsBySavedAsync(true, cancellationToken);
            var reSyncedCount = 0;
            foreach (var item in savedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await db.SyncGlobalSavedFlagAsync(item.Id, cancellationToken);
                reSyncedCount++;
            }

            if (reSyncedCount > 0)
                _logger.LogDebug("[InfiniteDrive] Save maintenance: Re-synced {Count} global saved flags", reSyncedCount);
        }

        private async Task PersistEnrichmentCountsAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            var blockedQuery = "SELECT COUNT(*) FROM catalog_items WHERE nfo_status = 'Blocked' AND removed_at IS NULL;";
            var blockedCount = await db.QueryScalarIntAsync(blockedQuery, cancellationToken);

            var needsEnrichQuery = "SELECT COUNT(*) FROM catalog_items WHERE nfo_status = 'NeedsEnrich' AND removed_at IS NULL;";
            var needsEnrichCount = await db.QueryScalarIntAsync(needsEnrichQuery, cancellationToken);

            await db.PersistMetadataAsync("blocked_enrichment_count", blockedCount.ToString(), cancellationToken);
            await db.PersistMetadataAsync("needs_enrich_count", needsEnrichCount.ToString(), cancellationToken);
        }

        private static string GetEmbyBaseUrl(PluginConfiguration config)
        {
            if (!string.IsNullOrEmpty(config.EmbyBaseUrl))
                return config.EmbyBaseUrl.TrimEnd('/');
            return "http://localhost:8096";
        }

    }
}
