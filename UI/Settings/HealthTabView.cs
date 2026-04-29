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

        public override bool IsCommandAllowed(string commandKey)
        {
            return true;
        }

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
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
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

        private Task TriggerViaApiAsync(string taskKey, StatusItem status, string successMsg)
        {
            status.StatusText = "Triggering...";
            status.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            try
            {
                var plugin = Plugin.Instance;
                var logMgr = plugin.LogManager;
                var libMgr = plugin.LibraryManager;

                switch (taskKey)
                {
                    case "marvin":
                        if (libMgr == null) throw new InvalidOperationException("LibraryManager not available");
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                var marvin = new Tasks.MarvinTask(logMgr, libMgr);
                                await marvin.Execute(CancellationToken.None, new Progress<double>()).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                plugin.Logger.LogWarning(ex, "[HealthUI] Marvin failed");
                            }
                        });
                        break;

                    case "catalog_sync":
                        if (libMgr == null) throw new InvalidOperationException("LibraryManager not available");
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                var marvin = new Tasks.MarvinTask(logMgr, libMgr);
                                await marvin.Execute(CancellationToken.None, new Progress<double>()).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                plugin.Logger.LogWarning(ex, "[HealthUI] Catalog sync failed");
                            }
                        });
                        break;

                    case "precache":
                        var precacheConfig = plugin.Configuration;
                        if (precacheConfig == null || !precacheConfig.EnablePreCache)
                            throw new InvalidOperationException("Pre-cache is disabled");
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                var task = new Tasks.PreCacheAioStreamsTask(logMgr);
                                await task.Execute(CancellationToken.None, new Progress<double>()).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                plugin.Logger.LogWarning(ex, "[HealthUI] Pre-cache failed");
                            }
                        });
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown task: {taskKey}");
                }

                status.StatusText = successMsg;
                status.Status = ItemStatus.Succeeded;
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

            return Task.CompletedTask;
        }
    }
}
