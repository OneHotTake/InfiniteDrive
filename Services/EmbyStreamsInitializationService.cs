using System;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using EmbyStreams.Services;
using EmbyStreams.Repositories;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Core plugin initialization service.
    /// Runs on server startup to initialize database, repositories, and plugin configuration.
    /// Defers heavy initialization from Plugin constructor per Emby conventions.
    /// </summary>
    public class EmbyStreamsInitializationService : IServerEntryPoint
    {
        private readonly ILogger<EmbyStreamsInitializationService> _logger;
        private readonly ILogManager _logManager;

        public EmbyStreamsInitializationService(ILogManager logManager)
        {
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<EmbyStreamsInitializationService>(
                logManager.GetLogger("EmbyStreams"));
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
                    _logger.LogError("[EmbyStreams] Plugin.Instance is null — initialization failed");
                    return;
                }

                _logger.LogInformation("[EmbyStreams] Core initialization starting");

                // Initialize database — ApplicationPaths guaranteed settled here
                instance.InitialiseDatabaseManager();

                // Auto-generate PluginSecret if absent
                instance.EnsurePluginSecret();

                _logger.LogInformation("[EmbyStreams] Core initialization complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Initialization failed");
                // Do not rethrow — a failed init should not crash the server
            }
        }

        /// <summary>
        /// No resources to clean up.
        /// </summary>
        public void Dispose() { }
    }
}
