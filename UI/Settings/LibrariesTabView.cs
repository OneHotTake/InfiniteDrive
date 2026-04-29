using System;
using System.Threading.Tasks;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class LibrariesTabView : PluginPageView
    {
        public LibrariesTabView(string pluginId, LibrariesUI ui) : base(pluginId)
        {
            ContentData = ui;
        }

        private LibrariesUI UI => (LibrariesUI)ContentData;

        public override async Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            SettingsController.SaveLibraries(UI, cfg);
            Plugin.Instance.SaveConfiguration();

            // Provision Emby libraries (creates folders + Emby library entries)
            try
            {
                var prov = Plugin.Instance.LibraryProvisioningService;
                if (prov != null)
                    await prov.EnsureLibrariesProvisionedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[LibrariesUI] Failed to provision libraries");
            }

            Plugin.Instance.TriggerBackgroundSync();
            return await base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
