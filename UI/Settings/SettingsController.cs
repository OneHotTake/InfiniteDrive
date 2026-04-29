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
                DisplayName = "Connect",
                IsMainConfigPage = true,
                EnableInMainMenu = false,
                MenuIcon = "settings",
            };

            var pid = pluginId;

            // Tab order: Libraries → Catalogs → Playback → Quality → Health → Advanced
            _tabs.Add(new TabPageController(pid, "Libraries", "Libraries", () =>
                new LibrariesTabView(pid, LoadLibraries())));

            _tabs.Add(new TabPageController(pid, "Catalogs", "Catalogs", () =>
                new CatalogsTabView(pid, LoadCatalogs())));

            _tabs.Add(new TabPageController(pid, "Playback", "Playback", () =>
                new PlaybackTabView(pid, LoadPlayback())));

            _tabs.Add(new TabPageController(pid, "Health", "Health", () =>
                new HealthTabView(pid, LoadHealth())));

            // Advanced is last — "you don't need this unless you have a reason"
            _tabs.Add(new TabPageController(pid, "Advanced", "Advanced", () =>
                new AdvancedTabView(pid, LoadAdvanced())));
        }

        public override PluginPageInfo PageInfo { get; }
        public IReadOnlyList<IPluginUIPageController> TabPageControllers => _tabs.AsReadOnly();

        public override Task<IPluginUIView> CreateDefaultPageView()
            => Task.FromResult<IPluginUIView>(new ProvidersTabView(PluginId, LoadProviders()));

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
                MetadataLanguage = c.MetadataLanguage ?? "en",
                MetadataCertificationCountry = c.MetadataCertificationCountry ?? "US",
                SubtitleDownloadLanguages = c.SubtitleDownloadLanguages ?? "en",
            };
        }

        private static CatalogsUI LoadCatalogs()
        {
            var c = Plugin.Instance.Configuration;
            return new CatalogsUI
            {
                CatalogSyncIntervalHours = c.CatalogSyncIntervalHours,
            };
        }

        private static PlaybackUI LoadPlayback()
        {
            var c = Plugin.Instance.Configuration;
            return new PlaybackUI
            {
                EnablePreCache = c.EnablePreCache,
            };
        }

        private static HealthUI LoadHealth()
        {
            return new HealthUI();
        }

        internal static AdvancedUI LoadAdvanced()
        {
            var c = Plugin.Instance.Configuration;
            return new AdvancedUI
            {
                SkipFutureEpisodes = c.SkipFutureEpisodes,
                ApiDailyBudget = c.ApiDailyBudget,
                CacheLifetimeMinutes = c.CacheLifetimeMinutes,
                SignatureValidityDays = c.SignatureValidityDays,
                PluginSecret = c.PluginSecret ?? string.Empty,
                DefaultSeriesSeasons = c.DefaultSeriesSeasons,
                DefaultSeriesEpisodesPerSeason = c.DefaultSeriesEpisodesPerSeason,
                DontPanic = c.DontPanic,
                MaxConcurrentProxyStreams = c.MaxConcurrentProxyStreams,
            };
        }

        // ── Save ─────────────────────────────────────────────────────────────

        internal static void SaveProviders(ProvidersUI ui, PluginConfiguration c)
        {
            c.PrimaryManifestUrl = ui.PrimaryManifestUrl ?? string.Empty;
            c.SecondaryManifestUrl = ui.SecondaryManifestUrl ?? string.Empty;
            // EnableBackup is implicit: if SecondaryManifestUrl is non-empty, it's enabled
            c.EnableBackupAioStreams = !string.IsNullOrWhiteSpace(ui.SecondaryManifestUrl);
        }

        internal static void SaveLibraries(LibrariesUI ui, PluginConfiguration c)
        {
            c.SyncPathMovies = ui.SyncPathMovies ?? string.Empty;
            c.SyncPathShows = ui.SyncPathShows ?? string.Empty;
            c.SyncPathAnime = ui.SyncPathAnime ?? string.Empty;
            c.LibraryNameMovies = ui.LibraryNameMovies ?? string.Empty;
            c.LibraryNameSeries = ui.LibraryNameSeries ?? string.Empty;
            c.LibraryNameAnime = ui.LibraryNameAnime ?? string.Empty;
            c.EmbyBaseUrl = ui.EmbyBaseUrl ?? string.Empty;
            c.MetadataLanguage = ui.MetadataLanguage ?? "en";
            c.MetadataCertificationCountry = ui.MetadataCertificationCountry ?? "US";
            c.SubtitleDownloadLanguages = ui.SubtitleDownloadLanguages ?? "en";
        }

        internal static void SaveCatalogs(CatalogsUI ui, PluginConfiguration c)
        {
            c.CatalogSyncIntervalHours = ui.CatalogSyncIntervalHours;
        }

        internal static void SavePlayback(PlaybackUI ui, PluginConfiguration c)
        {
            // Playback tab is intentionally minimal — only EnablePreCache is user-facing.
            // CacheLifetimeMinutes, ApiDailyBudget, etc. live in Advanced.
            c.EnablePreCache = ui.EnablePreCache;
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
