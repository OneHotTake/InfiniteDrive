namespace InfiniteDrive.Configuration.UI.views
{
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;

    internal class LibrariesPageView : PluginPageView
    {
        public LibrariesPageView(PluginInfo pluginInfo)
            : base(pluginInfo.Id)
        {
            var config = Plugin.Instance.Configuration;
            ContentData = new LibrariesUI
            {
                SyncPathMovies = config.SyncPathMovies,
                SyncPathShows = config.SyncPathShows,
                SyncPathAnime = config.SyncPathAnime,
                LibraryNameMovies = config.LibraryNameMovies,
                LibraryNameSeries = config.LibraryNameSeries,
                LibraryNameAnime = config.LibraryNameAnime,
                EnableAnimeLibrary = config.EnableAnimeLibrary,
            };
        }

        public LibrariesUI UI => ContentData as LibrariesUI;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (UI != null)
            {
                var config = Plugin.Instance.Configuration;
                config.SyncPathMovies = UI.SyncPathMovies;
                config.SyncPathShows = UI.SyncPathShows;
                config.SyncPathAnime = UI.SyncPathAnime;
                config.LibraryNameMovies = UI.LibraryNameMovies;
                config.LibraryNameSeries = UI.LibraryNameSeries;
                config.LibraryNameAnime = UI.LibraryNameAnime;
                config.EnableAnimeLibrary = UI.EnableAnimeLibrary;
                Plugin.Instance.SaveConfiguration();
            }
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
