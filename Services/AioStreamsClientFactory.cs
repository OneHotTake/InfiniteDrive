using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Centralized factory for creating AioStreamsClient instances.
    /// Ensures consistent config sourcing from Plugin.Instance and optional CooldownGate wiring.
    /// </summary>
    public static class AioStreamsClientFactory
    {
        /// <summary>
        /// Creates an AioStreamsClient using the current PluginConfiguration.
        /// Reads primary/secondary manifest URL from Plugin.Instance.Configuration.
        /// </summary>
        public static AioStreamsClient Create(ILogger logger)
        {
            var config = Plugin.Instance?.Configuration
                ?? throw new System.InvalidOperationException("Plugin not initialized");
            return new AioStreamsClient(config, logger);
        }
    }
}
