using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.Services;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class CatalogsAndListsTabView : PluginPageView
    {
        private static readonly JsonSerializerOptions _jsonOpts =
            new() { PropertyNameCaseInsensitive = true };

        public CatalogsAndListsTabView(string pluginId, CatalogsAndListsUI ui)
            : base(pluginId)
        {
            ContentData = ui;
            _ = LoadAllAsync();
        }

        private CatalogsAndListsUI UI => (CatalogsAndListsUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case CatalogsAndListsUI.SyncCatalogsCommand:
                    await SyncCatalogsAsync();
                    return this;

                case CatalogsAndListsUI.ToggleCatalogCommand:
                    await ToggleCatalogAsync(itemId);
                    return this;

                case CatalogsAndListsUI.AddSystemListCommand:
                    await AddSystemListAsync();
                    return this;

                case CatalogsAndListsUI.RemoveSystemListCommand:
                    await RemoveSystemListAsync(itemId);
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        // ═══════════════════════════════════════════════════════════════
        // Load
        // ═══════════════════════════════════════════════════════════════

        private async Task LoadAllAsync()
        {
            await Task.WhenAll(
                LoadCatalogsAsync(),
                LoadSystemListsAsync(),
                LoadUserListsAsync()
            ).ConfigureAwait(false);
        }

        private async Task LoadCatalogsAsync()
        {
            try
            {
                var db = Plugin.Instance.DatabaseManager;
                var states = await db.GetAllSyncStatesAsync().ConfigureAwait(false);
                var disabledKeys = LoadDisabledKeys();

                UI.CatalogList.Clear();

                if (states.Count == 0)
                {
                    UI.CatalogList.Add(new GenericListItem
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
                    var source = state.SourceKey.StartsWith("aio2:")
                        ? "Secondary Manifest"
                        : "Primary Manifest";

                    var details = $"{typeName} · {source} · {state.ItemCount} items";
                    if (!string.IsNullOrEmpty(state.LastSyncAt))
                        details += $" · synced {FormatTimeAgo(state.LastSyncAt)}";
                    if (state.ConsecutiveFailures > 0)
                        details += $" · {state.ConsecutiveFailures} failures";

                    UI.CatalogList.Add(new GenericListItem
                    {
                        PrimaryText = name,
                        SecondaryText = details,
                        Icon = isEnabled ? IconNames.check_circle : IconNames.radio_button_unchecked,
                        IconMode = ItemListIconMode.SmallRegular,
                        Status = isEnabled
                            ? (state.ConsecutiveFailures > 0 ? ItemStatus.Warning : ItemStatus.Succeeded)
                            : ItemStatus.Unavailable,
                        Toggle = new ToggleButtonItem
                        {
                            IsChecked = isEnabled,
                            Caption = "Enabled",
                            Data1 = state.SourceKey,
                            CommandId = CatalogsAndListsUI.ToggleCatalogCommand,
                        },
                    });
                }

                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsAndListsUI] Failed to load catalogs");
            }
        }

        private async Task LoadSystemListsAsync()
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                var traktClientId = config.TraktClientId;
                var tmdbApiKey = config.TmdbApiKey;

                UI.SystemListTable.Clear();

                // Get enabled providers info
                var enabled = ListFetcher.GetEnabledProviders(traktClientId, tmdbApiKey);

                // Get admin lists via API (directly call the service)
                var db = Plugin.Instance.DatabaseManager;
                var catalogs = await db.GetUserCatalogsByOwnerAsync("SERVER", activeOnly: false);

                foreach (var catalog in catalogs)
                {
                    var details = catalog.Service;
                    if (!string.IsNullOrEmpty(catalog.LastSyncedAt))
                        details += $" · synced {FormatTimeAgo(catalog.LastSyncedAt)}";
                    if (!string.IsNullOrEmpty(catalog.LastSyncStatus) && catalog.LastSyncStatus != "ok")
                        details += $" · {catalog.LastSyncStatus}";

                    UI.SystemListTable.Add(new GenericListItem
                    {
                        PrimaryText = catalog.DisplayName ?? catalog.ListUrl,
                        SecondaryText = details,
                        Icon = enabled.Contains(catalog.Service) ? IconNames.check_circle : IconNames.warning,
                        IconMode = ItemListIconMode.SmallRegular,
                        Status = enabled.Contains(catalog.Service) ? ItemStatus.Succeeded : ItemStatus.Warning,
                        Button1 = new ButtonItem("Remove")
                        {
                            Icon = IconNames.delete,
                            Data1 = catalog.Id,
                            CommandId = CatalogsAndListsUI.RemoveSystemListCommand,
                            ConfirmationPrompt = $"Remove system list '{catalog.DisplayName}'?",
                        },
                    });
                }

                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsAndListsUI] Failed to load system lists");
            }
        }

        private async Task LoadUserListsAsync()
        {
            try
            {
                UI.UserListTable.Clear();

                var db = Plugin.Instance.DatabaseManager;

                // For now, just show a message that user lists are managed by users
                // TODO: Add admin endpoint to view all user lists if needed
                var userCatalogCount = await db.GetActiveUserCatalogCountAsync();

                UI.UserListTable.Add(new GenericListItem
                {
                    PrimaryText = $"Total User Lists: {userCatalogCount}",
                    SecondaryText = $"Max per user: {UI.MaxListsPerUser}",
                    Icon = IconNames.people,
                    IconMode = ItemListIconMode.SmallRegular,
                });

                UI.UserListTable.Add(new GenericListItem
                {
                    PrimaryText = "User lists are managed by individual users",
                    SecondaryText = "Each user can add/manage their own lists via the Discover page",
                    Icon = IconNames.info,
                    IconMode = ItemListIconMode.SmallRegular,
                });

                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsAndListsUI] Failed to load user lists");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Commands
        // ═══════════════════════════════════════════════════════════════

        private Task SyncCatalogsAsync()
        {
            UI.CatalogSyncStatus.StatusText = "Syncing...";
            UI.CatalogSyncStatus.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            try
            {
                Plugin.Instance.TriggerBackgroundSync();
                UI.CatalogSyncStatus.StatusText = "Sync triggered — refresh page to see updates";
                UI.CatalogSyncStatus.Status = ItemStatus.Succeeded;
            }
            catch (Exception ex)
            {
                UI.CatalogSyncStatus.StatusText = $"Failed: {ex.Message}";
                UI.CatalogSyncStatus.Status = ItemStatus.Failed;
            }

            RaiseUIViewInfoChanged();
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

                await LoadCatalogsAsync();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsAndListsUI] Failed to toggle catalog");
            }
        }

        private async Task AddSystemListAsync()
        {
            var url = UI.SystemListUrlInput?.Trim();
            var name = UI.SystemListNameInput?.Trim();

            if (string.IsNullOrEmpty(url))
            {
                UI.SystemListStatus.StatusText = "Enter a list URL above, then click Add New System List.";
                UI.SystemListStatus.Status = ItemStatus.Warning;
                RaiseUIViewInfoChanged();
                return;
            }

            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                UI.SystemListStatus.StatusText = "List URL must use HTTPS.";
                UI.SystemListStatus.Status = ItemStatus.Warning;
                RaiseUIViewInfoChanged();
                return;
            }

            var provider = Services.ListFetcher.DetectProvider(url);
            if (provider == null)
            {
                UI.SystemListStatus.StatusText = "Unsupported list URL. Supported: mdblist.com, trakt.tv, themoviedb.org, anilist.co";
                UI.SystemListStatus.Status = ItemStatus.Warning;
                RaiseUIViewInfoChanged();
                return;
            }

            try
            {
                UI.SystemListStatus.StatusText = "Fetching list…";
                UI.SystemListStatus.Status = ItemStatus.InProgress;
                RaiseUIViewInfoChanged();

                var config = Plugin.Instance.Configuration;
                var fetchResult = await Services.ListFetcher.FetchAsync(
                    url, config.TraktClientId, config.TmdbApiKey,
                    Plugin.Instance.Logger, CancellationToken.None).ConfigureAwait(false);

                if (!fetchResult.Ok)
                {
                    UI.SystemListStatus.StatusText = $"Failed: {fetchResult.Error}";
                    UI.SystemListStatus.Status = ItemStatus.Failed;
                    RaiseUIViewInfoChanged();
                    return;
                }

                if (fetchResult.Items.Count == 0)
                {
                    UI.SystemListStatus.StatusText = "This list appears to be empty.";
                    UI.SystemListStatus.Status = ItemStatus.Warning;
                    RaiseUIViewInfoChanged();
                    return;
                }

                if (string.IsNullOrEmpty(name))
                    name = !string.IsNullOrWhiteSpace(fetchResult.DisplayName) ? fetchResult.DisplayName
                         : url.Split('?')[0].Split('/').LastOrDefault() ?? "System List";

                var db = Plugin.Instance.DatabaseManager;
                var catalogId = await db.CreateUserCatalogAsync("SERVER", provider, url, name).ConfigureAwait(false);

                Plugin.Instance.Logger.LogInformation(
                    "[CatalogsAndListsUI] Added system list: {Name} ({Url})", name, url);

                // Eager sync
                var syncService = new Services.UserCatalogSyncService(
                    Plugin.Instance.LogManager, db,
                    Plugin.Instance.StrmWriterService,
                    Plugin.Instance.CooldownGate,
                    Plugin.Instance.IdResolverService);
                var result = await syncService.SyncOneAsync(catalogId, CancellationToken.None).ConfigureAwait(false);

                UI.SystemListUrlInput = string.Empty;
                UI.SystemListNameInput = string.Empty;
                UI.SystemListStatus.StatusText = result.Ok
                    ? $"Added: {name} ({result.Added} items imported)"
                    : $"Added but sync failed: {result.Error}";
                UI.SystemListStatus.Status = result.Ok ? ItemStatus.Succeeded : ItemStatus.Warning;

                await LoadSystemListsAsync();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsAndListsUI] Failed to add system list");
                UI.SystemListStatus.StatusText = $"Failed: {ex.Message}";
                UI.SystemListStatus.Status = ItemStatus.Failed;
                RaiseUIViewInfoChanged();
            }
        }

        private async Task RemoveSystemListAsync(string catalogId)
        {
            try
            {
                var db = Plugin.Instance.DatabaseManager;
                await db.SetUserCatalogActiveAsync(catalogId, active: false);
                await LoadSystemListsAsync();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[CatalogsAndListsUI] Failed to remove system list");
                UI.SystemListStatus.StatusText = $"Failed: {ex.Message}";
                UI.SystemListStatus.Status = ItemStatus.Failed;
                RaiseUIViewInfoChanged();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Save
        // ═══════════════════════════════════════════════════════════════

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.TraktClientId = UI.TraktClientId ?? string.Empty;
            cfg.TmdbApiKey = UI.TmdbApiKey ?? string.Empty;
            cfg.MaxListsPerUser = UI.MaxListsPerUser;
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
    }
}
