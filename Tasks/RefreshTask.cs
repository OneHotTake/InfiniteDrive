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
using MediaBrowser.Controller.Providers;
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
            // CatalogSyncTask is the sole upstream catalog reader.  Marvin runs it in
            // parallel with Populate, so this method deliberately consumes only the
            // durable queue.  Fetching the manifest here used to bypass the provider's
            // allowlist, item caps, backup toggle, and interval guard on every 5-second
            // poll, causing an unbounded duplicate crawl.
            var existingItems = await Plugin.Instance!.DatabaseManager.GetActiveCatalogItemsAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var work = SelectPopulateWork(existingItems, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var queuedCount = work.Count(i => string.IsNullOrEmpty(i.StrmPath));
            var expansionCount = work.Count - queuedCount;

            if (queuedCount > 0)
                _logger.LogInformation("[InfiniteDrive] Found {Count} queued items without .strm files", queuedCount);
            if (expansionCount > 0)
                _logger.LogInformation("[InfiniteDrive] Found {Count} series items needing episode expansion", expansionCount);

            return work;
        }

        internal static List<CatalogItem> SelectPopulateWork(
            IEnumerable<CatalogItem> items,
            long nowUnixSeconds)
        {
            const int ReExpansionIntervalSec = 6 * 3600;

            return items
                .Where(i =>
                    (i.ItemState == ItemState.Queued && string.IsNullOrEmpty(i.StrmPath))
                    || ((string.Equals(i.MediaType, "series", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(i.MediaType, "anime", StringComparison.OrdinalIgnoreCase))
                        && !string.IsNullOrEmpty(i.AioId)
                        && !string.IsNullOrEmpty(i.StrmPath)
                        && (i.EpisodesExpanded != true
                            || i.LastExpandedAt == null
                            || nowUnixSeconds - i.LastExpandedAt >= ReExpansionIntervalSec)))
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        // ── Step 2: Write ────────────────────────────────────────────────────────

        private async Task<int> WriteStepAsync(List<CatalogItem> items, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;

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
                    item, config, movieGate, movieResults, cancellationToken));
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

                // ── Multi-version STRM prewriting ─────────────────────────────
                List<SelectedVersion>? versions = null;

                // Attempt to fetch live streams from AIOStreams
                try
                {
                    using var client = AioStreamsClientFactory.Create(_logger);
                    client.Cooldown = Plugin.Instance?.CooldownGate;
                    if (client.IsConfigured)
                    {
                        var response = await client.GetMovieStreamsAsync(
                            item.AioId ?? "", cancellationToken).ConfigureAwait(false);

                        if (response?.Streams?.Count > 0)
                        {
                            var parsed = StreamParser.ParseAll(response.Streams);
                            _logger.LogInformation(
                                "[Write] Movie {AioId} ({Title}): AIOStreams returned {Raw} streams, {Playable} playable",
                                item.AioId, item.Title, response.Streams.Count, parsed.Count);

                            if (parsed.Count > 0)
                            {
                                versions = VersionSelectorService.SelectBestVersions(
                                    parsed,
                                    config.DesiredVersions,
                                    RuntimePolicy.EmbyVersionLimit,
                                    config);
                            }
                        }
                        else
                        {
                            _logger.LogInformation(
                                "[Write] Movie {AioId} ({Title}): AIOStreams returned 0 streams (no debrid cache)",
                                item.AioId, item.Title);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Write] Stream fetch failed for {AioId}, falling back to resolve URL", item.AioId);
                }

                var fileManager = Plugin.Instance?.StrmFileManager;
                var folderBareName = Path.GetFileName(folderName);
                var written = 0;

                if (versions != null && versions.Count > 0 && fileManager != null)
                {
                    // Write multi-version .strm files with direct CDN URLs
                    written = await fileManager.WriteOrReplaceStrmFilesAsync(
                        folderPath, folderBareName, versions, cancellationToken);

                    item.SelectedVersionsJson = StrmFileManager.SerializeVersions(versions);
                    item.LastVersionRefreshAt = DateTime.UtcNow.ToString("o");
                }

                if (written > 0)
                {
                    item.ItemState = ItemState.Written;
                    item.StrmPath = folderPath;
                    item.LocalPath = folderPath;
                    item.LocalSource = "strm";
                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);

                    _logger.LogDebug("[Write] Movie {AioId} ({Title}) written {Versions} versions in {Ms}ms",
                        item.AioId, item.Title, written, itemSw.ElapsedMilliseconds);
                    results.Add((true, item));
                    QueueItemRefresh(folderPath);
                }
                else
                {
                    _logger.LogDebug("[Write] Movie {AioId} ({Title}) skipped — no streams available", item.AioId, item.Title);
                    results.Add((false, item));
                }
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

                // ── Multi-version STRM: per-episode parallel resolution ──────────
                var fileManager = Plugin.Instance?.StrmFileManager;
                var seriesPath = Path.Combine(basePath, folderName);
                var episodesWritten = 0;
                var representativeVersions = new List<SelectedVersion>();
                var realEpisodes = aioVideos
                    .Where(v => v.Season.HasValue && v.Season.Value > 0 && v.Episode.HasValue)
                    .ToList();

                try
                {
                    using var client = AioStreamsClientFactory.Create(_logger);
                    client.Cooldown = Plugin.Instance?.CooldownGate;

                    if (client.IsConfigured && fileManager != null && realEpisodes.Count > 0)
                    {
                        var concurrency = config.MaxConcurrentResolutions;
                        using var episodeGate = new SemaphoreSlim(concurrency, concurrency);
                        var resolved = 0;
                        var syncLock = new object();
                        var latestRep = (Season: 0, Ep: 0, Versions: (List<SelectedVersion>?)null);

                        var tasks = realEpisodes.Select(async ep =>
                        {
                            try
                            {
                                await episodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }

                            try
                            {
                                var (written, versions) = await ResolveAndWriteEpisodeAsync(
                                    client, fileManager, item, seriesPath,
                                    ep.Season!.Value, ep.Episode!.Value,
                                    config, cancellationToken).ConfigureAwait(false);

                                if (written > 0)
                                {
                                    lock (syncLock)
                                    {
                                        episodesWritten += written;
                                        resolved++;
                                        if (ep.Season!.Value > latestRep.Season ||
                                            (ep.Season!.Value == latestRep.Season && ep.Episode!.Value > latestRep.Ep))
                                        {
                                            latestRep = (ep.Season!.Value, ep.Episode!.Value, versions);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex,
                                    "[Write] Series {AioId}: S{S}E{E} — resolve failed",
                                    item.AioId, ep.Season, ep.Episode);
                            }
                            finally
                            {
                                episodeGate.Release();
                            }
                        }).ToList();

                        await Task.WhenAll(tasks).ConfigureAwait(false);

                        representativeVersions = latestRep.Versions ?? new List<SelectedVersion>();

                        _logger.LogInformation(
                            "[Write] Series {AioId} ({Title}): per-episode resolution: {Resolved}/{Total} episodes resolved",
                            item.AioId, item.Title, resolved, realEpisodes.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Write] AIOStreams client failed for series {AioId}", item.AioId);
                }

                if (episodesWritten > 0)
                {
                    item.EpisodesExpanded = true;
                    var jitterSec = Random.Shared.Next(0, ReExpansionIntervalSec);
                    item.LastExpandedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - jitterSec;
                    item.ItemState = ItemState.Written;
                    item.StrmPath = seriesPath;
                    item.LocalPath = seriesPath;
                    item.LocalSource = "strm";

                    if (representativeVersions.Count > 0)
                    {
                        item.SelectedVersionsJson = StrmFileManager.SerializeVersions(representativeVersions);
                        item.LastVersionRefreshAt = DateTime.UtcNow.ToString("o");
                    }

                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                    _logger.LogInformation(
                        "[InfiniteDrive] Episode expansion for {AioId} ({Title}) - {Count} episodes in {Ms}ms",
                        item.AioId, item.Title, episodesWritten, itemSw.ElapsedMilliseconds);
                    results.Add((true, item));
                    QueueItemRefresh(seriesPath);
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

        private async Task<(int written, List<SelectedVersion>? versions)> ResolveAndWriteEpisodeAsync(
            AioStreamsClient client,
            StrmFileManager fileManager,
            CatalogItem item,
            string seriesPath,
            int season,
            int episode,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var response = await client.GetSeriesStreamsAsync(
                item.AioId ?? "", season, episode, cancellationToken).ConfigureAwait(false);

            if (response?.Streams == null || response.Streams.Count == 0)
            {
                _logger.LogDebug(
                    "[Write] Series {AioId}: S{S}E{E} — 0 streams from AIOStreams",
                    item.AioId, season, episode);
                return (0, null);
            }

            var parsed = StreamParser.ParseAll(response.Streams);
            if (parsed.Count == 0)
            {
                _logger.LogDebug(
                    "[Write] Series {AioId}: S{S}E{E} — {Raw} raw streams, 0 playable after filtering",
                    item.AioId, season, episode, response.Streams.Count);
                return (0, null);
            }

            var versions = VersionSelectorService.SelectBestVersions(
                parsed, config.DesiredVersions, RuntimePolicy.EmbyVersionLimit, config);
            if (versions.Count == 0) return (0, null);


            var seasonDir = Path.Combine(seriesPath, $"Season {season:D2}");
            var epBaseName = NamingPolicyService.BuildStrmFileName(item, season, episode);
            var epBaseNameNoExt = Path.GetFileNameWithoutExtension(epBaseName);

            var written = await fileManager.WriteOrReplaceStrmFilesAsync(
                seasonDir, epBaseNameNoExt, versions, cancellationToken).ConfigureAwait(false);

            return (written, versions);
        }

        private void QueueItemRefresh(string itemPath)
        {
            var providerManager = Plugin.Instance?.ProviderManager;
            var fileSystem      = Plugin.Instance?.FileSystem;
            if (providerManager == null || fileSystem == null) return;

            var embyItem = _libraryManager.FindByPath(itemPath, false)
                        ?? _libraryManager.FindByPath(itemPath, true);
            if (embyItem == null) return;

            try
            {
                var options = new MetadataRefreshOptions(fileSystem);
                providerManager.QueueRefresh(embyItem.InternalId, options, RefreshPriority.Normal);
                _logger.LogDebug("[Write] Queued Emby refresh for {Path}", itemPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Write] Failed to queue refresh for {Path}", itemPath);
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
                        item.EnrichmentStatus = "Expanded";
                        await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
                        hinted++;
                        continue;
                    }

                    // NFO no longer needed — folder name provides ID hints to Emby
                    // Update enrichment status
                    item.EnrichmentStatus = "Hinted";
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

            // Query no-ID items from this run only (added_at >= runStartedAt AND enrichment_status = 'NeedsEnrich')
            var noIdItemsQuery = @"
                SELECT * FROM catalog_items
                WHERE enrichment_status = 'NeedsEnrich'
                AND added_at >= @runStartedAt
                AND (aio_id IS NULL OR aio_id = '')
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
                    UniqueIdsJson = row.IsDBNull(17) ? null : row.GetString(17),
                    EnrichmentStatus = row.IsDBNull(18) ? null : row.GetString(18),
                    RetryCount = row.GetInt(19),
                    NextRetryAt = row.IsDBNull(20) ? (long?)null : row.GetInt64(20),
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
                return 0;
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
                        item.EnrichmentStatus = "NeedsEnrich";
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

    }
}
