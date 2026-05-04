using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.Services;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class ContentControlsTabView : PluginPageView
    {
        private static readonly string[] AllQualityTiers =
        {
            "4K REMUX / HDR / Atmos",
            "4K 5.1 / DTS",
            "4K (any)",
            "1080p Atmos / TrueHD",
            "1080p 5.1",
            "1080p (any)",
            "720p",
            "SD / Unknown / Low-bandwidth",
        };

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
                case ContentControlsUI.ToggleTierCommand:
                    await ToggleTierAsync(data);
                    return this;

                case ContentControlsUI.AddToBlockListCommand:
                    await AddToBlockListAsync();
                    return this;

                case ContentControlsUI.UnblockItemCommand:
                    await UnblockItemAsync(itemId);
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        // ═══════════════════════════════════════════════════════════════
        // Load
        // ═══════════════════════════════════════════════════════════════

        private async Task LoadAllAsync()
        {
            LoadQualityTiers();
            await LoadBlockedItemsAsync();
        }

        private void LoadQualityTiers()
        {
            var cfg = Plugin.Instance.Configuration;
            var preferred = cfg.PreferredQualityTiers?.ToHashSet() ?? new HashSet<string>(AllQualityTiers);
            var defaultTier = cfg.DefaultQualityTier ?? "1080p (any)";

            UI.UseRemuxForAutoSelection = cfg.UseRemuxForAutoSelection;
            UI.QualityTierList.Clear();

            foreach (var tier in AllQualityTiers)
            {
                var isEnabled = preferred.Contains(tier);
                var isDefault = tier == defaultTier;

                UI.QualityTierList.Add(new GenericListItem
                {
                    PrimaryText = tier + (isDefault ? " (default)" : ""),
                    Icon = isEnabled ? IconNames.check_circle : IconNames.radio_button_unchecked,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = isEnabled ? ItemStatus.Succeeded : ItemStatus.Unavailable,
                    Toggle = new ToggleButtonItem
                    {
                        IsChecked = isEnabled,
                        Caption = isDefault ? "Default" : "Enabled",
                        Data1 = tier,
                        CommandId = ContentControlsUI.ToggleTierCommand,
                    },
                });
            }

            RaiseUIViewInfoChanged();
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
                    var id = item.ImdbId ?? item.TmdbId ?? item.AnilistId ?? "—";
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
        // Commands
        // ═══════════════════════════════════════════════════════════════

        private Task ToggleTierAsync(string tierName)
        {
            try
            {
                var cfg = Plugin.Instance.Configuration;
                var preferred = cfg.PreferredQualityTiers?.ToList() ?? new List<string>(AllQualityTiers);

                if (preferred.Contains(tierName))
                {
                    if (preferred.Count <= 1)
                    {
                        UI.BlockListStatus.StatusText = "Cannot disable the last enabled tier";
                        UI.BlockListStatus.Status = ItemStatus.Warning;
                        RaiseUIViewInfoChanged();
                        return Task.CompletedTask;
                    }
                    preferred.Remove(tierName);
                }
                else
                {
                    preferred.Add(tierName);
                }

                cfg.PreferredQualityTiers = preferred;
                Plugin.Instance.SaveConfiguration();
                Plugin.Instance.TriggerBackgroundSync();

                LoadQualityTiers();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[ContentControlsUI] Failed to toggle tier");
            }

            return Task.CompletedTask;
        }

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

                // Detect ID type
                string? imdbId = null, tmdbId = null;
                var title = input;
                var mediaType = "movie";

                if (input.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && input.Length >= 3)
                {
                    imdbId = input;
                    title = $"Blocked IMDB: {input}";
                }
                else if (int.TryParse(input, out _))
                {
                    tmdbId = input;
                    title = $"Blocked TMDB: {input}";
                }

                await db.UpsertBlockedItemAsync(imdbId, tmdbId, null, title, mediaType, "admin").ConfigureAwait(false);

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
            cfg.DefaultQualityTier = UI.DefaultQualityTier ?? "1080p (any)";
            cfg.UseRemuxForAutoSelection = UI.UseRemuxForAutoSelection;
            cfg.HideUnratedContent = UI.HideUnratedContent;

            // Sync DefaultSlotKey from UI tier selection so .strm files use the chosen quality
            if (ResolverService.UiTierNameToKey.TryGetValue(cfg.DefaultQualityTier, out var tierKey))
                cfg.DefaultSlotKey = tierKey;
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
