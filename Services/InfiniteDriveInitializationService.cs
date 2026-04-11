using System;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Services;
using InfiniteDrive.Repositories;
using MediaBrowser.Controller.Plugins;
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

        public InfiniteDriveInitializationService(ILogManager logManager)
        {
            _logManager = logManager;
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

                // Initialize database — ApplicationPaths guaranteed settled here
                instance.InitialiseDatabaseManager();

                // Auto-generate PluginSecret if absent
                instance.EnsurePluginSecret();

                // Initialize CooldownGate (Sprint 155: CooldownGate throttling)
                instance.CooldownGate = new CooldownGate(
                    () => instance.Configuration,
                    _logger);
                instance.CooldownGate.ProgressStreamer = Plugin.ProgressStreamer;

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
