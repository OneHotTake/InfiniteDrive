using Emby.Web.GenericEdit.Elements;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class StatusTabView : PluginPageView
    {
        public StatusTabView(string pluginId, StatusUI ui) : base(pluginId)
        {
            ContentData = ui;
        }

        private StatusUI UI => (StatusUI)ContentData;

        // ── Load status from current config ──────────────────────────────────

        public static StatusUI BuildUI()
        {
            var cfg = Plugin.Instance.Configuration;
            var ui = new StatusUI();

            // Provider
            var hasPrimary = !string.IsNullOrWhiteSpace(cfg.PrimaryManifestUrl);
            var hasSecondary = !string.IsNullOrWhiteSpace(cfg.SecondaryManifestUrl);
            if (hasPrimary && hasSecondary)
            {
                ui.ProviderStatus.StatusText = "Primary + Secondary configured";
                ui.ProviderStatus.Status = ItemStatus.Succeeded;
            }
            else if (hasPrimary)
            {
                ui.ProviderStatus.StatusText = "Primary configured (no backup)";
                ui.ProviderStatus.Status = ItemStatus.Warning;
            }
            else
            {
                ui.ProviderStatus.StatusText = "No AIOStreams manifest URL — go to Connect tab";
                ui.ProviderStatus.Status = ItemStatus.Failed;
            }

            // Libraries — check paths are set to non-default values AND directories exist on disk
            // Default paths (/media/infinitedrive/...) indicate the user has never run setup
            bool IsConfigured(string path, string def) =>
                !string.IsNullOrWhiteSpace(path) && path != def;

            var moviesOk = IsConfigured(cfg.SyncPathMovies, "/media/infinitedrive/movies")
                           && System.IO.Directory.Exists(cfg.SyncPathMovies);
            var showsOk  = IsConfigured(cfg.SyncPathShows, "/media/infinitedrive/shows")
                           && System.IO.Directory.Exists(cfg.SyncPathShows);
            var animeOk  = IsConfigured(cfg.SyncPathAnime, "/media/infinitedrive/anime")
                           && System.IO.Directory.Exists(cfg.SyncPathAnime);

            var configured = new System.Collections.Generic.List<string>();
            var missing    = new System.Collections.Generic.List<string>();
            if (moviesOk) configured.Add("Movies"); else missing.Add("Movies");
            if (showsOk)  configured.Add("Shows");  else missing.Add("Shows");
            if (animeOk)  configured.Add("Anime");  else missing.Add("Anime");

            if (configured.Count == 3)
            {
                ui.LibraryStatus.StatusText = "Movies, Shows, and Anime paths exist";
                ui.LibraryStatus.Status = ItemStatus.Succeeded;
            }
            else if (configured.Count == 0)
            {
                ui.LibraryStatus.StatusText = "Not configured — open 2 Libraries tab";
                ui.LibraryStatus.Status = ItemStatus.Failed;
            }
            else
            {
                ui.LibraryStatus.StatusText =
                    $"{string.Join(" + ", configured)} configured; {string.Join(", ", missing)} missing";
                ui.LibraryStatus.Status = ItemStatus.Warning;
            }

            // Quality tiers
            var buckets = cfg.DesiredVersions;
            if (buckets != null && buckets.Count > 0)
            {
                ui.QualityStatus.StatusText = $"{buckets.Count} bucket(s) defined";
                ui.QualityStatus.Status = ItemStatus.Succeeded;
            }
            else
            {
                ui.QualityStatus.StatusText = "No buckets — default 1080p/Any Audio applied automatically";
                ui.QualityStatus.Status = ItemStatus.Warning;
            }

            return ui;
        }

        // Status page is read-only — no save button needed
    }
}
