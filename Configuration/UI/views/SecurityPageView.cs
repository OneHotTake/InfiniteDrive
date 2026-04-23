namespace InfiniteDrive.Configuration.UI.views
{
    using System;
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;

    internal class SecurityPageView : PluginPageView
    {
        public SecurityPageView(PluginInfo pluginInfo)
            : base(pluginInfo.Id)
        {
            var config = Plugin.Instance.Configuration;
            ContentData = new SecurityUI
            {
                EmbyApiKey = config.EmbyApiKey,
                TmdbApiKey = config.TmdbApiKey,
                PluginSecretStatus = string.IsNullOrEmpty(config.PluginSecret) ? "Not set" : "Active",
                LastRotationInfo = config.PluginSecretRotatedAt > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(config.PluginSecretRotatedAt).ToString("yyyy-MM-dd HH:mm")
                    : "Never rotated",
            };
        }

        public SecurityUI UI => ContentData as SecurityUI;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (UI != null)
            {
                var config = Plugin.Instance.Configuration;
                config.EmbyApiKey = UI.EmbyApiKey;
                config.TmdbApiKey = UI.TmdbApiKey;
                Plugin.Instance.SaveConfiguration();
            }
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
