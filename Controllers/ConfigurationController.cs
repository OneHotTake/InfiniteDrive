using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Configuration;
using EmbyStreams.Data;
using EmbyStreams.Models;
using MediaBrowser.Model.Services;

namespace EmbyStreams.Controllers
{
    /// <summary>
    /// Controller for loading/saving ViewModels and handling button clicks.
    /// </summary>
    [Route("embystreams/config")]
    public class ConfigurationController : IService, IRequiresRequest
    {
        private readonly DatabaseManager _db;

        public ConfigurationController(DatabaseManager db)
        {
            _db = db;
        }

        public IRequest Request { get; set; } = null!;

        #region Wizard

        /// <summary>
        /// GET /embystreams/config/wizard
        /// Loads the wizard configuration.
        /// </summary>
        [Route("wizard")]
        public async Task<WizardViewModel> GetWizard(CancellationToken ct)
        {
            var config = EmbyStreams.Plugin.Instance?.Configuration;
            if (config == null)
            {
                return await Task.FromResult(new WizardViewModel());
            }

            var viewModel = new WizardViewModel
            {
                ApiKey = config.PrimaryManifestUrl,
                MoviesLibraryPath = config.SyncPathMovies,
                SeriesLibraryPath = config.SyncPathShows,
                AnimeLibraryPath = config.SyncPathAnime,
                EnableAutoSync = config.EnableAioStreamsCatalog,
                SyncIntervalHours = config.CatalogSyncIntervalHours,
                PluginVersion = "0.51.0.0",
                LastSyncAt = null, // TODO: Get from database
                Status = "OK"
            };

            return await Task.FromResult(viewModel);
        }

        /// <summary>
        /// POST /embystreams/config/wizard
        /// Saves the wizard configuration.
        /// </summary>
        [Route("wizard")]
        public async Task SaveWizard(WizardViewModel viewModel, CancellationToken ct)
        {
            var config = EmbyStreams.Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            config.PrimaryManifestUrl = viewModel.ApiKey;
            config.SyncPathMovies = viewModel.MoviesLibraryPath;
            config.SyncPathShows = viewModel.SeriesLibraryPath;
            config.SyncPathAnime = viewModel.AnimeLibraryPath;
            config.EnableAioStreamsCatalog = viewModel.EnableAutoSync;
            config.CatalogSyncIntervalHours = viewModel.SyncIntervalHours;

            EmbyStreams.Plugin.Instance?.SaveConfiguration();
            await Task.CompletedTask;
        }

        #endregion

        #region Content Management

        /// <summary>
        /// GET /embystreams/config/content-management
        /// Loads the content management page.
        /// </summary>
        [Route("content-management")]
        public async Task<ContentManagementViewModel> GetContentManagement(CancellationToken ct)
        {
            var viewModel = new ContentManagementViewModel();

            // Load sources
            var sources = await _db.GetSourcesWithShowAsCollectionAsync(ct);
            viewModel.Sources = sources.Select(s => new SourceRow
            {
                Name = s.Name,
                ItemCount = 0, // TODO: Get actual item count from database
                LastSyncedAt = s.LastSyncedAt,
                Enabled = s.Enabled,
                ShowAsCollection = s.ShowAsCollection
            }).ToList();

            // Load collections
            var collections = await _db.GetAllCollectionsListAsync(ct);
            viewModel.Collections = collections.Select(c => new CollectionRow
            {
                CollectionName = c.CollectionName ?? c.Name,
                SourceName = c.Name,
                LastSyncedAt = null // TODO: Get last synced timestamp
            }).ToList();

            // Load all items
            var allItems = await _db.GetItemsAsync(null, string.Empty, string.Empty, 50, 0, ct);
            viewModel.AllItems = allItems.Select(i => new ItemRow
            {
                Title = i.Title,
                Year = i.Year ?? 0,
                MediaType = i.MediaType,
                Status = i.Status,
                SaveReason = i.SaveReason?.ToString() ?? string.Empty,
                Superseded = i.Superseded,
                SupersededConflict = i.SupersededConflict
            }).ToList();

            // Load needs review (superseded_conflict = true)
            var needsReview = allItems.Where(i => i.SupersededConflict).ToList();
            viewModel.NeedsReview = needsReview.Select(i => new ItemRow
            {
                Title = i.Title,
                Year = i.Year ?? 0,
                MediaType = i.MediaType,
                Status = i.Status,
                SaveReason = i.SaveReason?.ToString() ?? string.Empty,
                Superseded = i.Superseded,
                SupersededConflict = i.SupersededConflict
            }).ToList();

            viewModel.PluginVersion = "0.51.0.0";
            viewModel.LastSyncAt = null; // TODO: Get from database
            viewModel.Status = "OK";

            return viewModel;
        }

        /// <summary>
        /// POST /embystreams/config/sync
        /// Triggers sync now.
        /// </summary>
        [Route("sync")]
        public async Task SyncNow(CancellationToken ct)
        {
            // TODO: Trigger sync task
            await Task.CompletedTask;
        }

        /// <summary>
        /// POST /embystreams/config/purge-cache
        /// Purges stream URL cache.
        /// </summary>
        [Route("purge-cache")]
        public async Task PurgeCache(CancellationToken ct)
        {
            // TODO: Purge stream URL cache
            await Task.CompletedTask;
        }

        /// <summary>
        /// POST /embystreams/config/reset
        /// Resets the database.
        /// </summary>
        [Route("reset")]
        public async Task ResetDatabase(CancellationToken ct)
        {
            // TODO: Reset database
            await Task.CompletedTask;
        }

        #endregion

        #region My Library

        /// <summary>
        /// GET /embystreams/config/my-library
        /// Loads the my library page for the current user.
        /// </summary>
        [Route("my-library")]
        public async Task<MyLibraryViewModel> GetMyLibrary(CancellationToken ct)
        {
            var userId = GetUserId();
            var viewModel = new MyLibraryViewModel();

            // Load saved items for this user
            var savedItems = await _db.GetItemsAsync(ItemStatus.Active, string.Empty, string.Empty, 50, 0, ct);
            // TODO: Filter by user
            viewModel.SavedItems = savedItems.Select(i => new ItemRow
            {
                Title = i.Title,
                Year = i.Year ?? 0,
                MediaType = i.MediaType,
                Status = i.Status
            }).ToList();

            // Load blocked items for this user
            // TODO: Implement blocked items query
            viewModel.BlockedItems = new List<ItemRow>();

            // Load watch history for this user
            // TODO: Implement watch history
            viewModel.WatchHistory = new List<WatchHistoryRow>();

            viewModel.PluginVersion = "0.51.0.0";
            viewModel.LastSyncAt = null; // TODO: Get from database
            viewModel.Status = "OK";

            return viewModel;
        }

        private string GetUserId()
        {
            // TODO: Get user ID from request context
            // For now, return "default"
            return "default";
        }

        #endregion
    }
}
