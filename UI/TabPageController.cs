using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI
{
    public class TabPageController : IPluginUIPageController
    {
        private readonly Func<IPluginUIView> _viewFactory;

        public TabPageController(string name, string displayName, Func<IPluginUIView> viewFactory)
        {
            PageInfo = new PluginPageInfo
            {
                Name = name,
                DisplayName = displayName
            };
            _viewFactory = viewFactory;
        }

        public PluginPageInfo PageInfo { get; }

        public Task Initialize(CancellationToken token) => Task.CompletedTask;

        public Task<IPluginUIView> CreateDefaultPageView()
        {
            return Task.FromResult(_viewFactory());
        }
    }
}
