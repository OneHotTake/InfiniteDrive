using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Single pipeline for ALL add-to-library operations across InfiniteDrive.
    /// Called by DiscoverService, UserCatalogSyncService, and MarvinTask.
    ///
    /// Pipeline: BlockList check → dedup → .strm write → playlist add → pre-cache trigger
    /// </summary>
    public class UnifiedItemService
    {
        private readonly ILogger<UnifiedItemService> _logger;
        private readonly DatabaseManager _db;
        private readonly StrmWriterService _strmWriter;
        private readonly BlockListService _blockList;
        private readonly ILibraryManager _libraryManager;

        public UnifiedItemService(
            ILogger<UnifiedItemService> logger,
            DatabaseManager db,
            StrmWriterService strmWriter,
            BlockListService blockList,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _db = db;
            _strmWriter = strmWriter;
            _blockList = blockList;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Adds a single item to the library. Returns the Emby item ID on success, null on failure.
        ///
        /// Pipeline: block check → dedup → .strm write → playlist add → pre-cache trigger
        /// </summary>
        public async Task<Guid?> AddItemAsync(
            string aioId, string type, string title, int? year,
            string? userId, CancellationToken ct)
        {
            // Step 1: Block list check
            var isBlocked = await _blockList.IsBlockedAsync(aioId, null, null);
            if (isBlocked)
            {
                _logger.LogWarning("[UnifiedItem] Blocked item skipped: {AioId}", aioId);
                return null;
            }

            // Step 2: Dedup — check if already in library
            var existingItem = FindInLibrary(aioId, type);
            if (existingItem != null)
            {
                _logger.LogDebug("[UnifiedItem] Already in library: {AioId} ({EmbyId})", aioId, existingItem.Id);
                return existingItem.Id;
            }

            // Step 3: Write .strm file
            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            var targetDir = type.ToLowerInvariant() switch
            {
                "movie" => config.SyncPathMovies,
                "series" => config.SyncPathShows,
                "anime" => config.SyncPathAnime,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                _logger.LogWarning("[UnifiedItem] No library path for type '{Type}'", type);
                return null;
            }

            var catalogItem = new CatalogItem
            {
                Id = Guid.NewGuid().ToString(),
                AioId = aioId,
                Title = title,
                Year = year,
                MediaType = type.ToLowerInvariant(),
                Source = "discover",
            };

            var strmPath = await _strmWriter.WriteAsync(catalogItem, SourceType.Aio, userId, ct);
            if (strmPath == null)
            {
                _logger.LogWarning("[UnifiedItem] .strm write failed for {AioId}", aioId);
                return null;
            }

            // Update catalog item with paths
            catalogItem.StrmPath = strmPath;
            catalogItem.LocalPath = strmPath;
            catalogItem.LocalSource = "strm";
            await _db.UpsertCatalogItemAsync(catalogItem);

            _logger.LogInformation("[UnifiedItem] Added {AioId} ({Title}) to library", aioId, title);

            // Step 4: Add to "My InfiniteDrive" playlist (fire-and-forget)
            var playlistService = Plugin.Instance?.PlaylistService;
            if (playlistService != null && !string.IsNullOrEmpty(userId))
            {
                var embyItem = FindInLibrary(aioId, type);
                if (embyItem != null)
                {
                    _logger.LogDebug("[UnifiedItem] Playlist add deferred to caller for {AioId}", aioId);
                }
            }

            // Step 5: Fire-and-forget pre-cache trigger
            var cacheService = Plugin.Instance?.StreamCacheService;
            if (cacheService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await cacheService.PreCacheSingleAsync(aioId, type, null, null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[UnifiedItem] Pre-cache trigger failed for {AioId}", aioId);
                    }
                }, CancellationToken.None);
            }

            return FindInLibrary(aioId, type)?.Id;
        }

        /// <summary>
        /// Finds an item in the Emby library by trying all provider IDs derived from the AIO ID.
        /// </summary>
        private BaseItem? FindInLibrary(string aioId, string type)
        {
            try
            {
                var includeTypes = type == "series"
                    ? new[] { "Series" }
                    : new[] { "Movie" };

                // Build provider ID pairs to try: direct AIO ID as Imdb, plus parsed providers
                var providerIds = new List<KeyValuePair<string, string>>();

                // If the AIO ID looks like an IMDB ID (tt-prefixed), try as Imdb
                if (aioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                    providerIds.Add(new KeyValuePair<string, string>("Imdb", aioId));

                // Try parsing as provider:id format (e.g. "kitsu:46474")
                var colonIdx = aioId.IndexOf(':');
                if (colonIdx > 0)
                {
                    var provider = aioId.Substring(0, colonIdx);
                    var id = aioId.Substring(colonIdx + 1);
                    // Normalize provider name: kitsu -> Kitsu, mal -> MAL, anilist -> AniList
                    var normalizedProvider = provider.ToLowerInvariant() switch
                    {
                        "mal" => "MAL",
                        _ => char.ToUpper(provider[0]) + provider[1..]
                    };
                    providerIds.Add(new KeyValuePair<string, string>(normalizedProvider, id));
                }

                // If no specific provider IDs built, try the raw ID as Imdb
                if (providerIds.Count == 0)
                    providerIds.Add(new KeyValuePair<string, string>("Imdb", aioId));

                foreach (var pid in providerIds)
                {
                    var result = _libraryManager.GetItemList(
                        new MediaBrowser.Controller.Entities.InternalItemsQuery
                        {
                            AnyProviderIdEquals = new[] { pid },
                            IncludeItemTypes = includeTypes,
                            Recursive = true
                        }).FirstOrDefault();
                    if (result != null) return result;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
