using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Phase 0 PoC: Creates test items to validate LocationType + HTTP Path approach.
    /// Test 1: The Matrix (IMDB-backed, standard metadata)
    /// Test 2: Rooster Fighter (Kitsu-only anime, no IMDB — tests AioMetadataProvider)
    /// </summary>
    public class VirtualItemPoC : IScheduledTask
    {
        private const string TaskName = "InfiniteDrive: Virtual Item PoC";
        private const string TaskKey = "InfiniteDriveVirtualItemPoC";
        private const string TaskCategory = "InfiniteDrive";

        // Test 1: The Matrix (1999)
        private const string MatrixId = "tt0133093";
        private const string MatrixTitle = "The Matrix";
        private const int MatrixYear = 1999;
        private const string MatrixOverview =
            "When a beautiful stranger leads computer hacker Neo to a forbidding underworld, " +
            "he discovers the shocking truth -- the life he knows is the elaborate deception " +
            "of an evil cyber-intelligence.";

        // Test 2: Rooster Fighter (2026 anime, Kitsu only)
        private const string AnimeId = "kitsu:49071";
        private const string AnimeTitle = "Rooster Fighter";
        private const int AnimeYear = 2026;
        private const string AnimeOverview =
            "A timid rooster transforms into a powerful chicken warrior to battle demons " +
            "threatening humanity in this action-comedy anime.";

        private readonly ILogger<VirtualItemPoC> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;

        public VirtualItemPoC(ILibraryManager libraryManager, IProviderManager providerManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _logger = new EmbyLoggerAdapter<VirtualItemPoC>(logManager.GetLogger("InfiniteDrive"));
        }

        public string Name => TaskName;
        public string Key => TaskKey;
        public string Description => "Creates test items (The Matrix + Rooster Fighter anime) to validate HTTP-path virtual items.";
        public string Category => TaskCategory;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
            => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogError("[VirtualItemPoC] Plugin not initialized");
                return Task.CompletedTask;
            }

            // Force-enable Kitsu metadata resolver (config API is broken)
            if (string.IsNullOrEmpty(config.MetadataIdTypeCensus) || config.MetadataIdTypeCensus == "{}")
            {
                config.MetadataIdTypeCensus = "{\"Kitsu\":\"1\"}";
                config.MetadataEnabledIdTypes = "[\"Kitsu\"]";
                Plugin.Instance?.SaveConfiguration();
                _logger.LogInformation("[VirtualItemPoC] Enabled Kitsu metadata resolver");
            }

            // Seed catalog DB for anime (so AioImageProvider + AioMetadataProvider can find it)
            SeedCatalogForAnime();

            // Test 1: The Matrix
            progress.Report(10);
            CreateTestMovie(MatrixTitle, MatrixYear, MatrixOverview, MatrixId, config.LibraryRootMovies);

            // Test 2: Rooster Fighter (use anime library if configured, else movies)
            progress.Report(50);
            var animePath = !string.IsNullOrWhiteSpace(config.SyncPathAnime)
                ? config.SyncPathAnime
                : config.LibraryRootMovies;
            CreateTestMovie(AnimeTitle, AnimeYear, AnimeOverview, AnimeId, animePath);

            progress.Report(100);
            return Task.CompletedTask;
        }

        private void SeedCatalogForAnime()
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[VirtualItemPoC] No DatabaseManager — skipping catalog seed");
                return;
            }

            try
            {
                var rawMeta = "{\"name\":\"Rooster Fighter\"," +
                    "\"description\":\"A timid rooster transforms into a powerful chicken warrior to battle demons threatening humanity in this action-comedy anime.\"," +
                    "\"year\":\"2026\"," +
                    "\"poster\":\"https://media.kitsu.app/anime/49071/poster_image/large-6d2a77869edd2d469aba00454e282aa9.jpeg\"," +
                    "\"background\":\"https://media.kitsu.app/anime/49071/cover_image/a295bfad81a7ff3b8dc07e379ae66afe.jpg\"," +
                    "\"genres\":[\"Action\",\"Comedy\",\"Sci-Fi\"]}";

                var item = new Models.CatalogItem
                {
                    Id = "kitsu:49071",
                    ImdbId = "",
                    Title = AnimeTitle,
                    Year = AnimeYear,
                    MediaType = "anime",
                    Source = "aiostreams",
                    UniqueIdsJson = "[{\"provider\":\"kitsu\",\"id\":\"49071\"}]",
                    RawMetaJson = rawMeta,
                };

                // Check if already exists
                var existing = db.GetCatalogItemByProviderIdAsync("Kitsu", "49071").GetAwaiter().GetResult();
                if (existing != null)
                {
                    _logger.LogInformation("[VirtualItemPoC] Catalog already has Rooster Fighter — skipping seed");
                    return;
                }

                db.UpsertCatalogItemAsync(item).GetAwaiter().GetResult();
                _logger.LogInformation("[VirtualItemPoC] Seeded catalog: Rooster Fighter (kitsu:49071)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VirtualItemPoC] Catalog seed failed");
            }
        }

        private void CreateTestMovie(
            string title, int year, string overview,
            string itemId, string libraryPath)
        {
            var parentFolder = ResolveParentFolder(libraryPath);
            if (parentFolder == null)
            {
                _logger.LogError("[VirtualItemPoC] No library for path: {Path}", libraryPath);
                return;
            }

            // Idempotent
            var existing = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AnyProviderIdEquals = new[]
                {
                    new KeyValuePair<string, string>("INFINITEDRIVE", itemId)
                },
                IncludeItemTypes = new[] { "Movie" },
                Limit = 1
            }).FirstOrDefault();

            if (existing != null)
            {
                _logger.LogInformation(
                    "[VirtualItemPoC] Already exists: {Name} ({Id}), LocationType={Loc}, IsVirtualItem={Virt}, Path={Path}",
                    existing.Name, existing.Id, existing.LocationType, existing.IsVirtualItem, existing.Path ?? "(null)");

                // Force image refresh — clears stale URL-as-path images, lets AioImageProvider repopulate
                _providerManager.QueueRefresh(existing.InternalId,
                    new MetadataRefreshOptions((MediaBrowser.Model.IO.IFileSystem?)null!)
                    {
                        ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ForceSave = true,
                        ReplaceAllImages = true,
                    }, RefreshPriority.High);
                return;
            }

            var movie = new Movie
            {
                Name = title,
                ProductionYear = year,
                Overview = overview,
                // Dummy scheme → LocationType.Virtual (no ffprobe spam)
                // GetMediaSources returns real CDN URLs at playback time
                Path = $"infinitedrive://{itemId}",
                IsVirtualItem = true,
                IsLocked = false,
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
            };

            // Provider IDs
            movie.ProviderIds["INFINITEDRIVE"] = itemId;
            if (itemId.StartsWith("kitsu:"))
                movie.ProviderIds["Kitsu"] = itemId.Substring(6);
            else
                movie.ProviderIds["Imdb"] = itemId;

            // Deterministic ID
            var id = _libraryManager.GetNewItemId($"infinitedrive://{itemId}", typeof(Movie));
            movie.Id = id;
            movie.ParentId = parentFolder.InternalId;

            _logger.LogInformation(
                "[VirtualItemPoC] Creating: {Name}, ID={Id}, Path={Path}, ParentId={ParentId}",
                movie.Name, movie.Id, movie.Path, movie.ParentId);

            // Gelato-style: set images directly on item before save
            SetImagesFromCatalog(movie, itemId);

            try
            {
                _libraryManager.CreateItem(movie, parentFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VirtualItemPoC] CreateItem failed for {Name}", title);
                return;
            }

            // Verify and force IsVirtualItem if needed
            var verify = _libraryManager.GetItemById(id);
            if (verify == null)
            {
                _logger.LogError("[VirtualItemPoC] Item not found after save: {Id}", id);
                return;
            }

            // Force LocationType.Virtual — Emby may override IsVirtualItem on save
            if (!verify.IsVirtualItem)
            {
                verify.IsVirtualItem = true;
                _libraryManager.UpdateItem(verify, parentFolder, ItemUpdateType.MetadataEdit);
                _logger.LogInformation("[VirtualItemPoC] Forced IsVirtualItem=true for {Name}", verify.Name);
            }

            _logger.LogInformation(
                "[VirtualItemPoC] Post-CreateItem: {Name}, LocationType={Loc}, Path={Path}",
                verify.Name, verify.LocationType, verify.Path ?? "(null)");

            // Trigger metadata + image refresh
            _providerManager.QueueRefresh(verify.InternalId, new MetadataRefreshOptions((MediaBrowser.Model.IO.IFileSystem?)null!)
            {
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ForceSave = true,
                ReplaceAllImages = true,
            }, RefreshPriority.High);

            _logger.LogInformation(
                "[VirtualItemPoC] SUCCESS: {Name} ({Id}), LocationType={Loc}, Path={Path}",
                verify.Name, verify.Id, verify.LocationType, verify.Path ?? "(null)");
        }

        private void SetImagesFromCatalog(Movie movie, string itemId)
        {
            // Images are provided by AioImageProvider (IRemoteImageProvider) which
            // reads raw_meta_json from the catalog DB and returns RemoteImageInfo.
            // ItemImageInfo.Path expects local filesystem paths, NOT remote URLs.
        }

        private Folder? ResolveParentFolder(string configPath)
        {
            var virtualFolders = _libraryManager.GetVirtualFolders();
            _logger.LogInformation("[VirtualItemPoC] Found {Count} virtual folders", virtualFolders.Count);

            var candidates = new List<string> { configPath };
            foreach (var vf in virtualFolders)
            {
                _logger.LogInformation(
                    "[VirtualItemPoC]   VirtualFolder: Name={Name}, Type={Type}, Locations={Locs}",
                    vf.Name, vf.CollectionType,
                    vf.Locations != null ? string.Join(",", vf.Locations) : "(none)");

                if (string.Equals(vf.CollectionType, "movies", StringComparison.OrdinalIgnoreCase) && vf.Locations != null)
                    candidates.InsertRange(1, vf.Locations);
                else if (vf.Locations != null)
                    candidates.AddRange(vf.Locations);
            }

            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                var folder = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Path = path,
                    Limit = 1
                }).OfType<Folder>().FirstOrDefault();

                if (folder != null)
                {
                    _logger.LogInformation("[VirtualItemPoC] Matched library: {Name} at {Path}", folder.Name, path);
                    return folder;
                }
            }

            return null;
        }
    }
}
