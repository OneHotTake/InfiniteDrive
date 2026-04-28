using System;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Initializes the Discover feature on server startup.
    ///
    /// Responsibilities:
    /// - Auto-trigger initial Discover catalog sync if AIOStreams is configured
    /// - Automatically scan libraries when .strm files are added
    /// - Ensure seamless first-run experience without user intervention
    /// </summary>
    public class DiscoverInitializationService : IServerEntryPoint
    {
        private readonly ILogger<DiscoverInitializationService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;

        public DiscoverInitializationService(
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<DiscoverInitializationService>(logManager.GetLogger("InfiniteDrive"));
        }

        /// <summary>
        /// Runs on server startup to initialize Discover features.
        /// </summary>
        public void Run()
        {
            try
            {
                _logger.LogInformation("[Discover] Initialization service started");

                // Hook into library item additions to trigger scans
                _libraryManager.ItemAdded += OnItemAdded;

                // Fire-and-forget: check if we should auto-trigger initial sync
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CheckAndAutoTriggerInitialSyncAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Discover] Error during initialization sync check");
                    }
                });

                _logger.LogInformation("[Discover] Initialization service ready");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Failed to start initialization service");
            }
        }

        /// <summary>
        /// Cleans up event subscriptions on server shutdown.
        /// </summary>
        public void Dispose()
        {
            try
            {
                _libraryManager.ItemAdded -= OnItemAdded;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Error disposing initialization service");
            }
        }

        /// <summary>
        /// Discover now uses live AIOStreams search — no catalog sync needed.
        /// </summary>
        private Task CheckAndAutoTriggerInitialSyncAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Discover] Using live AIOStreams search — no catalog sync required");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Triggered when items are added to the library.
        /// Auto-triggers a library refresh when .strm files are created.
        /// </summary>
        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e.Item;
                if (item?.Path == null)
                    return;

                // Check if this is a .strm file we created
                if (!item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                    return;

                _logger.LogInformation("[Discover] New .strm file detected: {Path}", item.Path);

                // Item is already added and indexed by Emby, no additional action needed
                // The library refresh happens automatically when we write the .strm file
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Error handling item addition");
            }
        }
    }
}
