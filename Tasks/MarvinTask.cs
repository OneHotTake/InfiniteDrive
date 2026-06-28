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
using MediaBrowser.Controller.Entities;
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
        private readonly IUserManager          _userManager;
        private readonly ILogManager           _logManager;

        private static readonly SemaphoreSlim _runningGate = new(1, 1);

        // ── Constructor ──────────────────────────────────────────────────────────

        public MarvinTask(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IUserManager userManager)
        {
            _logger         = new EmbyLoggerAdapter<MarvinTask>(logManager.GetLogger("InfiniteDrive"));
            _libraryManager = libraryManager;
            _userManager    = userManager;
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
                var syncTask = new CatalogSyncTask(_libraryManager, _userManager, _logManager)
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

                // ── Phase 3b: Version Refresh (80-85%) ──────────────────────
                Plugin.Pipeline.SetPhase("Marvin", "VersionRefresh");
                _logger.LogInformation("[InfiniteDrive] Marvin Phase 3b: Multi-Version Refresh");
                phaseSw.Restart();
                await VersionRefreshPassAsync(cancellationToken);
                _logger.LogDebug("[Marvin] Phase 3b (VersionRefresh) completed in {Ms}ms", phaseSw.ElapsedMilliseconds);

                progress?.Report(0.85);

                // ── Phase 3c: Collection Population (85-87%) ─────────────────
                Plugin.Pipeline.SetPhase("Marvin", "CollectionPopulation");
                _logger.LogInformation("[InfiniteDrive] Marvin Phase 3c: Collection Population");
                phaseSw.Restart();
                await CollectionPopulationPassAsync(cancellationToken);
                _logger.LogDebug("[Marvin] Phase 3c (CollectionPopulation) completed in {Ms}ms", phaseSw.ElapsedMilliseconds);

                progress?.Report(0.87);

                // ── Phase 4: Repair (87-100%) ────────────────────────────────
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

        // ── Phase 3c: Collection Population ────────────────────────────────────

        /// <summary>
        /// Resolves pending collection_membership rows (emby_item_id IS NULL)
        /// by looking up the Emby item via provider ID, then adding it to the
        /// appropriate BoxSet (user_id NULL) or playlist (user_id non-NULL).
        /// </summary>
        private async Task CollectionPopulationPassAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return;

            var pending = await db.GetPendingCollectionMembershipsAsync(cancellationToken);
            if (pending.Count == 0) return;

            _logger.LogInformation("[CollectionPopulation] Resolving {Count} pending memberships", pending.Count);

            var libraryManager = Plugin.Instance?.LibraryManager;
            var boxSetService = Plugin.Instance?.BoxSetService;
            if (libraryManager == null) return;

            int resolved = 0;
            foreach (var row in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Look up the catalog item to get provider IDs
                var catalogItem = await db.GetCatalogItemByAioIdAsync(row.AioId);
                if (catalogItem == null) continue;

                // Try to find Emby item via provider ID lookup
                string? embyItemId = null;
                try
                {
                    // Build provider ID pairs from CatalogItem's known IDs
                    var providerIds = new List<KeyValuePair<string, string>>();
                    if (!string.IsNullOrEmpty(catalogItem.TmdbId))
                        providerIds.Add(new KeyValuePair<string, string>("Tmdb", catalogItem.TmdbId));
                    if (!string.IsNullOrEmpty(catalogItem.AioId) && catalogItem.AioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                        providerIds.Add(new KeyValuePair<string, string>("Imdb", catalogItem.AioId));

                    // Also try UniqueIdsJson if available
                    if (!string.IsNullOrEmpty(catalogItem.UniqueIdsJson) && providerIds.Count == 0)
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(catalogItem.UniqueIdsJson);
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                var prov = el.TryGetProperty("provider", out var p) ? p.GetString() : null;
                                var val = el.TryGetProperty("id", out var v) ? v.GetString() : null;
                                if (!string.IsNullOrEmpty(prov) && !string.IsNullOrEmpty(val))
                                    providerIds.Add(new KeyValuePair<string, string>(prov, val));
                            }
                        }
                        catch { /* best effort */ }
                    }

                    if (providerIds.Count > 0)
                    {
                        var query = new InternalItemsQuery
                        {
                            AnyProviderIdEquals = providerIds,
                            IncludeItemTypes = new[] { "Movie", "Series" },
                            Limit = 1,
                        };
                        var results = libraryManager.GetItemList(query);
                        if (results != null && results.Length > 0 && results[0] != null)
                            embyItemId = results[0]!.Id.ToString("N");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[CollectionPopulation] Provider lookup failed for {AioId}", row.AioId);
                }

                if (embyItemId == null) continue; // will retry next cycle

                // Add to BoxSet or playlist
                try
                {
                    if (row.UserId == null)
                    {
                        // BoxSet membership
                        if (boxSetService != null)
                        {
                            var boxSet = await boxSetService.FindOrCreateBoxSetAsync(row.CollectionName, cancellationToken);
                            if (boxSet != null && Guid.TryParse(embyItemId, out var itemId))
                            {
                                await boxSetService.AddItemToBoxSetAsync(boxSet.Id, itemId, cancellationToken);
                            }
                        }
                    }
                    else
                    {
                        // Playlist membership
                        var playlistService = Plugin.Instance?.PlaylistService;
                        if (playlistService != null && Guid.TryParse(embyItemId, out var itemId))
                        {
                            await playlistService.AddItemToPlaylistAsync(row.CollectionName, itemId, row.UserId, cancellationToken);
                        }
                    }

                    // Update emby_item_id in membership row
                    await db.UpdateCollectionMembershipEmbyItemIdAsync(row.Id, embyItemId, cancellationToken);
                    resolved++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[CollectionPopulation] Failed to add {AioId} to {Collection}", row.AioId, row.CollectionName);
                }
            }

            if (resolved > 0)
                _logger.LogInformation("[CollectionPopulation] Resolved {Resolved}/{Total} pending memberships", resolved, pending.Count);
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

        // ── Phase 3b: Multi-Version Refresh ──────────────────────────────────

        /// <summary>
        /// Re-fetches streams for items with stale version selections and upgrades
        /// .strm files when better alternatives are available. This enables the
        /// "new releases improve over time" behaviour as more/better streams appear.
        /// </summary>
        private async Task VersionRefreshPassAsync(CancellationToken cancellationToken)
        {
            var db = Plugin.Instance?.DatabaseManager;
            var config = Plugin.Instance?.Configuration;
            var fileManager = Plugin.Instance?.StrmFileManager;
            if (db == null || config == null || fileManager == null) return;

            // Get all Written items that have version data (or are eligible for first versioning)
            var activeItems = await db.GetActiveCatalogItemsAsync();
            var candidates = activeItems
                .Where(i => i.ItemState == ItemState.Written
                         && !string.IsNullOrEmpty(i.StrmPath)
                         && !string.IsNullOrEmpty(i.AioId))
                .ToList();

            if (candidates.Count == 0) return;

            _logger.LogInformation("[VersionRefresh] Checking {Count} items for version upgrades", candidates.Count);

            var upgraded = 0;
            var checked_ = 0;

            using var client = Services.AioStreamsClientFactory.Create(_logger);
            client.Cooldown = Plugin.Instance?.CooldownGate;
            if (!client.IsConfigured) return;

            // Process in small batches to avoid API rate limits
            using var gate = new SemaphoreSlim(2);
            var tasks = candidates.Select(item => RefreshItemVersionsAsync(
                item, client, fileManager, config, db, gate, cancellationToken));

            var results = await Task.WhenAll(tasks);
            foreach (var (wasChecked, wasUpgraded) in results)
            {
                if (wasChecked) checked_++;
                if (wasUpgraded) upgraded++;
            }

            if (upgraded > 0)
                _logger.LogInformation("[VersionRefresh] Upgraded {Upgraded}/{Checked} items", upgraded, checked_);
            else
                _logger.LogDebug("[VersionRefresh] No upgrades needed across {Checked} items", checked_);
        }

        private async Task<(bool checked_, bool upgraded)> RefreshItemVersionsAsync(
            CatalogItem item,
            Services.AioStreamsClient client,
            Services.StrmFileManager fileManager,
            PluginConfiguration config,
            Data.DatabaseManager db,
            SemaphoreSlim gate,
            CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                // Throttle: only refresh items older than 1 hour since last refresh
                if (!string.IsNullOrEmpty(item.LastVersionRefreshAt)
                    && DateTime.TryParse(item.LastVersionRefreshAt, out var lastRefresh)
                    && (DateTime.UtcNow - lastRefresh) < TimeSpan.FromHours(1))
                    return (false, false);

                // Fetch fresh streams
                List<SelectedVersion> newVersions;

                if (item.MediaType == "series" || item.MediaType == "anime")
                {
                    // For series: fetch streams per actual episode and write versioned .strm for each
                    newVersions = await RefreshSeriesVersionsAsync(
                        item, client, fileManager, config, ct);
                }
                else
                {
                    // Movies: single fetch
                    var response = await client.GetMovieStreamsAsync(
                        item.AioId, ct).ConfigureAwait(false);

                    if (response?.Streams == null || response.Streams.Count == 0)
                        return (true, false);

                    var parsed = Services.StreamParser.ParseAll(response.Streams);
                    if (parsed.Count == 0) return (true, false);

                    newVersions = Services.VersionSelectorService.SelectBestVersions(
                        parsed, config.DesiredVersions, config.MaxVersionsPerItem, config);
                    Services.VersionSelectorService.AssignSecondaryUrls(newVersions, parsed);
                }

                if (newVersions.Count == 0)
                    return (true, false);

                // ── Stream list comparison healing ──────────────────────────────
                // Check stored URLs against fresh stream set. If primary is dead
                // but secondary is alive, swap. If both dead, fall through to full refresh.
                var storedVersions = Services.StrmFileManager.DeserializeVersions(item.SelectedVersionsJson);
                if (storedVersions.Count > 0)
                {
                    var freshUrls = new HashSet<string>(
                        newVersions.Select(v => v.Stream.Url), StringComparer.OrdinalIgnoreCase);

                    var healed = false;
                    foreach (var sv in storedVersions)
                    {
                        var primaryAlive = !string.IsNullOrEmpty(sv.Url) && freshUrls.Contains(sv.Url);
                        var secondaryAlive = !string.IsNullOrEmpty(sv.SecondaryUrl) && freshUrls.Contains(sv.SecondaryUrl);

                        if (primaryAlive) continue; // Both alive or primary alive — no action

                        if (!primaryAlive && secondaryAlive)
                        {
                            // Swap: secondary → primary
                            var oldSecondary = sv.SecondaryUrl;
                            sv.Url = oldSecondary!;
                            sv.SecondaryUrl = null;

                            // Try to find a new secondary from the fresh URL pool
                            // (prefer same resolution, exclude all currently-used URLs)
                            var claimedUrls = new HashSet<string>(
                                storedVersions.Where(v => !string.IsNullOrEmpty(v.Url)).Select(v => v.Url!),
                                StringComparer.OrdinalIgnoreCase);
                            claimedUrls.Add(sv.Url);
                            foreach (var v in storedVersions)
                                if (!string.IsNullOrEmpty(v.SecondaryUrl)) claimedUrls.Add(v.SecondaryUrl);

                            var newSecondary = newVersions.FirstOrDefault(nv =>
                                !claimedUrls.Contains(nv.Stream.Url))?.Stream.Url;
                            if (newSecondary != null)
                            {
                                sv.SecondaryUrl = newSecondary;
                                _logger.LogInformation(
                                    "[VersionRefresh] Healed {AioId}: promoted secondary → primary, assigned new secondary ({StreamKey})",
                                    item.AioId, sv.StreamKey);
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "[VersionRefresh] Healed {AioId}: promoted secondary → primary, no new secondary available ({StreamKey})",
                                    item.AioId, sv.StreamKey);
                            }

                            healed = true;

                            // Rewrite .strm file
                            if (!string.IsNullOrEmpty(item.StrmPath) && !string.IsNullOrEmpty(sv.StreamKey))
                            {
                                try { fileManager.RewriteSingleStrmFile(item.StrmPath, sv.Url); }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "[VersionRefresh] Rewrite failed for {AioId}", item.AioId);
                                }

                                // Update DB
                                try
                                {
                                    await db.UpdateStoredVersionUrlAsync(
                                        item.AioId, sv.StreamKey, sv.Url, null, ct).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "[VersionRefresh] DB update failed for {AioId}", item.AioId);
                                }
                            }
                        }
                        // Both dead → fall through to full ShouldReplace check below
                    }

                    if (healed)
                    {
                        // Update the stored JSON with healed versions
                        item.SelectedVersionsJson = Services.StrmFileManager.SerializeVersions(
                            storedVersions.Select(sv => new SelectedVersion
                            {
                                Stream = new ParsedStream
                                {
                                    Url = sv.Url,
                                    Resolution = sv.Resolution,
                                    AudioPretty = sv.AudioPretty,
                                    AudioGroup = "Any",
                                    SourceTag = sv.SourceTag,
                                    SizeGiB = sv.SizeGiB,
                                    RankScore = sv.RankScore,
                                    StreamKey = sv.StreamKey,
                                },
                                SecondaryUrl = sv.SecondaryUrl,
                                VersionLabel = sv.VersionLabel,
                                SelectedScore = sv.RankScore,
                            }).ToList());
                        item.LastVersionRefreshAt = DateTime.UtcNow.ToString("o");
                        item.UpdatedAt = DateTime.UtcNow.ToString("o");
                        await db.UpsertCatalogItemAsync(item, ct).ConfigureAwait(false);
                    }
                }

                // Compare against stored versions — clean direct comparison, no reconstruction
                if (!Services.VersionSelectorService.ShouldReplace(storedVersions, newVersions))
                    return (true, false);

                // Upgrade: write new version files
                var folderName = Services.NamingPolicyService.BuildFolderName(item);
                var folderPath = System.IO.Path.GetDirectoryName(item.StrmPath)!;
                var folderBareName = System.IO.Path.GetFileName(folderName);

                var written = await fileManager.WriteOrReplaceStrmFilesAsync(
                    folderPath, folderBareName, newVersions, ct);

                if (written > 0)
                {
                    item.SelectedVersionsJson = Services.StrmFileManager.SerializeVersions(newVersions);
                    item.LastVersionRefreshAt = DateTime.UtcNow.ToString("o");
                    item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    await db.UpsertCatalogItemAsync(item, ct);

                    _logger.LogInformation(
                        "[VersionRefresh] Upgraded {AioId} ({Title}): {Count} versions",
                        item.AioId, item.Title, written);
                    return (true, true);
                }

                return (true, false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VersionRefresh] Non-fatal error for {AioId}", item.AioId);
                return (false, false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Refreshes multi-version .strm files for series/anime by fetching streams
        /// for the most recent episode and writing versioned files into each season dir.
        /// Uses actual episode metadata from VideosJson, not S01E01 guessing.
        /// </summary>
        private async Task<List<SelectedVersion>> RefreshSeriesVersionsAsync(
            CatalogItem item,
            Services.AioStreamsClient client,
            Services.StrmFileManager fileManager,
            PluginConfiguration config,
            CancellationToken ct)
        {
            // Parse actual episodes from stored VideosJson
            var episodeKeys = Services.EpisodeDiffService.ParseVideoKeys(item.VideosJson);
            if (episodeKeys.Count == 0)
                return new();

            // Write versioned .strm files for every episode — no representative gate
            var folderName = Services.NamingPolicyService.BuildFolderName(item);
            var basePath = string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase)
                ? config.SyncPathAnime
                : config.SyncPathShows;
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(item.StrmPath))
                return new();

            var representativeVersions = new List<SelectedVersion>();

            var seasonGroups = episodeKeys.GroupBy(e => e.Season);
            foreach (var seasonGroup in seasonGroups)
            {
                var seasonNum = seasonGroup.Key;
                var seasonDir = System.IO.Path.Combine(item.StrmPath, $"Season {seasonNum:D2}");

                // Write multi-version .strm for each episode in this season
                foreach (var ep in seasonGroup)
                {
                    ct.ThrowIfCancellationRequested();

                    // Fetch streams for this specific episode
                    Services.AioStreamsStreamResponse? epResponse;
                    try
                    {
                        epResponse = await client.GetSeriesStreamsAsync(
                            item.AioId, seasonNum, ep.Episode, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[VersionRefresh] Failed to fetch S{S:D2}E{E:D2} for {AioId}, skipping",
                            seasonNum, ep.Episode, item.AioId);
                        continue;
                    }

                    if (epResponse?.Streams == null || epResponse.Streams.Count == 0)
                        continue;

                    var epParsed = Services.StreamParser.ParseAll(epResponse.Streams);
                    if (epParsed.Count == 0) continue;

                    var epVersions = Services.VersionSelectorService.SelectBestVersions(
                        epParsed, config.DesiredVersions, config.MaxVersionsPerItem, config);
                    Services.VersionSelectorService.AssignSecondaryUrls(epVersions, epParsed);
                    if (epVersions.Count == 0) continue;

                    var epBaseName = Services.NamingPolicyService.BuildStrmFileName(item, seasonNum, ep.Episode);
                    var epBaseNameNoExt = System.IO.Path.GetFileNameWithoutExtension(epBaseName);
                    await fileManager.WriteOrReplaceStrmFilesAsync(
                        seasonDir, epBaseNameNoExt, epVersions, ct);
                    representativeVersions = epVersions;
                }
            }

            return representativeVersions;
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

        private static Task TokenRenewalAsync(CancellationToken cancellationToken)
        {
            // Token renewal removed — HMAC signing infrastructure deleted.
            return Task.CompletedTask;
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

    }
}
