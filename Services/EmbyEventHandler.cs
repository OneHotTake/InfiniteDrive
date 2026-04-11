using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
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
    ///         <c>seasons_json</c> so <see cref="Tasks.EpisodeExpandTask"/> rewrites all
    ///         .strm files on its next run.</item>
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
        // Cache key format: "{imdbId}:S{season}"
        private static readonly ConcurrentDictionary<string, (int count, DateTime expires)>
            _episodeCountCache = new();

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
            _logger.LogInformation("[InfiniteDrive] EmbyEventHandler started — watching playback and library events");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _sessionManager.PlaybackStart   -= OnPlaybackStarted;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _libraryManager.ItemAdded       -= OnItemAdded;
        }

        // ── Private: item-added handler ─────────────────────────────────────────

        /// <summary>
        /// Fires whenever Emby indexes a new library item.  When the item is an
        /// <c>Episode</c> and its path lives inside the InfiniteDrive shows folder,
        /// the parent series' <c>seasons_json</c> is cleared so
        /// <see cref="Tasks.EpisodeExpandTask"/> rewrites all episode .strm files on
        /// its next run.
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
                // Resolve the IMDB ID of the parent series so we can clear its seasons_json
                string? imdbId = null;
                var series = _libraryManager.GetItemById(episode.SeriesId);
                series?.ProviderIds?.TryGetValue("Imdb", out imdbId);

                if (string.IsNullOrEmpty(imdbId)) return;

                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                // Clear seasons_json — EpisodeExpandTask will rewrite it with the
                // updated episode list on its next run (within 4 hours, or trigger manually).
                var catalogItem = await db.GetCatalogItemByImdbIdAsync(imdbId);
                if (catalogItem == null) return;

                await db.UpdateSeasonsJsonAsync(imdbId, catalogItem.Source, string.Empty);

                _logger.LogInformation(
                    "[InfiniteDrive] New episode indexed for {ImdbId} — seasons_json cleared for EpisodeExpandTask",
                    imdbId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] HandleNewEpisodeIndexedAsync failed");
            }
        }

        // ── Private: playback-started handler (BINGE-PREWARM) ───────────────────

        /// <summary>
        /// Fires when any item begins playing.  For InfiniteDrive episodes, queues
        /// the next two episodes for Tier 1 resolution immediately so the full
        /// episode runtime is available as the pre-warm window.
        /// </summary>
        private void OnPlaybackStarted(object? sender, PlaybackProgressEventArgs e)
        {
            var item = e.Item;
            if (item?.Path == null) return;
            if (!item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) return;

            _ = Task.Run(() => HandlePlaybackStartedAsync(item.Path, e));
        }

        private async Task HandlePlaybackStartedAsync(string strmPath, PlaybackProgressEventArgs e)
        {
            try
            {
                if (!File.Exists(strmPath)) return;

                var strmUrl = File.ReadAllText(strmPath).Trim();
                var (imdb, season, episode) = ParseStrmUrl(strmUrl);

                if (string.IsNullOrEmpty(imdb)) return;
                if (strmUrl.IndexOf("/InfiniteDrive/Play", StringComparison.OrdinalIgnoreCase) < 0) return;

                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                // ── Auto-pin on playback (Sprint 144) ─────────────────────────────

                // Auto-pin for all InfiniteDrive .strm files
                var userId = e.Users?.FirstOrDefault()?.Id.ToString();
                if (!string.IsNullOrEmpty(userId))
                {
                    var catalogItem = await db.GetCatalogItemByStrmPathAsync(strmPath);
                    if (catalogItem != null)
                    {
                        var userPinRepo = Plugin.Instance?.UserPinRepository;
                        if (userPinRepo != null)
                        {
                            await userPinRepo.AddPinAsync(userId, catalogItem.Id, "playback");
                            _logger.LogDebug(
                                "[InfiniteDrive] Auto-pinned {Title} for user {UserId} via playback",
                                catalogItem.Title, userId);
                        }
                    }
                }

                // Only pre-warm for series episodes
                if (!season.HasValue || !episode.HasValue) return;

                _logger.LogInformation(
                    "[InfiniteDrive] Binge pre-warm triggered: {Imdb} S{S}E{E} — queuing next episodes",
                    imdb, season, episode);

                await QueueNextEpisodesAsync(db, imdb, season.Value, episode.Value);

                // Refresh remaining season episodes in background
                _ = Task.Run(() => RefreshSeriesCacheAsync(db, imdb, season.Value, episode.Value));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] HandlePlaybackStartedAsync failed for {Path}", strmPath);
            }
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
                // Read the .strm file to extract imdb/season/episode
                if (!File.Exists(strmPath)) return;

                // File.ReadAllText is sync but this runs in a Task.Run thread — acceptable.
                var strmUrl = File.ReadAllText(strmPath).Trim();
                var (imdb, season, episode) = ParseStrmUrl(strmUrl);

                if (string.IsNullOrEmpty(imdb)) return;
                if (strmUrl.IndexOf("/InfiniteDrive/Play", StringComparison.OrdinalIgnoreCase) < 0) return;

                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                // Log playback stop details
                var clientType = ExtractClientType(e);
                _logger.LogInformation(
                    "[InfiniteDrive] Playback stopped: {Imdb} S{S}E{E} client={Client}",
                    imdb, season, episode, clientType);

                // ── Next-Up pre-warm ─────────────────────────────────────────────

                if (season.HasValue && episode.HasValue)
                {
                    // Queue episodes episode+1 and episode+2 for Tier 1 resolution
                    await QueueNextEpisodesAsync(db, imdb, season.Value, episode.Value);
                }

                // ── Update client compat if we have bitrate data ──────────────────

                await UpdateClientCompatIfNeededAsync(db, e, clientType);
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
            Data.DatabaseManager db, string imdb, int season, int episode)
        {
            try
            {
                var cacheLifetime = Plugin.Instance?.Configuration?.CacheLifetimeMinutes ?? 240;
                int episodesInSeason = GetEpisodeCountForSeason(db, imdb, season);
                int queued = 0;

                // Pre-warm all remaining episodes in the current season
                for (int ep = episode + 1; ep <= episodesInSeason; ep++)
                {
                    try
                    {
                        var existing = await db.GetCachedStreamAsync(imdb, season, ep);
                        if (existing != null && existing.Status == "valid")
                        {
                            if (DateTime.TryParse(existing.ResolvedAt, out var resolved))
                            {
                                var ageMinutes = (DateTime.UtcNow - resolved).TotalMinutes;
                                if (ageMinutes <= cacheLifetime * 0.7) continue;
                            }
                        }

                        await db.QueueForResolutionAsync(imdb, season, ep, "tier1");
                        queued++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[InfiniteDrive] RefreshSeriesCache: failed to queue {Imdb} S{S:D2}E{E:D2}",
                            imdb, season, ep);
                    }
                }

                // Queue first 2 episodes of next season
                for (int ep = 1; ep <= 2; ep++)
                {
                    try
                    {
                        var existing = await db.GetCachedStreamAsync(imdb, season + 1, ep);
                        if (existing != null && existing.Status == "valid")
                        {
                            if (DateTime.TryParse(existing.ResolvedAt, out var resolved))
                            {
                                var ageMinutes = (DateTime.UtcNow - resolved).TotalMinutes;
                                if (ageMinutes <= cacheLifetime * 0.7) continue;
                            }
                        }

                        await db.QueueForResolutionAsync(imdb, season + 1, ep, "tier1");
                        queued++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[InfiniteDrive] RefreshSeriesCache: failed to queue {Imdb} S{S:D2}E{E:D2}",
                            imdb, season + 1, ep);
                    }
                }

                if (queued > 0)
                {
                    _logger.LogInformation(
                        "[InfiniteDrive] Series cache refresh queued {Count} episodes for {Imdb} S{S:D2}",
                        queued, imdb, season);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "[InfiniteDrive] RefreshSeriesCacheAsync failed for {Imdb} S{S}", imdb, season);
            }
        }

        // ── Private: next-episode queue ──────────────────────────────────────────

        private async Task QueueNextEpisodesAsync(
            Data.DatabaseManager db, string imdb, int season, int episode)
        {
            // Resolve the actual episode count for the current season from Emby's library.
            // Fall back to a generous cap (30) if the data isn't indexed yet.
            int episodesInSeason = GetEpisodeCountForSeason(db, imdb, season);

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
                    var existing = await db.GetCachedStreamAsync(imdb, nextSeason, nextEp);
                    if (existing != null && existing.Status == "valid")
                    {
                        var cacheLifetime = Plugin.Instance?.Configuration?.CacheLifetimeMinutes ?? 240;
                        if (DateTime.TryParse(existing.ResolvedAt, out var resolved))
                        {
                            var ageMinutes = (DateTime.UtcNow - resolved).TotalMinutes;
                            if (ageMinutes <= cacheLifetime * 0.7)
                            {
                                _logger.LogDebug(
                                    "[InfiniteDrive] Skipping Tier 1 queue for {Imdb} S{S:D2}E{E:D2} — already fresh ({Age:F0} min old)",
                                    imdb, nextSeason, nextEp, ageMinutes);
                                continue;
                            }
                        }
                    }

                    await db.QueueForResolutionAsync(imdb, nextSeason, nextEp, "tier1");
                    _logger.LogDebug(
                        "[InfiniteDrive] Queued Tier 1: {Imdb} S{S:D2}E{E:D2}",
                        imdb, nextSeason, nextEp);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Failed to queue tier1 for {Imdb} S{S}E{E}",
                        imdb, nextSeason, nextEp);
                }
            }
        }

        /// <summary>
        /// Queries Emby library for number of episodes in <paramref name="season"/>
        /// of the series identified by <paramref name="imdbId"/>.  Returns 30 as a safe
        /// fallback when the series is not yet indexed or has no episodes.
        /// </summary>
        private int GetEpisodeCountForSeason(Data.DatabaseManager db, string imdbId, int season)
        {
            const int Fallback = 30;

            // Lazy cleanup: purge expired entries periodically (Sprint 104B-02)
            if (++_cacheAccessCount % CacheCleanupThreshold == 0)
            {
                CleanupExpiredCacheEntries();
            }

            // Check cache first (6-hour TTL)
            var cacheKey = $"{imdbId}:S{season}";
            if (_episodeCountCache.TryGetValue(cacheKey, out var cached) && cached.expires > DateTime.UtcNow)
            {
                return cached.count;
            }

            try
            {
                // Try IMDB first (existing logic)
                var count = QueryByProviderId(db, "Imdb", imdbId, season);
                if (count > 0)
                {
                    // Fallback to anime provider IDs from UniqueIdsJson
                    var catalogItem = db.GetCatalogItemByImdbIdSync(imdbId);
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
                _logger.LogDebug(ex, "[InfiniteDrive] Could not query episode count for {Imdb} S{Season}", imdbId, season);
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

        // ── Private: client compat update ────────────────────────────────────────

        private async Task UpdateClientCompatIfNeededAsync(
            Data.DatabaseManager db,
            PlaybackStopEventArgs e,
            string clientType)
        {
            // This fires on every successful playback stop (redirect or proxy).
            // Increment test_count and confirm that redirect works for this client type.
            // ThroughputTrackingStream handles the failure path (sets supports_redirect=0).
            try
            {
                // UpdateClientCompatAsync upserts and bumps test_count atomically.
                // Passing supportsRedirect=true and null bitrate keeps existing constraints.
                await db.UpdateClientCompatAsync(clientType, supportsRedirect: true, maxBitrate: null);
                _logger.LogDebug("[InfiniteDrive] Redirect-success recorded for client {Client}", clientType);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] client compat update skipped for {Client}", clientType);
            }
        }

        // ── Private: helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Parses the IMDB ID, season, and episode from an InfiniteDrive .strm URL.
        /// </summary>
        private static (string imdb, int? season, int? episode) ParseStrmUrl(string url)
        {
            var q = url.IndexOf('?');
            if (q < 0) return (string.Empty, null, null);

            string imdb   = string.Empty;
            int?   season  = null;
            int?   episode = null;

            foreach (var part in url.Substring(q + 1).Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;

                var key = part.Substring(0, eq);
                var val = part.Substring(eq + 1);

                if (key.Equals("imdb",    StringComparison.OrdinalIgnoreCase)) imdb = val;
                else if (key.Equals("season",  StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(val, out var s)) season = s;
                else if (key.Equals("episode", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(val, out var ep)) episode = ep;
            }

            return (imdb, season, episode);
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
