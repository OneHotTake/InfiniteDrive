using System.Threading.Tasks;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Discover
{
    public class DiscoverController : ControllerBase
    {
        public DiscoverController(string pluginId) : base(pluginId)
        {
            PageInfo = new PluginPageInfo
            {
                Name = "InfiniteDiscover",
                DisplayName = "Discover",
                IsMainConfigPage = false,
                EnableInMainMenu = true,
                EnableInUserMenu = true,
                MenuIcon = "explore",
                MenuSection = "server",
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
            => Task.FromResult<IPluginUIView>(new DiscoverPageView(PluginId));
    }
}
