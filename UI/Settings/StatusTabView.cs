using System.Threading.Tasks;
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

            // Libraries — check that paths are configured AND directories exist on disk
            var moviesConfigured = !string.IsNullOrWhiteSpace(cfg.SyncPathMovies);
            var showsConfigured  = !string.IsNullOrWhiteSpace(cfg.SyncPathShows);
            var moviesExists     = moviesConfigured && System.IO.Directory.Exists(cfg.SyncPathMovies);
            var showsExists      = showsConfigured  && System.IO.Directory.Exists(cfg.SyncPathShows);

            if (moviesExists && showsExists)
            {
                ui.LibraryStatus.StatusText = "Movies and Shows ready";
                ui.LibraryStatus.Status = ItemStatus.Succeeded;
            }
            else if (!moviesConfigured && !showsConfigured)
            {
                ui.LibraryStatus.StatusText = "Not configured — open Setup tab";
                ui.LibraryStatus.Status = ItemStatus.Failed;
            }
            else
            {
                var missing = new System.Collections.Generic.List<string>();
                if (!moviesExists) missing.Add("Movies");
                if (!showsExists)  missing.Add("Shows");
                ui.LibraryStatus.StatusText = $"{string.Join(", ", missing)} folder(s) not found — run library setup";
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

        // Status page is read-only — no save needed
        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
            => base.OnSaveCommand(itemId, commandId, data);
    }
}
