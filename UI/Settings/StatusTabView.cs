using System.Linq;
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
                ui.ProviderStatus.StatusText = "Primary + failover configured";
                ui.ProviderStatus.Status = ItemStatus.Succeeded;
            }
            else if (hasPrimary)
            {
                ui.ProviderStatus.StatusText = "Primary only · no failover";
                ui.ProviderStatus.Status = ItemStatus.Warning;
            }
            else
            {
                ui.ProviderStatus.StatusText = "No manifest URL — visit the Connect tab";
                ui.ProviderStatus.Status = ItemStatus.Failed;
            }

            // Libraries — path must be set and the directory must exist on disk
            bool IsConfigured(string path) =>
                !string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path);

            var moviesOk = IsConfigured(cfg.SyncPathMovies);
            var showsOk  = IsConfigured(cfg.SyncPathShows);
            var animeOk  = IsConfigured(cfg.SyncPathAnime);

            var configured = new System.Collections.Generic.List<string>();
            var missing    = new System.Collections.Generic.List<string>();
            if (moviesOk) configured.Add("Movies"); else missing.Add("Movies");
            if (showsOk)  configured.Add("Shows");  else missing.Add("Shows");
            if (animeOk)  configured.Add("Anime");  else missing.Add("Anime");

            if (configured.Count == 3)
            {
                ui.LibraryStatus.StatusText = "Movies · Shows · Anime";
                ui.LibraryStatus.Status = ItemStatus.Succeeded;
            }
            else if (configured.Count == 0)
            {
                ui.LibraryStatus.StatusText = "No paths set — visit the Libraries tab";
                ui.LibraryStatus.Status = ItemStatus.Failed;
            }
            else
            {
                ui.LibraryStatus.StatusText =
                    $"{string.Join(" · ", configured)} ready · {string.Join(", ", missing)} missing";
                ui.LibraryStatus.Status = ItemStatus.Warning;
            }

            // Quality tiers
            var buckets = cfg.DesiredVersions;
            if (buckets != null && buckets.Count > 0)
            {
                var total = buckets.Sum(b => b.Count);
                var label = buckets.Count == 1 ? "bucket" : "buckets";
                ui.QualityStatus.StatusText = $"{buckets.Count} {label} · {total} versions max";
                ui.QualityStatus.Status = ItemStatus.Succeeded;
            }
            else
            {
                ui.QualityStatus.StatusText = "No buckets · defaulting to 1080p, any audio";
                ui.QualityStatus.Status = ItemStatus.Warning;
            }

            return ui;
        }

        // Status page is read-only — no save button needed
    }
}
