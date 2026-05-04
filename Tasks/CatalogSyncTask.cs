using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Tasks
{

    // ── CatalogSyncTask ─────────────────────────────────────────────────────────

    /// <summary>
    /// Scheduled task that:
    /// <list type="number">
    ///   <item>Fetches catalog items from all enabled providers</item>
    ///   <item>Deduplicates by (imdb_id, source) and upserts into <c>catalog_items</c></item>
    ///   <item>Writes .strm files in batches of 50 (60-second pause between batches)</item>
    ///   <item>Triggers an Emby library scan for the target folders</item>
    /// </list>
    ///
    /// Default schedule: every 30 minutes.
    /// </summary>
    internal class CatalogSyncTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const int    StrmBatchSize    = 42;          // The answer was obvious
        private const int    StrmBatchPauseMs = 60_000;

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<CatalogSyncTask> _logger;
        private readonly ILibraryManager          _libraryManager;
        private readonly ILogManager              _logManager;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="libraryManager"/> and
        /// <paramref name="loggerFactory"/> automatically.
        /// </summary>
        public CatalogSyncTask(
            ILibraryManager  libraryManager,
            ILogManager      logManager)
        {
            _libraryManager = libraryManager;
            _logManager     = logManager;
            _logger         = new EmbyLoggerAdapter<CatalogSyncTask>(logManager.GetLogger("InfiniteDrive"));
        }

        /// <summary>
        /// Internal entry point for MarvinTask to call directly (no jitter, no SyncLock — Marvin holds the lock).
        /// </summary>
        internal async Task RunSyncAsync(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("[InfiniteDrive] CatalogSyncTask started");
            progress?.Report(0);

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[InfiniteDrive] Plugin configuration not available — aborting sync");
                return;
            }

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[InfiniteDrive] DatabaseManager not available — aborting sync");
                return;
            }

            // 1. Build provider list
            Plugin.Pipeline.SetPhase("CatalogSync", "BuildProviders");
            var providers = BuildProviders(config);
            if (providers.Count == 0)
            {
                _logger.LogInformation("[InfiniteDrive] No catalog sources enabled — nothing to sync");
                progress?.Report(100);
                return;
            }

            // 2. Fetch from all providers (with interval guard + health recording)
            Plugin.Pipeline.SetPhase("CatalogSync", "Fetch");
            progress?.Report(5);
            _logger.LogDebug("[InfiniteDrive] Starting FetchFromAllProvidersAsync with {Count} providers", providers.Count);
            var (allItems, fetchedSourceIds, attemptedProviders) = await FetchFromAllProvidersAsync(providers, config, db, cancellationToken);
            _logger.LogInformation(
                "[InfiniteDrive] Fetched {Count} raw catalog items from all sources (attempted {Attempted} providers)",
                allItems.Count, attemptedProviders);

            // 2b. If AIOStreams was just detected as stream-only this run AND Cinemeta wasn't
            //     already scheduled, run Cinemeta immediately (same sync) rather than making
            //     the user wait until the next scheduled sync.
            if (config.EnableCinemetaDefault
                && config.AioStreamsIsStreamOnly
                && !providers.Any(p => p is CinemetaDefaultProvider))
            {
                _logger.LogInformation(
                    "[InfiniteDrive] AIOStreams detected as stream-only — running Cinemeta fallback in this sync");
                var (cinemetaItems, cinemetaIds, _) = await FetchFromAllProvidersAsync(
                    new List<ICatalogProvider> { new CinemetaDefaultProvider() },
                    config, db, cancellationToken);
                allItems.AddRange(cinemetaItems);
                foreach (var kvp in cinemetaIds)
                    fetchedSourceIds[kvp.Key] = kvp.Value;
                _logger.LogInformation(
                    "[InfiniteDrive] Cinemeta fallback returned {Count} items", cinemetaItems.Count);
            }

            // 3. Deduplicate and upsert
            progress?.Report(20);
            cancellationToken.ThrowIfCancellationRequested();

            var deduplicated = DeduplicateItems(allItems);
            _logger.LogInformation("[InfiniteDrive] {Count} items after deduplication", deduplicated.Count);

            await UpsertItemsAsync(db, deduplicated, cancellationToken);
            progress?.Report(40);

            // 3b. Prune items removed from their sources (with safety check)
            await PruneRemovedItemsAsync(db, fetchedSourceIds, attemptedProviders, cancellationToken);

            // 4. Check that Emby libraries cover the sync paths; warn if not
            WarnIfLibrariesMissing(config);

            progress?.Report(90);

            // Sprint 158: Backstop sync for all active user RSS catalogs (Trakt / MDBList).
            try
            {
                var userCatalogs = await db.GetAllActiveUserCatalogsAsync(cancellationToken);
                if (userCatalogs.Count > 0)
                {
                    _logger.LogInformation(
                        "[InfiniteDrive] CatalogSyncTask: syncing {Count} active user catalogs", userCatalogs.Count);
                    var userCatalogSync = new Services.UserCatalogSyncService(
                        _logManager, db, Plugin.Instance!.StrmWriterService, Plugin.Instance.CooldownGate,
                        Plugin.Instance.IdResolverService);
                    using var catalogGate = new SemaphoreSlim(4);
                    var catalogTasks = userCatalogs.Select(uc => Task.Run(async () =>
                    {
                        await catalogGate.WaitAsync(cancellationToken);
                        try
                        {
                            var result = await userCatalogSync.SyncOneAsync(uc.Id, cancellationToken);
                            _logger.LogInformation(
                                "[InfiniteDrive] UserCatalog {Id} ({Name}): ok={Ok} fetched={F} added={A} elapsed={Ms}ms",
                                uc.Id, uc.DisplayName, result.Ok, result.Fetched, result.Added, result.ElapsedMs);
                        }
                        finally
                        {
                            catalogGate.Release();
                        }
                    }, cancellationToken));
                    await Task.WhenAll(catalogTasks);
                }
            }
            catch (OperationCanceledException) { /* swallow — task was cancelled */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] CatalogSyncTask: error syncing user catalogs (non-fatal)");
            }

            // 5. Write ID type census from all active catalog items
            await WriteIdTypeCensusAsync(db, config);

            progress?.Report(100);

            _logger.LogInformation("[InfiniteDrive] CatalogSyncTask complete");
        }

        // ── Private: ID type census ────────────────────────────────────────────

        private static async Task WriteIdTypeCensusAsync(Data.DatabaseManager db, PluginConfiguration config)
        {
            try
            {
                var items = await db.GetActiveCatalogItemsAsync();
                var census = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.UniqueIdsJson)) continue;
                    try
                    {
                        var ids = db.ParseUniqueIdsJson(item.UniqueIdsJson);
                        foreach (var kvp in ids)
                        {
                            if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                            {
                                if (!census.TryGetValue(kvp.Key, out var count))
                                    count = 0;
                                census[kvp.Key] = count + 1;
                            }
                        }
                    }
                    catch { /* malformed JSON — skip */ }
                }

                if (census.Count == 0) return;

                // Sort by count descending
                var sorted = census
                    .OrderByDescending(kvp => kvp.Value)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

                config.MetadataIdTypeCensus = JsonSerializer.Serialize(sorted);

                // Auto-opt-in: if enabled types is empty, populate with all non-native types
                if (string.IsNullOrWhiteSpace(config.MetadataEnabledIdTypes) || config.MetadataEnabledIdTypes == "[]")
                {
                    var native = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IMDB", "TMDB", "TVDB" };
                    var autoTypes = sorted.Keys.Where(k => !native.Contains(k)).ToList();
                    if (autoTypes.Count > 0)
                        config.MetadataEnabledIdTypes = JsonSerializer.Serialize(autoTypes);
                }

                Plugin.Instance?.SaveConfiguration();
            }
            catch (Exception)
            {
                // Non-fatal — census is best-effort
            }
        }

        // ── Private: provider management ───────────────────────────────────────

        private static List<ICatalogProvider> BuildProviders(PluginConfiguration config)
        {
            var list = new List<ICatalogProvider>();
            if (config.EnableAioStreamsCatalog)
            {
                list.Add(new AioStreamsCatalogProvider());
                if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                    list.Add(new AioStreamsCatalogProvider(config.SecondaryManifestUrl));
            }

            // DEFAULT-CATALOG: auto-inject Cinemeta when no catalog source is available.
            //
            // Triggers when ALL of:
            //   (a) AIOStreams either has no URL configured, or is known to be
            //       stream-only (detected on the previous sync run, e.g. DuckKota).
            //
            // This ensures users always have Top Movies and Top Series in their
            // library even before they configure a proper catalog source — "fail
            // in setup, not on family night."
            if (config.EnableCinemetaDefault)
            {
                var aioStreamsProvidesItems = config.EnableAioStreamsCatalog
                    && !string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
                    && !config.AioStreamsIsStreamOnly;

                if (!aioStreamsProvidesItems)
                    list.Add(new CinemetaDefaultProvider());
            }

            return list;
        }

        /// <summary>
        /// Fetches catalog items from all providers.
        /// Returns the flat item list, a per-source set of IMDB IDs for every
        /// provider that completed successfully (used by the prune step), and the count
        /// of attempted providers for safety check.
        /// Providers that were skipped (interval guard) are NOT included in
        /// <paramref name="fetchedSourceIds"/> so they are not pruned.
        /// </summary>
        private async Task<(List<CatalogItem> items, Dictionary<string, HashSet<string>> fetchedSourceIds, int attemptedProviders)>
            FetchFromAllProvidersAsync(
            List<ICatalogProvider>  providers,
            PluginConfiguration     config,
            Data.DatabaseManager    db,
            CancellationToken       cancellationToken)
        {
            var results          = new ConcurrentBag<CatalogItem>();
            var fetchedSourceIds = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var attemptedProviders = 0;

            using var providerGate = new SemaphoreSlim(4);
            var providerTasks = providers.Select(provider => Task.Run(async () =>
            {
                await providerGate.WaitAsync(cancellationToken);
                try
                {
                    // ── Interval guard ────────────────────────────────────────────
                    try
                    {
                        var state = await db.GetSyncStateAsync(provider.SourceKey);
                        if (ShouldSkipProvider(state, config))
                        {
                            _logger.LogInformation(
                                "[InfiniteDrive] Skipping {Provider} — within sync interval ({Hours}h)",
                                provider.ProviderName, config.CatalogSyncIntervalHours);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[InfiniteDrive] Could not read sync state for {Key}", provider.SourceKey);
                    }

                    Interlocked.Increment(ref attemptedProviders);

                    _logger.LogInformation("[InfiniteDrive] Fetching catalog from {Provider}", provider.ProviderName);

                    CatalogFetchResult fetchResult;
                    try
                    {
                        fetchResult = await provider.FetchItemsAsync(config, _logger, db, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[InfiniteDrive] Provider {Provider} threw unexpectedly", provider.ProviderName);
                        fetchResult = new CatalogFetchResult
                        {
                            ProviderReachable = false,
                            ErrorMessage      = ex.Message,
                        };
                    }

                    foreach (var fi in fetchResult.Items)
                        results.Add(fi);

                    if (fetchResult.ProviderReachable && fetchResult.Items.Count > 0)
                    {
                        var idSet = fetchedSourceIds.GetOrAdd(provider.SourceKey,
                            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        lock (idSet)
                        {
                            foreach (var item in fetchResult.Items)
                                idSet.Add(item.ImdbId);
                        }
                    }

                    // ── Record provider-level health ──────────────────────────────
                    try
                    {
                        if (fetchResult.ProviderReachable)
                            await db.RecordSyncSuccessAsync(provider.SourceKey, fetchResult.Items.Count);
                        else
                            await db.RecordSyncFailureAsync(provider.SourceKey, fetchResult.ErrorMessage ?? "Unknown error");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[InfiniteDrive] Failed to record health for {Key}", provider.SourceKey);
                    }

                    // ── Record per-catalog health (AIOStreams per-catalog outcomes) ─
                    foreach (var kvp in fetchResult.CatalogOutcomes)
                    {
                        try
                        {
                            if (kvp.Value.Succeeded)
                                await db.RecordSyncSuccessAsync(kvp.Key, kvp.Value.ItemCount);
                            else
                                await db.RecordSyncFailureAsync(kvp.Key, kvp.Value.Error ?? "Unknown error");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[InfiniteDrive] Failed to record health for catalog {Key}", kvp.Key);
                        }
                    }

                    _logger.LogInformation(
                        "[InfiniteDrive] {Provider} returned {Count} items (reachable={Reachable})",
                        provider.ProviderName, fetchResult.Items.Count, fetchResult.ProviderReachable);
                }
                finally
                {
                    providerGate.Release();
                }
            }, cancellationToken));

            await Task.WhenAll(providerTasks);

            return (results.ToList(), new Dictionary<string, HashSet<string>>(fetchedSourceIds), attemptedProviders);
        }

        /// <summary>
        /// Returns true if the provider should be skipped because it synced
        /// successfully within the configured interval.
        /// Providers in error state (ConsecutiveFailures > 0) always run.
        /// </summary>
        private static bool ShouldSkipProvider(SyncState? state, PluginConfiguration config)
        {
            if (state == null) return false;
            if (state.ConsecutiveFailures > 0) return false;
            if (string.IsNullOrEmpty(state.LastSyncAt)) return false;

            if (!DateTime.TryParse(state.LastSyncAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastSync))
                return false;

            return (DateTime.UtcNow - lastSync).TotalHours < config.CatalogSyncIntervalHours;
        }

        // ── Private: deduplication ──────────────────────────────────────────────

        private static List<CatalogItem> DeduplicateItems(List<CatalogItem> items)
        {
            var seen = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.ImdbId)) continue;
                var key = $"{item.ImdbId}|{item.Source}";

                // FIX-217-05: Also dedup by TMDB ID when available — anime items
                // may have kitsu:XXX as ImdbId but share tmdb_id with a regular catalog entry
                var tmdbKey = !string.IsNullOrEmpty(item.TmdbId) ? $"tmdb:{item.TmdbId}|{item.Source}" : null;

                if (seen.TryGetValue(key, out var existing))
                {
                    // FIX-217-06: Anime always wins — if either is anime, keep anime
                    if (string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase))
                        seen[key] = item;
                    continue;
                }

                if (tmdbKey != null && seen.TryGetValue(tmdbKey, out _))
                {
                    // Same TMDB ID from same source — anime wins
                    if (string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase))
                        seen[tmdbKey] = item;
                    continue;
                }

                seen[key] = item;
                if (tmdbKey != null)
                    seen[tmdbKey] = item;
            }
            return new List<CatalogItem>(seen.Values);
        }

        // ── Private: database upsert ────────────────────────────────────────────

        private async Task UpsertItemsAsync(
            Data.DatabaseManager db,
            List<CatalogItem>    items,
            CancellationToken    cancellationToken)
        {
            if (items.Count == 0) return;

            try
            {
                await db.BulkUpsertCatalogItemsAsync(items, cancellationToken);
                _logger.LogInformation(
                    "[InfiniteDrive] Bulk upserted {Count} catalog items", items.Count);
            }
            catch (Exception ex)
            {
                // Fallback: if bulk fails, try one-by-one so partial progress is saved
                _logger.LogWarning(ex,
                    "[InfiniteDrive] Bulk upsert failed, falling back to one-by-one for {Count} items", items.Count);
                var upserted = 0;
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await db.UpsertCatalogItemAsync(item, cancellationToken);
                        upserted++;
                    }
                    catch (Exception itemEx)
                    {
                        _logger.LogDebug(itemEx,
                            "[InfiniteDrive] Failed to upsert catalog item {ImdbId}", item.ImdbId);
                    }
                }
                _logger.LogInformation(
                    "[InfiniteDrive] Upserted {Count}/{Total} catalog items (fallback)", upserted, items.Count);
            }
        }

        // ── Private: source diff / prune ────────────────────────────────────────

        /// <summary>
        /// Sprint 302-06: Marvin sync safety.
        /// For each source that completed a successful fetch this run, compares the
        /// returned IMDB IDs against the previously active catalog rows.  Any item
        /// no longer present is soft-deleted in the database and its .strm file (plus
        /// the parent Season directory and show directory if they become empty) is
        /// removed from disk.
        ///
        /// Safety: Skip pruning entirely if any source failed to fetch.
        /// Also updates last_verified_at for items found in catalog.
        /// </summary>
        private async Task PruneRemovedItemsAsync(
            Data.DatabaseManager                    db,
            Dictionary<string, HashSet<string>>     fetchedSourceIds,
            int                                   totalProviders,
            CancellationToken                       cancellationToken)
        {
            // Sprint 302-06: Skip pruning if any source failed to fetch
            // This prevents mass removal when a resolver is temporarily down
            if (fetchedSourceIds.Count < totalProviders)
            {
                _logger.LogInformation(
                    "[InfiniteDrive] Skipping pruning — only {Fetched}/{Total} sources fetched successfully. Some resolvers may be down.",
                    fetchedSourceIds.Count, totalProviders);
                return;
            }

            // Sprint 302-06: Update last_verified_at for items found in catalog
            foreach (var kvp in fetchedSourceIds)
            {
                await db.UpdateLastVerifiedAtAsync(kvp.Value, kvp.Key, cancellationToken);
            }

            foreach (var kvp in fetchedSourceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourceKey      = kvp.Key;
                var currentIds     = kvp.Value;

                List<string> removedPaths;
                try
                {
                    removedPaths = await db.PruneSourceAsync(sourceKey, currentIds);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[InfiniteDrive] CatalogSyncTask: prune failed for source {Source}", sourceKey);
                    continue;
                }

                if (removedPaths.Count == 0)
                    continue;

                _logger.LogInformation(
                    "[InfiniteDrive] CatalogSyncTask: pruning {Count} removed items from source {Source}",
                    removedPaths.Count, sourceKey);

                foreach (var strmPath in removedPaths)
                {
                    try
                    {
                        DeleteStrmAndCleanupDirs(strmPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[InfiniteDrive] CatalogSyncTask: could not delete {Path}", strmPath);
                    }
                }
            }
        }

        /// <summary>
        /// Deletes <paramref name="strmPath"/> and removes the parent directory
        /// (and its parent) if they are left empty — prevents ghost Season/show folders.
        /// </summary>
        private static void DeleteStrmAndCleanupDirs(string strmPath)
        {
            if (!File.Exists(strmPath)) return;

            File.Delete(strmPath);

            // Remove empty Season directory
            var seasonDir = Path.GetDirectoryName(strmPath);
            if (!string.IsNullOrEmpty(seasonDir)
                && Directory.Exists(seasonDir)
                && !Directory.EnumerateFileSystemEntries(seasonDir).Any())
            {
                Directory.Delete(seasonDir);

                // Remove empty show directory
                var showDir = Path.GetDirectoryName(seasonDir);
                if (!string.IsNullOrEmpty(showDir)
                    && Directory.Exists(showDir)
                    && !Directory.EnumerateFileSystemEntries(showDir).Any())
                {
                    Directory.Delete(showDir);
                }
            }
        }

        /// <summary>
        /// Builds a dictionary mapping IMDB ID → item path for all Movie and
        /// Series items in the Emby library that live <em>outside</em> the
        /// plugin's own sync directories.
        ///
        /// Exposed as a public static helper so <see cref="LibraryReadoptionTask"/>
        /// can reuse the same logic without duplicating it.
        ///
        /// Returns an empty dictionary on any error so callers can proceed safely.
        /// </summary>
        public static Dictionary<string, string> BuildLibraryItemMapPublic(
            PluginConfiguration config,
            ILibraryManager     libraryManager,
            ILogger             logger)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive        = true,
                };

                var items      = libraryManager.GetItemList(query);
                var syncMovies = (config.SyncPathMovies ?? string.Empty).TrimEnd('/', '\\');
                var syncShows  = (config.SyncPathShows  ?? string.Empty).TrimEnd('/', '\\');

                foreach (var item in items)
                {
                    var path = item.Path ?? string.Empty;

                    if (!string.IsNullOrEmpty(syncMovies)
                        && path.StartsWith(syncMovies, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(syncShows)
                        && path.StartsWith(syncShows, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? imdbId = null;
                    item.ProviderIds?.TryGetValue("Imdb", out imdbId);
                    if (!string.IsNullOrEmpty(imdbId) && !map.ContainsKey(imdbId))
                        map[imdbId] = path;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[InfiniteDrive] BuildLibraryItemMapPublic: query failed");
            }

            return map;
        }

        // ── Private: library check / warn ───────────────────────────────────────

        /// <summary>
        /// Logs a warning if the configured sync paths are not covered by any
        /// Emby virtual folder (i.e. the library hasn't been created yet).
        /// This does NOT block the sync — it just tells the user what to do.
        /// </summary>
        private void WarnIfLibrariesMissing(PluginConfiguration config)
        {
            try
            {
                var folders = _libraryManager.GetVirtualFolders();
                var allPaths = new HashSet<string>(
                    folders.SelectMany(f => f.Locations ?? Array.Empty<string>()),
                    StringComparer.OrdinalIgnoreCase);

                void Check(string? syncPath, string collectionType, string name)
                {
                    if (string.IsNullOrWhiteSpace(syncPath)) return;
                    var norm = syncPath.TrimEnd('/', '\\');
                    if (!allPaths.Any(p => string.Equals(p.TrimEnd('/', '\\'), norm, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogWarning(
                            "[InfiniteDrive] No Emby library points to '{Path}'. " +
                            "Create a {Type} library via Emby Dashboard → Libraries → Add Library → " +
                            "set type to '{CollectionType}' and add path '{Path}'. " +
                            "Without this, synced .strm files will not appear in Emby.",
                            syncPath, name, collectionType, syncPath);
                    }
                }

                Check(config.SyncPathMovies, "movies", "Movies");
                Check(config.SyncPathShows,  "tvshows", "TV Shows");
                Check(config.SyncPathAnime, "mixed", "Anime");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] WarnIfLibrariesMissing: could not read virtual folders");
            }
        }

        // ── Private: library scan ────────────────────────────────────────────────

        private async Task TriggerLibraryScanAsync()
        {
            try
            {
                _logger.LogInformation("[InfiniteDrive] Triggering targeted Emby library scan");

                // Use ValidateMediaLibrary for more efficient targeted scanning
                // This validates specific paths rather than scanning all libraries
                var progress = new Progress<double>();
                await _libraryManager.ValidateMediaLibrary(progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Failed to trigger library scan");
            }
        }

    }
}
