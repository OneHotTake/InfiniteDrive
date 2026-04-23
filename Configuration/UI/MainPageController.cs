namespace InfiniteDrive.Configuration.UI
{
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;

    /// <summary>
    /// Main page controller for InfiniteDrive native config UI.
    /// Implements IHasTabbedUIPages — the default view is Providers,
    /// with additional tabs registered via TabPageControllers.
    /// </summary>
    internal class MainPageController : ControllerBase, IHasTabbedUIPages
    {
        private readonly PluginInfo pluginInfo;
        private readonly List<IPluginUIPageController> tabPages = new List<IPluginUIPageController>();

        public MainPageController(PluginInfo pluginInfo)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            PageInfo = new PluginPageInfo
            {
                Name = "InfiniteDriveConfig",
                EnableInMainMenu = true,
                DisplayName = "InfiniteDrive",
                MenuIcon = "settings",
                IsMainConfigPage = true,
            };

            // Phase 2+ will add more tabs here
            // tabPages.Add(new TabPageController(pluginInfo, "Libraries", "Libraries", ...));
            // tabPages.Add(new TabPageController(pluginInfo, "Security", "Security", ...));
            // etc.
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new views.ProvidersPageView(pluginInfo);
            return Task.FromResult(view);
        }

        public IReadOnlyList<IPluginUIPageController> TabPageControllers => tabPages.AsReadOnly();
    }
}
