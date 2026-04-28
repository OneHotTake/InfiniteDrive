using System;
using System.Threading.Tasks;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    internal class TabPageController : ControllerBase
    {
        private readonly Func<IPluginUIView> _factory;

        public TabPageController(string pluginId, string name, string displayName, Func<IPluginUIView> factory)
            : base(pluginId)
        {
            _factory = factory;
            PageInfo = new PluginPageInfo
            {
                Name = name,
                DisplayName = displayName,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
            => Task.FromResult(_factory());
    }
}
