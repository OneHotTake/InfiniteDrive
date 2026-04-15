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
    /// Marvin scheduled task for 18-24 hour validation cycles.
    /// Handles full library validation, orphan cleanup, enriched metadata trickle,
    /// token renewal, and integrity checks.
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

        private static readonly SemaphoreSlim _runningGate = new(1, 1);

        // ── Constructor ──────────────────────────────────────────────────────────

        public MarvinTask(
            ILogManager logManager,
            ILibraryManager libraryManager)
        {
            _logger         = new EmbyLoggerAdapter<MarvinTask>(logManager.GetLogger("InfiniteDrive"));
            _libraryManager = libraryManager;
        }

        // ── IScheduledTask ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Full library validation: orphan cleanup, token renewal, metadata enrichment with retry backoff.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(18).Ticks,
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

        // ── Internal execution ─────────────────────────────────────────────────

        private async Task ExecuteInternalAsync(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("[InfiniteDrive] MarvinTask started");

            // Sprint 311: Restore primary provider if it's back up
            _ = TryRestorePrimaryAsync();

            try
            {
                // Phase 1: Validation pass
                progress?.Report(0.2);
                await ValidationPassAsync(cancellationToken);

                // Phase 2: Enrichment trickle
                progress?.Report(0.5);
                await EnrichmentTrickleAsync(cancellationToken);

                // Phase 3: Token renewal
                progress?.Report(0.8);
                await TokenRenewalAsync(cancellationToken);

                // Phase 4: Save maintenance
                progress?.Report(0.85);
                await SaveMaintenancePassAsync(cancellationToken);

                // Persist last run time
                progress?.Report(0.95);
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync(
                    "last_marvin_run_time",
                    DateTime.UtcNow.ToString("o"),
                    cancellationToken);

                // Persist enrichment counts
                await PersistEnrichmentCountsAsync(cancellationToken);

                progress?.Report(1.0);
                _logger.LogInformation("[InfiniteDrive] MarvinTask completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] MarvinTask failed");
                throw;
            }
        }

        // ── Phase 1: Validation Pass ───────────────────────────────────────

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

                    // Persist restored state so it survives restart
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
                        // Check .strm file exists on disk
                        if (!Directory.Exists(item.StrmPath))
                        {
                            // Transition to Queued (will be re-written by RefreshTask)
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
                    // Verify real file still exists at local_path
                    if (!string.IsNullOrEmpty(item.LocalPath) && !File.Exists(item.LocalPath))
                    {
                        // Transition back to Queued (resurrection)
                        item.ItemState = ItemState.Queued;
                        item.UpdatedAt = DateTime.UtcNow.ToString("o");
                        await db.UpsertCatalogItemAsync(item, cancellationToken);
                        _logger.LogInformation(
                            "[InfiniteDrive] Resurrection: {Imdb} real file missing, reset to Queued",
                            item.ImdbId);
                    }
                }
            }

            // Scan filesystem for orphan .strm files
            await CleanupOrphanFilesAsync(cancellationToken);
        }

        private async Task CleanupOrphanFilesAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;
            var config = Plugin.Instance!.Configuration;

            // Collect all strm_paths from active items
            var activeStrmPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeItems = await db.GetActiveCatalogItemsAsync();
            foreach (var item in activeItems)
            {
                if (!string.IsNullOrEmpty(item.StrmPath))
                    activeStrmPaths.Add(item.StrmPath!);
            }

            // Scan library directories for orphan .strm files
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

                        // Delete orphan .nfo alongside .strm
                        var nfoPath = Path.ChangeExtension(orphanFile, ".nfo");
                        if (File.Exists(nfoPath))
                        {
                            File.Delete(nfoPath);
                        }

                        // Delete empty parent folder
                        var parentDir = Path.GetDirectoryName(orphanFile);
                        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        {
                            try
                            {
                                Directory.Delete(parentDir);
                                _logger.LogDebug("[InfiniteDrive] Deleted empty folder: {Path}", parentDir);
                            }
                            catch
                            {
                                // Folder may not be empty, skip
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[InfiniteDrive] Failed to delete orphan file: {Path}", orphanFile);
                    }
                }
            }

            if (orphanedCount > 0)
            {
                _logger.LogInformation("[InfiniteDrive] Cleanup: Deleted {Count} orphan files", orphanedCount);
            }
        }

        // ── Phase 2: Enrichment Trickle ─────────────────────────────────────

        private async Task EnrichmentTrickleAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            // Query items with nfo_status = 'NeedsEnrich', prioritizing no-ID items first
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
                LIMIT 42;"; // 42: max enrichment items per Marvin cycle — balanced to avoid long I/O stalls

            var needsEnrichItems = await db.QueryListAsync<EnrichedItem>(
                needsEnrichQuery,
                cmd => { },
                row => new EnrichedItem
                {
                    Id = row.GetString(0),
                    ImdbId = row.GetString(1),
                    Title = row.GetString(4),
                    Year = row.IsDBNull(5) ? (int?)null : row.GetInt(5),
                    RetryCount = row.GetInt(20),
                    NextRetryAt = row.IsDBNull(22) ? (long?)null : row.GetInt64(22)
                });

            if (!needsEnrichItems.Any())
                return;

            var aioClient = new AioMetadataClient(Plugin.Instance!.Configuration, _logger);
            aioClient.Cooldown = Plugin.Instance?.CooldownGate;
            var enrichedCount = 0;
            var blockedCount = 0;

            // Rate-limit: 1 call per 2 seconds
            foreach (var item in needsEnrichItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Check retry_count and next_retry_at
                if (item.RetryCount == 1 && item.NextRetryAt.HasValue)
                {
                    var retryTime = DateTimeOffset.FromUnixTimeSeconds(item.NextRetryAt.Value).DateTime;
                    if (retryTime > DateTime.UtcNow)
                    {
                        // Waiting for +4h backoff
                        continue;
                    }
                }
                else if (item.RetryCount == 2 && item.NextRetryAt.HasValue)
                {
                    var retryTime = DateTimeOffset.FromUnixTimeSeconds(item.NextRetryAt.Value).DateTime;
                    if (retryTime > DateTime.UtcNow)
                    {
                        // Waiting for +24h backoff
                        continue;
                    }
                }
                else if (item.RetryCount >= 3)
                {
                    // Max retries reached, already blocked
                    blockedCount++;
                    continue;
                }

                // Fetch metadata from AIOMetadata
                var enriched = await aioClient.FetchAsync(item.ImdbId, item.Year);
                if (enriched == null)
                {
                    // Failure: increment retry_count, set next_retry_at
                    item.RetryCount++;
                    var nextRetry = item.RetryCount switch
                    {
                        0 => DateTime.UtcNow.AddHours(4),
                        1 => DateTime.UtcNow.AddHours(24),
                        _ => DateTime.UtcNow // Will be set to Blocked below
                    };

                    item.NextRetryAt = new DateTimeOffset(nextRetry).ToUnixTimeSeconds();

                    if (item.RetryCount >= 3)
                    {
                        // Transition to Blocked
                        await db.SetNfoStatusAsync(item.Id, "Blocked", cancellationToken);
                        blockedCount++;
                        _logger.LogWarning(
                            "[InfiniteDrive] Enrichment blocked for {Imdb} after 3 retries",
                            item.ImdbId);
                    }
                    else
                    {
                        if (item.NextRetryAt.HasValue)
                        {
                            await db.UpdateItemRetryInfoAsync(item.Id, item.RetryCount, item.NextRetryAt.Value, cancellationToken);
                        }
                    }

                    _logger.LogDebug(
                        "[InfiniteDrive] Enrichment failed for {Imdb}, retry {Count}",
                        item.ImdbId, item.RetryCount);
                }
                else
                {
                    // Success: enriched is non-null — write enriched .nfo
                    await WriteEnrichedNfoAsync(item, enriched, cancellationToken);

                    // Set nfo_status = 'Enriched', retry_count = 0
                    await db.SetNfoStatusAsync(item.Id, "Enriched", cancellationToken);
                    await db.UpdateItemRetryInfoAsync(item.Id, 0, null, cancellationToken);

                    enrichedCount++;
                    _logger.LogDebug("[InfiniteDrive] Enriched metadata for {Imdb}", item.ImdbId);
                }

                // Rate-limit delay: 2 seconds between calls
                await Task.Delay(2000, cancellationToken);
            }

            _logger.LogInformation($"[InfiniteDrive] Enrichment: Processed {needsEnrichItems.Count} items, {enrichedCount} enriched, {blockedCount} blocked");

            // Notify Emby of updated NFO files
            if (enrichedCount > 0)
            {
                try
                {
                    _libraryManager.QueueLibraryScan();
                    _logger.LogInformation("[InfiniteDrive] Enrichment: Triggered library scan for {Count} enriched items", enrichedCount);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Enrichment: Failed to trigger library scan");
                }
            }
        }

        private async Task WriteEnrichedNfoAsync(EnrichedItem item, AioMetadataClient.EnrichedMetadata meta, CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;
            var catalogItem = await db.GetCatalogItemByImdbIdAsync(item.ImdbId);
            if (catalogItem == null || string.IsNullOrEmpty(catalogItem.StrmPath))
                return;

            var folderPath = catalogItem.StrmPath!;
            var isSeries = catalogItem.MediaType == "series" || catalogItem.MediaType == "anime";
            var rootElement = isSeries ? "tvshow" : "movie";

            var nfoSb = new StringBuilder();
            nfoSb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            nfoSb.AppendLine($"<{rootElement}>");
            nfoSb.AppendLine($"  <title>{SanitizeXml(meta.Name)}</title>");

            if (meta.Year.HasValue)
            {
                nfoSb.AppendLine($"  <year>{meta.Year.Value}</year>");
            }

            if (!string.IsNullOrEmpty(meta.Description))
            {
                nfoSb.AppendLine($"  <plot>{SanitizeXml(meta.Description)}</plot>");
            }

            // Write uniqueid - prefer IMDB, fall back to TMDB
            if (!string.IsNullOrEmpty(meta.ImdbId))
            {
                nfoSb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{meta.ImdbId}</uniqueid>");
            }
            else if (!string.IsNullOrEmpty(meta.TmdbId))
            {
                nfoSb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{meta.TmdbId}</uniqueid>");
            }

            // Write genres
            if (meta.Genres != null && meta.Genres.Count > 0)
            {
                foreach (var genre in meta.Genres)
                {
                    nfoSb.AppendLine($"  <genre>{SanitizeXml(genre)}</genre>");
                }
            }

            nfoSb.AppendLine($"</{rootElement}>");

            // Write .nfo for each version slot
            var slots = await Plugin.Instance!.VersionSlotRepository.GetEnabledSlotsAsync(cancellationToken);
            if (!slots.Any())
                return;

            var defaultSlot = slots.FirstOrDefault(s => s.IsDefault) ?? slots.First();
            var baseName = catalogItem.Title;

            foreach (var slot in slots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = $"{baseName}{(slot.IsDefault ? "" : $"_{slot.SlotKey}")}.nfo";
                var fullPath = Path.Combine(folderPath, fileName);
                var tmpPath = fullPath + ".tmp";

                try
                {
                    await File.WriteAllTextAsync(tmpPath, nfoSb.ToString(), new UTF8Encoding(false));
                    File.Move(tmpPath, fullPath, overwrite: true);
                    _logger.LogDebug("[InfiniteDrive] Wrote enriched .nfo: {Path}", fullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Failed to write enriched .nfo: {Path}", fullPath);
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }
            }
        }

        private static string SanitizeXml(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Basic XML sanitization
            return input
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        // ── Phase 3: Token Renewal ─────────────────────────────────────────

        private async Task TokenRenewalAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            // Query items with tokens expiring within 90 days
            var expiringItems = await db.GetCatalogItemsWithExpiringTokensAsync(
                int.MaxValue, // No limit for token renewal
                cancellationToken);

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
                    // Rewrite .strm files with fresh tokens
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

                    // Update token expiry timestamp
                    item.StrmTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds();
                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await db.UpsertCatalogItemAsync(item, cancellationToken);

                    renewedCount++;
                    _logger.LogDebug("[InfiniteDrive] Token renewal: {Imdb}", item.ImdbId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Token renewal failed for {Imdb}", item.ImdbId);
                }
            }

            if (renewedCount > 0)
            {
                _logger.LogInformation("[InfiniteDrive] Token renewal: Renewed {Count} items", renewedCount);
            }
        }

        // ── Phase 4: Save Maintenance ─────────────────────────────────────

        private async Task SaveMaintenancePassAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            // Clean up orphaned saves (media_item no longer exists)
            var orphans = await db.GetOrphanedUserSavesAsync(cancellationToken);
            var orphanCount = 0;
            foreach (var (saveId, userId, mediaItemId) in orphans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await db.DeleteUserSaveByIdAsync(saveId, cancellationToken);
                orphanCount++;
            }

            if (orphanCount > 0)
            {
                _logger.LogInformation("[InfiniteDrive] Save maintenance: Removed {Count} orphaned saves", orphanCount);
            }

            // Re-sync global saved flags for items marked as saved but with no user saves
            var savedItems = await db.GetItemsBySavedAsync(true, cancellationToken);
            var reSyncedCount = 0;
            foreach (var item in savedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasSave = await db.HasUserSaveAsync("", item.Id, cancellationToken);
                // If checking with empty user returns false, do a full re-sync
                // Actually, SyncGlobalSavedFlagAsync handles this atomically
                await db.SyncGlobalSavedFlagAsync(item.Id, cancellationToken);
                reSyncedCount++;
            }

            if (reSyncedCount > 0)
            {
                _logger.LogDebug("[InfiniteDrive] Save maintenance: Re-synced {Count} global saved flags", reSyncedCount);
            }
        }

        private async Task PersistEnrichmentCountsAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            // Count items with nfo_status = 'Blocked'
            var blockedQuery = "SELECT COUNT(*) FROM catalog_items WHERE nfo_status = 'Blocked' AND removed_at IS NULL;";
            var blockedCount = await db.QueryScalarIntAsync(blockedQuery, cancellationToken);

            // Count items with nfo_status = 'NeedsEnrich'
            var needsEnrichQuery = "SELECT COUNT(*) FROM catalog_items WHERE nfo_status = 'NeedsEnrich' AND removed_at IS NULL;";
            var needsEnrichCount = await db.QueryScalarIntAsync(needsEnrichQuery, cancellationToken);

            await db.PersistMetadataAsync("blocked_enrichment_count", blockedCount.ToString(), cancellationToken);
            await db.PersistMetadataAsync("needs_enrich_count", needsEnrichCount.ToString(), cancellationToken);
        }

        private static string GetEmbyBaseUrl(PluginConfiguration config)
        {
            // Use configured Emby base URL for resolve tokens
            // This ensures .strm files point to the local Emby server for proxying
            if (!string.IsNullOrEmpty(config.EmbyBaseUrl))
            {
                return config.EmbyBaseUrl.TrimEnd('/');
            }

            // Fallback to localhost if not configured
            return "http://localhost:8096";
        }

        // ── Helper types ─────────────────────────────────────────────────────────

        private record EnrichedItem
        {
            public string Id { get; init; }
            public string ImdbId { get; init; }
            public string Title { get; init; }
            public int? Year { get; init; }
            public int RetryCount { get; set; }
            public long? NextRetryAt { get; set; }
        }
    }
}
