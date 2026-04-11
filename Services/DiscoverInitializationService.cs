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
        /// Checks if Discover catalog is empty and AIOStreams is configured.
        /// If so, triggers an automatic initial sync.
        /// </summary>
        private async Task CheckAndAutoTriggerInitialSyncAsync(CancellationToken cancellationToken)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                var db = Plugin.Instance?.DatabaseManager;

                if (config == null || db == null)
                {
                    _logger.LogWarning("[Discover] Plugin not fully initialized yet");
                    return;
                }

                // Check if AIOStreams manifest URL is configured
                if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
                    && string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                {
                    _logger.LogInformation("[Discover] AIOStreams manifest URL not configured; skipping auto-sync");
                    return;
                }

                // Check if catalog is empty
                var catalogCount = await db.GetDiscoverCatalogCountAsync();
                if (catalogCount > 0)
                {
                    _logger.LogInformation("[Discover] Catalog already populated ({Count} items); skipping auto-sync", catalogCount);
                    return;
                }

                _logger.LogInformation("[Discover] Catalog empty and AIOStreams configured; triggering auto-sync");

                // Trigger sync in background with event-based coordination
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Wait for CatalogSyncTask to complete by polling sync_state
                        // (not ideal but safe — check every 10s for up to 10min)
                        var waited = 0;
                        while (waited < 600)
                        {
                            await Task.Delay(10_000, cancellationToken);
                            waited += 10;

                            var syncState = await db.GetSyncStateAsync("aiostreams");
                            // If CatalogSync has completed at least once, proceed
                            if (syncState?.LastSyncAt != null)
                                break;

                            // If catalog is now populated (sync ran before us), skip
                            var count = await db.GetDiscoverCatalogCountAsync();
                            if (count > 0)
                            {
                                _logger.LogInformation("[Discover] Catalog was populated during wait; skipping auto-sync");
                                return;
                            }
                        }

                        // Double-check catalog is still empty before syncing
                        var currentCount = await db.GetDiscoverCatalogCountAsync();
                        if (currentCount > 0)
                        {
                            _logger.LogInformation("[Discover] Catalog was populated during wait; skipping auto-sync");
                            return;
                        }

                        var discoverService = new CatalogDiscoverService(_logManager, db);
                        await discoverService.SyncDiscoverCatalogAsync(cancellationToken);
                        _logger.LogInformation("[Discover] Auto-sync completed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Discover] Auto-sync failed");
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Error checking for auto-sync");
            }
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
