using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using EmbyStreams.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Tasks
{
    /// <summary>
    /// Library Worker scheduled task implementing the first three steps of the 5-step pipeline:
    /// Collect (pull new/changed items since watermark), Write (.strm files with resolve tokens),
    /// and Hint (Identity Hint NFO alongside every .strm).
    ///
    /// Runs on a 6-minute cycle, processing only incremental changes since the last run.
    /// </summary>
    public class RefreshTask : IScheduledTask
    {
        // ── Constants ────────────────────────────────────────────────────────────

        private const string TaskName     = "EmbyStreams Refresh Worker";
        private const string TaskKey      = "EmbyStreamsRefresh";
        private const string TaskCategory = "EmbyStreams";
        private const int    NotifyLimit = 42;  // Non-negotiable bound

        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ILogger<RefreshTask>           _logger;
        private readonly ILibraryManager               _libraryManager;
        private readonly VersionMaterializer?           _materializer;

        private static readonly SemaphoreSlim _runningGate = new(1, 1);

        // ── Constructor ──────────────────────────────────────────────────────────

        public RefreshTask(
            ILogManager logManager,
            ILibraryManager libraryManager)
        {
            _logger         = new EmbyLoggerAdapter<RefreshTask>(logManager.GetLogger("EmbyStreams"));
            _libraryManager = libraryManager;
            _materializer   = new VersionMaterializer(_logger);
        }

        // ── IScheduledTask ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Incremental library worker that Collects new/changed items, Writes .strm files, and Creates Identity Hint NFOs.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromMinutes(6).Ticks,
                }
            };

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Concurrency guard — skip if another instance is already running
            if (!_runningGate.Wait(0))
            {
                _logger.LogInformation("[EmbyStreams] RefreshTask already running, skipping");
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
            _logger.LogInformation("[EmbyStreams] RefreshTask started");

            // Create run log entry
            var runLogId = await Plugin.Instance!.DatabaseManager.InsertRunLogAsync("RefreshTask", "start", cancellationToken);

            try
            {
                var totalItemsAffected = 0;

                // Step 1: Collect
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "collect", cancellationToken);
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", "0", cancellationToken);
                progress?.Report(0.08);
                var collected = await CollectStepAsync(cancellationToken);
                if (!collected.Any())
                {
                    _logger.LogDebug("[EmbyStreams] RefreshTask: No new/changed items found in Collect step");
                }
                else
                {
                    _logger.LogInformation("[EmbyStreams] RefreshTask: Collected {Count} new/changed items", collected.Count);
                    totalItemsAffected += collected.Count;
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 2: Write (only if we have collected items)
                var writtenItems = new List<CatalogItem>();
                if (collected.Any())
                {
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "write", cancellationToken);
                    progress?.Report(0.25);
                    var written = await WriteStepAsync(collected, cancellationToken);
                    _logger.LogInformation("[EmbyStreams] RefreshTask: Wrote {Count} .strm files", written);
                    writtenItems.AddRange(collected);
                    totalItemsAffected += written;
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 3: Hint (only for written items)
                if (writtenItems.Any())
                {
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "hint", cancellationToken);
                    progress?.Report(0.42);
                    var hinted = await HintStepAsync(writtenItems, cancellationToken);
                    _logger.LogInformation("[EmbyStreams] RefreshTask: Created {Count} Identity Hint NFOs", hinted);
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 4: Notify (42-item bound)
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "notify", cancellationToken);
                progress?.Report(0.58);
                var notified = await NotifyStepAsync(cancellationToken);
                if (notified > 0)
                {
                    _logger.LogInformation("[EmbyStreams] RefreshTask: Notified {Count} items to Emby", notified);
                    totalItemsAffected += notified;
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 5: Verify (42-item bound + token renewal)
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "verify", cancellationToken);
                progress?.Report(0.75);
                var verified = await VerifyStepAsync(cancellationToken);
                if (verified > 0)
                {
                    _logger.LogInformation("[EmbyStreams] RefreshTask: Verified {Count} items", verified);
                    totalItemsAffected += verified;
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 6: Stalled-item promotion (>24h Notified -> NeedsEnrich)
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "promote", cancellationToken);
                progress?.Report(0.92);
                var promoted = await PromoteStalledItemsAsync(cancellationToken);
                if (promoted > 0)
                {
                    _logger.LogInformation("[EmbyStreams] RefreshTask: Promoted {Count} stalled items to NeedsEnrich", promoted);
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                progress?.Report(1.0);
                _logger.LogInformation("[EmbyStreams] RefreshTask completed successfully. Total affected: {Count}", totalItemsAffected);

                // Clear active step and persist last run time
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "", cancellationToken);
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("last_refresh_run_time", DateTime.UtcNow.ToString("o"), cancellationToken);

                await Plugin.Instance!.DatabaseManager.UpdateRunLogAsync(runLogId, "complete", totalItemsAffected, "All steps completed", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] RefreshTask failed");
                await Plugin.Instance!.DatabaseManager.UpdateRunLogAsync(runLogId, "error", 0, ex.Message, cancellationToken);
                throw;
            }
        }

        // ── Step 1: Collect ──────────────────────────────────────────────────────

        private async Task<List<CatalogItem>> CollectStepAsync(CancellationToken cancellationToken)
        {
            var newItems = new List<CatalogItem>();

            // For now, we only process AIOStreams source
            // In future sprints, this will iterate over all configured sources
            var sourceId = "aiostreams";

            // Load ingestion state
            var ingestionState = await Plugin.Instance!.DatabaseManager.GetIngestionStateAsync(sourceId, cancellationToken);

            // Fetch AIOStreams catalog (full fetch + diff)
            var config = Plugin.Instance!.Configuration;
            if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                _logger.LogWarning("[EmbyStreams] AIOStreams URL is not configured");
                return newItems;
            }

            using var client = new AioStreamsClient(config, _logger);
            if (!client.IsConfigured)
            {
                _logger.LogWarning("[EmbyStreams] AIOStreams client could not be configured");
                return newItems;
            }

            // Fetch catalog items
            var catalogItems = await FetchCatalogItemsAsync(client, cancellationToken);
            if (catalogItems.Count == 0)
                return newItems;

            // Get existing catalog items for comparison
            var existingItems = await Plugin.Instance!.DatabaseManager.GetActiveCatalogItemsAsync();
            var existingByImdb = existingItems.ToDictionary(i => i.ImdbId);

            // Diff: identify new and changed items
            foreach (var catalogItem in catalogItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (existingByImdb.TryGetValue(catalogItem.ImdbId, out var existing))
                {
                    // Check if changed (title or year)
                    if (existing.Title != catalogItem.Title || existing.Year != catalogItem.Year)
                    {
                        existing.Title = catalogItem.Title;
                        existing.Year = catalogItem.Year;
                        existing.ItemState = ItemState.Queued;
                        existing.UpdatedAt = DateTime.UtcNow.ToString("o");
                        await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(existing, cancellationToken);
                        newItems.Add(existing);
                        _logger.LogDebug("[EmbyStreams] Changed item: {ImdbId}", catalogItem.ImdbId);
                    }
                }
                else
                {
                    // New item
                    catalogItem.ItemState = ItemState.Queued;
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(catalogItem, cancellationToken);
                    newItems.Add(catalogItem);
                    _logger.LogDebug("[EmbyStreams] New item: {ImdbId}", catalogItem.ImdbId);
                }
            }

            // Update ingestion state watermark
            var state = new IngestionState
            {
                SourceId = sourceId,
                LastPollAt = DateTime.UtcNow.ToString("o"),
                LastFoundAt = DateTime.UtcNow.ToString("o"),
                Watermark = DateTime.UtcNow.Ticks.ToString()
            };
            await Plugin.Instance!.DatabaseManager.UpsertIngestionStateAsync(state, cancellationToken);

            return newItems;
        }

        private async Task<List<CatalogItem>> FetchCatalogItemsAsync(
            AioStreamsClient client,
            CancellationToken cancellationToken)
        {
            var items = new List<CatalogItem>();

            try
            {
                // Fetch manifest
                var manifest = await client.GetManifestAsync(cancellationToken);
                if (manifest == null || manifest.Catalogs == null || manifest.Catalogs.Count == 0)
                    return items;

                // Fetch each catalog (simplified — in full implementation, this would
                // respect catalog limits and use ICatalogProvider pattern)
                foreach (var catalog in manifest.Catalogs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Only process movie and series catalogs for now
                    if (catalog.Type != "movie" && catalog.Type != "series")
                        continue;

                    var catalogId = catalog.Id ?? "unknown";
                    var catalogType = catalog.Type ?? "movie";

                    try
                    {
                        var catalogData = await client.GetCatalogAsync(catalogType, catalogId, cancellationToken);
                        if (catalogData == null || catalogData.Metas == null)
                            continue;

                        foreach (var meta in catalogData.Metas)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var imdbId = meta.ImdbId ?? meta.Id;
                            if (string.IsNullOrEmpty(imdbId))
                                continue;

                            var item = new CatalogItem
                            {
                                ImdbId = imdbId,
                                Title = meta.Name ?? string.Empty,
                                Year = ParseYear(meta.ReleaseInfo),
                                MediaType = catalogType,
                                Source = "aiostreams",
                                SourceListId = catalogId,
                                UniqueIdsJson = BuildUniqueIdsJson(meta),
                                TmdbId = meta.TmdbId ?? meta.TmdbIdAlt
                            };

                            items.Add(item);
                        }

                        // Cap at configured limit
                        var config = Plugin.Instance!.Configuration;
                        if (items.Count >= config.CatalogItemCap)
                            break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[EmbyStreams] Failed to fetch catalog {CatalogId}", catalogId);
                        // Continue with other catalogs
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Failed to fetch AIOStreams catalog");
            }

            return items;
        }

        // ── Step 2: Write ────────────────────────────────────────────────────────

        private async Task<int> WriteStepAsync(List<CatalogItem> items, CancellationToken cancellationToken)
        {
            var written = 0;

            // Get enabled version slots
            var slots = await Plugin.Instance!.VersionSlotRepository.GetEnabledSlotsAsync(cancellationToken);
            if (!slots.Any())
            {
                _logger.LogWarning("[EmbyStreams] No enabled version slots configured");
                return 0;
            }

            var defaultSlot = slots.FirstOrDefault(s => s.IsDefault) ?? slots.First();
            var config = Plugin.Instance!.Configuration;
            var embyBaseUrl = GetEmbyBaseUrl(config);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Build folder name
                var folderName = BuildFolderName(item);
                var basePath = GetLibraryPath(config, item.MediaType);
                var folderPath = Path.Combine(basePath, folderName);

                Directory.CreateDirectory(folderPath);

                // Write .strm file for each slot
                foreach (var slot in slots)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Build strm URL with token
                    var (strmUrl, expiresAtUnix) = (_materializer ?? throw new InvalidOperationException("Materializer not initialized")).BuildStrmUrlWithExpiry(
                        embyBaseUrl,
                        item.ImdbId,
                        slot.SlotKey,
                        "imdb",
                        null, // season
                        null); // episode

                    // Write .strm file
                    var baseName = Path.GetFileNameWithoutExtension(folderName);
                    var fileName = _materializer.GetFileName(baseName, slot, defaultSlot, ".strm");
                    var fullPath = Path.Combine(folderPath, fileName);
                    var tmpPath = fullPath + ".tmp";

                    try
                    {
                        await File.WriteAllTextAsync(tmpPath, strmUrl, new UTF8Encoding(false));
                        File.Move(tmpPath, fullPath, overwrite: true);
                        _logger.LogDebug("[EmbyStreams] Wrote .strm: {Path}", fullPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[EmbyStreams] Failed to write .strm: {Path}", fullPath);
                        // Clean up tmp file if it exists
                        if (File.Exists(tmpPath))
                            File.Delete(tmpPath);
                        continue;
                    }
                }

                // Update item state
                item.ItemState = ItemState.Written;
                item.StrmPath = folderPath;
                item.LocalPath = folderPath;
                item.LocalSource = "strm";
                item.StrmTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds();
                item.UpdatedAt = DateTime.UtcNow.ToString("o");

                await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                written++;
            }

            return written;
        }

        // ── Step 3: Hint ────────────────────────────────────────────────────────

        private async Task<int> HintStepAsync(List<CatalogItem> items, CancellationToken cancellationToken)
        {
            var hinted = 0;
            var slots = await Plugin.Instance!.VersionSlotRepository.GetEnabledSlotsAsync(cancellationToken);
            if (!slots.Any())
                return 0;

            var defaultSlot = slots.FirstOrDefault(s => s.IsDefault) ?? slots.First();

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var folderName = BuildFolderName(item);
                var folderPath = item.StrmPath ?? folderName;
                var baseName = Path.GetFileNameWithoutExtension(folderName);

                // Determine uniqueid type and value
                string uniqueidType;
                string uniqueidValue;

                if (!string.IsNullOrEmpty(item.TmdbId))
                {
                    uniqueidType = "tmdb";
                    uniqueidValue = item.TmdbId;
                }
                else if (!string.IsNullOrEmpty(item.ImdbId))
                {
                    uniqueidType = "imdb";
                    uniqueidValue = item.ImdbId;
                }
                else
                {
                    // No known IDs — mark as NeedsEnrich
                    item.NfoStatus = "NeedsEnrich";
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                    _logger.LogDebug("[EmbyStreams] No IDs for item {Title}, marked as NeedsEnrich", item.Title);
                    continue;
                }

                // Write Identity Hint NFO for each slot
                foreach (var slot in slots)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var rootElement = item.MediaType == "movie" ? "movie" : "tvshow";
                    var fileName = (_materializer ?? throw new InvalidOperationException("Materializer not initialized")).GetFileName(baseName, slot, defaultSlot, ".nfo");
                    var fullPath = Path.Combine(folderPath, fileName);
                    var tmpPath = fullPath + ".tmp";

                    try
                    {
                        var nfoSb = new StringBuilder();
                        nfoSb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                        nfoSb.AppendLine($"<{rootElement} lockdata=\"false\">");
                        nfoSb.AppendLine($"  <uniqueid type=\"{uniqueidType}\" default=\"true\">");
                        nfoSb.AppendLine($"    {uniqueidValue}");
                        nfoSb.AppendLine("  </uniqueid>");
                        nfoSb.AppendLine($"</{rootElement}>");

                        await File.WriteAllTextAsync(tmpPath, nfoSb.ToString(), new UTF8Encoding(false));
                        File.Move(tmpPath, fullPath, overwrite: true);
                        _logger.LogDebug("[EmbyStreams] Wrote Identity Hint .nfo: {Path}", fullPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[EmbyStreams] Failed to write .nfo: {Path}", fullPath);
                        if (File.Exists(tmpPath))
                            File.Delete(tmpPath);
                        continue;
                    }
                }

                // Update NFO status
                item.NfoStatus = "Hinted";
                await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                hinted++;
            }

            return hinted;
        }

        // ── Step 4: Notify ────────────────────────────────────────────────────────

        private async Task<int> NotifyStepAsync(CancellationToken cancellationToken)
        {
            var notified = 0;

            // Query Written items (bounded at 42)
            var writtenItems = await Plugin.Instance!.DatabaseManager.GetCatalogItemsByStateAsync(
                ItemState.Written,
                NotifyLimit,
                cancellationToken);

            if (!writtenItems.Any())
                return 0;

            if (writtenItems.Any())
            {
                // Queue a single library scan for all items at once
                // This is the fallback approach since surgical API may not be available
                try
                {
                    _libraryManager.QueueLibraryScan();
                    _logger.LogDebug("[EmbyStreams] Notify: Queued library scan for {Count} items", writtenItems.Count);

                    // Transition all Written items to Notified state
                    foreach (var item in writtenItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        item.ItemState = ItemState.Notified;
                        item.UpdatedAt = DateTime.UtcNow.ToString("o");
                        await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                        notified++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EmbyStreams] Notify: Failed to queue library scan");
                }
            }

            return notified;
        }

        // ── Step 5: Verify ────────────────────────────────────────────────────────

        private async Task<int> VerifyStepAsync(CancellationToken cancellationToken)
        {
            var verified = 0;

            // Query Notified items (bounded at 42)
            var notifiedItems = await Plugin.Instance!.DatabaseManager.GetCatalogItemsByStateAsync(
                ItemState.Notified,
                NotifyLimit,
                cancellationToken);

            if (!notifiedItems.Any())
            {
                // No Notified items, check for token renewal
                return await RenewTokensAsync(NotifyLimit, cancellationToken);
            }

            foreach (var item in notifiedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(item.StrmPath))
                    continue;

                // Verify that .strm files exist on disk and were written recently
                // If files exist and item has been Notified for at least one cycle,
                // assume Emby has indexed them (simplified verification)
                try
                {
                    var folderPath = item.StrmPath;
                    if (Directory.Exists(folderPath))
                    {
                        // Check for .strm files in the folder
                        var strmFiles = Directory.GetFiles(folderPath, "*.strm");
                        if (strmFiles.Length > 0)
                        {
                            // .strm files exist - transition to Ready
                            item.ItemState = ItemState.Ready;
                            item.UpdatedAt = DateTime.UtcNow.ToString("o");
                            await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                            verified++;
                            _logger.LogDebug("[EmbyStreams] Verify: Confirmed .strm files for {Imdb}", item.ImdbId);
                        }
                        else
                        {
                            // No .strm files found - leave as Notified
                            // Stalled-item promotion will handle items >24h
                            _logger.LogDebug("[EmbyStreams] Verify: No .strm files for {Imdb}, remains Notified", item.ImdbId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EmbyStreams] Verify: Failed to verify {Imdb}", item.ImdbId);
                }
            }

            return verified;
        }

        /// <summary>
        /// Renews tokens for items expiring within 90 days.
        /// Shares the 42-item budget with Verify step.
        /// </summary>
        private async Task<int> RenewTokensAsync(int budget, CancellationToken cancellationToken)
        {
            var renewed = 0;

            var expiringItems = await Plugin.Instance!.DatabaseManager.GetCatalogItemsWithExpiringTokensAsync(
                budget,
                cancellationToken);

            if (!expiringItems.Any())
                return 0;

            var slots = await Plugin.Instance!.VersionSlotRepository.GetEnabledSlotsAsync(cancellationToken);
            if (!slots.Any())
                return 0;

            var defaultSlot = slots.FirstOrDefault(s => s.IsDefault) ?? slots.First();
            var config = Plugin.Instance!.Configuration;
            var embyBaseUrl = GetEmbyBaseUrl(config);

            foreach (var item in expiringItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(item.StrmPath) || string.IsNullOrEmpty(item.LocalPath))
                    continue;

                // Rewrite .strm files with fresh tokens
                var folderPath = item.LocalPath;
                var baseName = Path.GetFileNameWithoutExtension(folderPath);

                try
                {
                    foreach (var slot in slots)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var (strmUrl, expiresAtUnix) = (_materializer ?? throw new InvalidOperationException("Materializer not initialized")).BuildStrmUrlWithExpiry(
                            embyBaseUrl,
                            item.ImdbId,
                            slot.SlotKey,
                            "imdb",
                            null,
                            null);

                        var fileName = _materializer.GetFileName(baseName, slot, defaultSlot, ".strm");
                        var fullPath = Path.Combine(folderPath, fileName);
                        var tmpPath = fullPath + ".tmp";

                        await File.WriteAllTextAsync(tmpPath, strmUrl, new UTF8Encoding(false));
                        File.Move(tmpPath, fullPath, overwrite: true);
                    }

                    // Update token expiry timestamp
                    item.StrmTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds();
                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);

                    renewed++;
                    _logger.LogDebug("[EmbyStreams] Renew: Refreshed token for {Imdb}", item.ImdbId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EmbyStreams] Renew: Failed to refresh token for {Imdb}", item.ImdbId);
                }
            }

            return renewed;
        }

        // ── Stalled-Item Promotion ──────────────────────────────────────────────

        private async Task<int> PromoteStalledItemsAsync(CancellationToken cancellationToken)
        {
            var promoted = 0;

            // Query all Notified items to check for stalled ones
            var notifiedItems = await Plugin.Instance!.DatabaseManager.GetCatalogItemsByStateAsync(
                ItemState.Notified,
                int.MaxValue,  // No limit for stalled check
                cancellationToken);

            var stalledThreshold = DateTime.UtcNow.AddHours(-24);

            foreach (var item in notifiedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Check if item has been Notified for >24 hours
                if (DateTime.TryParse(item.UpdatedAt, out var updatedAt) && updatedAt < stalledThreshold)
                {
                    // Promote to NeedsEnrich
                    item.ItemState = ItemState.NeedsEnrich;
                    item.NfoStatus = "NeedsEnrich";
                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);

                    promoted++;
                    _logger.LogInformation(
                        "[EmbyStreams] Stalled: Promoted {Imdb} to NeedsEnrich (Notified >24h)",
                        item.ImdbId);
                }
            }

            return promoted;
        }

        // ── Helper methods ───────────────────────────────────────────────────────

        private static string BuildFolderName(CatalogItem item)
        {
            if (!string.IsNullOrEmpty(item.TmdbId))
                return $"{item.Title} ({item.Year}) [tmdbid={item.TmdbId}]";
            else
                return $"{item.Title} ({item.Year}) [imdbid-{item.ImdbId}]";
        }

        private static string GetLibraryPath(PluginConfiguration config, string mediaType)
        {
            // Return appropriate library path based on media type
            return mediaType switch
            {
                "anime" => config.SyncPathAnime,
                "series" => config.SyncPathShows,
                _ => config.SyncPathMovies
            };
        }

        private static string GetEmbyBaseUrl(PluginConfiguration config)
        {
            // Build Emby base URL for resolve tokens
            // Extract base URL from manifest URL
            if (!string.IsNullOrEmpty(config.PrimaryManifestUrl))
            {
                var uri = new Uri(config.PrimaryManifestUrl);
                return $"{uri.Scheme}://{uri.Host}";
            }

            return "http://localhost:8096";
        }

        private static int? ParseYear(string? releaseInfo)
        {
            if (string.IsNullOrEmpty(releaseInfo))
                return null;

            var yearStr = releaseInfo.Split('–', '—')[0].Trim();
            if (int.TryParse(yearStr, out var year))
                return year;

            return null;
        }

        private static string? BuildUniqueIdsJson(AioStreamsMeta meta)
        {
            var ids = new List<object>();

            // Collect all available provider IDs
            if (!string.IsNullOrEmpty(meta.ImdbId))
                ids.Add(new { provider = "imdb", id = meta.ImdbId });

            var tmdbId = meta.TmdbId ?? meta.TmdbIdAlt;
            if (!string.IsNullOrEmpty(tmdbId))
                ids.Add(new { provider = "tmdb", id = tmdbId });

            if (!string.IsNullOrEmpty(meta.Id))
            {
                // Use ID as fallback if it starts with a known prefix
                var id = meta.Id;
                if (id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                    ids.Add(new { provider = "imdb", id = id });
                else if (id.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase))
                    ids.Add(new { provider = "kitsu", id = id.Replace("kitsu:", "") });
            }

            if (ids.Count == 0)
                return null;

            return System.Text.Json.JsonSerializer.Serialize(ids);
        }
    }
}
