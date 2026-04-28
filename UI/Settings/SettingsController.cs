using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class SettingsController : ControllerBase, IHasTabbedUIPages
    {
        private readonly List<IPluginUIPageController> _tabs = new List<IPluginUIPageController>();

        public SettingsController(string pluginId) : base(pluginId)
        {
            PageInfo = new PluginPageInfo
            {
                Name = "InfiniteDriveSettings",
                DisplayName = "InfiniteDrive",
                IsMainConfigPage = true,
                EnableInMainMenu = false,
                MenuIcon = "settings",
            };

            var pid = pluginId;

            _tabs.Add(new TabPageController(pid, "Providers", "Providers", () =>
                new ProvidersTabView(pid, LoadProviders())));

            _tabs.Add(new TabPageController(pid, "Libraries", "Libraries", () =>
                new SettingsTabView<LibrariesUI>(pid, LoadLibraries(), SaveLibraries)));

            _tabs.Add(new TabPageController(pid, "Catalogs", "Catalogs", () =>
                new CatalogsTabView(pid, LoadCatalogs())));

            _tabs.Add(new TabPageController(pid, "Playback", "Playback", () =>
                new SettingsTabView<PlaybackUI>(pid, LoadPlayback(), SavePlayback)));

            _tabs.Add(new TabPageController(pid, "Metadata", "Metadata", () =>
                new SettingsTabView<MetadataUI>(pid, LoadMetadata(), SaveMetadata)));

            _tabs.Add(new TabPageController(pid, "Security", "Security", () =>
                new SettingsTabView<SecurityUI>(pid, LoadSecurity(), SaveSecurity)));

            _tabs.Add(new TabPageController(pid, "Health", "Health", () =>
                new HealthTabView(pid, LoadHealth())));
        }

        public override PluginPageInfo PageInfo { get; }
        public IReadOnlyList<IPluginUIPageController> TabPageControllers => _tabs.AsReadOnly();

        public override Task<IPluginUIView> CreateDefaultPageView()
            => _tabs[0].CreateDefaultPageView();

        // ── Load ─────────────────────────────────────────────────────────────

        internal static ProvidersUI LoadProviders()
        {
            var c = Plugin.Instance.Configuration;
            return new ProvidersUI
            {
                PrimaryManifestUrl = c.PrimaryManifestUrl ?? string.Empty,
                SecondaryManifestUrl = c.SecondaryManifestUrl ?? string.Empty,
            };
        }

        private static LibrariesUI LoadLibraries()
        {
            var c = Plugin.Instance.Configuration;
            return new LibrariesUI
            {
                SyncPathMovies = c.SyncPathMovies ?? "/media/infinitedrive/movies",
                SyncPathShows = c.SyncPathShows ?? "/media/infinitedrive/shows",
                SyncPathAnime = c.SyncPathAnime ?? "/media/infinitedrive/anime",
                LibraryNameMovies = c.LibraryNameMovies ?? "Streamed Movies",
                LibraryNameSeries = c.LibraryNameSeries ?? "Streamed Series",
                LibraryNameAnime = c.LibraryNameAnime ?? "Streamed Anime",
                EmbyBaseUrl = ResolveEmbyBaseUrl(c.EmbyBaseUrl),
            };
        }

        private static CatalogsUI LoadCatalogs()
        {
            var c = Plugin.Instance.Configuration;
            return new CatalogsUI
            {
                CatalogItemCap = c.CatalogItemCap,
                CatalogSyncIntervalHours = c.CatalogSyncIntervalHours,
            };
        }

        private static PlaybackUI LoadPlayback()
        {
            var c = Plugin.Instance.Configuration;
            return new PlaybackUI
            {
                CacheLifetimeMinutes = c.CacheLifetimeMinutes,
                ApiDailyBudget = c.ApiDailyBudget,
                MaxConcurrentResolutions = c.MaxConcurrentResolutions,
                EnablePreCache = c.EnablePreCache,
                PreCacheBatchSize = c.PreCacheBatchSize,
                PreCacheIntervalHours = c.PreCacheIntervalHours,
                PreCacheTTLDays = c.PreCacheTTLDays,
            };
        }

        private static MetadataUI LoadMetadata()
        {
            var c = Plugin.Instance.Configuration;
            return new MetadataUI
            {
                MetadataLanguage = c.MetadataLanguage ?? "en",
                MetadataCertificationCountry = c.MetadataCertificationCountry ?? "US",
                SubtitleDownloadLanguages = c.SubtitleDownloadLanguages ?? "en",
                SkipFutureEpisodes = c.SkipFutureEpisodes,
                FutureEpisodeBufferDays = c.FutureEpisodeBufferDays,
            };
        }

        private static SecurityUI LoadSecurity()
        {
            var c = Plugin.Instance.Configuration;
            return new SecurityUI
            {
                SignatureValidityDays = c.SignatureValidityDays,
                PluginSecret = c.PluginSecret ?? string.Empty,
            };
        }

        private static HealthUI LoadHealth()
        {
            return new HealthUI();
        }

        // ── Save ─────────────────────────────────────────────────────────────

        internal static void SaveProviders(ProvidersUI ui, PluginConfiguration c)
        {
            c.PrimaryManifestUrl = ui.PrimaryManifestUrl ?? string.Empty;
            c.SecondaryManifestUrl = ui.SecondaryManifestUrl ?? string.Empty;
            // EnableBackup is implicit: if SecondaryManifestUrl is non-empty, it's enabled
            c.EnableBackupAioStreams = !string.IsNullOrWhiteSpace(ui.SecondaryManifestUrl);
        }

        private static void SaveLibraries(LibrariesUI ui, PluginConfiguration c)
        {
            c.SyncPathMovies = ui.SyncPathMovies ?? string.Empty;
            c.SyncPathShows = ui.SyncPathShows ?? string.Empty;
            c.SyncPathAnime = ui.SyncPathAnime ?? string.Empty;
            c.LibraryNameMovies = ui.LibraryNameMovies ?? string.Empty;
            c.LibraryNameSeries = ui.LibraryNameSeries ?? string.Empty;
            c.LibraryNameAnime = ui.LibraryNameAnime ?? string.Empty;
            c.EmbyBaseUrl = ui.EmbyBaseUrl ?? string.Empty;
        }

        internal static void SaveCatalogs(CatalogsUI ui, PluginConfiguration c)
        {
            c.CatalogItemCap = ui.CatalogItemCap;
            c.CatalogSyncIntervalHours = ui.CatalogSyncIntervalHours;
        }

        private static void SavePlayback(PlaybackUI ui, PluginConfiguration c)
        {
            c.CacheLifetimeMinutes = ui.CacheLifetimeMinutes;
            c.ApiDailyBudget = ui.ApiDailyBudget;
            c.MaxConcurrentResolutions = ui.MaxConcurrentResolutions;
            c.EnablePreCache = ui.EnablePreCache;
            c.PreCacheBatchSize = ui.PreCacheBatchSize;
            c.PreCacheIntervalHours = ui.PreCacheIntervalHours;
            c.PreCacheTTLDays = ui.PreCacheTTLDays;
        }

        private static void SaveMetadata(MetadataUI ui, PluginConfiguration c)
        {
            c.MetadataLanguage = ui.MetadataLanguage ?? string.Empty;
            c.MetadataCertificationCountry = ui.MetadataCertificationCountry ?? string.Empty;
            c.SubtitleDownloadLanguages = ui.SubtitleDownloadLanguages ?? string.Empty;
            c.SkipFutureEpisodes = ui.SkipFutureEpisodes;
            c.FutureEpisodeBufferDays = ui.FutureEpisodeBufferDays;
        }

        private static void SaveSecurity(SecurityUI ui, PluginConfiguration c)
        {
            c.SignatureValidityDays = ui.SignatureValidityDays;
            c.PluginSecret = ui.PluginSecret ?? string.Empty;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string ResolveEmbyBaseUrl(string current)
        {
            // If already set to something non-default, keep it
            if (!string.IsNullOrEmpty(current) &&
                !current.StartsWith("http://127.0.0.1") &&
                !current.StartsWith("http://localhost"))
            {
                return current;
            }

            // Try to detect the external IP
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !ip.ToString().StartsWith("127."))
                    {
                        return $"http://{ip}:8096";
                    }
                }
            }
            catch { }

            return current;
        }
    }
}
