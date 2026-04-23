namespace InfiniteDrive.Configuration.UI
{
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins.UI.Views;

    /// <summary>
    /// Base class for plugin page views with save/back support.
    /// Adapted from Emby SDK demo UIBaseClasses/Views/PluginPageView.cs.
    /// </summary>
    public abstract class PluginPageView : PluginViewBase, IPluginPageView
    {
        protected PluginPageView(string pluginId) : base(pluginId) { }

        public bool ShowSave { get; set; } = true;

        public bool ShowBack { get; set; } = false;

        public bool AllowSave { get; set; } = true;

        public bool AllowBack { get; set; } = true;

        public virtual Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            return Task.FromResult((IPluginUIView)this);
        }
    }
}
