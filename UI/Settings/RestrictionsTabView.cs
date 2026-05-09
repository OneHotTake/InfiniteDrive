using System;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class RestrictionsTabView : PluginPageView
    {
        public RestrictionsTabView(string pluginId, RestrictionsUI ui) : base(pluginId)
        {
            ContentData = ui;
            _ = LoadBlockedItemsAsync();
        }

        private RestrictionsUI UI => (RestrictionsUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case RestrictionsUI.AddToBlockListCommand:
                    await AddToBlockListAsync();
                    return this;

                case RestrictionsUI.UnblockItemCommand:
                    await UnblockItemAsync(itemId);
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        // ── Load ──────────────────────────────────────────────────────────────

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
                        PrimaryText = "No blocked content — use the field above to block titles or IDs",
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
                            CommandId = RestrictionsUI.UnblockItemCommand,
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
                Plugin.Instance.Logger.LogWarning(ex, "[RestrictionsUI] Failed to load blocked items");
            }
        }

        // ── Commands ──────────────────────────────────────────────────────────

        private async Task AddToBlockListAsync()
        {
            var input = UI.BlockListInput?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                UI.BlockListStatus.StatusText = "Enter a title or ID above, then click Block.";
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

                Plugin.Instance.Logger.LogInformation("[RestrictionsUI] Blocked: {Input}", input);

                UI.BlockListInput = string.Empty;
                UI.BlockListStatus.StatusText = $"Blocked: {input}";
                UI.BlockListStatus.Status = ItemStatus.Succeeded;

                await LoadBlockedItemsAsync();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[RestrictionsUI] Failed to block: {Input}", input);
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

                Plugin.Instance.Logger.LogInformation("[RestrictionsUI] Unblocked item {Id}", id);

                await LoadBlockedItemsAsync();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[RestrictionsUI] Failed to unblock item");
                UI.BlockListStatus.StatusText = $"Failed: {ex.Message}";
                UI.BlockListStatus.Status = ItemStatus.Failed;
                RaiseUIViewInfoChanged();
            }
        }

        // ── Save ──────────────────────────────────────────────────────────────

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.HideUnratedContent = UI.HideUnratedContent;
            Plugin.Instance.SaveConfiguration();
            return base.OnSaveCommand(itemId, commandId, data);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

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
