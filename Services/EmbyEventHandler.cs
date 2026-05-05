using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Subscribes to Emby server events to enable:
    /// <list type="bullet">
    ///   <item><b>Binge pre-warm (start)</b>: when an episode begins playing, queues
    ///         the next two episodes for Tier 1 background resolution.  Firing on
    ///         <c>PlaybackStart</c> gives the full episode runtime (20–60 min) as the
    ///         pre-warm window.  Already-fresh cache entries are skipped.</item>
    ///   <item><b>Next-Up pre-warm (stop)</b>: same queueing fires again when an
    ///         episode finishes, covering the brief inter-episode gap as a safety net.</item>
    ///   <item><b>Instant episode expansion</b>: when Emby indexes a new Episode item
    ///         from an InfiniteDrive .strm folder, resets the parent series'
    ///         <c>seasons_json</c> so MarvinTask rewrites all .strm files on its next run.</item>
    ///   <item><b>Redirect-success learning</b>: each completed redirect play
    ///         increments the client's <c>test_count</c> so the auto-mode decision
    ///         improves over time.</item>
    /// </list>
    /// </summary>
    public class EmbyEventHandler : IServerEntryPoint
    {
        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ISessionManager          _sessionManager;
        private readonly ILibraryManager          _libraryManager;
        private readonly ILogger<EmbyEventHandler> _logger;

        // Episode count cache to prevent repeated queries during binge sessions.
        // Cache key format: "{aioId}:S{season}"
        private static readonly ConcurrentDictionary<string, (int count, DateTime expires)>
            _episodeCountCache = new();

        // Per-item cooldown for metadata refresh pre-cache (5-minute window)
        private static readonly ConcurrentDictionary<string, DateTime>
            _refreshCooldowns = new();
        private static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(5);

        // Lazy cleanup threshold: clean expired entries after this many access
        private static int _cacheAccessCount;
        private const int CacheCleanupThreshold = 100;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects all parameters automatically at server startup.
        /// </summary>
        public EmbyEventHandler(
            ISessionManager  sessionManager,
            ILibraryManager  libraryManager,
            ILogManager      logManager)
        {
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _logger         = new EmbyLoggerAdapter<EmbyEventHandler>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── IServerEntryPoint ────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Run()
        {
            _sessionManager.PlaybackStart   += OnPlaybackStarted;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _libraryManager.ItemAdded       += OnItemAdded;
            _libraryManager.ItemUpdated     += OnItemUpdated;
            _logger.LogInformation("[InfiniteDrive] EmbyEventHandler started — watching playback, library, and metadata events");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _sessionManager.PlaybackStart   -= OnPlaybackStarted;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _libraryManager.ItemAdded       -= OnItemAdded;
            _libraryManager.ItemUpdated     -= OnItemUpdated;
        }

        // ── Private: item-added handler ─────────────────────────────────────────

        /// <summary>
        /// Fires whenever Emby indexes a new library item.  When the item is an
        /// <c>Episode</c> and its path lives inside the InfiniteDrive shows folder,
        /// the parent series' <c>seasons_json</c> is cleared so MarvinTask
        /// rewrites all episode .strm files on its next run.
        /// </summary>
        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            if (item == null) return;

            // Only care about newly indexed Episode items
            if (!(item is Episode)) return;

            var config = Plugin.Instance?.Configuration;
            var syncPathShows = config?.SyncPathShows;
            if (string.IsNullOrWhiteSpace(syncPathShows)) return;

            // Check whether this episode lives under our managed shows folder
            var itemPath = item.Path;
            if (string.IsNullOrEmpty(itemPath)) return;
            if (!itemPath.StartsWith(syncPathShows, StringComparison.OrdinalIgnoreCase)) return;

            _ = Task.Run(() => HandleNewEpisodeIndexedAsync(item));
        }

        private async Task HandleNewEpisodeIndexedAsync(BaseItem episode)
        {
            try
            {
                // Resolve the AIO ID of the parent series by trying all provider IDs
                string? aioId = null;
                var series = _libraryManager.GetItemById(episode.SeriesId);
                if (series?.ProviderIds == null) return;

                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                foreach (var kvp in series.ProviderIds)
                {
                    var catalogItem = await db.GetCatalogItemByProviderIdAsync(kvp.Key, kvp.Value)
                        .ConfigureAwait(false);
                    if (catalogItem != null && !string.IsNullOrEmpty(catalogItem.AioId))
                    {
                        aioId = catalogItem.AioId;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(aioId)) return;

                // Clear seasons_json — MarvinTask will rewrite it on its next run.
                var item = await db.GetCatalogItemByAioIdAsync(aioId);
                if (item == null) return;

                await db.UpdateSeasonsJsonAsync(aioId, item.Source, string.Empty);

                _logger.LogInformation(
                    "[InfiniteDrive] New episode indexed for {AioId} — seasons_json cleared",
                    aioId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] HandleNewEpisodeIndexedAsync failed");
            }
        }

        // ── Private: item-updated handler (SMART REFRESH) ──────────────────────

        /// <summary>
        /// Fires when Emby updates metadata for a library item.
        /// For InfiniteDrive items (Movie/Series/Episode), invalidates the pre-cache
        /// and triggers a single-item re-resolution with a 5-minute per-item cooldown.
        /// </summary>
        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            if (item == null) return;

            // Only care about our items
            if (item.ProviderIds == null || !item.ProviderIds.ContainsKey("INFINITEDRIVE"))
                return;

            // Only Movies, Series, and Episodes
            if (!(item is Movie || item is Series || item is Episode))
                return;

            _ = Task.Run(() => HandleItemUpdatedAsync(item));
        }

        private async Task HandleItemUpdatedAsync(BaseItem item)
        {
            try
            {
                // Extract AIO ID by trying all provider IDs
                string? aioId = null;
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                if (item.ProviderIds != null)
                {
                    foreach (var kvp in item.ProviderIds)
                    {
                        if (string.Equals(kvp.Key, "INFINITEDRIVE", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var catalogItem = await db.GetCatalogItemByProviderIdAsync(kvp.Key, kvp.Value)
                            .ConfigureAwait(false);
                        if (catalogItem != null && !string.IsNullOrEmpty(catalogItem.AioId))
                        {
                            aioId = catalogItem.AioId;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(aioId)) return;

                // Per-item cooldown: skip if same item refreshed in last 5 minutes
                var cooldownKey = aioId;
                if (_refreshCooldowns.TryGetValue(cooldownKey, out var lastRefresh))
                {
                    if (DateTime.UtcNow - lastRefresh < RefreshCooldown)
                    {
                        _logger.LogDebug("[InfiniteDrive] Skipping refresh for {AioId} — cooldown ({Remaining:F0}s remaining)",
                            aioId, (RefreshCooldown - (DateTime.UtcNow - lastRefresh)).TotalSeconds);
                        return;
                    }
                }

                _refreshCooldowns[cooldownKey] = DateTime.UtcNow;

                // Determine media type and season/episode
                string mediaType;
                int? season = null, episode = null;

                if (item is Movie)
                {
                    mediaType = "movie";
                }
                else if (item is Episode ep)
                {
                    mediaType = "series";
                    season = ep.ParentIndexNumber;
                    episode = ep.IndexNumber;
                }
                else // Series
                {
                    mediaType = "series";
                }

                var cacheService = Plugin.Instance?.StreamCacheService;
                if (cacheService == null) return;

                // Invalidate existing cache
                await cacheService.InvalidateAsync(aioId, season, episode).ConfigureAwait(false);

                // Fire-and-forget single-item pre-cache
                _ = cacheService.PreCacheSingleAsync(aioId, mediaType, season, episode);

                _logger.LogInformation(
                    "[InfiniteDrive] Smart refresh: invalidated + queued refresh for {AioId} S{S}E{E}",
                    aioId, season, episode);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] HandleItemUpdatedAsync failed for {Name}", item.Name);
            }
        }

        // ── Private: playback-started handler (BINGE-PREWARM + TITLE RESTORATION) ─

        /// <summary>
        /// Fires when any item begins playing.  For InfiniteDrive items:
        /// (1) Restores the correct catalog title if Emby overwrote it with
        ///     MKV-embedded metadata (raw torrent filenames).
        /// (2) For series episodes, queues the next two episodes for Tier 1
        ///     resolution immediately so the full episode runtime is available
        ///     as the pre-warm window.
        /// </summary>
        private void OnPlaybackStarted(object? sender, PlaybackProgressEventArgs e)
        {
            var item = e.Item;
            if (item == null) return;

            // Detect InfiniteDrive items by provider ID (covers version picker playback)
            var isInfiniteDrive = item.ProviderIds != null &&
                item.ProviderIds.ContainsKey("INFINITEDRIVE");

            // Also detect by .strm path (legacy fallback)
            if (!isInfiniteDrive && item.Path != null &&
                item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                isInfiniteDrive = true;
            }

            if (!isInfiniteDrive) return;

            _ = Task.Run(() => HandlePlaybackStartedAsync(item, e));
        }

        private async Task HandlePlaybackStartedAsync(BaseItem item, PlaybackProgressEventArgs e)
        {
            try
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                // ── Title restoration: correct MKV-embedded title overwrite ────────
                await RestoreTitleAsync(item, db).ConfigureAwait(false);

                // ── Binge pre-warm (series only) ───────────────────────────────────
                // Extract AIO ID for binge pre-warm by trying all provider IDs
                string? aioId = null;
                if (item.ProviderIds != null)
                {
                    foreach (var kvp in item.ProviderIds)
                    {
                        if (string.Equals(kvp.Key, "INFINITEDRIVE", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var catalogItem = await db.GetCatalogItemByProviderIdAsync(kvp.Key, kvp.Value)
                            .ConfigureAwait(false);
                        if (catalogItem != null && !string.IsNullOrEmpty(catalogItem.AioId))
                        {
                            aioId = catalogItem.AioId;
                            break;
                        }
                    }
                }

                // Fallback: try to extract from .strm path
                if (string.IsNullOrEmpty(aioId) && item.Path != null &&
                    item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    (aioId, _, _) = await ExtractInfoFromStrmAsync(item.Path).ConfigureAwait(false);
                }

                int? season = null, episode = null;

                if (item is Episode ep)
                {
                    season = ep.ParentIndexNumber;
                    episode = ep.IndexNumber;
                }
                else if (item.Path != null && item.Path.Contains("Season ", StringComparison.OrdinalIgnoreCase))
                {
                    (season, episode) = ParseSeasonEpisodeFromPath(item.Path);
                }

                if (string.IsNullOrEmpty(aioId) || !season.HasValue || !episode.HasValue) return;

                _logger.LogInformation(
                    "[InfiniteDrive] Binge pre-warm triggered: {AioId} S{S}E{E} — queuing next episodes",
                    aioId, season, episode);

                await QueueNextEpisodesAsync(db, aioId, season.Value, episode.Value);

                // Refresh remaining season episodes in background
                _ = Task.Run(() => RefreshSeriesCacheAsync(db, aioId, season.Value, episode.Value));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] HandlePlaybackStartedAsync failed for {Name}", item.Name);
            }
        }

        /// <summary>
        /// Restores the correct catalog title if Emby overwrote it with
        /// MKV-embedded metadata (raw torrent filenames) during playback.
        /// Iterates through all provider IDs (IMDB, TMDB, Kitsu, AniList, MAL, etc.)
        /// to find the catalog item.
        /// </summary>
        private async Task RestoreTitleAsync(BaseItem item, Data.DatabaseManager db)
        {
            try
            {
                if (item.ProviderIds == null || item.ProviderIds.Count == 0) return;

                CatalogItem? catalogItem = null;

                // Try each provider ID until we find a match
                foreach (var kvp in item.ProviderIds)
                {
                    if (string.Equals(kvp.Key, "INFINITEDRIVE", StringComparison.OrdinalIgnoreCase))
                        continue;

                    catalogItem = await db.GetCatalogItemByProviderIdAsync(kvp.Key, kvp.Value)
                        .ConfigureAwait(false);

                    if (catalogItem != null && !string.IsNullOrEmpty(catalogItem.Title))
                        break;
                }

                if (catalogItem == null || string.IsNullOrEmpty(catalogItem.Title)) return;

                // Check if the current item name differs from the catalog title
                if (string.Equals(item.Name, catalogItem.Title, StringComparison.Ordinal)) return;

                _logger.LogInformation(
                    "[InfiniteDrive] Title correction: restoring '{Correct}' (was '{Wrong}')",
                    catalogItem.Title, item.Name);

                // Update in-memory item
                item.Name = catalogItem.Title;

                // Persist to Emby database
                _libraryManager.UpdateItem(item, item.GetParent(), ItemUpdateType.MetadataEdit);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] RestoreTitleAsync failed for {Name}", item.Name);
            }
        }

        /// <summary>
        /// Extracts AIO ID, season, and episode from a .strm file URL.
        /// </summary>
        private static async Task<(string aioId, int? season, int? episode)> ExtractInfoFromStrmAsync(string strmPath)
        {
            try
            {
                if (!File.Exists(strmPath)) return (string.Empty, null, null);
                var strmUrl = await File.ReadAllTextAsync(strmPath).ConfigureAwait(false);
                return ParseStrmUrl(strmUrl.Trim());
            }
            catch
            {
                return (string.Empty, null, null);
            }
        }

        private static (int? season, int? episode) ParseSeasonEpisodeFromPath(string path)
        {
            var seasonIdx = path.IndexOf("Season ", StringComparison.OrdinalIgnoreCase);
            if (seasonIdx < 0) return (null, null);

            var afterSeason = path.Substring(seasonIdx + 7);
            var epIdx = afterSeason.IndexOf("\\", StringComparison.OrdinalIgnoreCase);
            if (epIdx < 0) epIdx = afterSeason.IndexOf("/", StringComparison.OrdinalIgnoreCase);
            if (epIdx < 0) return (null, null);

            if (!int.TryParse(afterSeason.Substring(0, epIdx), out var season)) return (null, null);

            // Episode number from filename is unreliable; provider IDs are preferred
            return (season, null);
        }

        // ── Private: playback-stopped handler ───────────────────────────────────

        private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            // Only process items that came from InfiniteDrive .strm files
            var item = e.Item;
            if (item?.Path == null) return;
            if (!item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) return;

            _ = Task.Run(() => HandlePlaybackStoppedAsync(item.Path, e));
        }

        private async Task HandlePlaybackStoppedAsync(string strmPath, PlaybackStopEventArgs e)
        {
            try
            {
                // Read the .strm file to extract aioId/season/episode
                if (!File.Exists(strmPath)) return;

                // File.ReadAllText is sync but this runs in a Task.Run thread — acceptable.
                var strmUrl = File.ReadAllText(strmPath).Trim();
                var (aioId, season, episode) = ParseStrmUrl(strmUrl);

                if (string.IsNullOrEmpty(aioId)) return;
                if (!IsInfiniteDriveUrl(strmUrl)) return;

                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                // Log playback stop details
                var clientType = ExtractClientType(e);
                _logger.LogInformation(
                    "[InfiniteDrive] Playback stopped: {AioId} S{S}E{E} client={Client}",
                    aioId, season, episode, clientType);

                // ── Next-Up pre-warm ─────────────────────────────────────────────

                if (season.HasValue && episode.HasValue)
                {
                    // Queue episodes episode+1 and episode+2 for Tier 1 resolution
                    await QueueNextEpisodesAsync(db, aioId, season.Value, episode.Value);
                }

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Error in playback-stopped handler for {Path}", strmPath);
            }
        }

        // ── Private: series-wide cache refresh ───────────────────────────────────

        /// <summary>
        /// On PlaybackStart, pre-warms remaining episodes in the current season
        /// and the first episodes of the next season.  Skips episodes that already
        /// have a fresh cache entry (within 70% of TTL).
        /// </summary>
        private async Task RefreshSeriesCacheAsync(
            Data.DatabaseManager db, string aioId, int season, int episode)
        {
            try
            {
                var cacheLifetime = Plugin.Instance?.Configuration?.CacheLifetimeMinutes ?? 240;
                int episodesInSeason = GetEpisodeCountForSeason(db, aioId, season);
                int queued = 0;

                // Pre-warm all remaining episodes in the current season
                for (int ep = episode + 1; ep <= episodesInSeason; ep++)
                {
                    try
                    {
                        var existing = await db.GetCachedStreamAsync(aioId, season, ep);
                        if (existing != null && existing.Status == "valid")
                        {
                            if (DateTime.TryParse(existing.ResolvedAt, out var resolved))
                            {
                                var ageMinutes = (DateTime.UtcNow - resolved).TotalMinutes;
                                if (ageMinutes <= cacheLifetime * 0.7) continue;
                            }
                        }

                        await db.QueueForResolutionAsync(aioId, season, ep, "tier1");
                        queued++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[InfiniteDrive] RefreshSeriesCache: failed to queue {AioId} S{S:D2}E{E:D2}",
                            aioId, season, ep);
                    }
                }

                // Queue first 2 episodes of next season
                for (int ep = 1; ep <= 2; ep++)
                {
                    try
                    {
                        var existing = await db.GetCachedStreamAsync(aioId, season + 1, ep);
                        if (existing != null && existing.Status == "valid")
                        {
                            if (DateTime.TryParse(existing.ResolvedAt, out var resolved))
                            {
                                var ageMinutes = (DateTime.UtcNow - resolved).TotalMinutes;
                                if (ageMinutes <= cacheLifetime * 0.7) continue;
                            }
                        }

                        await db.QueueForResolutionAsync(aioId, season + 1, ep, "tier1");
                        queued++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[InfiniteDrive] RefreshSeriesCache: failed to queue {AioId} S{S:D2}E{E:D2}",
                            aioId, season + 1, ep);
                    }
                }

                if (queued > 0)
                {
                    _logger.LogInformation(
                        "[InfiniteDrive] Series cache refresh queued {Count} episodes for {AioId} S{S:D2}",
                        queued, aioId, season);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "[InfiniteDrive] RefreshSeriesCacheAsync failed for {AioId} S{S}", aioId, season);
            }
        }

        // ── Private: next-episode queue ──────────────────────────────────────────

        private async Task QueueNextEpisodesAsync(
            Data.DatabaseManager db, string aioId, int season, int episode)
        {
            // Resolve the actual episode count for the current season from Emby's library.
            // Fall back to a generous cap (30) if the data isn't indexed yet.
            int episodesInSeason = GetEpisodeCountForSeason(db, aioId, season);

            var lookahead = Plugin.Instance?.Configuration?.NextUpLookaheadEpisodes ?? 2;
            for (int i = 1; i <= lookahead; i++)
            {
                int nextEp     = episode + i;
                int nextSeason = season;

                if (nextEp > episodesInSeason)
                {
                    // Roll to episode 1 of the next season
                    nextSeason++;
                    nextEp = 1;
                }

                try
                {
                    // Dedup: skip if next episode already has a fresh, non-aging cache entry.
                    var existing = await db.GetCachedStreamAsync(aioId, nextSeason, nextEp);
                    if (existing != null && existing.Status == "valid")
                    {
                        var cacheLifetime = Plugin.Instance?.Configuration?.CacheLifetimeMinutes ?? 240;
                        if (DateTime.TryParse(existing.ResolvedAt, out var resolved))
                        {
                            var ageMinutes = (DateTime.UtcNow - resolved).TotalMinutes;
                            if (ageMinutes <= cacheLifetime * 0.7)
                            {
                                _logger.LogDebug(
                                    "[InfiniteDrive] Skipping Tier 1 queue for {AioId} S{S:D2}E{E:D2} — already fresh ({Age:F0} min old)",
                                    aioId, nextSeason, nextEp, ageMinutes);
                                continue;
                            }
                        }
                    }

                    await db.QueueForResolutionAsync(aioId, nextSeason, nextEp, "tier1");
                    _logger.LogDebug(
                        "[InfiniteDrive] Queued Tier 1: {AioId} S{S:D2}E{E:D2}",
                        aioId, nextSeason, nextEp);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Failed to queue tier1 for {AioId} S{S}E{E}",
                        aioId, nextSeason, nextEp);
                }
            }
        }

        /// <summary>
        /// Queries Emby library for number of episodes in <paramref name="season"/>
        /// of the series identified by <paramref name="aioId"/>.  Returns 30 as a safe
        /// fallback when the series is not yet indexed or has no episodes.
        /// </summary>
        private int GetEpisodeCountForSeason(Data.DatabaseManager db, string aioId, int season)
        {
            const int Fallback = 30;

            // Lazy cleanup: purge expired entries periodically (Sprint 104B-02)
            if (++_cacheAccessCount % CacheCleanupThreshold == 0)
            {
                CleanupExpiredCacheEntries();
            }

            // Check cache first (6-hour TTL)
            var cacheKey = $"{aioId}:S{season}";
            if (_episodeCountCache.TryGetValue(cacheKey, out var cached) && cached.expires > DateTime.UtcNow)
            {
                return cached.count;
            }

            try
            {
                // Try IMDB first (existing logic)
                var count = QueryByProviderId(db, "Imdb", aioId, season);
                if (count > 0)
                {
                    // Fallback to anime provider IDs from UniqueIdsJson
                    var catalogItem = db.GetCatalogItemByAioIdSync(aioId);
                    if (catalogItem != null)
                    {
                        var uniqueIds = db.ParseUniqueIdsJson(catalogItem.UniqueIdsJson);
                        foreach (var (provider, id) in uniqueIds)
                        {
                            if (provider.Equals("kitsu", StringComparison.OrdinalIgnoreCase))
                            {
                                count = QueryByProviderId(db, "Kitsu", id, season);
                                if (count > 0) break;
                            }
                            else if (provider.Equals("anilist", StringComparison.OrdinalIgnoreCase))
                            {
                                count = QueryByProviderId(db, "AniList", id, season);
                                if (count > 0) break;
                            }
                            else if (provider.Equals("mal", StringComparison.OrdinalIgnoreCase))
                            {
                                count = QueryByProviderId(db, "MyAnimeList", id, season);
                                if (count > 0) break;
                            }
                        }
                    }
                }

                // Cache result for 6 hours to prevent repeated queries during binge sessions
                _episodeCountCache[cacheKey] = (count, DateTime.UtcNow.AddHours(6));

                return count > 0 ? count : Fallback;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Could not query episode count for {AioId} S{Season}", aioId, season);
                return Fallback;
            }
        }

        /// <summary>
        /// Helper to query Emby library by a specific provider ID.
        /// </summary>
        private int QueryByProviderId(Data.DatabaseManager db, string provider, string id, int season)
        {
            try
            {
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    ParentIndexNumber = season,
                    AnySeriesProviderIdEquals = new[]
                    {
                        new KeyValuePair<string, string>(provider, id)
                    },
                });
                return episodes.Length;
            }
            catch
            {
                return 0;
            }
        }

        // ── Private: helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Parses the IMDB ID, season, and episode from an InfiniteDrive .strm URL.
        /// </summary>
        private static bool IsInfiniteDriveUrl(string url)
        {
            return url.Contains("/InfiniteDrive/", StringComparison.OrdinalIgnoreCase);
        }

        private static (string aioId, int? season, int? episode) ParseStrmUrl(string url)
        {
            var q = url.IndexOf('?');
            if (q < 0) return (string.Empty, null, null);

            string aioId   = string.Empty;
            int?   season  = null;
            int?   episode = null;

            foreach (var part in url.Substring(q + 1).Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;

                var key = part.Substring(0, eq);
                var val = part.Substring(eq + 1);

                if (key.Equals("imdb",    StringComparison.OrdinalIgnoreCase)
                    || key.Equals("id", StringComparison.OrdinalIgnoreCase)) aioId = val;
                else if (key.Equals("season",  StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(val, out var s)) season = s;
                else if (key.Equals("episode", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(val, out var ep)) episode = ep;
            }

            return (aioId, season, episode);
        }

        private static string ExtractClientType(PlaybackStopEventArgs e)
        {
            var client = e.Session?.Client;
            return StreamHelpers.NormalizeClientType(client);
        }

        /// <summary>
        /// Removes expired cache entries to prevent memory leaks (Sprint 104B-02).
        /// Called lazily every 100 cache accesses to avoid impacting performance.
        /// </summary>
        private static void CleanupExpiredCacheEntries()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = new List<string>();

            foreach (var kvp in _episodeCountCache)
            {
                if (kvp.Value.expires < now)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                _episodeCountCache.TryRemove(key, out _);
            }
        }
    }
}
