namespace InfiniteDrive.Configuration.UI.views
{
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;

    /// <summary>
    /// Page view for the Providers tab.
    /// Loads config into ProvidersUI on creation, saves back on save command.
    /// </summary>
    internal class ProvidersPageView : PluginPageView
    {
        public ProvidersPageView(PluginInfo pluginInfo)
            : base(pluginInfo.Id)
        {
            var config = Plugin.Instance.Configuration;
            ContentData = new ProvidersUI
            {
                PrimaryManifestUrl = config.PrimaryManifestUrl,
                SecondaryManifestUrl = config.SecondaryManifestUrl,
                EmbyApiKey = config.EmbyApiKey,
            };
        }

        public ProvidersUI ProvidersUI => ContentData as ProvidersUI;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (ProvidersUI != null)
            {
                var config = Plugin.Instance.Configuration;
                config.PrimaryManifestUrl = ProvidersUI.PrimaryManifestUrl;
                config.SecondaryManifestUrl = ProvidersUI.SecondaryManifestUrl;
                config.EmbyApiKey = ProvidersUI.EmbyApiKey;
                Plugin.Instance.SaveConfiguration();
            }
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
