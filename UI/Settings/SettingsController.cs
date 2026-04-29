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

            // Tab order: Setup → Catalogs & Lists → Content Controls → Sync & Marvin → Advanced
            _tabs.Add(new TabPageController(pid, "Setup", "Setup", () =>
                new SetupTabView(pid, LoadSetup())));

            _tabs.Add(new TabPageController(pid, "CatalogsAndLists", "Catalogs & Lists", () =>
                new CatalogsAndListsTabView(pid, LoadCatalogsAndLists())));

            _tabs.Add(new TabPageController(pid, "ContentControls", "Content Controls", () =>
                new ContentControlsTabView(pid, LoadContentControls())));

            _tabs.Add(new TabPageController(pid, "SyncAndMarvin", "Sync & Marvin", () =>
                new SyncAndMarvinTabView(pid, LoadSyncAndMarvin())));

            // Advanced is last — "you don't need this unless you have a reason"
            _tabs.Add(new TabPageController(pid, "Advanced", "Advanced", () =>
                new AdvancedTabView(pid, LoadAdvanced())));
        }

        public override PluginPageInfo PageInfo { get; }
        public IReadOnlyList<IPluginUIPageController> TabPageControllers => _tabs.AsReadOnly();

        public override Task<IPluginUIView> CreateDefaultPageView()
            => Task.FromResult<IPluginUIView>(new SetupTabView(PluginId, LoadSetup()));

        // ── Load ─────────────────────────────────────────────────────────────

        internal static SetupUI LoadSetup()
        {
            var c = Plugin.Instance.Configuration;
            return new SetupUI
            {
                PrimaryManifestUrl = c.PrimaryManifestUrl ?? string.Empty,
                SecondaryManifestUrl = c.SecondaryManifestUrl ?? string.Empty,
                EmbyBaseUrl = ResolveEmbyBaseUrl(c.EmbyBaseUrl),
                MoviesLibraryName = c.MoviesLibraryName ?? "InfiniteDrive Movies",
                MoviesLibraryPath = c.MoviesLibraryPath ?? string.Empty,
                SeriesLibraryName = c.SeriesLibraryName ?? "InfiniteDrive Series",
                SeriesLibraryPath = c.SeriesLibraryPath ?? string.Empty,
                AnimeLibraryName = c.AnimeLibraryName ?? "InfiniteDrive Anime",
                AnimeLibraryPath = c.AnimeLibraryPath ?? string.Empty,
                MetadataLanguage = c.MetadataLanguage ?? "en",
                CertificationCountry = c.CertificationCountry ?? "US",
                DefaultSubtitleLanguage = c.DefaultSubtitleLanguage ?? "en",
                DefaultQualityTier = c.DefaultQualityTier ?? "1080p (any)",
            };
        }

        private static CatalogsAndListsUI LoadCatalogsAndLists()
        {
            var c = Plugin.Instance.Configuration;
            return new CatalogsAndListsUI
            {
                CatalogSyncIntervalHours = c.CatalogSyncIntervalHours,
                TraktClientId = c.TraktClientId ?? string.Empty,
                TmdbApiKey = c.TmdbApiKey ?? string.Empty,
                MaxListsPerUser = c.MaxListsPerUser,
            };
        }

        private static ContentControlsUI LoadContentControls()
        {
            var c = Plugin.Instance.Configuration;
            return new ContentControlsUI
            {
                DefaultQualityTier = c.DefaultQualityTier ?? "1080p (any)",
                HideUnratedContent = c.HideUnratedContent,
            };
        }

        private static SyncAndMarvinUI LoadSyncAndMarvin()
        {
            var c = Plugin.Instance.Configuration;
            return new SyncAndMarvinUI
            {
                MarvinProcessIntervalMinutes = c.MarvinProcessIntervalMinutes,
                StreamResolutionBatchSize = c.StreamResolutionBatchSize,
                MarvinActionsPerHour = c.MarvinActionsPerHour,
                RespectPlaylistsWhenPruning = c.RespectPlaylistsWhenPruning,
                AutoDeduplicatePhysicalMedia = c.AutoDeduplicatePhysicalMedia,
            };
        }

        internal static AdvancedUI LoadAdvanced()
        {
            var c = Plugin.Instance.Configuration;
            return new AdvancedUI
            {
                PluginLogLevel = c.PluginLogLevel ?? "Info",
                CacheRefreshIntervalDays = c.CacheRefreshIntervalDays,
            };
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
