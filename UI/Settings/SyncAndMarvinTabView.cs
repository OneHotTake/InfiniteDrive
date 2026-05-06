using System;
using System.Linq;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

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
                    await RunMarvinNowAsync();
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
                $"Interval: {cfg.MarvinProcessIntervalMinutes}m · Batch: {cfg.StreamResolutionBatchSize} · " +
                $"Rate limit: {cfg.MarvinActionsPerHour}/hr";
            ui.MarvinStatus.Status = ItemStatus.Succeeded;

            // Show actual bucket config
            var buckets = cfg.DesiredVersions;
            if (buckets != null && buckets.Count > 0)
            {
                var bucketDesc = string.Join(" + ", buckets.Select(b =>
                {
                    var res = string.IsNullOrEmpty(b.Resolution) ? "Any" : b.Resolution;
                    var audio = string.IsNullOrEmpty(b.Audio) || b.Audio == "Any Audio" ? "" : $" {b.Audio}";
                    return $"{b.Count}x {res}{audio}";
                }));
                ui.VersionBucketsSummary = new Emby.Web.GenericEdit.Elements.LabelItem(
                    $"Quality buckets: {bucketDesc}. Remaining slots fill with next-best streams.");
            }

            RaiseUIViewInfoChanged();
        }

        // ═══════════════════════════════════════════════════════════════
        // Commands
        // ═══════════════════════════════════════════════════════════════

        private Task RunMarvinNowAsync()
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
            return Task.CompletedTask;
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
            cfg.MaxVersionsPerItem = UI.MaxVersionsPerItem;
            cfg.RespectPlaylistsWhenPruning = UI.RespectPlaylistsWhenPruning;
            cfg.AutoDeduplicatePhysicalMedia = UI.AutoDeduplicatePhysicalMedia;
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            LoadMarvinStatus(UI);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
