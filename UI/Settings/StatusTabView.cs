using System;
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
            // Overview is a read-only status dashboard — no fields to persist, so hide Save.
            ShowSave = false;
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
                ui.ProviderStatus.StatusText = "No manifest URL — visit the Providers tab";
                ui.ProviderStatus.Status = ItemStatus.Failed;
            }

            // Libraries — path must exist on disk AND Emby must have a virtual folder pointing
            // at that path. After factory reset, LibraryProvisioningService hasn't run so no
            // virtual folders exist yet → red. After saving the Libraries tab, provisioning
            // runs and registers the folders → green. Pure state-machine, no extra flags.
            var lm = Plugin.Instance.LibraryManager;
            bool IsConfigured(string path)
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return false;
                if (lm == null) return false;
                var norm = path.TrimEnd('/', '\\');
                return lm.GetVirtualFolders().Any(f =>
                    f.Locations != null &&
                    f.Locations.Any(loc =>
                        string.Equals(loc.TrimEnd('/', '\\'), norm, StringComparison.OrdinalIgnoreCase)));
            }

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
                ui.QualityStatus.StatusText = $"{buckets.Count} {label} · {total} of 8 slots configured";
                ui.QualityStatus.Status = ItemStatus.Succeeded;
            }
            else
            {
                ui.QualityStatus.StatusText = "No buckets · defaulting to 1080p, any audio";
                ui.QualityStatus.Status = ItemStatus.Warning;
            }

            // ── Content readiness (headline) ─────────────────────────────────
            // GREEN  = provider + library configured AND (catalog source OR ≥1 list)
            // YELLOW = configured but no catalog source AND no lists (no content yet)
            // RED    = provider or library not configured
            // Catalogs and lists are EITHER-OR: either alone yields a working system.
            bool providerConfigured = hasPrimary || hasSecondary;
            bool libraryConfigured = configured.Count > 0;
            bool hasCatalogs =
                (cfg.EnableAioStreamsCatalog && providerConfigured && !cfg.AioStreamsIsStreamOnly)
                || cfg.EnableCinemetaDefault;

            bool hasLists = false;
            try
            {
                var db = Plugin.Instance.DatabaseManager;
                if (db != null)
                {
                    var admin = db.GetUserCatalogsByOwnerAsync("SERVER", activeOnly: true).GetAwaiter().GetResult();
                    var users = db.GetActiveUserCatalogCountAsync().GetAwaiter().GetResult();
                    hasLists = (admin?.Count ?? 0) > 0 || users > 0;
                }
            }
            catch { /* best effort — readiness must never break the page */ }

            if (!providerConfigured || !libraryConfigured)
            {
                ui.ContentReadiness.StatusText = !providerConfigured
                    ? "Add your AIOStreams manifest on the Providers tab"
                    : "Set your library paths on the Libraries tab";
                ui.ContentReadiness.Status = ItemStatus.Failed;
            }
            else if (hasCatalogs || hasLists)
            {
                ui.ContentReadiness.StatusText =
                    hasCatalogs && hasLists ? "Ready — catalogs and lists are feeding your library"
                    : hasCatalogs ? "Ready — catalog source active"
                    : "Ready — list(s) active";
                ui.ContentReadiness.Status = ItemStatus.Succeeded;
            }
            else
            {
                ui.ContentReadiness.StatusText =
                    "Set up, but no content yet — add a catalog in AIOStreams, or add a list";
                ui.ContentReadiness.Status = ItemStatus.Warning;
            }

            return ui;
        }

        // Status page is read-only — no save button needed
    }
}
