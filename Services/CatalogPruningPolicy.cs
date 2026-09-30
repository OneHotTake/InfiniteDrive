using System;
using System.Collections.Generic;
using System.Linq;
using InfiniteDrive.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace InfiniteDrive.Services;

internal static class CatalogPruningPolicy
{
    internal static bool IsCompleteProviderSnapshot(CatalogFetchResult result) =>
        result.ProviderReachable && string.IsNullOrEmpty(result.ErrorMessage)
        && result.CatalogOutcomes.Count > 0 && result.CatalogOutcomes.Values.All(x => x.Succeeded);

    internal static bool CanObserveAbsence(int completeProviders, int plannedProviders) =>
        plannedProviders > 0 && completeProviders == plannedProviders;

    internal static bool IsManagedPath(PluginConfiguration config, string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && new[] { config.SyncPathMovies, config.SyncPathShows, config.SyncPathAnime }
                .Any(root => !string.IsNullOrWhiteSpace(root) && ImportInventory.SafeManagedPath(root, path!));
        }
        catch { return false; }
    }

    // Every native version, every user's state and every series episode participates.
    // Missing/failed lookup evidence must never be interpreted as permission to prune.
    internal static bool HasProtectedNativeItems(IEnumerable<BaseItem>? matches, IEnumerable<User> users,
        Func<BaseItem, IEnumerable<BaseItem>?> episodes,
        Func<User, BaseItem, UserItemData?> userData, Func<BaseItem, bool> owned)
    {
        try
        {
            if (matches == null) return true;
            var allUsers = users.ToArray();
            if (allUsers.Length == 0) return true;
            foreach (var root in matches)
            {
                if (root == null || Protected(root)) return true;
                if (root is Series)
                {
                    var children = episodes(root);
                    if (children == null) return true;
                    foreach (var child in children)
                        if (child == null || Protected(child)) return true;
                }
            }
            return false;

            bool Protected(BaseItem item)
            {
                if (owned(item)) return true;
                foreach (var user in allUsers)
                {
                    var state = userData(user, item);
                    if (state == null || state.Played || state.PlayCount > 0 || state.PlaybackPositionTicks > 0
                        || state.LastPlayedDate.HasValue || state.IsFavorite || state.Rating.HasValue) return true;
                }
                return false;
            }
        }
        catch { return true; }
    }
}
