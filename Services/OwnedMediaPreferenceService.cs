using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Enforces the owned-media-first invariant at every streamed write boundary.
    /// A physical Emby item outside InfiniteDrive's managed roots always supersedes
    /// the matching catalog item.
    /// </summary>
    public sealed class OwnedMediaPreferenceService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public OwnedMediaPreferenceService(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// Finds a physical Emby movie or series with the same provider identity,
        /// excluding .strm entries and everything beneath InfiniteDrive's roots.
        /// </summary>
        public BaseItem? FindOwnedMedia(CatalogItem item)
        {
            var providerIds = BuildProviderIds(item);
            if (providerIds.Count == 0) return null;

            var includeTypes = IsSeries(item)
                ? new[] { "Series" }
                : new[] { "Movie" };

            var matches = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AnyProviderIdEquals = providerIds.ToArray(),
                IncludeItemTypes = includeTypes,
                IsVirtualItem = false,
                Recursive = true,
            });

            var roots = GetManagedRoots();
            return matches.FirstOrDefault(match =>
                !string.IsNullOrWhiteSpace(match.Path)
                && !match.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                && !IsPathWithinRoots(match.Path, roots));
        }

        /// <summary>
        /// Retires the streamed catalog item immediately when an owned match exists.
        /// Returns true when the write must be skipped.
        /// </summary>
        public async Task<bool> RetireIfOwnedAsync(
            DatabaseManager db,
            CatalogItem item,
            CancellationToken cancellationToken)
        {
            var owned = FindOwnedMedia(item);
            if (owned == null) return false;

            DeleteManagedItemTree(item.StrmPath, GetManagedRoots());
            await db.RetireCatalogItemForOwnedMediaAsync(
                item.Id,
                owned.Path,
                cancellationToken).ConfigureAwait(false);

            item.ItemState = ItemState.Retired;
            item.LocalSource = "library";
            item.LocalPath = owned.Path;
            item.StrmPath = null;
            item.SelectedVersionsJson = null;
            item.LastVersionRefreshAt = null;
            item.UpdatedAt = DateTime.UtcNow.ToString("o");
            // RetireCatalogItemForOwnedMediaAsync updates existing rows atomically.
            // This upsert also records a brand-new Discover item that was rejected
            // before it ever acquired a streamed path.
            await db.UpsertCatalogItemAsync(item, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "[InfiniteDrive] Owned media takes precedence for {Title} ({AioId}); streamed write skipped",
                item.Title,
                item.AioId);
            return true;
        }

        internal static List<KeyValuePair<string, string>> BuildProviderIds(CatalogItem item)
        {
            var ids = new List<KeyValuePair<string, string>>();

            void Add(string? provider, string? id)
            {
                if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id)) return;
                if (ids.Any(pair =>
                    string.Equals(pair.Key, provider, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pair.Value, id, StringComparison.OrdinalIgnoreCase))) return;
                ids.Add(new KeyValuePair<string, string>(provider, id));
            }

            if (!string.IsNullOrWhiteSpace(item.AioId))
            {
                if (item.AioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    Add("Imdb", item.AioId);
                }
                else
                {
                    var colon = item.AioId.IndexOf(':');
                    if (colon > 0 && colon < item.AioId.Length - 1)
                        Add(NormalizeProvider(item.AioId[..colon]), item.AioId[(colon + 1)..]);
                }
            }

            Add("Tmdb", item.TmdbId);
            Add("Tvdb", item.TvdbId);

            if (!string.IsNullOrWhiteSpace(item.UniqueIdsJson))
            {
                try
                {
                    using var document = JsonDocument.Parse(item.UniqueIdsJson);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var value in document.RootElement.EnumerateArray())
                        {
                            if (!value.TryGetProperty("provider", out var provider)
                                || !value.TryGetProperty("id", out var id)) continue;
                            Add(NormalizeProvider(provider.GetString()), id.GetString());
                        }
                    }
                }
                catch (JsonException)
                {
                    // The direct AIO/TMDB/TVDB fields still provide safe matches.
                }
            }

            return ids;
        }

        internal static bool IsPathWithinRoots(string path, IEnumerable<string> roots)
        {
            var fullPath = Path.GetFullPath(path);
            return roots.Any(root =>
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            });
        }

        internal static string? ResolveManagedItemDirectory(string? path, IEnumerable<string> roots)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var fullPath = Path.GetFullPath(path);

            foreach (var root in roots)
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                var prefix = fullRoot + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = fullPath[prefix.Length..];
                var firstSeparator = relative.IndexOf(Path.DirectorySeparatorChar);
                var firstComponent = firstSeparator < 0 ? relative : relative[..firstSeparator];
                if (string.IsNullOrWhiteSpace(firstComponent)) return null;
                return Path.Combine(fullRoot, firstComponent);
            }

            return null;
        }

        private void DeleteManagedItemTree(string? path, IReadOnlyCollection<string> roots)
        {
            var itemDirectory = ResolveManagedItemDirectory(path, roots);
            if (itemDirectory == null || !Directory.Exists(itemDirectory)) return;

            try
            {
                Directory.Delete(itemDirectory, recursive: true);
                _logger.LogInformation(
                    "[InfiniteDrive] Removed superseded managed stream tree: {Path}",
                    itemDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[InfiniteDrive] Could not remove superseded managed stream tree: {Path}",
                    itemDirectory);
            }
        }

        private static IReadOnlyCollection<string> GetManagedRoots()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return Array.Empty<string>();
            return new[] { config.SyncPathMovies, config.SyncPathShows, config.SyncPathAnime }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();
        }

        private static bool IsSeries(CatalogItem item) =>
            string.Equals(item.MediaType, "series", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeProvider(string? provider) =>
            provider?.ToLowerInvariant() switch
            {
                "imdb" => "Imdb",
                "tmdb" => "Tmdb",
                "tvdb" => "Tvdb",
                "mal" => "MAL",
                "anilist" => "AniList",
                "kitsu" => "Kitsu",
                _ => provider ?? string.Empty,
            };
    }
}
