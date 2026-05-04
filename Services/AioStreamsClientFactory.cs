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

        /// <summary>
        /// Creates an AioStreamsClient for a specific provider record.
        /// </summary>
        public static AioStreamsClient CreateForProvider(ProviderInfo provider, ILogger logger) =>
            new AioStreamsClient(provider.Url, provider.Uuid, provider.Token, logger);

        /// <summary>
        /// Parses a manifest URL and creates an AioStreamsClient.
        /// Returns null if the URL cannot be parsed.
        /// </summary>
        public static AioStreamsClient? TryCreateForManifest(string manifestUrl, ILogger logger)
        {
            var (baseUrl, uuid, token) = AioStreamsClient.TryParseManifestUrl(manifestUrl);
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            return new AioStreamsClient(baseUrl, uuid ?? string.Empty, token ?? string.Empty, logger);
        }
    }
}
