using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
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
    /// Library Worker scheduled task implementing the first three steps of the 5-step pipeline:
    /// Collect (pull new/changed items since watermark), Write (.strm files with resolve tokens),
    /// and Hint (Identity Hint NFO alongside every .strm).
    ///
    /// Runs on a 6-minute cycle, processing only incremental changes since the last run.
    /// </summary>
    public class RefreshTask : IScheduledTask
    {
        // ── Constants ────────────────────────────────────────────────────────────

        private const string TaskName     = "InfiniteDrive Refresh Worker";
        private const string TaskKey      = "InfiniteDriveRefresh";
        private const string TaskCategory = "InfiniteDrive";
        private const int NotifyLimit = 50;

        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ILogger<RefreshTask>           _logger;
        private readonly ILibraryManager               _libraryManager;
        private readonly VersionMaterializer?           _materializer;
        private SeriesPreExpansionService?              _expansionService;

        private static readonly SemaphoreSlim _runningGate = new(1, 1);

        // ── Constructor ──────────────────────────────────────────────────────────

        public RefreshTask(
            ILogManager logManager,
            ILibraryManager libraryManager)
        {
            _logger         = new EmbyLoggerAdapter<RefreshTask>(logManager.GetLogger("InfiniteDrive"));
            _libraryManager = libraryManager;
            _materializer   = new VersionMaterializer(_logger);

            // SeriesPreExpansionService — created lazily when config is available
            _expansionService = null; // initialized on first use via EnsureExpansionService()
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
                _logger.LogInformation("[InfiniteDrive] RefreshTask already running, skipping");
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
            _logger.LogInformation("[InfiniteDrive] RefreshTask started");

            var runStartedAt = DateTime.UtcNow;

            // Create run log entry
            var runLogId = await Plugin.Instance!.DatabaseManager.InsertRunLogAsync("RefreshTask", "start", cancellationToken);

            try
            {
                var totalItemsAffected = 0;

                // Step 1: Collect
                Plugin.Pipeline.SetPhase("Refresh", "Collect");
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "collect", cancellationToken);
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", "0", cancellationToken);
                progress?.Report(0.16);
                var collected = await CollectStepAsync(cancellationToken);
                if (!collected.Any())
                {
                    _logger.LogDebug("[InfiniteDrive] RefreshTask: No new/changed items found in Collect step");
                }
                else
                {
                    _logger.LogInformation("[InfiniteDrive] RefreshTask: Collected {Count} new/changed items", collected.Count);
                    totalItemsAffected += collected.Count;
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 2: Write (only if we have collected items)
                var writtenItems = new List<CatalogItem>();
                if (collected.Any())
                {
                    Plugin.Pipeline.SetPhase("Refresh", "Write");
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "write", cancellationToken);
                    progress?.Report(0.33);
                    var written = await WriteStepAsync(collected, cancellationToken);
                    _logger.LogInformation("[InfiniteDrive] RefreshTask: Wrote {Count} .strm files", written);
                    writtenItems.AddRange(collected);
                    totalItemsAffected += written;
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 3: Hint (only for written items)
                if (writtenItems.Any())
                {
                    Plugin.Pipeline.SetPhase("Refresh", "Hint");
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "hint", cancellationToken);
                    progress?.Report(0.50);
                    var hinted = await HintStepAsync(writtenItems, cancellationToken);
                    _logger.LogInformation("[InfiniteDrive] RefreshTask: Created {Count} Identity Hint NFOs", hinted);
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 4: Enrich (inline, no-ID items from this run only)
                if (writtenItems.Any())
                {
                    Plugin.Pipeline.SetPhase("Refresh", "Enrich");
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "enrich", cancellationToken);
                    progress?.Report(0.67);
                    var enriched = await EnrichStepAsync(runStartedAt, cancellationToken);
                    if (enriched > 0)
                    {
                        _logger.LogInformation("[InfiniteDrive] RefreshTask: Enriched {Count} no-ID items", enriched);
                        totalItemsAffected += enriched;
                        await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                    }
                }

                // Step 5: Notify (42-item bound)
                Plugin.Pipeline.SetPhase("Refresh", "Notify");
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "notify", cancellationToken);
                progress?.Report(0.83);
                var notified = await NotifyStepAsync(cancellationToken);
                if (notified > 0)
                {
                    _logger.LogInformation("[InfiniteDrive] RefreshTask: Notified {Count} items to Emby", notified);
                    totalItemsAffected += notified;
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                // Step 6: Verify (42-item bound + token renewal)
                Plugin.Pipeline.SetPhase("Refresh", "Verify");
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "verify", cancellationToken);
                progress?.Report(1.0);
                var verified = await VerifyStepAsync(cancellationToken);
                if (verified > 0)
                {
                    _logger.LogInformation("[InfiniteDrive] RefreshTask: Verified {Count} items", verified);
                    totalItemsAffected += verified;
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                _logger.LogInformation("[InfiniteDrive] RefreshTask completed successfully. Total affected: {Count}", totalItemsAffected);

                // Clear active step and persist last run time
                // TODO: Fix NOT NULL constraint error with plugin_metadata.value
                // await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "", cancellationToken);
                // await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("last_refresh_run_time", DateTime.UtcNow.ToString("o"), cancellationToken);

                await Plugin.Instance!.DatabaseManager.UpdateRunLogAsync(runLogId, "complete", totalItemsAffected, "All steps completed", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] RefreshTask failed");
                await Plugin.Instance!.DatabaseManager.UpdateRunLogAsync(runLogId, "error", 0, ex.Message, cancellationToken);
                throw;
            }
            finally
            {
                Plugin.Pipeline.Clear();
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
                _logger.LogWarning("[InfiniteDrive] AIOStreams URL is not configured");
                return newItems;
            }

            using var client = new AioStreamsClient(config, _logger);
            client.Cooldown = Plugin.Instance?.CooldownGate;
            if (!client.IsConfigured)
            {
                _logger.LogWarning("[InfiniteDrive] AIOStreams client could not be configured");
                return newItems;
            }

            // Fetch catalog items
            var catalogItems = await FetchCatalogItemsAsync(client, cancellationToken);
            if (catalogItems.Count == 0)
                return newItems;

            // Get existing catalog items for comparison
            var existingItems = await Plugin.Instance!.DatabaseManager.GetActiveCatalogItemsAsync();
            var existingByImdb = existingItems.ToLookup(i => i.ImdbId).ToDictionary(g => g.Key, g => g.First());

            // Also collect queued items that don't have .strm files yet
            // This handles the case where CatalogSyncTask has already created items
            // but RefreshTask hasn't processed them yet
            var queuedWithoutStrm = existingItems
                .Where(i => i.ItemState == ItemState.Queued && string.IsNullOrEmpty(i.StrmPath))
                .ToList();

            if (queuedWithoutStrm.Count > 0)
            {
                _logger.LogInformation("[InfiniteDrive] Found {Count} queued items without .strm files, adding to processing queue", queuedWithoutStrm.Count);
                foreach (var item in queuedWithoutStrm)
                {
                    newItems.Add(item);
                }
            }

            // Diff: identify new and changed items from manifest
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
                        // Only add if not already in newItems (from queuedWithoutStrm)
                        if (!newItems.Any(i => i.Id == existing.Id))
                            newItems.Add(existing);
                        _logger.LogDebug("[InfiniteDrive] Changed item: {ImdbId}", catalogItem.ImdbId);
                    }
                }
                else
                {
                    // New item
                    catalogItem.ItemState = ItemState.Queued;
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(catalogItem, cancellationToken);
                    newItems.Add(catalogItem);
                    _logger.LogDebug("[InfiniteDrive] New item: {ImdbId}", catalogItem.ImdbId);
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
                    _logger.LogDebug("[InfiniteDrive] Processing catalog: {CatalogId}, Type: {CatalogType}", catalogId, catalogType);

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

                            var now = DateTime.UtcNow.ToString("o");
                            var year = ParseYear(meta.ReleaseInfo);
                            var item = new CatalogItem
                            {
                                Id = GenerateDeterministicId(imdbId, "aiostreams"),
                                ImdbId = imdbId,
                                Title = meta.Name ?? string.Empty,
                                Year = year,
                                MediaType = catalogType,
                                Source = "aiostreams",
                                SourceListId = catalogId,
                                UniqueIdsJson = BuildUniqueIdsJson(meta),
                                TmdbId = meta.TmdbId ?? meta.TmdbIdAlt,
                                AddedAt = now,
                                UpdatedAt = now
                            };
                            _logger.LogDebug("[InfiniteDrive] Created item: {ImdbId}, Title: {Title}, MediaType: {MediaType}, Year: {Year}, CatalogType: {CatalogType}",
                                item.ImdbId, item.Title, item.MediaType, item.Year, catalogType);
                            items.Add(item);
                        }

                        // Cap at configured limit
                        var config = Plugin.Instance!.Configuration;
                        if (items.Count >= config.CatalogItemCap)
                            break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[InfiniteDrive] Failed to fetch catalog {CatalogId}", catalogId);
                        // Continue with other catalogs
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Failed to fetch AIOStreams catalog");
            }

            // Deduplicate by (imdb_id, source) to prevent INSERT conflicts
            var deduplicated = items.GroupBy(i => (i.ImdbId, i.Source)).Select(g => g.First()).ToList();
            return deduplicated;
        }

        // ── Step 2: Write ────────────────────────────────────────────────────────

        private async Task<int> WriteStepAsync(List<CatalogItem> items, CancellationToken cancellationToken)
        {
            var written = 0;

            // Get enabled version slots
            var slots = await Plugin.Instance!.VersionSlotRepository.GetEnabledSlotsAsync(cancellationToken);
            if (!slots.Any())
            {
                _logger.LogWarning("[InfiniteDrive] No enabled version slots configured");
                return 0;
            }

            var defaultSlot = slots.FirstOrDefault(s => s.IsDefault) ?? slots.First();
            var config = Plugin.Instance!.Configuration;
            var embyBaseUrl = GetEmbyBaseUrl(config);

            // Sprint 370: Create AIOStreams client for fetching Videos[] from meta endpoint
            if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                _logger.LogWarning("[InfiniteDrive] AIOStreams URL is not configured for Videos[] fetch");
            }

            using var client = new AioStreamsClient(config, _logger);
            if (!client.IsConfigured)
            {
                _logger.LogWarning("[InfiniteDrive] AIOStreams client could not be configured for Videos[] fetch");
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Build folder name and base path (shared by series expansion and movie write)
                var folderName = NamingPolicyService.BuildFolderName(item);
                var basePath = GetLibraryPath(config, item.MediaType);

                // ── Series/anime: expand to per-episode .strm files ────────────
                var isSeries = string.Equals(item.MediaType, "series", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase);

                if (isSeries && client.IsConfigured && !string.IsNullOrEmpty(item.ImdbId))
                {
                    // Sprint 370: Try one-pass sync from AIOStreams meta endpoint first
                    var aioVideos = await FetchAioVideosAsync(item.ImdbId, item.MediaType, client, cancellationToken);
                    if (aioVideos != null && aioVideos.Count > 0)
                    {
                        // Store Videos[] for future re-sync
                        item.VideosJson = EpisodeDiffService.SerializeForStorage(aioVideos);

                        _logger.LogInformation(
                            "[InfiniteDrive] Writing episodes from AIOStreams meta: {ImdbId} ({Title})",
                            item.ImdbId, item.Title);

                        var strm = Plugin.Instance?.StrmWriterService;
                        if (strm != null)
                        {
                            var episodesWritten = await strm.WriteEpisodesFromVideosJsonAsync(item, config, cancellationToken);
                            if (episodesWritten > 0)
                            {
                                item.EpisodesExpanded = true;
                                item.ItemState = ItemState.Written;
                                item.StrmPath = Path.Combine(basePath, folderName);
                                item.LocalPath = item.StrmPath;
                                item.LocalSource = "strm";
                                item.UpdatedAt = DateTime.UtcNow.ToString("o");
                                await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                                _logger.LogInformation(
                                    "[InfiniteDrive] One-pass sync complete for {ImdbId} ({Title}) - {Count} episodes written",
                                    item.ImdbId, item.Title, episodesWritten);
                                written++;
                                continue;
                            }
                        }
                        // Fall through to SeriesPreExpansionService if VideosJson write fails
                    }
                }

                if (isSeries)
                {
                    // Fallback: SeriesPreExpansionService (fetches from Stremio)
                    var expansion = EnsureExpansionService();
                    if (expansion != null)
                    {
                        _logger.LogInformation(
                            "[InfiniteDrive] Expanding series via Stremio: {ImdbId} ({Title})",
                            item.ImdbId, item.Title);

                        var expanded = await expansion.ExpandSeriesFromMetadataAsync(item, config, cancellationToken);

                        // Sprint 301: Only mark series as expanded if expansion succeeded
                        // Series won't be promoted to Visible until all episodes are written
                        if (expanded)
                        {
                            item.EpisodesExpanded = true;
                            item.ItemState = ItemState.Written;
                            _logger.LogInformation(
                                "[InfiniteDrive] Series expansion succeeded for {ImdbId} ({Title}) - all episodes written",
                                item.ImdbId, item.Title);
                        }
                        else
                        {
                            item.EpisodesExpanded = false;
                            item.ItemState = ItemState.Queued; // Keep in Queued to retry later
                            _logger.LogWarning(
                                "[InfiniteDrive] Series expansion failed for {ImdbId} ({Title}) - will retry",
                                item.ImdbId, item.Title);
                        }

                        item.UpdatedAt = DateTime.UtcNow.ToString("o");

                        // Set StrmPath to the series folder for downstream hint step
                        item.StrmPath = Path.Combine(basePath, folderName);
                        item.LocalPath = item.StrmPath;
                        item.LocalSource = "strm";

                        await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                        written++;
                        continue;
                    }

                    // Expansion service unavailable — fall through to single-strm write
                    _logger.LogWarning(
                        "[InfiniteDrive] SeriesPreExpansionService unavailable for {ImdbId}, writing single .strm",
                        item.ImdbId);
                }

                // ── Movies (or series fallback): write single .strm per slot ─────

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
                        _logger.LogDebug("[InfiniteDrive] Wrote .strm: {Path}", fullPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[InfiniteDrive] Failed to write .strm: {Path}", fullPath);
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

                // Skip hint for series/anime items — SeriesPreExpansionService already
                // handled during WriteStepAsync
                var isSeries = string.Equals(item.MediaType, "series", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase);
                if (isSeries && !string.IsNullOrEmpty(item.StrmPath))
                {
                    item.NfoStatus = "Expanded";
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                    hinted++;
                    continue;
                }

                // NFO no longer needed — folder name provides ID hints to Emby
                foreach (var slot in slots)
                    cancellationToken.ThrowIfCancellationRequested();

                // Update NFO status
                item.NfoStatus = "Hinted";
                await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                hinted++;
            }

            return hinted;
        }

        // ── Step 4: Enrich ───────────────────────────────────────────

        /// <summary>
        /// Enriches no-ID items from AIOMetadata inline within current Refresh cycle.
        /// Cap: 10 items per run, throttled at 2s per AIOMetadata call.
        /// </summary>
        private async Task<int> EnrichStepAsync(DateTime runStartedAt, CancellationToken cancellationToken)
        {
            var db = Plugin.Instance!.DatabaseManager;

            // Query no-ID items from this run only (added_at >= runStartedAt AND nfo_status = 'NeedsEnrich')
            var noIdItemsQuery = @"
                SELECT * FROM catalog_items
                WHERE nfo_status = 'NeedsEnrich'
                AND added_at >= @runStartedAt
                AND (imdb_id IS NULL OR imdb_id = '')
                AND (tmdb_id IS NULL OR tmdb_id = '')
                AND removed_at IS NULL
                ORDER BY added_at ASC
                LIMIT 10;";

            var noIdItems = await db.QueryListAsync<CatalogItem>(
                noIdItemsQuery,
                cmd => cmd.BindParameters["@runStartedAt"].Bind(runStartedAt.ToString("o")),
                row => new CatalogItem
                {
                    Id = row.GetString(0),
                    ImdbId = row.IsDBNull(1) ? null : row.GetString(1),
                    TmdbId = row.IsDBNull(2) ? null : row.GetString(2),
                    Title = row.GetString(3),
                    Year = row.IsDBNull(4) ? (int?)null : row.GetInt(4),
                    MediaType = row.GetString(5),
                    Source = row.GetString(6),
                    SourceListId = row.IsDBNull(7) ? null : row.GetString(7),
                    SeasonsJson = row.IsDBNull(8) ? null : row.GetString(8),
                    StrmPath = row.IsDBNull(9) ? null : row.GetString(9),
                    AddedAt = row.GetString(10),
                    UpdatedAt = row.GetString(11),
                    RemovedAt = row.IsDBNull(12) ? null : row.GetString(12),
                    LocalPath = row.IsDBNull(13) ? null : row.GetString(13),
                    LocalSource = row.IsDBNull(14) ? null : row.GetString(14),
                    ItemState = (ItemState)row.GetInt(16),
                    PinSource = row.IsDBNull(17) ? null : row.GetString(17),
                    PinnedAt = row.IsDBNull(18) ? null : row.GetString(18),
                    UniqueIdsJson = row.IsDBNull(19) ? null : row.GetString(19),
                    NfoStatus = row.IsDBNull(20) ? null : row.GetString(20),
                    RetryCount = row.GetInt(21),
                    NextRetryAt = row.IsDBNull(22) ? (long?)null : row.GetInt64(22),
                });

            if (!noIdItems.Any())
            {
                _logger.LogDebug("[InfiniteDrive] RefreshTask: Enrich: No no-ID items from this run");
                return 0;
            }

            var aioClient = new AioMetadataClient(Plugin.Instance!.Configuration, _logger);
            aioClient.Cooldown = Plugin.Instance?.CooldownGate;

            // Map CatalogItems to EnrichmentRequests (passing CatalogItem for direct NFO write)
            var requests = noIdItems.Select(ci => new EnrichmentRequest
            {
                Id = ci.Id,
                ImdbId = ci.ImdbId,
                Title = ci.Title,
                Year = ci.Year,
                RetryCount = ci.RetryCount,
                NextRetryAt = ci.NextRetryAt,
                CatalogItem = ci
            }).ToList();

            var result = await MetadataEnrichmentService.EnrichBatchAsync(
                requests,
                (req, ct) => aioClient.FetchByTitleAsync(req.Title, req.Year, ct),
                db, _logger, cancellationToken);

            _logger.LogInformation(
                "[InfiniteDrive] RefreshTask: Enriched {Enriched} no-ID items ({Blocked} blocked, {Skipped} skipped)",
                result.EnrichedCount, result.BlockedCount, result.SkippedCount);

            return result.EnrichedCount;
        }

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
                    _logger.LogDebug("[InfiniteDrive] Notify: Queued library scan for {Count} items", writtenItems.Count);

                    // Transition all Written items to Notified state
                    // Sprint 301: Series must have EpisodesExpanded = true before being notified
                    foreach (var item in writtenItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Skip series/anime items that haven't completed episode expansion
                        bool isSeries = string.Equals(item.MediaType, "series", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase);
                        if (isSeries && item.EpisodesExpanded != true)
                        {
                            _logger.LogDebug("[InfiniteDrive] Notify: Skipping {ImdbId} ({Title}) - episodes not fully expanded yet",
                                item.ImdbId, item.Title);
                            continue;
                        }

                        item.ItemState = ItemState.Notified;
                        item.UpdatedAt = DateTime.UtcNow.ToString("o");
                        await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                        notified++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Notify: Failed to queue library scan");
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

                // Sprint 301: Skip series/anime that haven't completed episode expansion
                bool isSeries = string.Equals(item.MediaType, "series", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase);
                if (isSeries && item.EpisodesExpanded != true)
                {
                    _logger.LogDebug("[InfiniteDrive] Verify: Skipping {ImdbId} ({Title}) - episodes not fully expanded yet",
                        item.ImdbId, item.Title);
                    continue;
                }

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
                            _logger.LogDebug("[InfiniteDrive] Verify: Confirmed .strm files for {Imdb}", item.ImdbId);
                        }
                        else
                        {
                            // No .strm files found - leave as Notified
                            // Stalled-item promotion will handle items >24h
                            _logger.LogDebug("[InfiniteDrive] Verify: No .strm files for {Imdb}, remains Notified", item.ImdbId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Verify: Failed to verify {Imdb}", item.ImdbId);
                }
            }

            // Sub-step: Promote stalled items (>24h Notified -> NeedsEnrich)
            var promoted = await PromoteStalledItemsAsync(cancellationToken);
            if (promoted > 0)
            {
                _logger.LogInformation("[InfiniteDrive] Verify: Promoted {Count} stalled items to NeedsEnrich", promoted);
                verified += promoted;
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
                    _logger.LogDebug("[InfiniteDrive] Renew: Refreshed token for {Imdb}", item.ImdbId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Renew: Failed to refresh token for {Imdb}", item.ImdbId);
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
                        "[InfiniteDrive] Stalled: Promoted {Imdb} to NeedsEnrich (Notified >24h)",
                        item.ImdbId);
                }
            }

            return promoted;
        }

        // ── Helper methods ───────────────────────────────────────────────────────

        private SeriesPreExpansionService? EnsureExpansionService()
        {
            if (_expansionService != null)
                return _expansionService;

            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
                return null;

            var (baseUrl, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);

            // Fall back to secondary manifest URL if primary failed
            if (string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                (baseUrl, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.SecondaryManifestUrl);

            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            // Build stremio base (same logic as AioStreamsClient.BuildStremioBase)
            var stremioBase = string.Equals(uuid, "DIRECT", StringComparison.Ordinal)
                ? baseUrl.TrimEnd('/')
                : $"{baseUrl.TrimEnd('/')}/stremio" +
                  (!string.IsNullOrWhiteSpace(uuid) ? $"/{uuid}" : "") +
                  (!string.IsNullOrWhiteSpace(token) ? $"/{token}" : "");

            var provider = new StremioMetadataProvider(stremioBase, _logger);
            _expansionService = new SeriesPreExpansionService(_libraryManager, _logger, provider);
            return _expansionService;
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
            // Use configured Emby base URL for resolve tokens
            // This ensures .strm files point to the local Emby server for proxying
            if (!string.IsNullOrEmpty(config.EmbyBaseUrl))
            {
                return config.EmbyBaseUrl.TrimEnd('/');
            }

            // Fallback to localhost if not configured
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

        /// <summary>
        /// Fetches Videos[] from AIOStreams meta endpoint for one-pass series episode sync.
        /// Sprint 370: One-pass series episode sync from AIOStreams.
        /// Returns null on failure or if no videos found.
        /// </summary>
        private async Task<List<Services.StremioVideo>?> FetchAioVideosAsync(
            string imdbId, string mediaType, AioStreamsClient client, CancellationToken cancellationToken)
        {
            try
            {
                var metaResponse = await client.GetMetaAsyncTyped(mediaType, imdbId, cancellationToken);
                if (metaResponse?.Meta?.Videos == null || metaResponse.Meta.Videos.Count == 0)
                {
                    _logger.LogDebug("[InfiniteDrive] No Videos[] found in AIOStreams meta for {ImdbId}", imdbId);
                    return null;
                }

                // Convert AioVideo[] to StremioVideo[]
                var videos = new List<Services.StremioVideo>();
                foreach (var aioVideo in metaResponse.Meta.Videos)
                {
                    if (aioVideo.Season.HasValue && aioVideo.Episode.HasValue)
                    {
                        videos.Add(new Services.StremioVideo
                        {
                            Id = aioVideo.Id ?? $"{aioVideo.Season}-{aioVideo.Episode}",
                            Name = aioVideo.Title,
                            Season = aioVideo.Season,
                            Episode = aioVideo.Episode,
                            Number = aioVideo.Episode,
                            Released = ParseAioVideoReleased(aioVideo.Released)
                        });
                    }
                }

                _logger.LogInformation(
                    "[InfiniteDrive] Fetched {Count} episodes from AIOStreams meta for {ImdbId}",
                    videos.Count, imdbId);

                return videos;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[InfiniteDrive] Failed to fetch Videos[] from AIOStreams meta for {ImdbId}",
                    imdbId);
                return null;
            }
        }

        /// <summary>
        /// Parses AIOStreams video released date string to DateTime.
        /// </summary>
        private static DateTime? ParseAioVideoReleased(string? released)
        {
            if (string.IsNullOrEmpty(released))
                return null;

            if (DateTime.TryParse(released, out var dt))
                return dt;

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

        /// <summary>
        /// Generates a deterministic ID based on imdb_id and source.
        /// This ensures the same item always gets the same ID, preventing
        /// UNIQUE constraint violations during upsert operations.
        /// </summary>
        private static string GenerateDeterministicId(string imdbId, string source)
        {
            // Use a hash of (imdb_id + source) to create a deterministic ID
            // Format: {first 8 chars of hash}-{imdb_id}
            using var hash = System.Security.Cryptography.MD5.Create();
            var input = $"{imdbId}:{source}";
            var hashBytes = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            var hashString = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
            return $"{hashString}-{imdbId}";
        }
    }
}
