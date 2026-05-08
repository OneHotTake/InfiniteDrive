using System.Linq;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using DesiredVersionBucket = InfiniteDrive.Models.DesiredVersionBucket;

namespace InfiniteDrive.UI.Settings
{
    public class ContentControlsTabView : PluginPageView
    {
        public ContentControlsTabView(string pluginId, ContentControlsUI ui)
            : base(pluginId)
        {
            ContentData = ui;
            LoadAll();
        }

        private ContentControlsUI UI => (ContentControlsUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case ContentControlsUI.AddBucketCommand:
                    AddBucket();
                    return this;

                case ContentControlsUI.RemoveBucketCommand:
                    RemoveBucket(data);
                    return this;

                default:
                    if (cmd.StartsWith(ContentControlsUI.RemoveBucketCommand + "_"))
                    {
                        var idx = cmd.Substring(ContentControlsUI.RemoveBucketCommand.Length + 1);
                        RemoveBucket(idx);
                        return this;
                    }
                    break;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        // ═══════════════════════════════════════════════════════════════
        // Load
        // ═══════════════════════════════════════════════════════════════

        private void LoadAll()
        {
            LoadQualitySettings();
            LoadBuckets();
        }

        private void LoadQualitySettings()
        {
            var cfg = Plugin.Instance.Configuration;
            UI.UseRemuxForAutoSelection = cfg.UseRemuxForAutoSelection;
            UI.PrioritizeExtendedEditions = cfg.PrioritizeExtendedEditions;
            RaiseUIViewInfoChanged();
        }

        private void LoadBuckets()
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();

            // Auto-create default bucket on first load so users have something to start with
            if (buckets.Count == 0)
            {
                buckets.Add(new DesiredVersionBucket { Resolution = "1080p", Audio = "Any Audio", Count = 2 });
                cfg.DesiredVersions = buckets;
                Plugin.Instance.SaveConfiguration();
            }

            UI.BucketList.Clear();

            for (int i = 0; i < buckets.Count; i++)
            {
                var b = buckets[i];
                var res = string.IsNullOrEmpty(b.Resolution) ? "Any" : b.Resolution;
                var audio = string.IsNullOrEmpty(b.Audio) || b.Audio == "Any Audio" ? "Any Audio" : b.Audio;

                UI.BucketList.Add(new GenericListItem
                {
                    PrimaryText = $"{b.Count}x {res} · {audio}",
                    SecondaryText = $"Bucket #{i + 1} — priority {i + 1}",
                    Icon = IconNames.tune,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded,
                    Button1 = new ButtonItem("Remove")
                    {
                        Icon = IconNames.delete,
                        CommandId = $"{ContentControlsUI.RemoveBucketCommand}_{i}",
                        ConfirmationPrompt = $"Remove bucket: {b.Count}x {res} · {audio}?",
                    },
                });
            }

            UpdateBucketStatus();
            RaiseUIViewInfoChanged();
        }

        private void UpdateBucketStatus()
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();
            var total = buckets.Sum(b => b.Count);

            if (buckets.Count == 0)
            {
                UI.BucketStatus.StatusText = "No buckets — Marvin cannot select versions. Add at least one bucket.";
                UI.BucketStatus.Status = ItemStatus.Warning;
                return;
            }

            UI.BucketStatus.StatusText = total > 8
                ? $"Total across all buckets: {total} — exceeds hardcoded max of 8"
                : $"Total across all buckets: {total} / 8 max versions";
            UI.BucketStatus.Status = total > 8 ? ItemStatus.Warning : ItemStatus.Succeeded;
        }


        // ═══════════════════════════════════════════════════════════════
        // Bucket Commands
        // ═══════════════════════════════════════════════════════════════

        private void AddBucket()
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();

            var resolution = UI.NewBucketResolution ?? "1080p";
            var audio = string.IsNullOrEmpty(UI.NewBucketAudio) ? "Any Audio" : UI.NewBucketAudio;
            var count = int.TryParse(UI.NewBucketCount, out var c) ? c : 1;

            var total = buckets.Sum(b => b.Count) + count;
            if (total > 8)
            {
                UI.BucketStatus.StatusText = $"Cannot add: total would be {total}, exceeding max of 8";
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

            LoadBuckets();
        }

        private void RemoveBucket(string data)
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();

            // Data1 format: "RemoveBucketCommand:0"
            var indexStr = data.Contains(':') ? data.Split(':').Last() : data;

            if (!int.TryParse(indexStr, out var index) || index < 0 || index >= buckets.Count)
            {
                UI.BucketStatus.StatusText = $"Invalid bucket index (data='{data}')";
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

            LoadBuckets();
        }


        // ═══════════════════════════════════════════════════════════════
        // Save
        // ═══════════════════════════════════════════════════════════════

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.UseRemuxForAutoSelection = UI.UseRemuxForAutoSelection;
            cfg.PrioritizeExtendedEditions = UI.PrioritizeExtendedEditions;
            // Note: DesiredVersions bucket list is saved immediately on Add/Remove
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            return base.OnSaveCommand(itemId, commandId, data);
        }

    }
}
