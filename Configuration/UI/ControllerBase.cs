namespace InfiniteDrive.Configuration.UI
{
    using System.Threading;
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;

    /// <summary>
    /// Base class for plugin UI page controllers.
    /// Adapted from Emby SDK demo UIBaseClasses/ControllerBase.cs.
    /// </summary>
    public abstract class ControllerBase : IPluginUIPageController
    {
        protected ControllerBase(string pluginId)
        {
            PluginId = pluginId;
        }

        public abstract PluginPageInfo PageInfo { get; }

        public string PluginId { get; }

        public virtual Task Initialize(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public abstract Task<IPluginUIView> CreateDefaultPageView();
    }
}
