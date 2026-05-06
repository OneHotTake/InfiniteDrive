using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Common;
using InfiniteDrive.UI;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;
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

            // Tab order: Connect → Libraries → Catalogs & Lists → Content Controls → Sync & Marvin → Advanced
            // Names prefixed with zero-padded index to enforce ordering (Emby sorts tabs alphabetically by Name)
            _tabs.Add(new TabPageController(pid, "01Connect", "Connect", () =>
                new ConnectTabView(pid, LoadConnect())));

            _tabs.Add(new TabPageController(pid, "02Libraries", "Libraries", () =>
                new SetupTabView(pid, LoadSetup())));

            _tabs.Add(new TabPageController(pid, "03Catalogs", "Catalogs & Lists", () =>
                new CatalogsAndListsTabView(pid, LoadCatalogsAndLists())));

            _tabs.Add(new TabPageController(pid, "04Content", "Content Controls", () =>
                new ContentControlsTabView(pid, LoadContentControls())));

            _tabs.Add(new TabPageController(pid, "05Sync", "Sync & Marvin", () =>
                new SyncAndMarvinTabView(pid, LoadSyncAndMarvin())));

            // Advanced is last — "you don't need this unless you have a reason"
            _tabs.Add(new TabPageController(pid, "06Advanced", "Advanced", () =>
                new AdvancedTabView(pid, LoadAdvanced())));
        }

        public override PluginPageInfo PageInfo { get; }
        public IReadOnlyList<IPluginUIPageController> TabPageControllers => _tabs.AsReadOnly();

        public override Task<IPluginUIView> CreateDefaultPageView()
            => Task.FromResult<IPluginUIView>(null!);

        // ── Load ─────────────────────────────────────────────────────────────

        internal static ConnectUI LoadConnect()
        {
            var c = Plugin.Instance.Configuration;
            return new ConnectUI
            {
                PrimaryManifestUrl = c.PrimaryManifestUrl ?? string.Empty,
                SecondaryManifestUrl = c.SecondaryManifestUrl ?? string.Empty,
                EmbyBaseUrl = ResolveEmbyBaseUrl(c.EmbyBaseUrl),
            };
        }

        internal static SetupUI LoadSetup()
        {
            var c = Plugin.Instance.Configuration;
            var ui = new SetupUI
            {
                MoviesLibraryName = c.LibraryNameMovies ?? "Streamed Movies",
                MoviesLibraryPath = string.IsNullOrWhiteSpace(c.SyncPathMovies) ? "/media/infinitedrive/movies" : c.SyncPathMovies,
                SeriesLibraryName = c.LibraryNameSeries ?? "Streamed Series",
                SeriesLibraryPath = string.IsNullOrWhiteSpace(c.SyncPathShows) ? "/media/infinitedrive/shows" : c.SyncPathShows,
                AnimeLibraryName = c.LibraryNameAnime ?? "Streamed Anime",
                AnimeLibraryPath = string.IsNullOrWhiteSpace(c.SyncPathAnime) ? "/media/infinitedrive/anime" : c.SyncPathAnime,
                MetadataLanguage = c.MetadataLanguage ?? "en",
                CertificationCountry = c.MetadataCertificationCountry ?? "US",
                DefaultSubtitleLanguage = c.DefaultSubtitleLanguage ?? "en",
            };

            // Populate language/country dropdowns from Emby's ILocalizationManager
            var loc = Plugin.Instance.LocalizationManager;
            if (loc != null)
            {
                ui.LanguageOptions = loc.GetCultures()
                    .Select(culture => new EditorSelectOption
                    {
                        Value = culture.TwoLetterISOLanguageName ?? culture.Name,
                        Name = $"{culture.Name} ({culture.TwoLetterISOLanguageName})",
                        IsEnabled = true
                    })
                    .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                ui.CountryOptions = loc.GetCountries()
                    .Select(country => new EditorSelectOption
                    {
                        Value = country.TwoLetterISORegionName ?? country.Name,
                        Name = $"{country.DisplayName} ({country.TwoLetterISORegionName})",
                        IsEnabled = true
                    })
                    .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return ui;
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
                UseRemuxForAutoSelection = c.UseRemuxForAutoSelection,
                HideUnratedContent = c.HideUnratedContent,
                MaxVersionsPerItem = c.MaxVersionsPerItem,
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
            catch (Exception ex) { Plugin.Instance?.Logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "resolve external IP for EmbyBaseUrl"); }

            return current;
        }
    }
}
