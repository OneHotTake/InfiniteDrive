using System;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class CatalogsTabView : PluginPageView
    {
        public CatalogsTabView(string pluginId, CatalogsUI ui) : base(pluginId)
        {
            ContentData = ui;
            LoadCatalogsAsync(ui).ConfigureAwait(false);
        }

        private CatalogsUI UI => (CatalogsUI)ContentData;

        private async Task LoadCatalogsAsync(CatalogsUI ui)
        {
            try
            {
                var db = Plugin.Instance.DatabaseManager;
                var counts = await db.GetCatalogCountsBySourceAsync().ConfigureAwait(false);

                ui.CatalogList.Clear();

                if (counts.Count == 0)
                {
                    ui.CatalogList.Add(new GenericListItem
                    {
                        PrimaryText = "No catalogs found — run a catalog sync first",
                        Icon = IconNames.info,
                        IconMode = ItemListIconMode.SmallRegular,
                    });
                    return;
                }

                foreach (var kvp in counts)
                {
                    ui.CatalogList.Add(new GenericListItem
                    {
                        PrimaryText = kvp.Key,
                        SecondaryText = $"{kvp.Value} items",
                        Icon = IconNames.folder,
                        IconMode = ItemListIconMode.SmallRegular,
                        Status = ItemStatus.Succeeded,
                    });
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsUI] Failed to load catalogs");
            }
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            SettingsController.SaveCatalogs(UI, cfg);
            Plugin.Instance.SaveConfiguration();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
