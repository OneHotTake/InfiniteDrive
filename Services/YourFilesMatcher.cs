using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Matches "Your Files" items against media_item_ids table.
    /// Uses multi-provider ID matching (IMDB, TMDB, TVDB, AniList, AniDB, Kitsu).
    /// </summary>
    public class YourFilesMatcher
    {
        private readonly DatabaseManager _db;
        private readonly ILogger<YourFilesMatcher> _logger;

        public YourFilesMatcher(DatabaseManager db, ILogger<YourFilesMatcher> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Matches "Your Files" items against media_item_ids.
        /// </summary>
        public async Task<List<YourFilesMatchResult>> MatchAsync(
            List<BaseItem> yourFilesItems,
            CancellationToken ct = default)
        {
            var matches = new List<YourFilesMatchResult>();

            foreach (var item in yourFilesItems)
            {
                var matchedItem = await FindMatchingMediaItemAsync(item, ct);
                if (matchedItem != null)
                {
                    matches.Add(new YourFilesMatchResult(
                        item,
                        matchedItem,
                        DetermineMatchType(item)
                    ));
                }
            }

            _logger.LogInformation("[YourFilesMatcher] Matched {Count} 'Your Files' items", matches.Count);

            return matches;
        }

        /// <summary>
        /// Finds a matching media item by provider ID.
        /// </summary>
        private async Task<MediaItem?> FindMatchingMediaItemAsync(BaseItem item, CancellationToken ct)
        {
            if (item.ProviderIds == null || item.ProviderIds.Count == 0)
            {
                return null;
            }

            // Try IMDB first (most reliable)
            if (item.ProviderIds.TryGetValue("imdb", out var imdbId) && !string.IsNullOrEmpty(imdbId))
            {
                var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                    "imdb", imdbId, ct);
                if (mediaItem != null) return mediaItem;
            }

            // Try TMDB
            if (item.ProviderIds.TryGetValue("tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
            {
                var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                    "tmdb", tmdbId, ct);
                if (mediaItem != null) return mediaItem;
            }

            // Try Tvdb
            if (item.ProviderIds.TryGetValue("tvdb", out var tvdbId) && !string.IsNullOrEmpty(tvdbId))
            {
                var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                    "tvdb", tvdbId, ct);
                if (mediaItem != null) return mediaItem;
            }

            // Try AniList
            if (item.ProviderIds.TryGetValue("anilist", out var anilistId) && !string.IsNullOrEmpty(anilistId))
            {
                var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                    "anilist", anilistId, ct);
                if (mediaItem != null) return mediaItem;
            }

            // Try AniDB
            if (item.ProviderIds.TryGetValue("anidb", out var anidbId) && !string.IsNullOrEmpty(anidbId))
            {
                var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                    "anidb", anidbId, ct);
                if (mediaItem != null) return mediaItem;
            }

            // Try Kitsu
            if (item.ProviderIds.TryGetValue("kitsu", out var kitsuId) && !string.IsNullOrEmpty(kitsuId))
            {
                var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                    "kitsu", kitsuId, ct);
                if (mediaItem != null) return mediaItem;
            }

            return null;
        }

        /// <summary>
        /// Determines the match type based on provider ID.
        /// </summary>
        private YourFilesMatchType DetermineMatchType(BaseItem item)
        {
            if (item.ProviderIds == null)
            {
                return YourFilesMatchType.Other;
            }

            // Prefer higher-quality provider IDs
            if (item.ProviderIds.ContainsKey("imdb"))
                return YourFilesMatchType.Imdb;

            if (item.ProviderIds.ContainsKey("tmdb"))
                return YourFilesMatchType.Tmdb;

            if (item.ProviderIds.ContainsKey("tvdb"))
                return YourFilesMatchType.Tvdb;

            if (item.ProviderIds.ContainsKey("anilist"))
                return YourFilesMatchType.AniList;

            if (item.ProviderIds.ContainsKey("anidb"))
                return YourFilesMatchType.AniDB;

            if (item.ProviderIds.ContainsKey("kitsu"))
                return YourFilesMatchType.Kitsu;

            return YourFilesMatchType.Other;
        }
    }

    /// <summary>
    /// Match type for "Your Files" items.
    /// </summary>
    public enum YourFilesMatchType
    {
        Imdb,
        Tmdb,
        Tvdb,
        AniList,
        AniDB,
        Kitsu,
        Other
    }

    /// <summary>
    /// Represents a match between a "Your Files" item and a MediaItem.
    /// </summary>
    public record YourFilesMatchResult(
        MediaBrowser.Controller.Entities.BaseItem YourFilesItem,
        MediaItem MediaItem,
        YourFilesMatchType MatchType
    );
}
