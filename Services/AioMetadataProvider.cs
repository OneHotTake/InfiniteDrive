using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Shared helper for metadata resolution from our catalog database.
    /// </summary>
    internal static class AioMetadataHelper
    {
        internal static HashSet<string>? GetEnabledTypes(PluginConfiguration config)
        {
            var enabledRaw = config.MetadataEnabledIdTypes;
            var censusRaw = config.MetadataIdTypeCensus;

            if (string.IsNullOrWhiteSpace(censusRaw) || censusRaw == "{}")
                return null;

            if (string.IsNullOrWhiteSpace(enabledRaw) || enabledRaw == "[]")
            {
                var census = JsonSerializer.Deserialize<Dictionary<string, string>>(censusRaw);
                if (census == null || census.Count == 0) return null;
                var native = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IMDB", "TMDB", "TVDB" };
                return new HashSet<string>(
                    census.Keys.Where(k => !native.Contains(k)),
                    StringComparer.OrdinalIgnoreCase);
            }

            var enabled = JsonSerializer.Deserialize<List<string>>(enabledRaw);
            return enabled?.Count > 0
                ? new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase)
                : null;
        }

        internal static async Task<CatalogItem?> FindInDb(string providerKey, string providerValue)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return null;
            return await db.GetCatalogItemByProviderIdAsync(providerKey, providerValue).ConfigureAwait(false);
        }

        /// <summary>
        /// Multi-tier catalog lookup:
        ///   Tier 1: info.ProviderIds (Emby parsed from folder name [imdbid-XXX])
        ///   Tier 2: Parse directory name for [imdbid-XXX], [tmdbid-XXX], [tvdbid-XXX]
        ///   Tier 3: Read .strm file, extract &amp;id= from InfiniteDrive resolve URL
        ///   Tier 4: Title+year search in catalog DB (last resort)
        /// </summary>
        internal static async Task<(CatalogItem? item, string? matchedKey)> ResolveCatalogItemAsync(
            Dictionary<string, string> providerIds,
            string? itemPath,
            string? itemName,
            int? itemYear,
            HashSet<string> enabledTypes)
        {
            // Tier 1: Provider IDs already set by Emby
            foreach (var kvp in providerIds)
            {
                if (enabledTypes.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var found = await FindInDb(kvp.Key, kvp.Value).ConfigureAwait(false);
                    if (found != null) return (found, kvp.Key);
                }
            }

            // Tier 2: Parse directory name for ID hints
            if (!string.IsNullOrEmpty(itemPath))
            {
                var fromPath = await ResolveFromPathNameAsync(itemPath, enabledTypes).ConfigureAwait(false);
                if (fromPath != null) return (fromPath, "path");
            }

            // Tier 3: Read .strm file, extract &id= from InfiniteDrive URL
            if (!string.IsNullOrEmpty(itemPath))
            {
                var fromStrm = await ResolveFromStrmFileAsync(itemPath, enabledTypes).ConfigureAwait(false);
                if (fromStrm != null) return (fromStrm, "strm");
            }

            // Tier 4: Title+year search (last resort)
            if (!string.IsNullOrEmpty(itemName))
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db != null)
                {
                    var fromTitle = await db.GetCatalogItemByTitleAsync(itemName, itemYear, null).ConfigureAwait(false);
                    if (fromTitle != null) return (fromTitle, "title");
                }
            }

            return (null, null);
        }

        private static readonly Regex IdTagPattern = new(
            @"[[{](imdbid|tmdbid|tvdbid)[=-](.+?)[\]}}]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static async Task<CatalogItem?> ResolveFromPathNameAsync(string path, HashSet<string> enabledTypes)
        {
            var dirName = path;
            if (File.Exists(path))
                dirName = Path.GetDirectoryName(path) ?? path;

            var folderName = Path.GetFileName(dirName);
            if (string.IsNullOrEmpty(folderName)) return null;

            foreach (Match m in IdTagPattern.Matches(folderName))
            {
                var key = char.ToUpper(m.Groups[1].Value[0]) + m.Groups[1].Value[1..];
                var value = m.Groups[2].Value;

                if (!enabledTypes.Contains(key)) continue;

                var item = await FindInDb(key, value).ConfigureAwait(false);
                if (item != null) return item;
            }

            return null;
        }

        private static async Task<CatalogItem?> ResolveFromStrmFileAsync(string itemPath, HashSet<string> enabledTypes)
        {
            string? strmContent = null;

            if (itemPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && File.Exists(itemPath))
            {
                try { strmContent = await File.ReadAllTextAsync(itemPath).ConfigureAwait(false); }
                catch { return null; }
            }
            else if (Directory.Exists(itemPath))
            {
                try
                {
                    var strmFile = Directory.GetFiles(itemPath, "*.strm", SearchOption.AllDirectories).FirstOrDefault();
                    if (strmFile != null)
                        strmContent = await File.ReadAllTextAsync(strmFile).ConfigureAwait(false);
                }
                catch { return null; }
            }
            else if (File.Exists(itemPath))
            {
                var dir = Path.GetDirectoryName(itemPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    try
                    {
                        var strmFile = Directory.GetFiles(dir, "*.strm").FirstOrDefault();
                        if (strmFile != null)
                            strmContent = await File.ReadAllTextAsync(strmFile).ConfigureAwait(false);
                    }
                    catch { return null; }
                }
            }

            if (string.IsNullOrEmpty(strmContent)) return null;

            // Guard: only parse InfiniteDrive URLs
            if (!strmContent.Contains("/InfiniteDrive/resolve", StringComparison.OrdinalIgnoreCase))
                return null;

            var idMatch = Regex.Match(strmContent, @"[?&]id=([^&\s]+)");
            if (!idMatch.Success) return null;

            var idValue = Uri.UnescapeDataString(idMatch.Groups[1].Value);

            // IMDB: ttXXXXXX
            if (idValue.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                if (!enabledTypes.Contains("IMDB")) return null;
                return await FindInDb("IMDB", idValue).ConfigureAwait(false);
            }

            // Kitsu: kitsu:XXXXX
            if (idValue.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase))
            {
                var kitsuId = idValue["kitsu:".Length..];
                if (!enabledTypes.Contains("Kitsu")) return null;
                return await FindInDb("Kitsu", kitsuId).ConfigureAwait(false);
            }

            // Numeric: try as TMDB
            if (int.TryParse(idValue, out _))
            {
                if (!enabledTypes.Contains("TMDB")) return null;
                return await FindInDb("TMDB", idValue).ConfigureAwait(false);
            }

            return null;
        }

        internal static Dictionary<string, string> ParseUniqueIds(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new();
            try
            {
                var arr = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
                if (arr == null) return new();
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in arr)
                {
                    if (entry.TryGetValue("provider", out var provider) &&
                        entry.TryGetValue("id", out var id) &&
                        !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(id))
                    {
                        var key = char.ToUpper(provider[0]) + provider[1..];
                        dict[key] = id;
                    }
                }
                return dict;
            }
            catch { return new(); }
        }

        internal static void ApplyMetadata<T>(MetadataResult<T> result, AioMeta meta, CatalogItem catalogItem)
            where T : BaseItem, new()
        {
            var item = result.Item;

            if (!string.IsNullOrWhiteSpace(meta.Name))
                item.Name = meta.Name;

            if (!string.IsNullOrWhiteSpace(meta.Description))
                item.Overview = meta.Description;

            if (meta.Year.HasValue)
                item.ProductionYear = meta.Year.Value;

            if (!string.IsNullOrWhiteSpace(meta.ImdbRating) && float.TryParse(meta.ImdbRating, out var rating))
                item.CommunityRating = rating;

            if (meta.Genres?.Count > 0)
                item.Genres = meta.Genres.ToArray();

            if (!string.IsNullOrWhiteSpace(meta.ReleaseInfo) && !meta.Year.HasValue)
            {
                if (int.TryParse(meta.ReleaseInfo.Split('-')[0].Trim(), out var riYear))
                    item.ProductionYear = riYear;
            }

            if (!string.IsNullOrWhiteSpace(meta.Runtime) && int.TryParse(meta.Runtime, out var runtimeMin))
            {
                if (item is Movie movie)
                    movie.RunTimeTicks = TimeSpan.FromMinutes(runtimeMin).Ticks;
            }

            if (!string.IsNullOrWhiteSpace(meta.Country))
                item.Studios = new[] { meta.Country };

            if (meta.Cast?.Count > 0)
            {
                result.People = meta.Cast
                    .Select(name => new PersonInfo { Name = name, Type = PersonType.Actor })
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(meta.Director))
            {
                var people = result.People ?? new List<PersonInfo>();
                people.Add(new PersonInfo { Name = meta.Director, Type = PersonType.Director });
                result.People = people;
            }

            SetProviderIds(item, catalogItem);

            result.HasMetadata = true;
            result.Provider = "InfiniteDrive";
        }

        private static void SetProviderIds(BaseItem item, CatalogItem catalogItem)
        {
            if (!string.IsNullOrWhiteSpace(catalogItem.ImdbId))
                item.SetProviderId("IMDB", catalogItem.ImdbId);
            if (!string.IsNullOrWhiteSpace(catalogItem.TmdbId))
                item.SetProviderId("TMDB", catalogItem.TmdbId);
            if (!string.IsNullOrWhiteSpace(catalogItem.TvdbId))
                item.SetProviderId("TVDB", catalogItem.TvdbId);

            var ids = ParseUniqueIds(catalogItem.UniqueIdsJson);
            foreach (var kvp in ids)
                item.SetProviderId(kvp.Key, kvp.Value);
        }
    }

    /// <summary>Series metadata provider — auto-discovered by Emby.</summary>
    public class AioSeriesMetadataProvider : IRemoteMetadataProvider<Series, SeriesInfo>
    {
        public string Name => "InfiniteDrive";
        public int Order => 100;

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series> { Item = new Series() };
            var config = Plugin.Instance?.Configuration;
            if (config == null) return result;

            var enabledTypes = AioMetadataHelper.GetEnabledTypes(config);
            if (enabledTypes == null) return result;

            var (catalogItem, matchedKey) = await AioMetadataHelper.ResolveCatalogItemAsync(
                info.ProviderIds, info.Path, info.Name, info.Year, enabledTypes).ConfigureAwait(false);

            if (catalogItem == null || string.IsNullOrEmpty(catalogItem.RawMetaJson))
                return result;

            var logger = Plugin.Instance?.Logger;
            logger?.LogDebug("[InfiniteDrive] MetadataProvider: lookup by {Key} -> hit ({Title})",
                matchedKey, catalogItem.Title);

            var meta = JsonSerializer.Deserialize<AioMeta>(catalogItem.RawMetaJson);
            if (meta == null) return result;

            AioMetadataHelper.ApplyMetadata(result, meta, catalogItem);
            return result;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    /// <summary>Movie metadata provider — auto-discovered by Emby.</summary>
    public class AioMovieMetadataProvider : IRemoteMetadataProvider<Movie, MovieInfo>
    {
        public string Name => "InfiniteDrive";
        public int Order => 100;

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie> { Item = new Movie() };
            var config = Plugin.Instance?.Configuration;
            if (config == null) return result;

            var enabledTypes = AioMetadataHelper.GetEnabledTypes(config);
            if (enabledTypes == null) return result;

            var (catalogItem, matchedKey) = await AioMetadataHelper.ResolveCatalogItemAsync(
                info.ProviderIds, info.Path, info.Name, info.Year, enabledTypes).ConfigureAwait(false);

            if (catalogItem == null || string.IsNullOrEmpty(catalogItem.RawMetaJson))
                return result;

            var logger = Plugin.Instance?.Logger;
            logger?.LogDebug("[InfiniteDrive] MetadataProvider: lookup by {Key} -> hit ({Title})",
                matchedKey, catalogItem.Title);

            var meta = JsonSerializer.Deserialize<AioMeta>(catalogItem.RawMetaJson);
            if (meta == null) return result;

            AioMetadataHelper.ApplyMetadata(result, meta, catalogItem);
            return result;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
