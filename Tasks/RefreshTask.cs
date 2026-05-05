using System;
using System.Collections.Concurrent;
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
    internal class RefreshTask
    {
        // ── Constants ────────────────────────────────────────────────────────────

        private const int NotifyLimit = 50;

        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ILogger<RefreshTask>           _logger;
        private readonly ILibraryManager               _libraryManager;

        private static readonly SemaphoreSlim _runningGate = new(1, 1);

        // ── Constructor ──────────────────────────────────────────────────────────

        public RefreshTask(
            ILogManager logManager,
            ILibraryManager libraryManager)
        {
            _logger         = new EmbyLoggerAdapter<RefreshTask>(logManager.GetLogger("InfiniteDrive"));
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Populate phase: Collect + Write + Hint (steps 1-3).
        /// Marvin calls this directly as Phase 2.
        /// </summary>
        internal async Task<List<CatalogItem>> RunPopulateAsync(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("[InfiniteDrive] RefreshTask Populate started");
            var populateSw = System.Diagnostics.Stopwatch.StartNew();

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
                var stepSw = System.Diagnostics.Stopwatch.StartNew();
                var collected = await CollectStepAsync(cancellationToken);
                _logger.LogDebug("[Refresh] Collect step completed in {Ms}ms — {Count} items", stepSw.ElapsedMilliseconds, collected.Count);
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
                    stepSw.Restart();
                    var written = await WriteStepAsync(collected, cancellationToken);
                    _logger.LogDebug("[Refresh] Write step completed in {Ms}ms — {Count} files written", stepSw.ElapsedMilliseconds, written);
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
                    stepSw.Restart();
                    var hinted = await HintStepAsync(writtenItems, cancellationToken);
                    _logger.LogDebug("[Refresh] Hint step completed in {Ms}ms — {Count} items", stepSw.ElapsedMilliseconds, hinted);
                    _logger.LogInformation("[InfiniteDrive] RefreshTask: Created {Count} Identity Hint NFOs", hinted);
                    await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
                }

                await Plugin.Instance!.DatabaseManager.UpdateRunLogAsync(runLogId, "populate_complete", totalItemsAffected, "Populate steps completed", cancellationToken);

                return writtenItems;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] RefreshTask Populate failed");
                try { await Plugin.Instance!.DatabaseManager.UpdateRunLogAsync(runLogId, "error", 0, ex.Message, cancellationToken); } catch (Exception logEx) { Plugin.Instance?.Logger.LogDebug(logEx, "[InfiniteDrive] Non-fatal: {Context}", "update run log on error"); }
                throw;
            }
        }

        /// <summary>
        /// Resolve phase: Enrich + Notify + Verify (steps 4-6).
        /// Marvin calls this directly as Phase 3.
        /// </summary>
        internal async Task RunResolveAsync(CancellationToken cancellationToken, IProgress<double> progress, List<CatalogItem>? writtenItems = null)
        {
            _logger.LogInformation("[InfiniteDrive] RefreshTask Resolve started");
            var resolveSw = System.Diagnostics.Stopwatch.StartNew();

            var runStartedAt = DateTime.UtcNow;
            var totalItemsAffected = 0;

            // Step 4: Enrich (inline, no-ID items from this run only)
            if (writtenItems != null && writtenItems.Any())
            {
                Plugin.Pipeline.SetPhase("Refresh", "Enrich");
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_active_step", "enrich", cancellationToken);
                progress?.Report(0.67);
                var stepSw = System.Diagnostics.Stopwatch.StartNew();
                var enriched = await EnrichStepAsync(runStartedAt, cancellationToken);
                _logger.LogDebug("[Refresh] Enrich step completed in {Ms}ms — {Count} items", stepSw.ElapsedMilliseconds, enriched);
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
            var notifySw = System.Diagnostics.Stopwatch.StartNew();
            var notified = await NotifyStepAsync(cancellationToken);
            _logger.LogDebug("[Refresh] Notify step completed in {Ms}ms — {Count} items", notifySw.ElapsedMilliseconds, notified);
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
            var verifySw = System.Diagnostics.Stopwatch.StartNew();
            var verified = await VerifyStepAsync(cancellationToken);
            _logger.LogDebug("[Refresh] Verify step completed in {Ms}ms — {Count} items", verifySw.ElapsedMilliseconds, verified);
            if (verified > 0)
            {
                _logger.LogInformation("[InfiniteDrive] RefreshTask: Verified {Count} items", verified);
                totalItemsAffected += verified;
                await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("refresh_items_processed", totalItemsAffected.ToString(), cancellationToken);
            }

            _logger.LogInformation("[InfiniteDrive] RefreshTask Resolve completed in {Ms}ms. Total affected: {Count}", resolveSw.ElapsedMilliseconds, totalItemsAffected);

            Plugin.Pipeline.Clear();
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

            using var client = AioStreamsClientFactory.Create(_logger);
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
            var existingByAio = existingItems.ToLookup(i => i.AioId).ToDictionary(g => g.Key, g => g.First());

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

            // Also re-collect series items that need episode expansion (never expanded or eligible for re-expansion)
            const int ReExpansionIntervalSec = 6 * 3600; // 6 hours
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var seriesNeedingExpansion = existingItems
                .Where(i => (i.MediaType == "series" || i.MediaType == "anime")
                         && !string.IsNullOrEmpty(i.AioId)
                         && !string.IsNullOrEmpty(i.StrmPath)
                         && (i.EpisodesExpanded != true
                             || i.LastExpandedAt == null
                             || now - i.LastExpandedAt >= ReExpansionIntervalSec))
                .ToList();

            if (seriesNeedingExpansion.Count > 0)
            {
                _logger.LogInformation("[InfiniteDrive] Found {Count} series items needing episode expansion", seriesNeedingExpansion.Count);
                foreach (var item in seriesNeedingExpansion)
                {
                    if (!newItems.Any(i => i.Id == item.Id))
                        newItems.Add(item);
                }
            }

            // Diff: identify new and changed items from manifest
            foreach (var catalogItem in catalogItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (existingByAio.TryGetValue(catalogItem.AioId, out var existing))
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
                        _logger.LogDebug("[InfiniteDrive] Changed item: {AioId}", catalogItem.AioId);
                    }
                }
                else
                {
                    // New item
                    catalogItem.ItemState = ItemState.Queued;
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(catalogItem, cancellationToken);
                    newItems.Add(catalogItem);
                    _logger.LogDebug("[InfiniteDrive] New item: {AioId}", catalogItem.AioId);
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
            var items = new ConcurrentBag<CatalogItem>();

            try
            {
                // Fetch manifest
                var manifest = await client.GetManifestAsync(cancellationToken);
                if (manifest == null || manifest.Catalogs == null || manifest.Catalogs.Count == 0)
                    return items.ToList();

                // Filter to movie/series catalogs only
                var catalogs = manifest.Catalogs
                    .Where(c => c.Type == "movie" || c.Type == "series")
                    .ToList();

                _logger.LogInformation("[InfiniteDrive] Fetching {Count} catalogs in parallel (max 4 concurrent)", catalogs.Count);

                // Fetch all catalogs concurrently, capped at 4
                using var catalogGate = new SemaphoreSlim(4);
                var fetchTasks = catalogs.Select(catalog => FetchSingleCatalogAsync(client, catalog, items, catalogGate, cancellationToken));
                await Task.WhenAll(fetchTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Failed to fetch AIOStreams catalog");
            }

            // Deduplicate by (imdb_id, source) to prevent INSERT conflicts
            var deduplicated = items.GroupBy(i => (i.AioId, i.Source)).Select(g => g.First()).ToList();
            return deduplicated;
        }

        private async Task FetchSingleCatalogAsync(
            AioStreamsClient client,
            AioStreamsCatalogDef catalog,
            ConcurrentBag<CatalogItem> results,
            SemaphoreSlim gate,
            CancellationToken cancellationToken)
        {
            var catalogId = catalog.Id ?? "unknown";
            var catalogType = catalog.Type ?? "movie";

            await gate.WaitAsync(cancellationToken);
            try
            {
                _logger.LogDebug("[InfiniteDrive] Fetching catalog: {CatalogId}, Type: {CatalogType}", catalogId, catalogType);
                var catalogData = await client.GetCatalogAsync(catalogType, catalogId, cancellationToken);
                if (catalogData?.Metas == null) return;

                foreach (var meta in catalogData.Metas)
                {
                    var aioId = meta.ImdbId ?? meta.Id;
                    if (string.IsNullOrEmpty(aioId))
                        continue;

                    var now = DateTime.UtcNow.ToString("o");
                    var year = ParseYear(meta.ReleaseInfo);
                    results.Add(new CatalogItem
                    {
                        Id = GenerateDeterministicId(aioId, "aiostreams"),
                        AioId = aioId,
                        Title = meta.Name ?? string.Empty,
                        Year = year,
                        MediaType = catalogType,
                        Source = "aiostreams",
                        SourceListId = catalogId,
                        UniqueIdsJson = BuildUniqueIdsJson(meta),
                        TmdbId = meta.TmdbId ?? meta.TmdbIdAlt,
                        AddedAt = now,
                        UpdatedAt = now
                    });
                }

                _logger.LogDebug("[InfiniteDrive] Catalog {CatalogId} returned {Count} items", catalogId, catalogData.Metas.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Failed to fetch catalog {CatalogId}", catalogId);
            }
            finally
            {
                gate.Release();
            }
        }

        // ── Step 2: Write ────────────────────────────────────────────────────────

        private async Task<int> WriteStepAsync(List<CatalogItem> items, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var embyBaseUrl = string.IsNullOrEmpty(config.EmbyBaseUrl) ? "http://localhost:8096" : config.EmbyBaseUrl.TrimEnd('/');

            // Split into movies and series
            var series = items.Where(i =>
                (string.Equals(i.MediaType, "series", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(i.MediaType, "anime", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrEmpty(i.AioId)).ToList();
            var movies = items.Except(series).ToList();

            _logger.LogInformation("[Write] Processing {Movies} movies (parallel) + {Series} series (bounded)",
                movies.Count, series.Count);

            var written = 0;
            var errors = 0;

            // ── Movies: parallel (cheap local I/O, max 6 concurrent) ──────────
            if (movies.Count > 0)
            {
                using var movieGate = new SemaphoreSlim(6);
                var movieResults = new ConcurrentBag<(bool success, CatalogItem item)>();

                var movieTasks = movies.Select(item => ProcessMovieItemAsync(
                    item, config, embyBaseUrl, movieGate, movieResults, cancellationToken));
                await Task.WhenAll(movieTasks);

                foreach (var (success, item) in movieResults)
                {
                    if (success) written++;
                    else errors++;
                }
            }

            // ── Series: bounded parallel (network-bound episode fetches, max 2 concurrent) ──
            if (series.Count > 0)
            {
                using var seriesGate = new SemaphoreSlim(2);
                var seriesResults = new ConcurrentBag<(bool success, CatalogItem item)>();

                var seriesTasks = series.Select(item => ProcessSeriesItemAsync(
                    item, config, seriesGate, seriesResults, cancellationToken));
                await Task.WhenAll(seriesTasks);

                foreach (var (success, item) in seriesResults)
                {
                    if (success) written++;
                    else errors++;
                }
            }

            if (errors > 0)
                _logger.LogWarning("[InfiniteDrive] WriteStep: {Errors} items skipped due to errors out of {Total}",
                    errors, items.Count);

            return written;
        }

        private async Task ProcessMovieItemAsync(
            CatalogItem item,
            PluginConfiguration config,
            string embyBaseUrl,
            SemaphoreSlim gate,
            ConcurrentBag<(bool, CatalogItem)> results,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var itemSw = System.Diagnostics.Stopwatch.StartNew();
                var folderName = NamingPolicyService.BuildFolderName(item);
                var basePath = GetLibraryPath(config, item.MediaType);
                var folderPath = Path.Combine(basePath, folderName);

                Directory.CreateDirectory(folderPath);

                var baseName = Path.GetFileNameWithoutExtension(folderName);
                var strmUrl = StrmWriterService.BuildSignedStrmUrl(config, item.AioId ?? "", "imdb", null, null);
                var fileName = baseName + ".strm";
                var fullPath = Path.Combine(folderPath, fileName);
                var tmpPath = fullPath + ".tmp";

                try
                {
                    await File.WriteAllTextAsync(tmpPath, strmUrl, new UTF8Encoding(false));
                    File.Move(tmpPath, fullPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Failed to write .strm: {Path}", fullPath);
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }

                item.ItemState = ItemState.Written;
                item.StrmPath = folderPath;
                item.LocalPath = folderPath;
                item.LocalSource = "strm";
                item.StrmTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(config.SignatureValidityDays).ToUnixTimeSeconds();
                item.UpdatedAt = DateTime.UtcNow.ToString("o");
                await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);

                _logger.LogDebug("[Write] Movie {AioId} ({Title}) written in {Ms}ms", item.AioId, item.Title, itemSw.ElapsedMilliseconds);
                results.Add((true, item));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] WriteStep: skipping movie {AioId} ({Title})", item.AioId, item.Title);
                results.Add((false, item));
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task ProcessSeriesItemAsync(
            CatalogItem item,
            PluginConfiguration config,
            SemaphoreSlim gate,
            ConcurrentBag<(bool, CatalogItem)> results,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var itemSw = System.Diagnostics.Stopwatch.StartNew();
                var folderName = NamingPolicyService.BuildFolderName(item);
                var basePath = GetLibraryPath(config, item.MediaType);

                _logger.LogDebug("[Write] Fetching episodes for {AioId} ({Title})", item.AioId, item.Title);
                var aioVideos = await FetchAioVideosAsync(item, cancellationToken);

                if (aioVideos == null || aioVideos.Count == 0)
                {
                    _logger.LogDebug(
                        "[InfiniteDrive] Series {AioId} ({Title}) — no episode metadata, skipping",
                        item.AioId, item.Title);
                    results.Add((false, item));
                    return;
                }

                // Diff-before-write: if already expanded, check for new episodes before doing I/O
                const int ReExpansionIntervalSec = 6 * 3600;
                var previousVideosJson = item.VideosJson;
                item.VideosJson = EpisodeDiffService.SerializeForStorage(aioVideos);

                if (item.EpisodesExpanded == true)
                {
                    var diff = EpisodeDiffService.DiffEpisodes(previousVideosJson, aioVideos);
                    if (diff.AddedEpisodes.Count == 0)
                    {
                        // No new episodes — just update timestamp with fresh jitter and skip file I/O
                        var jitterSec = Random.Shared.Next(0, ReExpansionIntervalSec);
                        item.LastExpandedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - jitterSec;
                        item.UpdatedAt = DateTime.UtcNow.ToString("o");
                        await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                        _logger.LogDebug(
                            "[InfiniteDrive] Series {AioId} ({Title}) — re-expansion: no new episodes, timestamp updated",
                            item.AioId, item.Title);
                        results.Add((true, item));
                        return;
                    }

                    _logger.LogInformation(
                        "[InfiniteDrive] Series {AioId} ({Title}) — re-expansion: {Count} new episodes found",
                        item.AioId, item.Title, diff.AddedEpisodes.Count);
                }

                var strm = Plugin.Instance?.StrmWriterService;
                if (strm == null)
                {
                    results.Add((false, item));
                    return;
                }

                var episodesWritten = await strm.WriteEpisodesFromVideosJsonAsync(item, config, cancellationToken);
                if (episodesWritten > 0)
                {
                    item.EpisodesExpanded = true;
                    // Stagger re-expansion: set timestamp randomly in the past (0 to 6h)
                    // so titles don't all re-expand at the same 6-hour mark
                    var jitterSec = Random.Shared.Next(0, ReExpansionIntervalSec);
                    item.LastExpandedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - jitterSec;
                    item.ItemState = ItemState.Written;
                    item.StrmPath = Path.Combine(basePath, folderName);
                    item.LocalPath = item.StrmPath;
                    item.LocalSource = "strm";
                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                    _logger.LogInformation(
                        "[InfiniteDrive] Episode expansion for {AioId} ({Title}) - {Count} episodes in {Ms}ms",
                        item.AioId, item.Title, episodesWritten, itemSw.ElapsedMilliseconds);
                    results.Add((true, item));
                }
                else
                {
                    results.Add((false, item));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] WriteStep: skipping series {AioId} ({Title})", item.AioId, item.Title);
                results.Add((false, item));
            }
            finally
            {
                gate.Release();
            }
        }

        // ── Step 3: Hint ────────────────────────────────────────────────────────

        private async Task<int> HintStepAsync(List<CatalogItem> items, CancellationToken cancellationToken)
        {
            var hinted = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Skip hint for series/anime items — handled during WriteStepAsync
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
                    // Update NFO status
                    item.NfoStatus = "Hinted";
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                    hinted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[InfiniteDrive] HintStep: skipping item {AioId} ({Title}) due to error",
                        item.AioId, item.Title);
                }
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
                    AioId = row.IsDBNull(1) ? null : row.GetString(1),
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
                AioId = ci.AioId,
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

                        try
                        {
                            // Skip series/anime items that haven't completed episode expansion
                            bool isSeries = string.Equals(item.MediaType, "series", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase);
                            if (isSeries && item.EpisodesExpanded != true)
                            {
                                _logger.LogDebug("[InfiniteDrive] Notify: Skipping {AioId} ({Title}) - episodes not fully expanded yet",
                                    item.AioId, item.Title);
                                continue;
                            }

                            item.ItemState = ItemState.Notified;
                            item.UpdatedAt = DateTime.UtcNow.ToString("o");
                            await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                            notified++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "[InfiniteDrive] NotifyStep: skipping item {AioId} ({Title}) due to error",
                                item.AioId, item.Title);
                        }
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
                    _logger.LogDebug("[InfiniteDrive] Verify: Skipping {AioId} ({Title}) - episodes not fully expanded yet",
                        item.AioId, item.Title);
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
                            _logger.LogDebug("[InfiniteDrive] Verify: Confirmed .strm files for {AioId}", item.AioId);
                        }
                        else
                        {
                            // No .strm files found - leave as Notified
                            // Stalled-item promotion will handle items >24h
                            _logger.LogDebug("[InfiniteDrive] Verify: No .strm files for {AioId}, remains Notified", item.AioId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Verify: Failed to verify {AioId}", item.AioId);
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

            var config = Plugin.Instance!.Configuration;

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
                    var strmUrl = StrmWriterService.BuildSignedStrmUrl(config, item.AioId ?? "", "imdb", null, null);
                    var fileName = baseName + ".strm";
                    var fullPath = Path.Combine(folderPath, fileName);
                    var tmpPath = fullPath + ".tmp";

                    await File.WriteAllTextAsync(tmpPath, strmUrl, new UTF8Encoding(false));
                    File.Move(tmpPath, fullPath, overwrite: true);

                    // Update token expiry timestamp
                    item.StrmTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(Plugin.Instance!.Configuration.SignatureValidityDays).ToUnixTimeSeconds();
                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);

                    renewed++;
                    _logger.LogDebug("[InfiniteDrive] Renew: Refreshed token for {AioId}", item.AioId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Renew: Failed to refresh token for {AioId}", item.AioId);
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

                try
                {
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
                            "[InfiniteDrive] Stalled: Promoted {AioId} to NeedsEnrich (Notified >24h)",
                            item.AioId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[InfiniteDrive] PromoteStalled: skipping item {AioId} ({Title}) due to error",
                        item.AioId, item.Title);
                }
            }

            return promoted;
        }

        // ── Helper methods ───────────────────────────────────────────────────────

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
        /// Fetches episode lists from metadata providers.
        /// Tries AIOStreams first, then falls back to Cinemeta (for IMDB IDs)
        /// or Kitsu/AniList APIs (for anime IDs).
        /// Only returns episodes that actually exist — no guessing.
        /// </summary>
        private async Task<List<Services.StremioVideo>?> FetchAioVideosAsync(
            CatalogItem item, CancellationToken cancellationToken)
        {
            var aioId = item.AioId;
            var mediaType = item.MediaType;

            // Try originating manifest first
            var client = BuildClientForManifest(item.SourceManifestUrl);
            if (client != null && client.IsConfigured)
            {
                try
                {
                    client.ActiveCooldownKind = CooldownKind.SeriesMeta;
                    var metaResponse = await client.GetMetaAsyncTyped(mediaType, aioId, cancellationToken);
                    if (metaResponse?.Meta?.Videos != null && metaResponse.Meta.Videos.Count > 0)
                    {
                        var videos = ConvertAioVideos(metaResponse.Meta.Videos);
                        if (videos.Count > 0)
                        {
                            _logger.LogInformation(
                                "[InfiniteDrive] Fetched {Count} episodes from originating manifest for {Id}",
                                videos.Count, aioId);
                            return videos;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "[InfiniteDrive] Originating manifest meta failed for {Id}, trying fallbacks", aioId);
                }
            }

            // Fallback: config-based client (primary → secondary)
            var config = Plugin.Instance?.Configuration;
            if (config != null)
            {
                var fallbackClient = AioStreamsClientFactory.Create(_logger);
                if (fallbackClient.IsConfigured)
                {
                    try
                    {
                        fallbackClient.ActiveCooldownKind = CooldownKind.SeriesMeta;
                        var metaResponse = await fallbackClient.GetMetaAsyncTyped(mediaType, aioId, cancellationToken);
                        if (metaResponse?.Meta?.Videos != null && metaResponse.Meta.Videos.Count > 0)
                        {
                            var videos = ConvertAioVideos(metaResponse.Meta.Videos);
                            if (videos.Count > 0)
                            {
                                _logger.LogInformation(
                                    "[InfiniteDrive] Fetched {Count} episodes from config-fallback manifest for {Id}",
                                    videos.Count, aioId);
                                return videos;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[InfiniteDrive] Config-fallback manifest meta failed for {Id}", aioId);
                    }
                }
            }

            // Fallback: Cinemeta for IMDB IDs
            if (aioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return await FetchCinemetaVideosAsync(aioId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "[InfiniteDrive] Cinemeta meta failed for {Id}", aioId);
                }
            }

            _logger.LogInformation(
                "[InfiniteDrive] No episode metadata available for {Id} ({MediaType})",
                aioId, mediaType);
            return null;
        }

        private AioStreamsClient? BuildClientForManifest(string? manifestUrl)
        {
            if (string.IsNullOrEmpty(manifestUrl)) return null;
            return AioStreamsClientFactory.TryCreateForManifest(manifestUrl, _logger);
        }

        /// <summary>
        /// Fetches episode list from Cinemeta (public Stremio metadata provider).
        /// Only used for IMDB IDs. Returns actual episode data — no guessing.
        /// </summary>
        private async Task<List<Services.StremioVideo>?> FetchCinemetaVideosAsync(
            string aioId, CancellationToken cancellationToken)
        {
            var url = $"https://v3-cinemeta.strem.io/meta/series/{Uri.EscapeDataString(aioId)}.json";
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var json = await http.GetStringAsync(url, cancellationToken);

            if (string.IsNullOrEmpty(json))
                return null;

            var metaResponse = System.Text.Json.JsonSerializer.Deserialize<Models.AioMetaResponse>(json);
            if (metaResponse?.Meta?.Videos == null || metaResponse.Meta.Videos.Count == 0)
                return null;

            var videos = ConvertAioVideos(metaResponse.Meta.Videos);
            if (videos.Count > 0)
            {
                _logger.LogInformation(
                    "[InfiniteDrive] Fetched {Count} episodes from Cinemeta for {Id}",
                    videos.Count, aioId);
            }
            return videos;
        }

        private static List<Services.StremioVideo> ConvertAioVideos(List<Models.AioVideo> aioVideos)
        {
            var videos = new List<Services.StremioVideo>();
            foreach (var aioVideo in aioVideos)
            {
                if (aioVideo.Season.HasValue && aioVideo.Episode.HasValue && aioVideo.Season.Value > 0)
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
            return videos;
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
        private static string GenerateDeterministicId(string aioId, string source)
        {
            // Use a hash of (aio_id + source) to create a deterministic ID
            // Format: {first 8 chars of hash}-{aio_id}
            using var hash = System.Security.Cryptography.MD5.Create();
            var input = $"{aioId}:{source}";
            var hashBytes = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            var hashString = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
            return $"{hashString}-{aioId}";
        }
    }
}
