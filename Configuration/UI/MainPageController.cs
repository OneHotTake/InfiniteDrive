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

            // Phase 2: Easy tabs
            tabPages.Add(new TabPageController(pluginInfo, "Libraries", "Libraries",
                pi => new views.LibrariesPageView(pi)));
            tabPages.Add(new TabPageController(pluginInfo, "Security", "Security",
                pi => new views.SecurityPageView(pi)));
            tabPages.Add(new TabPageController(pluginInfo, "Metadata", "Metadata",
                pi => new views.MetadataPageView(pi)));
            tabPages.Add(new TabPageController(pluginInfo, "Catalogs", "Catalogs",
                pi => new views.CatalogsPageView(pi)));

            // Phase 3: Medium tabs (to be added)
            // tabPages.Add(new TabPageController(pluginInfo, "Overview", "Overview", ...));
            // tabPages.Add(new TabPageController(pluginInfo, "Lists", "Lists", ...));
            // tabPages.Add(new TabPageController(pluginInfo, "ContentFiltering", "Content Filtering", ...));
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
