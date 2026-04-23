namespace InfiniteDrive.Configuration.UI.views
{
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;

    internal class CatalogsPageView : PluginPageView
    {
        public CatalogsPageView(PluginInfo pluginInfo)
            : base(pluginInfo.Id)
        {
            var config = Plugin.Instance.Configuration;
            ContentData = new CatalogsUI
            {
                EnableAioStreamsCatalog = config.EnableAioStreamsCatalog,
                AioStreamsCatalogIds = config.AioStreamsCatalogIds,
                UserCatalogLimit = config.UserCatalogLimit,
            };
        }

        public CatalogsUI UI => ContentData as CatalogsUI;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (UI != null)
            {
                var config = Plugin.Instance.Configuration;
                config.EnableAioStreamsCatalog = UI.EnableAioStreamsCatalog;
                config.AioStreamsCatalogIds = UI.AioStreamsCatalogIds;
                config.UserCatalogLimit = UI.UserCatalogLimit;
                Plugin.Instance.SaveConfiguration();
            }
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
