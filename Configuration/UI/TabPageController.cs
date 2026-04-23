namespace InfiniteDrive.Configuration.UI
{
    using System;
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;

    /// <summary>
    /// Generic tab page controller — creates views via factory function.
    /// Adapted from Emby SDK demo UI/TabPageController.cs.
    /// </summary>
    internal class TabPageController : ControllerBase
    {
        private readonly PluginInfo pluginInfo;
        private readonly Func<PluginInfo, IPluginUIView> factoryFunc;

        public TabPageController(PluginInfo pluginInfo, string name, string displayName, Func<PluginInfo, IPluginUIView> factoryFunc)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.factoryFunc = factoryFunc;
            PageInfo = new PluginPageInfo
            {
                Name = name,
                DisplayName = displayName,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            var view = factoryFunc(pluginInfo);
            return Task.FromResult(view);
        }
    }
}
