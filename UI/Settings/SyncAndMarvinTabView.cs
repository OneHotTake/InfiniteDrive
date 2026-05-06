using System;
using System.Linq;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;
using DesiredVersionBucket = InfiniteDrive.Models.DesiredVersionBucket;

namespace InfiniteDrive.UI.Settings
{
    public class SyncAndMarvinTabView : PluginPageView
    {
        public SyncAndMarvinTabView(string pluginId, SyncAndMarvinUI ui)
            : base(pluginId)
        {
            ContentData = ui;
            LoadMarvinStatus(ui);
            LoadBuckets(ui);
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

                case SyncAndMarvinUI.AddBucketCommand:
                    AddBucket();
                    return this;

                case SyncAndMarvinUI.RemoveBucketCommand:
                    RemoveBucket(data);
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
            RaiseUIViewInfoChanged();
        }

        private void LoadBuckets(SyncAndMarvinUI ui)
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();
            ui.BucketList.Clear();

            if (buckets.Count == 0)
            {
                ui.BucketList.Add(new GenericListItem
                {
                    PrimaryText = "No quality buckets configured — all versions fill from next-best streams",
                    Icon = IconNames.info,
                    IconMode = ItemListIconMode.SmallRegular,
                });
                UpdateBucketStatus(ui);
                RaiseUIViewInfoChanged();
                return;
            }

            for (int i = 0; i < buckets.Count; i++)
            {
                var b = buckets[i];
                var res = string.IsNullOrEmpty(b.Resolution) ? "Any" : b.Resolution;
                var audio = string.IsNullOrEmpty(b.Audio) || b.Audio == "Any Audio" ? "Any Audio" : b.Audio;

                ui.BucketList.Add(new GenericListItem
                {
                    PrimaryText = $"{b.Count}x {res} · {audio}",
                    SecondaryText = $"Bucket #{i + 1} — priority {i + 1}",
                    Icon = IconNames.tune,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded,
                    Button1 = new ButtonItem("Remove")
                    {
                        Icon = IconNames.delete,
                        Data1 = i.ToString(),
                        CommandId = SyncAndMarvinUI.RemoveBucketCommand,
                        ConfirmationPrompt = $"Remove bucket: {b.Count}x {res} · {audio}?",
                    },
                });
            }

            UpdateBucketStatus(ui);
            RaiseUIViewInfoChanged();
        }

        private void UpdateBucketStatus(SyncAndMarvinUI ui)
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();
            var total = buckets.Sum(b => b.Count);
            var max = ui.MaxVersionsPerItem;

            if (buckets.Count == 0)
            {
                ui.BucketStatus.StatusText = "No buckets. All versions fill from next-best streams.";
                ui.BucketStatus.Status = ItemStatus.None;
                return;
            }

            ui.BucketStatus.StatusText = $"Total across all buckets: {total} / {max} max versions";
            ui.BucketStatus.Status = total > max ? ItemStatus.Warning : ItemStatus.Succeeded;
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

        private void AddBucket()
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();

            var resolution = UI.NewBucketResolution ?? "1080p";
            var audio = string.IsNullOrEmpty(UI.NewBucketAudio) ? "Any Audio" : UI.NewBucketAudio;
            var count = int.TryParse(UI.NewBucketCount, out var c) ? c : 1;

            var total = buckets.Sum(b => b.Count) + count;
            if (total > UI.MaxVersionsPerItem)
            {
                UI.BucketStatus.StatusText = $"Cannot add: total would be {total}, exceeding max {UI.MaxVersionsPerItem}";
                UI.BucketStatus.Status = ItemStatus.Warning;
                RaiseUIViewInfoChanged();
                return;
            }

            buckets.Add(new DesiredVersionBucket
            {
                Resolution = resolution,
                Audio = audio,
                Count = count,
            });

            cfg.DesiredVersions = buckets;
            Plugin.Instance.SaveConfiguration();

            UI.BucketStatus.StatusText = $"Added: {count}x {resolution} · {audio}";
            UI.BucketStatus.Status = ItemStatus.Succeeded;

            LoadBuckets(UI);
        }

        private void RemoveBucket(string data)
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();

            // Extract index from Data1 (format: "RemoveBucketCommand:3" or just "3")
            var indexStr = data;
            if (data.Contains(':'))
                indexStr = data.Split(':').Last();

            if (!int.TryParse(indexStr, out var index) || index < 0 || index >= buckets.Count)
            {
                UI.BucketStatus.StatusText = "Invalid bucket index";
                UI.BucketStatus.Status = ItemStatus.Warning;
                RaiseUIViewInfoChanged();
                return;
            }

            var removed = buckets[index];
            buckets.RemoveAt(index);

            cfg.DesiredVersions = buckets;
            Plugin.Instance.SaveConfiguration();

            var res = string.IsNullOrEmpty(removed.Resolution) ? "Any" : removed.Resolution;
            var audio = string.IsNullOrEmpty(removed.Audio) || removed.Audio == "Any Audio" ? "Any Audio" : removed.Audio;
            UI.BucketStatus.StatusText = $"Removed: {removed.Count}x {res} · {audio}";
            UI.BucketStatus.Status = ItemStatus.Succeeded;

            LoadBuckets(UI);
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
            // Note: DesiredVersions bucket list is saved immediately on Add/Remove
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            LoadMarvinStatus(UI);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
