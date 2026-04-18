using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Creates/updates virtual library items and registers the AIO media source provider.
    ///
    /// Architecture:
    ///   - Catalog sync = metadata only (no stream resolution, no .strm files)
    ///   - Items have deterministic paths: /emby-aio/{externalId}
    ///   - Deduplication via path lookup — re-running updates, never duplicates
    ///   - Playback streams resolved on-demand by AioMediaSourceProvider
    ///
    /// Auto-discovered by Emby's DI container via IServerEntryPoint.
    /// </summary>
    public class VirtualAioEntryPoint : IServerEntryPoint
    {
        private const string VirtualFolderName = "Infinite AIO Drive";
        private const string AioPathPrefix = "/emby-aio/";
        private const string ProviderKey = "AIO";

        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ILogger<VirtualAioEntryPoint> _logger;
        private readonly ILogManager _logManager;

        private AioMediaSourceProvider _provider;

        public VirtualAioEntryPoint(
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<VirtualAioEntryPoint>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── IServerEntryPoint ────────────────────────────────────────────────

        public void Run()
        {
            try
            {
                _logger.LogInformation("[AIO] VirtualAioEntryPoint starting");

                // 1. Register media source provider
                _provider = new AioMediaSourceProvider(_logManager);
                _mediaSourceManager.AddParts(new[] { _provider });
                _logger.LogInformation("[AIO] MediaSourceProvider registered");

                // 2. Ensure virtual library folder exists
                var parent = EnsureVirtualFolder();

                // 3. Create or update virtual items (deduplicated)
                var items = SyncVirtualItems(parent);
                _logger.LogInformation("[AIO] Synced {Count} virtual items", items.Count);

                // 4. Self-test
                RunSelfTest(items);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AIO] VirtualAioEntryPoint failed during startup");
            }
        }

        public void Dispose() { }

        // ── Virtual folder ───────────────────────────────────────────────────

        private CollectionFolder EnsureVirtualFolder()
        {
            var existing = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => string.Equals(
                    f.Name, VirtualFolderName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                _logger.LogInformation("[AIO] Virtual folder '{Name}' already exists", VirtualFolderName);
                return ResolveCollectionFolder(existing);
            }

            var options = new LibraryOptions();
            var folder = _libraryManager.AddVirtualFolder(VirtualFolderName, options, true);
            _logger.LogInformation(
                "[AIO] Created virtual folder '{Name}' (Id: {Id})",
                VirtualFolderName, folder.Id);
            return folder;
        }

        private CollectionFolder ResolveCollectionFolder(
            MediaBrowser.Model.Entities.VirtualFolderInfo vfi)
        {
            // Try each ID property the Emby SDK exposes
            if (Guid.TryParse(vfi.ItemId, out var g1)
                && _libraryManager.GetItemById(g1) is CollectionFolder cf1)
                return cf1;

            if (long.TryParse(vfi.Id, out var longId)
                && _libraryManager.GetItemById(longId) is CollectionFolder cf2)
                return cf2;

            if (Guid.TryParse(vfi.Guid, out var g2)
                && _libraryManager.GetItemById(g2) is CollectionFolder cf3)
                return cf3;

            _logger.LogWarning("[AIO] Could not resolve CollectionFolder for '{Name}'", vfi.Name);
            return null;
        }

        // ── Create/update virtual items (deduplicated) ───────────────────────

        /// <summary>
        /// Mock catalog data with deterministic externalIds.
        /// Production: replace with AIOStreams catalog fetch (metadata only, no streams).
        /// </summary>
        private static readonly (string ExternalId, string Title, string Overview, int Year)[] SampleCatalog =
        {
            ("tt0111161", "The Shawshank Redemption",
                "Two imprisoned men bond over a number of years.", 1994),
            ("tt0068646", "The Godfather",
                "The aging patriarch of an organized crime dynasty transfers control.", 1972),
            ("tt0468569", "The Dark Knight",
                "Batman raises the stakes in his war on crime.", 2008),
        };

        private List<Movie> SyncVirtualItems(CollectionFolder parent)
        {
            var items = new List<Movie>();

            foreach (var entry in SampleCatalog)
            {
                var path = $"{AioPathPrefix}{entry.ExternalId}";

                // Deduplication: look up existing item by deterministic path
                var existing = FindItemByPath(path);

                if (existing != null)
                {
                    // Update metadata in place
                    existing.Name = entry.Title;
                    existing.Overview = entry.Overview;
                    existing.ProductionYear = entry.Year;
                    existing.Genres = new[] { "Drama", "Crime" };

                    _libraryManager.UpdateItem(existing, parent,
                        MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit);
                    items.Add(existing);

                    _logger.LogInformation(
                        "[AIO TEST] Item updated: {Title} | Path: {Path}",
                        existing.Name, existing.Path);
                }
                else
                {
                    // Create new item — set Id explicitly (CreateItem doesn't propagate it back)
                    var movie = new Movie
                    {
                        Id = Guid.NewGuid(),
                        Name = entry.Title,
                        Overview = entry.Overview,
                        Path = path,
                        ProductionYear = entry.Year,
                        Genres = new[] { "Drama", "Crime" },
                        ProviderIds = new ProviderIdDictionary
                        {
                            { ProviderKey, entry.ExternalId },
                        },
                    };

                    _libraryManager.CreateItem(movie, parent);
                    items.Add(movie);

                    _logger.LogInformation(
                        "[AIO TEST] Item created: {Title} | Path: {Path}",
                        movie.Name, movie.Path);
                }
            }

            return items;
        }

        /// <summary>
        /// Finds an existing item by its deterministic virtual path.
        /// Paths are /emby-aio/{externalId} — unique per item.
        /// </summary>
        private Movie FindItemByPath(string path)
        {
            try
            {
                var results = _libraryManager.GetItemList(
                    new InternalItemsQuery
                    {
                        Path = path,
                        IncludeItemTypes = new[] { typeof(Movie).Name },
                        Limit = 1,
                    });

                return results?.FirstOrDefault() as Movie;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AIO] Path lookup failed for {Path}", path);
                return null;
            }
        }

        // ── Self-test ────────────────────────────────────────────────────────

        private void RunSelfTest(List<Movie> createdItems)
        {
            var passCount = 0;
            var totalSources = 0;

            foreach (var item in createdItems)
            {
                // Verify item persisted in library
                var found = _libraryManager.GetItemById(item.Id);
                if (found == null)
                {
                    _logger.LogWarning(
                        "[AIO TEST] FAILED to retrieve '{Title}' (Id: {Id})",
                        item.Name, item.Id);
                    continue;
                }

                // Test media source resolution (this is what happens at playback time)
                var sources = _provider.GetMediaSources(found, CancellationToken.None).Result;
                totalSources += sources.Count;
                passCount++;

                _logger.LogInformation(
                    "[AIO TEST] Media sources returned: {Count} for '{Title}'",
                    sources.Count, found.Name);
            }

            if (passCount == createdItems.Count)
            {
                _logger.LogInformation(
                    "[AIO TEST] RESULT: PASS — {ItemCount} items synced, {SourceCount} total MediaSourceInfo objects",
                    passCount, totalSources);
            }
            else
            {
                _logger.LogWarning(
                    "[AIO TEST] RESULT: PARTIAL — {Pass}/{Total} items, {SourceCount} sources",
                    passCount, createdItems.Count, totalSources);
            }
        }

        // ── Public test method ───────────────────────────────────────────────

        /// <summary>
        /// Manual verification hook for admin API or test harness.
        /// </summary>
        public Task<string> TestVirtualItemCreationAsync()
        {
            var folders = _libraryManager.GetVirtualFolders();
            var aioFolder = folders.FirstOrDefault(f => f.Name == VirtualFolderName);
            var status = aioFolder != null
                ? $"Virtual folder '{VirtualFolderName}' exists (ItemId: {aioFolder.ItemId}). Provider: {_provider != null}"
                : $"Virtual folder '{VirtualFolderName}' NOT found.";
            return Task.FromResult(status);
        }
    }
}
