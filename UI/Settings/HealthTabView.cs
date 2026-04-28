using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.Services;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;
using SystemSnapshot = InfiniteDrive.Services.SystemSnapshot;
using ProviderHealth = InfiniteDrive.Services.ProviderHealth;
using SystemStateEnum = InfiniteDrive.Models.SystemStateEnum;

namespace InfiniteDrive.UI.Settings
{
    public class HealthTabView : PluginPageView
    {
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        public HealthTabView(string pluginId, HealthUI ui) : base(pluginId)
        {
            ShowSave = false;
            ContentData = ui;
            LoadHealthAsync(ui).ConfigureAwait(false);
        }

        private HealthUI UI => (HealthUI)ContentData;

        // ── Load ────────────────────────────────────────────────────────────

        private async Task LoadHealthAsync(HealthUI ui)
        {
            try
            {
                var stateSvc = Plugin.Instance.SystemStateService;
                if (stateSvc == null) return;

                var snapshot = await stateSvc.GetStateAsync().ConfigureAwait(false);
                ApplySnapshot(ui, snapshot);
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[HealthUI] Failed to load health state");
            }
        }

        private void ApplySnapshot(HealthUI ui, SystemSnapshot snapshot)
        {
            ui.OverallStatus.StatusText = $"{snapshot.State}: {snapshot.Description}";
            ui.OverallStatus.Status = snapshot.State switch
            {
                SystemStateEnum.Ready => ItemStatus.Succeeded,
                SystemStateEnum.Degraded => ItemStatus.Warning,
                SystemStateEnum.Error => ItemStatus.Failed,
                SystemStateEnum.Unconfigured => ItemStatus.Unavailable,
                _ => ItemStatus.Unknown
            };

            ui.HealthDetails.Clear();
            AddProviderRow(ui, "Primary", snapshot.PrimaryProvider);
            if (snapshot.SecondaryProvider.IsConfigured)
                AddProviderRow(ui, "Secondary", snapshot.SecondaryProvider);

            var lib = snapshot.Library;
            ui.HealthDetails.Add(new GenericListItem
            {
                PrimaryText = lib.IsConfigured ? "Libraries configured" : "Libraries NOT configured",
                SecondaryText = $"{lib.CatalogItemCount} catalog items, {lib.StrmFileCount} .strm files" +
                                (lib.IsAccessible ? "" : " — paths NOT accessible"),
                Icon = lib.IsConfigured && lib.IsAccessible ? IconNames.folder : IconNames.folder_off,
                IconMode = ItemListIconMode.SmallRegular,
                Status = lib.IsConfigured && lib.IsAccessible ? ItemStatus.Succeeded : ItemStatus.Failed,
            });

            RaiseUIViewInfoChanged();
        }

        private void AddProviderRow(HealthUI ui, string label, ProviderHealth provider)
        {
            ui.HealthDetails.Add(new GenericListItem
            {
                PrimaryText = $"{label}: {(provider.IsConfigured ? (provider.IsReachable ? "Connected" : "Unreachable") : "Not configured")}",
                SecondaryText = provider.IsConfigured
                    ? $"{provider.Message}{(provider.LatencyMs >= 0 ? $" ({provider.LatencyMs}ms)" : "")}"
                    : "",
                Icon = provider.IsReachable ? IconNames.cloud_done : (provider.IsConfigured ? IconNames.cloud_off : IconNames.cloud),
                IconMode = ItemListIconMode.SmallRegular,
                Status = provider.IsReachable ? ItemStatus.Succeeded : (provider.IsConfigured ? ItemStatus.Failed : ItemStatus.Unavailable),
            });
        }

        // ── Commands ────────────────────────────────────────────────────────

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var effectiveCommand = data?.Split(':')[0] ?? commandId;
            switch (effectiveCommand)
            {
                case HealthUI.SummonMarvinCommand:
                    await TriggerViaApiAsync("marvin", UI.MarvinStatus, "Marvin summoned");
                    return this;

                case HealthUI.RunCatalogSyncCommand:
                    await TriggerViaApiAsync("catalog_sync", UI.CatalogSyncStatus, "Catalog sync triggered");
                    return this;

                case HealthUI.RunPreCacheCommand:
                    await TriggerViaApiAsync("precache", UI.PreCacheStatus, "Pre-cache triggered");
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        private async Task TriggerViaApiAsync(string taskKey, StatusItem status, string successMsg)
        {
            status.StatusText = "Triggering...";
            status.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            try
            {
                var cfg = Plugin.Instance.Configuration;
                var baseUrl = cfg.EmbyBaseUrl.TrimEnd('/');
                var url = $"{baseUrl}/emby/InfiniteDrive/Trigger?task={taskKey}";

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var resp = await http.PostAsync(url, new StringContent("{}")).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    status.StatusText = successMsg;
                    status.Status = ItemStatus.Succeeded;
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    status.StatusText = $"Failed ({resp.StatusCode}): {body}";
                    status.Status = ItemStatus.Failed;
                }
            }
            catch (Exception ex)
            {
                status.StatusText = $"Failed: {ex.Message}";
                status.Status = ItemStatus.Failed;
            }

            RaiseUIViewInfoChanged();

            // Refresh health after a delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000).ConfigureAwait(false);
                await LoadHealthAsync(UI).ConfigureAwait(false);
            });
        }
    }
}
