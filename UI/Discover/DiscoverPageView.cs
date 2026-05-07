using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.Services;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Discover
{
    public class DiscoverPageView : PluginPageView
    {
        public DiscoverPageView(string pluginId) : base(pluginId)
        {
            ShowSave = false;
            var ui = new DiscoverUI();
            ContentData = ui;
            LoadInitialDataAsync(ui).ConfigureAwait(false);
        }

        private DiscoverUI UI => ContentData as DiscoverUI;

        private async Task LoadInitialDataAsync(DiscoverUI ui)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var ct = cts.Token;

                var cfg = Plugin.Instance.Configuration;
                var logger = Plugin.Instance.Logger;

                // Load popular rails from Cinemeta
                using var client = AioStreamsClientFactory.Create(logger);

                var movieTop = await client.GetCinemetaTopAsync("movie", 20, ct).ConfigureAwait(false);
                foreach (var m in movieTop)
                {
                    ui.PopularMovies.Add(new GenericListItem
                    {
                        PrimaryText = m.Name ?? string.Empty,
                        SecondaryText = $"{m.Type} · {m.ReleaseInfo}",
                        Icon = IconNames.movie,
                        IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem("Add to Library")
                        {
                            Icon = IconNames.add_circle,
                            Data1 = $"{AddToLibCmd}:{m.Id}:{m.Type}"
                        },
                    });
                }

                var seriesTop = await client.GetCinemetaTopAsync("series", 20, ct).ConfigureAwait(false);
                foreach (var s in seriesTop)
                {
                    ui.PopularSeries.Add(new GenericListItem
                    {
                        PrimaryText = s.Name ?? string.Empty,
                        SecondaryText = $"{s.Type} · {s.ReleaseInfo}",
                        Icon = IconNames.tv,
                        IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem("Add to Library")
                        {
                            Icon = IconNames.add_circle,
                            Data1 = $"{AddToLibCmd}:{s.Id}:{s.Type}"
                        },
                    });
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[DiscoverUI] Failed to load popular rails");
            }

            await LoadBlockedItemsAsync(UI).ConfigureAwait(false);
        }

        private async Task LoadBlockedItemsAsync(DiscoverUI ui)
        {
            try
            {
                ui.BlockedItems.Clear();
                var blocked = await Plugin.Instance.BlockListService
                    .GetBlockedItemsAsync(0, 200).ConfigureAwait(false);
                foreach (var item in blocked)
                {
                    ui.BlockedItems.Add(new GenericListItem
                    {
                        PrimaryText = item.Title.Length > 0 ? item.Title : (item.AioId ?? item.Id.ToString()),
                        SecondaryText = $"Blocked on {item.BlockedAt}",
                        Icon = IconNames.block,
                        IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem("Unblock")
                        {
                            Icon = IconNames.remove_circle,
                            Data1 = $"{UnblockCmd}:{item.Id}"
                        },
                    });
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[DiscoverUI] Failed to load blocked items");
            }
        }

        private const string AddToLibCmd = DiscoverUI.AddToLibraryCommand;
        private const string UnblockCmd = DiscoverUI.UnblockCommand;

        public override bool IsCommandAllowed(string commandKey)
        {
            switch (commandKey)
            {
                case DiscoverUI.SearchCommand:
                case DiscoverUI.AddToLibraryCommand:
                case DiscoverUI.UnblockCommand:
                    return true;
            }
            // Button Data1 values are also command keys
            if (commandKey == DiscoverUI.SearchCommand) return true;
            return base.IsCommandAllowed(commandKey);
        }

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var ui = UI;
            // commandId is primary; fall back to Data1 prefix for list-item buttons
            var effectiveCommand = commandId;
            if (string.IsNullOrEmpty(effectiveCommand) && !string.IsNullOrEmpty(data))
                effectiveCommand = data.Split(':')[0];
            switch (effectiveCommand)
            {
                case DiscoverUI.SearchCommand:
                    await RunSearchAsync(ui).ConfigureAwait(false);
                    return this;

                case DiscoverUI.AddToLibraryCommand:
                    await RunAddToLibraryAsync(data ?? string.Empty).ConfigureAwait(false);
                    return this;

                case DiscoverUI.UnblockCommand:
                    await RunUnblockAsync(data ?? string.Empty, ui).ConfigureAwait(false);
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data).ConfigureAwait(false);
        }

        private async Task RunSearchAsync(DiscoverUI ui)
        {
            ui.SearchResults.Clear();
            var query = ui.SearchQuery?.Trim();
            if (string.IsNullOrEmpty(query)) return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var cfg = Plugin.Instance.Configuration;
                var logger = Plugin.Instance.Logger;

                using var client = AioStreamsClientFactory.Create(logger);

                // Search both types in parallel
                var movieTask = client.SearchLiveAsync(query, "movie", 0, 10, cts.Token);
                var seriesTask = client.SearchLiveAsync(query, "series", 0, 10, cts.Token);
                await Task.WhenAll(movieTask, seriesTask).ConfigureAwait(false);

                var movieResp = await movieTask;
                var seriesResp = await seriesTask;
                var movieResults = movieResp?.Metas ?? new System.Collections.Generic.List<AioStreamsMeta>();
                var seriesResults = seriesResp?.Metas ?? new System.Collections.Generic.List<AioStreamsMeta>();

                foreach (var m in movieResults)
                {
                    ui.SearchResults.Add(new GenericListItem
                    {
                        PrimaryText = m.Name ?? string.Empty,
                        SecondaryText = $"Movie · {m.ReleaseInfo}",
                        Icon = IconNames.movie,
                        IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem("Add to Library")
                        {
                            Icon = IconNames.add_circle,
                            Data1 = $"{AddToLibCmd}:{m.Id}:movie"
                        },
                    });
                }

                foreach (var s in seriesResults)
                {
                    ui.SearchResults.Add(new GenericListItem
                    {
                        PrimaryText = s.Name ?? string.Empty,
                        SecondaryText = $"Series · {s.ReleaseInfo}",
                        Icon = IconNames.tv,
                        IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem("Add to Library")
                        {
                            Icon = IconNames.add_circle,
                            Data1 = $"{AddToLibCmd}:{s.Id}:series"
                        },
                    });
                }

                if (ui.SearchResults.Count == 0)
                {
                    ui.SearchResults.Add(new GenericListItem
                    {
                        PrimaryText = "No results found",
                        Icon = IconNames.search_off,
                        IconMode = ItemListIconMode.SmallRegular,
                    });
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[DiscoverUI] Search failed");
                ui.SearchResults.Add(new GenericListItem
                {
                    PrimaryText = "Search failed — check AIOStreams connection",
                    Icon = IconNames.error,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Failed,
                });
            }
        }

        private async Task RunAddToLibraryAsync(string data)
        {
            // data format: "AddToLibraryCommand:{id}:{type}"
            var svc = Plugin.Instance.UnifiedItemService;
            if (svc == null) return;

            var parts = data.Split(':');
            if (parts.Length < 3) return;

            var id = parts[1];
            var type = parts[2];

            try
            {
                await svc.AddItemAsync(id, type, string.Empty, null, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[DiscoverUI] AddToLibrary failed for {Id}", id);
            }
        }

        private async Task RunUnblockAsync(string data, DiscoverUI ui)
        {
            // data format: "UnblockCommand:{rowId}"
            var parts = data.Split(':');
            if (parts.Length < 2 || !long.TryParse(parts[1], out var rowId)) return;

            try
            {
                await Plugin.Instance.BlockListService
                    .UnblockItemAsync(rowId, Guid.Empty).ConfigureAwait(false);
                await LoadBlockedItemsAsync(ui).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[DiscoverUI] Unblock failed for row {Id}", rowId);
            }
        }
    }
}
