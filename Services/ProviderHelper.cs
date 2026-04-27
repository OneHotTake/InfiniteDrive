using System.Collections.Generic;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Shared provider descriptor used by stream resolution, prefetch, and failover.
    /// </summary>
    public class ProviderInfo
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Uuid { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }

    /// <summary>
    /// Single source of truth for building the ordered provider list from config.
    /// Replaces 4 identical inline copies across AioMediaSourceProvider,
    /// BingePrefetchService, ResolverService, and StreamResolutionHelper.
    /// </summary>
    public static class ProviderHelper
    {
        /// <summary>
        /// Parses primary and secondary manifest URLs from config into an ordered provider list.
        /// Returns an empty list if no providers are configured.
        /// </summary>
        public static List<ProviderInfo> GetProviders(PluginConfiguration config)
        {
            var providers = new List<ProviderInfo>();

            if (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                    providers.Add(new ProviderInfo { DisplayName = "Primary", Url = url, Uuid = uuid ?? "", Token = token ?? "" });
            }

            if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.SecondaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                    providers.Add(new ProviderInfo { DisplayName = "Secondary", Url = url, Uuid = uuid ?? "", Token = token ?? "" });
            }

            return providers;
        }
    }
}
