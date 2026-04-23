namespace InfiniteDrive.Configuration.UI.views
{
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;

    internal class MetadataPageView : PluginPageView
    {
        public MetadataPageView(PluginInfo pluginInfo)
            : base(pluginInfo.Id)
        {
            var config = Plugin.Instance.Configuration;
            ContentData = new MetadataUI
            {
                MetadataLanguage = config.MetadataLanguage,
                MetadataCountryCode = config.MetadataCountryCode,
                AioMetadataBaseUrl = config.AioMetadataBaseUrl,
                CatalogSyncIntervalHours = config.CatalogSyncIntervalHours,
                CatalogItemCap = config.CatalogItemCap,
            };
        }

        public MetadataUI UI => ContentData as MetadataUI;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (UI != null)
            {
                var config = Plugin.Instance.Configuration;
                config.MetadataLanguage = UI.MetadataLanguage;
                config.MetadataCountryCode = UI.MetadataCountryCode;
                config.AioMetadataBaseUrl = UI.AioMetadataBaseUrl;
                config.CatalogSyncIntervalHours = UI.CatalogSyncIntervalHours;
                config.CatalogItemCap = UI.CatalogItemCap;
                Plugin.Instance.SaveConfiguration();
            }
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
