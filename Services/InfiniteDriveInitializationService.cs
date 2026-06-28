using System;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Services;
using InfiniteDrive.Repositories;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Core plugin initialization service.
    /// Runs on server startup to initialize database, repositories, and plugin configuration.
    /// Defers heavy initialization from Plugin constructor per Emby conventions.
    /// </summary>
    public class InfiniteDriveInitializationService : IServerEntryPoint
    {
        private readonly ILogger<InfiniteDriveInitializationService> _logger;
        private readonly ILogManager _logManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IPlaylistManager _playlistManager;
        private readonly MediaBrowser.Controller.Collections.ICollectionManager? _collectionManager;
        private readonly IUserManager _userManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly INotificationManager _notificationManager;

        public InfiniteDriveInitializationService(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            MediaBrowser.Controller.Collections.ICollectionManager? collectionManager,
            ILocalizationManager localizationManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            INotificationManager notificationManager,
            IUserManager userManager)
        {
            _logManager = logManager;
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _collectionManager = collectionManager;
            _userManager = userManager;
            _localizationManager = localizationManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _notificationManager = notificationManager;
            _logger = new EmbyLoggerAdapter<InfiniteDriveInitializationService>(
                logManager.GetLogger("InfiniteDrive"));
        }

        /// <summary>
        /// Runs on server startup to initialize core plugin components.
        /// IServerEntryPoint.Run() is called before any scheduled tasks fire.
        /// </summary>
        public void Run()
        {
            try
            {
                var instance = Plugin.Instance;
                if (instance == null)
                {
                    _logger.LogError("[InfiniteDrive] Plugin.Instance is null — initialization failed");
                    return;
                }

                _logger.LogInformation("[InfiniteDrive] Core initialization starting");

                // Store localization manager for UI dropdowns
                instance.LocalizationManager = _localizationManager;

                // Store ProviderManager + FileSystem for targeted metadata refresh
                instance.ProviderManager = _providerManager;
                instance.FileSystem = _fileSystem;

                // Initialize notification service
                NotificationService.Initialize(_notificationManager);

                // Initialize database — ApplicationPaths guaranteed settled here
                instance.InitialiseDatabaseManager();

                // Initialize CooldownGate (Sprint 155: CooldownGate throttling)
                instance.CooldownGate = new CooldownGate(
                    () => instance.Configuration,
                    _logger);
                instance.CooldownGate.ProgressStreamer = Plugin.ProgressStreamer;

                // Initialize PlaylistService with SDK interfaces
                instance.InitializePlaylistService(_playlistManager, _libraryManager, _collectionManager, _userManager);

                // Ensure at least one quality bucket exists — never let Marvin run blind
                var cfg = instance.Configuration;
                if (cfg.DesiredVersions == null || cfg.DesiredVersions.Count == 0)
                {
                    cfg.DesiredVersions = new System.Collections.Generic.List<Models.DesiredVersionBucket>
                    {
                        new Models.DesiredVersionBucket { Resolution = "1080p", Audio = "Any Audio", Count = 2 }
                    };
                    instance.SaveConfiguration();
                    _logger.LogInformation("[InfiniteDrive] No quality buckets found — created default (1080p × 2)");
                }

                _logger.LogInformation("[InfiniteDrive] Core initialization complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Initialization failed");
                // Do not rethrow — a failed init should not crash the server
            }
        }

        /// <summary>
        /// No resources to clean up.
        /// </summary>
        public void Dispose() { }
    }
}
