using System.Threading.Tasks;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class SetupTabView : PluginPageView
    {
        public SetupTabView(string pluginId, SetupUI ui) : base(pluginId)
        {
            ContentData = ui;
        }

        private SetupUI UI => (SetupUI)ContentData;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.LibraryNameMovies = UI.MoviesLibraryName ?? "Streamed Movies";
            cfg.SyncPathMovies = UI.MoviesLibraryPath ?? "/media/infinitedrive/movies";
            cfg.LibraryNameSeries = UI.SeriesLibraryName ?? "Streamed Series";
            cfg.SyncPathShows = UI.SeriesLibraryPath ?? "/media/infinitedrive/shows";
            cfg.LibraryNameAnime = UI.AnimeLibraryName ?? "Streamed Anime";
            cfg.SyncPathAnime = UI.AnimeLibraryPath ?? "/media/infinitedrive/anime";
            cfg.MetadataLanguage = UI.MetadataLanguage ?? "en";
            cfg.MetadataCertificationCountry = UI.CertificationCountry ?? "US";
            cfg.DefaultSubtitleLanguage = UI.DefaultSubtitleLanguage ?? "en";
            cfg.LibraryPathsUserConfigured = true;
            Plugin.Instance.SaveConfiguration();

            // Provision libraries (create directories + register with Emby) before sync
            _ = Plugin.Instance.LibraryProvisioningService?.EnsureLibrariesProvisionedAsync();

            Plugin.Instance.TriggerBackgroundSync();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
