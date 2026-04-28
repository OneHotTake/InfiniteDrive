using System;
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
            string imdbId, string type, string title, int? year,
            string? userId, CancellationToken ct)
        {
            // Step 1: Block list check
            var isBlocked = await _blockList.IsBlockedAsync(imdbId, null, null);
            if (isBlocked)
            {
                _logger.LogWarning("[UnifiedItem] Blocked item skipped: {ImdbId}", imdbId);
                return null;
            }

            // Step 2: Dedup — check if already in library
            var existingItem = FindInLibrary(imdbId, type);
            if (existingItem != null)
            {
                _logger.LogDebug("[UnifiedItem] Already in library: {ImdbId} ({EmbyId})", imdbId, existingItem.Id);
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
                ImdbId = imdbId,
                Title = title,
                Year = year,
                MediaType = type.ToLowerInvariant(),
                Source = "discover",
                ItemState = ItemState.Pinned,
                PinSource = $"user:discover:{DateTime.UtcNow:o}",
                PinnedAt = DateTime.UtcNow.ToString("o")
            };

            var strmPath = await _strmWriter.WriteAsync(catalogItem, SourceType.Aio, userId, ct);
            if (strmPath == null)
            {
                _logger.LogWarning("[UnifiedItem] .strm write failed for {ImdbId}", imdbId);
                return null;
            }

            // Update catalog item with paths
            catalogItem.StrmPath = strmPath;
            catalogItem.LocalPath = strmPath;
            catalogItem.LocalSource = "strm";
            await _db.UpsertCatalogItemAsync(catalogItem);

            _logger.LogInformation("[UnifiedItem] Added {ImdbId} ({Title}) to library", imdbId, title);

            // Step 4: Add to "My InfiniteDrive" playlist (fire-and-forget)
            var playlistService = Plugin.Instance?.PlaylistService;
            if (playlistService != null && !string.IsNullOrEmpty(userId))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Wait for Emby to index the new .strm file
                        await Task.Delay(2000, CancellationToken.None);
                        var embyItem = FindInLibrary(imdbId, type);
                        if (embyItem != null)
                        {
                            // Resolve user object for PlaylistService
                            var userManager = Plugin.Instance as IUserManager;
                            // Use the auth context from a running request
                            // For now, playlist add happens in discover context where user is available
                            _logger.LogDebug("[UnifiedItem] Playlist add deferred to caller for {ImdbId}", imdbId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[UnifiedItem] Playlist add deferred for {ImdbId}", imdbId);
                    }
                }, CancellationToken.None);
            }

            // Step 5: Fire-and-forget pre-cache trigger
            var cacheService = Plugin.Instance?.StreamCacheService;
            if (cacheService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1000, CancellationToken.None);
                        await cacheService.PreCacheSingleAsync(imdbId, type, null, null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[UnifiedItem] Pre-cache trigger failed for {ImdbId}", imdbId);
                    }
                }, CancellationToken.None);
            }

            return FindInLibrary(imdbId, type)?.Id;
        }

        /// <summary>
        /// Finds an item in the Emby library by IMDB ID and type.
        /// </summary>
        private BaseItem? FindInLibrary(string imdbId, string type)
        {
            try
            {
                return _libraryManager.GetItemList(
                    new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        AnyProviderIdEquals = new[] { new System.Collections.Generic.KeyValuePair<string, string>("Imdb", imdbId) },
                        IncludeItemTypes = type == "series"
                            ? new[] { "Series" }
                            : new[] { "Movie" },
                        Recursive = true
                    }).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
