using System;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class SyncAndMarvinTabView : PluginPageView
    {
        public SyncAndMarvinTabView(string pluginId, SyncAndMarvinUI ui)
            : base(pluginId)
        {
            ContentData = ui;
            LoadMarvinStatus(ui);
        }

        private SyncAndMarvinUI UI => (SyncAndMarvinUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case SyncAndMarvinUI.RunMarvinNowCommand:
                    RunMarvinNow();
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        // ═══════════════════════════════════════════════════════════════
        // Load
        // ═══════════════════════════════════════════════════════════════

        private void LoadMarvinStatus(SyncAndMarvinUI ui)
        {
            var cfg = Plugin.Instance.Configuration;
            ui.MarvinStatus.StatusText =
                $"Every {cfg.MarvinProcessIntervalMinutes}m · {cfg.StreamResolutionBatchSize} items per pass · " +
                $"{cfg.MarvinActionsPerHour} actions/hr ceiling";
            ui.MarvinStatus.Status = ItemStatus.Succeeded;
            RaiseUIViewInfoChanged();
        }

        // ═══════════════════════════════════════════════════════════════
        // Commands
        // ═══════════════════════════════════════════════════════════════

        private void RunMarvinNow()
        {
            UI.MarvinStatus.StatusText = "Running Marvin...";
            UI.MarvinStatus.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            try
            {
                Plugin.Instance.TriggerBackgroundSync();
                UI.MarvinStatus.StatusText = "Marvin triggered — refresh page to see updates";
                UI.MarvinStatus.Status = ItemStatus.Succeeded;
            }
            catch (Exception ex)
            {
                UI.MarvinStatus.StatusText = $"Failed: {ex.Message}";
                UI.MarvinStatus.Status = ItemStatus.Failed;
            }

            RaiseUIViewInfoChanged();
        }

        // ═══════════════════════════════════════════════════════════════
        // Save
        // ═══════════════════════════════════════════════════════════════

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.MarvinProcessIntervalMinutes = UI.MarvinProcessIntervalMinutes;
            cfg.StreamResolutionBatchSize = UI.StreamResolutionBatchSize;
            cfg.MarvinActionsPerHour = UI.MarvinActionsPerHour;
            cfg.RespectPlaylistsWhenPruning = UI.RespectPlaylistsWhenPruning;
            cfg.AutoDeduplicatePhysicalMedia = UI.AutoDeduplicatePhysicalMedia;
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            LoadMarvinStatus(UI);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
