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
    ///         the next released, indexed episode for Tier 1 background resolution. Firing on
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
        private readonly IUserManager             _userManager;
        private readonly ILogger<EmbyEventHandler> _logger;

        // Per-item cooldown for metadata refresh pre-cache (5-minute window)
        private static readonly ConcurrentDictionary<string, DateTime>
            _refreshCooldowns = new();
        private static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(5);

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects all parameters automatically at server startup.
        /// </summary>
        public EmbyEventHandler(
            ISessionManager  sessionManager,
            ILibraryManager  libraryManager,
            IUserManager     userManager,
            ILogManager      logManager)
        {
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _userManager    = userManager;
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
            _userManager.UserDeleted        += OnUserDeleted;
            _logger.LogInformation("[InfiniteDrive] EmbyEventHandler started — watching playback, library, metadata, and user events");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _sessionManager.PlaybackStart   -= OnPlaybackStarted;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _libraryManager.ItemAdded       -= OnItemAdded;
            _libraryManager.ItemUpdated     -= OnItemUpdated;
            _userManager.UserDeleted        -= OnUserDeleted;
        }

        // ── User deletion cleanup ────────────────────────────────────────────────

        /// <summary>
        /// When an Emby user is deleted, remove their InfiniteDrive data (lists,
        /// collection memberships, saves) so nothing is orphaned and their memberships
        /// stop protecting shared items. Best-effort; never throws into Emby.
        /// </summary>
        private void OnUserDeleted(object? sender, MediaBrowser.Model.Events.GenericEventArgs<User> e)
        {
            var user = e?.Argument;
            if (user == null) return;
            var userId = user.Id.ToString("N");
            _ = Task.Run(async () =>
            {
                try
                {
                    var db = Plugin.Instance?.DatabaseManager;
                    if (db == null) return;
                    await db.DeleteAllUserDataAsync(userId).ConfigureAwait(false);
                    _logger.LogInformation("[InfiniteDrive] Cleaned up InfiniteDrive data for deleted user {UserId}", userId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Failed cleaning up data for deleted user {UserId}", userId);
                }
            });
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
                    "[InfiniteDrive] Binge pre-warm triggered: {AioId} S{S}E{E} — evaluating actual next episode",
                    aioId, season, episode);

                await QueueNextEpisodesAsync(db, aioId, season.Value, episode.Value);
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

                // Record the play: powers the RecentPlays dashboard stat and the
                // ever-watched fast-path guard used by the absent-item prune.
                try
                {
                    await db.LogPlaybackAsync(new PlaybackEntry
                    {
                        AioId          = aioId,
                        Title          = e.Item?.Name,
                        Season         = season,
                        Episode        = episode,
                        ResolutionMode = "direct",
                        ClientType     = clientType,
                    }).ConfigureAwait(false);
                }
                catch (Exception logEx)
                {
                    _logger.LogDebug(logEx, "[InfiniteDrive] playback_log write failed for {AioId}", aioId);
                }

                // ── Next-Up pre-warm ─────────────────────────────────────────────

                if (season.HasValue && episode.HasValue)
                {
                    // Queue the next released episode proven to exist in Emby.
                    await QueueNextEpisodesAsync(db, aioId, season.Value, episode.Value);
                }

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Error in playback-stopped handler for {Path}", strmPath);
            }
        }

        // ── Private: next-episode queue ──────────────────────────────────────────

        private async Task QueueNextEpisodesAsync(
            Data.DatabaseManager db, string aioId, int season, int episode)
        {
            // Resolve actual indexed and released state. Never infer continuity from
            // a count: specials, gaps and future placeholders are common in Emby.
            var next = FindNextReleasedEpisode(db, aioId, season, episode);
            if (!next.HasValue) return;
            var (nextSeason, nextEp) = next.Value;

            try
            {
                // Dedup: skip if next episode already has a fresh cache entry.
                var existing = await db.GetCachedStreamAsync(aioId, nextSeason, nextEp);
                if (existing != null && existing.Status == "valid")
                {
                    if (DateTime.TryParse(existing.ResolvedAt, out var resolved))
                    {
                        var ageMinutes = (DateTime.UtcNow - resolved).TotalMinutes;
                        if (ageMinutes <= RuntimePolicy.FallbackStreamCacheMinutes * 0.7)
                        {
                            _logger.LogDebug(
                                "[InfiniteDrive] Skipping Tier 1 queue for {AioId} S{S:D2}E{E:D2} — already fresh ({Age:F0} min old)",
                                aioId, nextSeason, nextEp, ageMinutes);
                            return;
                        }
                    }
                }

                await db.QueueForResolutionAsync(aioId, nextSeason, nextEp, "tier1");
                _logger.LogDebug(
                    "[InfiniteDrive] Queued actual next episode: {AioId} S{S:D2}E{E:D2}",
                    aioId, nextSeason, nextEp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Failed to queue tier1 for {AioId} S{S}E{E}",
                    aioId, nextSeason, nextEp);
            }
        }

        /// <summary>
        /// Finds the next episode from Emby's indexed state and ignores future-dated
        /// placeholders. The next numbered season is checked only when the current
        /// season has no later released episode.
        /// </summary>
        private (int Season, int Episode)? FindNextReleasedEpisode(
            Data.DatabaseManager db, string aioId, int season, int episode)
        {
            try
            {
                var identities = new List<(string Provider, string Id)> { ("Imdb", aioId) };
                var catalogItem = db.GetCatalogItemByAioIdSync(aioId);
                if (catalogItem != null)
                {
                    foreach (var (provider, id) in db.ParseUniqueIdsJson(catalogItem.UniqueIdsJson))
                    {
                        if (provider.Equals("kitsu", StringComparison.OrdinalIgnoreCase))
                            identities.Add(("Kitsu", id));
                        else if (provider.Equals("anilist", StringComparison.OrdinalIgnoreCase))
                            identities.Add(("AniList", id));
                        else if (provider.Equals("mal", StringComparison.OrdinalIgnoreCase))
                            identities.Add(("MyAnimeList", id));
                    }
                }

                foreach (var identity in identities.Distinct())
                {
                    var currentSeason = QueryReleasedEpisodes(identity.Provider, identity.Id, season)
                        .FirstOrDefault(value => value > episode);
                    if (currentSeason > 0) return (season, currentSeason);

                    var followingSeason = QueryReleasedEpisodes(identity.Provider, identity.Id, season + 1)
                        .FirstOrDefault();
                    if (followingSeason > 0) return (season + 1, followingSeason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Could not find next released episode for {AioId} S{Season}", aioId, season);
            }

            return null;
        }

        /// <summary>
        /// Queries released episode numbers in a season by a specific provider ID.
        /// </summary>
        private IEnumerable<int> QueryReleasedEpisodes(string provider, string id, int season)
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
                var now = DateTime.UtcNow;
                return episodes
                    .Where(item => item.IndexNumber.HasValue
                        && (!item.PremiereDate.HasValue || item.PremiereDate.Value <= now))
                    .Select(item => item.IndexNumber!.Value)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<int>();
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

    }
}
