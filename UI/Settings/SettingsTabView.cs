using System;
using System.Threading.Tasks;
using Emby.Web.GenericEdit;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    /// <summary>
    /// Generic tab page view that reads/writes from PluginConfiguration.
    /// The saver callback receives the UI model so it can copy values to config.
    /// </summary>
    public class SettingsTabView<T> : PluginPageView where T : EditableOptionsBase
    {
        private readonly Action<T, PluginConfiguration> _saver;

        public SettingsTabView(string pluginId, T ui, Action<T, PluginConfiguration> saver)
            : base(pluginId)
        {
            ContentData = ui;
            _saver = saver;
        }

        private T UI => (T)ContentData;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            _saver(UI, cfg);
            Plugin.Instance.SaveConfiguration();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
