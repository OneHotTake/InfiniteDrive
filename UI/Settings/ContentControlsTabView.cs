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
    public class ContentControlsTabView : PluginPageView
    {
        public ContentControlsTabView(string pluginId, ContentControlsUI ui)
            : base(pluginId)
        {
            ContentData = ui;
            _ = LoadAllAsync();
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
                case ContentControlsUI.AddToBlockListCommand:
                    await AddToBlockListAsync();
                    return this;

                case ContentControlsUI.UnblockItemCommand:
                    await UnblockItemAsync(itemId);
                    return this;

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

        private async Task LoadAllAsync()
        {
            LoadQualitySettings();
            LoadBuckets();
            await LoadBlockedItemsAsync();
        }

        private void LoadQualitySettings()
        {
            var cfg = Plugin.Instance.Configuration;
            UI.UseRemuxForAutoSelection = cfg.UseRemuxForAutoSelection;
            RaiseUIViewInfoChanged();
        }

        private void LoadBuckets()
        {
            var cfg = Plugin.Instance.Configuration;
            var buckets = cfg.DesiredVersions ?? new();
            UI.BucketList.Clear();

            if (buckets.Count == 0)
            {
                UI.BucketList.Add(new GenericListItem
                {
                    PrimaryText = "No quality buckets configured — all versions fill from next-best streams",
                    Icon = IconNames.info,
                    IconMode = ItemListIconMode.SmallRegular,
                });
                UpdateBucketStatus();
                RaiseUIViewInfoChanged();
                return;
            }

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
                UI.BucketStatus.StatusText = "No buckets. All versions fill from next-best streams.";
                UI.BucketStatus.Status = ItemStatus.None;
                return;
            }

            UI.BucketStatus.StatusText = total > 8
                ? $"Total across all buckets: {total} — exceeds hardcoded max of 8"
                : $"Total across all buckets: {total} / 8 max versions";
            UI.BucketStatus.Status = total > 8 ? ItemStatus.Warning : ItemStatus.Succeeded;
        }

        private async Task LoadBlockedItemsAsync()
        {
            try
            {
                UI.BlockedItemList.Clear();

                var db = Plugin.Instance.DatabaseManager;
                var items = await db.GetBlockedItemsAsync(skip: 0, limit: 100).ConfigureAwait(false);

                if (items.Count == 0)
                {
                    UI.BlockedItemList.Add(new GenericListItem
                    {
                        PrimaryText = "No blocked content — use 'Add to Block List' to block titles or IDs",
                        Icon = IconNames.info,
                        IconMode = ItemListIconMode.SmallRegular,
                    });
                    RaiseUIViewInfoChanged();
                    return;
                }

                foreach (var item in items)
                {
                    var details = $"{item.MediaType} · blocked {FormatTimeAgo(item.BlockedAt)}";

                    UI.BlockedItemList.Add(new GenericListItem
                    {
                        PrimaryText = item.Title,
                        SecondaryText = details,
                        Icon = IconNames.block,
                        IconMode = ItemListIconMode.SmallRegular,
                        Status = ItemStatus.Unavailable,
                        Button1 = new ButtonItem("Unblock")
                        {
                            Icon = IconNames.check_circle,
                            Data1 = item.Id.ToString(),
                            CommandId = ContentControlsUI.UnblockItemCommand,
                            ConfirmationPrompt = $"Unblock '{item.Title}'?",
                        },
                    });
                }

                UI.BlockListStatus.StatusText = $"{items.Count} item(s) blocked";
                UI.BlockListStatus.Status = ItemStatus.Warning;
                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[ContentControlsUI] Failed to load blocked items");
            }
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
        // Block List Commands
        // ═══════════════════════════════════════════════════════════════

        private async Task AddToBlockListAsync()
        {
            var input = UI.BlockListInput?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                UI.BlockListStatus.StatusText = "Enter a title or ID above, then click Add to Block List.";
                UI.BlockListStatus.Status = ItemStatus.Warning;
                RaiseUIViewInfoChanged();
                return;
            }

            try
            {
                var db = Plugin.Instance.DatabaseManager;

                string? aioId = null, tmdbId = null;
                var title = input;
                var mediaType = "movie";

                if (input.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && input.Length >= 3)
                {
                    aioId = input;
                    title = $"Blocked IMDB: {input}";
                }
                else if (int.TryParse(input, out _))
                {
                    tmdbId = input;
                    title = $"Blocked TMDB: {input}";
                }

                await db.UpsertBlockedItemAsync(aioId, tmdbId, null, title, mediaType, "admin").ConfigureAwait(false);

                Plugin.Instance.Logger.LogInformation(
                    "[ContentControlsUI] Blocked: {Input}", input);

                UI.BlockListInput = string.Empty;
                UI.BlockListStatus.StatusText = $"Blocked: {input}";
                UI.BlockListStatus.Status = ItemStatus.Succeeded;

                await LoadBlockedItemsAsync();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[ContentControlsUI] Failed to block: {Input}", input);
                UI.BlockListStatus.StatusText = $"Failed: {ex.Message}";
                UI.BlockListStatus.Status = ItemStatus.Failed;
                RaiseUIViewInfoChanged();
            }
        }

        private async Task UnblockItemAsync(string itemIdStr)
        {
            try
            {
                if (!long.TryParse(itemIdStr, out var id))
                    return;

                var db = Plugin.Instance.DatabaseManager;
                await db.UnblockItemAsync(id, unblockedBy: "admin").ConfigureAwait(false);

                Plugin.Instance.Logger.LogInformation(
                    "[ContentControlsUI] Unblocked item {Id}", id);

                await LoadBlockedItemsAsync();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[ContentControlsUI] Failed to unblock item");
                UI.BlockListStatus.StatusText = $"Failed: {ex.Message}";
                UI.BlockListStatus.Status = ItemStatus.Failed;
                RaiseUIViewInfoChanged();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Save
        // ═══════════════════════════════════════════════════════════════

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.UseRemuxForAutoSelection = UI.UseRemuxForAutoSelection;
            cfg.HideUnratedContent = UI.HideUnratedContent;
            // Note: DesiredVersions bucket list is saved immediately on Add/Remove
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            return base.OnSaveCommand(itemId, commandId, data);
        }

        // ═══════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════

        private static string FormatTimeAgo(string isoTime)
        {
            try
            {
                var dt = DateTime.Parse(isoTime, null, System.Globalization.DateTimeStyles.RoundtripKind);
                var ago = DateTime.UtcNow - dt;
                if (ago.TotalMinutes < 1) return "just now";
                if (ago.TotalHours < 1) return $"{(int)ago.TotalMinutes}m ago";
                if (ago.TotalDays < 1) return $"{(int)ago.TotalHours}h ago";
                return $"{(int)ago.TotalDays}d ago";
            }
            catch { return isoTime; }
        }
    }
}
