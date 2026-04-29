using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class CatalogsTabView : PluginPageView
    {
        public CatalogsTabView(string pluginId, CatalogsUI ui) : base(pluginId)
        {
            ContentData = ui;
            LoadCatalogsAsync(ui).ConfigureAwait(false);
        }

        private CatalogsUI UI => (CatalogsUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        // ── Load ────────────────────────────────────────────────────────────

        private async Task LoadCatalogsAsync(CatalogsUI ui)
        {
            try
            {
                var db = Plugin.Instance.DatabaseManager;
                var states = await db.GetAllSyncStatesAsync().ConfigureAwait(false);
                var disabledKeys = LoadDisabledKeys();

                ui.CatalogList.Clear();

                if (states.Count == 0)
                {
                    ui.CatalogList.Add(new GenericListItem
                    {
                        PrimaryText = "No catalogs found — save your provider URLs and run a sync",
                        Icon = IconNames.info,
                        IconMode = ItemListIconMode.SmallRegular,
                    });
                    RaiseUIViewInfoChanged();
                    return;
                }

                foreach (var state in states.OrderBy(s => s.CatalogType).ThenBy(s => s.CatalogName))
                {
                    var isEnabled = !disabledKeys.Contains(state.SourceKey);
                    var typeName = state.CatalogType ?? "unknown";
                    var name = state.CatalogName ?? state.SourceKey;

                    // Build detail line: type, count, last sync, errors
                    var details = $"{typeName} · {state.ItemCount} items";
                    if (!string.IsNullOrEmpty(state.LastSyncAt))
                        details += $" · synced {FormatTimeAgo(state.LastSyncAt)}";
                    if (state.ConsecutiveFailures > 0)
                        details += $" · {state.ConsecutiveFailures} failures";

                    ui.CatalogList.Add(new GenericListItem
                    {
                        PrimaryText = name,
                        SecondaryText = details,
                        Icon = isEnabled ? IconNames.check_circle : IconNames.radio_button_unchecked,
                        IconMode = ItemListIconMode.SmallRegular,
                        Status = isEnabled
                            ? (state.Status == "ok" ? ItemStatus.Succeeded : ItemStatus.Warning)
                            : ItemStatus.Unavailable,
                        Toggle = new ToggleButtonItem
                        {
                            IsChecked = isEnabled,
                            Caption = "Enabled",
                            Data1 = state.SourceKey,
                            CommandId = CatalogsUI.ToggleCatalogCommand,
                        },
                    });
                }

                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsUI] Failed to load catalogs");
            }
        }

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

        private static HashSet<string> LoadDisabledKeys()
        {
            try
            {
                var json = Plugin.Instance.Configuration.DisabledSourceKeysJson;
                if (string.IsNullOrWhiteSpace(json) || json == "[]")
                    return new HashSet<string>();
                return JsonSerializer.Deserialize<string[]>(json)?.ToHashSet() ?? new HashSet<string>();
            }
            catch { return new HashSet<string>(); }
        }

        // ── Commands ────────────────────────────────────────────────────────

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case CatalogsUI.SyncNowCommand:
                    await TriggerSyncAsync();
                    return this;

                case CatalogsUI.ToggleCatalogCommand:
                    var sourceKey = !string.IsNullOrEmpty(data) ? data : itemId;
                    if (!string.IsNullOrEmpty(sourceKey))
                        await ToggleCatalogAsync(sourceKey);
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        private Task TriggerSyncAsync()
        {
            UI.SyncStatus.StatusText = "Syncing...";
            UI.SyncStatus.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            try
            {
                Plugin.Instance.TriggerBackgroundSync();
                UI.SyncStatus.StatusText = "Sync triggered — refresh page to see updates";
                UI.SyncStatus.Status = ItemStatus.Succeeded;
            }
            catch (Exception ex)
            {
                UI.SyncStatus.StatusText = $"Failed: {ex.Message}";
                UI.SyncStatus.Status = ItemStatus.Failed;
            }

            RaiseUIViewInfoChanged();

            // Auto-refresh after delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(10000).ConfigureAwait(false);
                await LoadCatalogsAsync(UI).ConfigureAwait(false);
            });

            return Task.CompletedTask;
        }

        private async Task ToggleCatalogAsync(string sourceKey)
        {
            try
            {
                var disabled = LoadDisabledKeys();
                if (disabled.Contains(sourceKey))
                    disabled.Remove(sourceKey);
                else
                    disabled.Add(sourceKey);

                var cfg = Plugin.Instance.Configuration;
                cfg.DisabledSourceKeysJson = JsonSerializer.Serialize(disabled.ToArray());
                Plugin.Instance.SaveConfiguration();

                await LoadCatalogsAsync(UI).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsUI] Failed to toggle catalog");
            }
        }

        // ── Save ────────────────────────────────────────────────────────────

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            SettingsController.SaveCatalogs(UI, cfg);
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
