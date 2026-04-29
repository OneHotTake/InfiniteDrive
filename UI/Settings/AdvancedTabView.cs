using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class AdvancedTabView : PluginPageView
    {
        public AdvancedTabView(string pluginId, AdvancedUI ui) : base(pluginId)
        {
            ContentData = ui;
        }

        private AdvancedUI UI => (AdvancedUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            Save(UI);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var effective = commandId;
            if (string.IsNullOrEmpty(effective) && !string.IsNullOrEmpty(data))
                effective = data.Split(':')[0];

            if (effective == AdvancedUI.RotateSecretCommand)
            {
                try
                {
                    var newSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    UI.PluginSecret = newSecret;
                    var cfg = Plugin.Instance.Configuration;
                    cfg.PluginSecret = newSecret;
                    cfg.PluginSecretRotatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    Plugin.Instance.SaveConfiguration();
                    Plugin.Instance.Logger.LogInformation("[Advanced] Plugin secret rotated");
                    RaiseUIViewInfoChanged();
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.LogWarning(ex, "[Advanced] Secret rotation failed");
                }

                return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        internal static void Save(AdvancedUI ui)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.SkipFutureEpisodes = ui.SkipFutureEpisodes;
            cfg.ApiDailyBudget = ui.ApiDailyBudget;
            cfg.CacheLifetimeMinutes = ui.CacheLifetimeMinutes;
            cfg.SignatureValidityDays = ui.SignatureValidityDays;
            cfg.PluginSecret = ui.PluginSecret ?? string.Empty;
            cfg.DefaultSeriesSeasons = ui.DefaultSeriesSeasons;
            cfg.DefaultSeriesEpisodesPerSeason = ui.DefaultSeriesEpisodesPerSeason;
            cfg.DontPanic = ui.DontPanic;
            cfg.MaxConcurrentProxyStreams = ui.MaxConcurrentProxyStreams;
            Plugin.Instance.SaveConfiguration();
            // GLOBAL RULE (Sprint 502): After ANY settings change on ANY tab, immediately summon Marvin.
            Plugin.Instance.TriggerBackgroundSync();
        }
    }
}
