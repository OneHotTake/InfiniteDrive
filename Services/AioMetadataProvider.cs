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
            @"[[{](imdbid|tmdbid|tvdbid|kitsu|anilist|mal|anidb|simkl)[=-](.+?)[\]}}]",
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

            // Prefixed schemes: kitsu:XXXXX, mal:XXXXX, anilist:XXXXX, tmdb:XXXXX, tvdb:XXXXX, anidb:XXXXX, simkl:XXXXX
            if (idValue.Contains(':'))
            {
                var sep = idValue.IndexOf(':');
                var provider = idValue[..sep];
                var providerId = idValue[(sep + 1)..];
                var normalizedKey = char.ToUpper(provider[0]) + provider[1..].ToLowerInvariant();
                if (!enabledTypes.Contains(normalizedKey)) return null;
                return await FindInDb(normalizedKey, providerId).ConfigureAwait(false);
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

            // Always lock the Name field to prevent Emby's ffprobe / media-info extraction
            // from overwriting it with MKV-embedded metadata (raw torrent filenames).
            var locked = item.LockedFields?.ToList() ?? new List<MetadataFields>();
            if (!locked.Contains(MetadataFields.Name))
            {
                locked.Add(MetadataFields.Name);
                item.LockedFields = locked.ToArray();
            }

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

            if (!string.IsNullOrWhiteSpace(meta.Runtime))
            {
                int totalMinutes = ParseRuntimeMinutes(meta.Runtime);
                if (totalMinutes > 0 && item is Movie rMovie)
                    rMovie.RunTimeTicks = TimeSpan.FromMinutes(totalMinutes).Ticks;
                else if (totalMinutes > 0 && item is Series rSeries)
                    rSeries.RunTimeTicks = TimeSpan.FromMinutes(totalMinutes).Ticks;
            }

            if (!string.IsNullOrWhiteSpace(meta.Country))
                item.Studios = new[] { meta.Country };

            if (meta.AppExtras?.Cast?.Count > 0)
            {
                result.People = meta.AppExtras.Cast
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => new PersonInfo
                    {
                        Name = c.Name!,
                        Role = c.Character,
                        ImageUrl = c.Photo,
                        Type = PersonType.Actor
                    })
                    .ToList();
            }
            else if (meta.Cast?.Count > 0)
            {
                result.People = meta.Cast
                    .Select(name => new PersonInfo { Name = name, Type = PersonType.Actor })
                    .ToList();
            }

            if (meta.Directors?.Count > 0)
            {
                var people = result.People ?? new List<PersonInfo>();
                foreach (var d in meta.Directors)
                    if (!string.IsNullOrWhiteSpace(d))
                        people.Add(new PersonInfo { Name = d, Type = PersonType.Director });
                result.People = people;
            }

            // Populate IMDB ID from meta direct field or behaviorHints if not in catalogItem
            var imdbFromMeta = meta.ImdbId
                ?? meta.BehaviorHints?.DefaultVideoId;
            if (!string.IsNullOrWhiteSpace(imdbFromMeta) && imdbFromMeta.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                item.SetProviderId("IMDB", imdbFromMeta);

            if (!string.IsNullOrWhiteSpace(meta.Released)
                && DateTimeOffset.TryParse(meta.Released, null, System.Globalization.DateTimeStyles.RoundtripKind, out var releaseDate))
            {
                item.PremiereDate = releaseDate.UtcDateTime;
            }

            if (meta.Links?.Count > 0)
            {
                var knownCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "imdb", "share", "Genres", "Cast", "Directors" };
                var collectionName = meta.Links
                    .Where(l => !string.IsNullOrWhiteSpace(l.Category) && !knownCategories.Contains(l.Category))
                    .Select(l => l.Category!)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(collectionName))
                    item.AddTag(collectionName);
            }

            SetProviderIds(item, catalogItem);

            result.HasMetadata = true;
            result.Provider = "InfiniteDrive";
        }

        internal static void AddSearchResult(List<RemoteSearchResult> results, CatalogItem item)
        {
            AioMeta? meta = null;
            if (!string.IsNullOrEmpty(item.RawMetaJson))
                try { meta = System.Text.Json.JsonSerializer.Deserialize<AioMeta>(item.RawMetaJson); } catch { }

            var r = new RemoteSearchResult
            {
                Name = item.Title ?? meta?.Name ?? string.Empty,
                ProductionYear = meta?.Year,
                Overview = meta?.Description,
                ImageUrl = meta?.Poster,
                SearchProviderName = "InfiniteDrive",
            };

            var isAnime = string.Equals(item.CatalogType, "anime", StringComparison.OrdinalIgnoreCase)
                || IsAnimePrefixedId(item.AioId);

            // IMDB: only when the primary ID is a tt-prefixed IMDB ID
            if (!string.IsNullOrWhiteSpace(item.AioId) && item.AioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                r.SetProviderId("IMDB", item.AioId);

            // TMDB: skip for anime — stored TMDB IDs are often movie IDs on series items,
            // causing Emby's TMDB plugin to query the wrong endpoint ("Failed to parse meta")
            if (!isAnime && !string.IsNullOrWhiteSpace(item.TmdbId))
                r.SetProviderId("TMDB", item.TmdbId);

            // TVDB: preferred for anime series; Emby's TVDB plugin handles anime well
            if (!string.IsNullOrWhiteSpace(item.TvdbId))
                r.SetProviderId("TVDB", item.TvdbId);

            results.Add(r);
        }

        private static bool IsAnimePrefixedId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var c = char.ToLowerInvariant(id[0]);
            return c switch
            {
                'k' => id.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase),
                'a' => id.StartsWith("anilist:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("anidb:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("anidb_id:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("anidbid:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("animeplanet:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("ap:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("anisearch:", StringComparison.OrdinalIgnoreCase),
                'm' => id.StartsWith("mal:", StringComparison.OrdinalIgnoreCase),
                'n' => id.StartsWith("notifymoe:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("nm:", StringComparison.OrdinalIgnoreCase),
                's' => id.StartsWith("simkl:", StringComparison.OrdinalIgnoreCase),
                'l' => id.StartsWith("livechart:", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private static int ParseRuntimeMinutes(string runtime)
        {
            var s = runtime.Trim();
            // "Xh Ymin" or "XhYmin"
            var hm = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*h\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (hm.Success)
                return int.Parse(hm.Groups[1].Value) * 60 + int.Parse(hm.Groups[2].Value);
            // "Xh" only
            var ho = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*h", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (ho.Success)
                return int.Parse(ho.Groups[1].Value) * 60;
            // "X min"
            var mo = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*min", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mo.Success)
                return int.Parse(mo.Groups[1].Value);
            // plain integer
            if (int.TryParse(s, out var plain)) return plain;
            return 0;
        }

        private static void SetProviderIds(BaseItem item, CatalogItem catalogItem)
        {
            if (!string.IsNullOrWhiteSpace(catalogItem.AioId) && catalogItem.AioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                item.SetProviderId("IMDB", catalogItem.AioId);
            if (!string.IsNullOrWhiteSpace(catalogItem.TmdbId))
                item.SetProviderId("TMDB", catalogItem.TmdbId);
            if (!string.IsNullOrWhiteSpace(catalogItem.TvdbId))
                item.SetProviderId("TVDB", catalogItem.TvdbId);

            var ids = ParseUniqueIds(catalogItem.UniqueIdsJson);
            foreach (var kvp in ids)
                item.SetProviderId(kvp.Key, kvp.Value);

            // Mark this item as belonging to InfiniteDrive so Emby uses GetMediaSources + OpenMediaSource
            // instead of treating it as a regular media file and probing the .strm URL directly
            item.SetProviderId("INFINITEDRIVE", "1");
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

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return Enumerable.Empty<RemoteSearchResult>();

            var results = new List<RemoteSearchResult>();

            foreach (var kvp in searchInfo.ProviderIds)
            {
                var item = await db.GetCatalogItemByProviderIdAsync(kvp.Key, kvp.Value).ConfigureAwait(false);
                if (item != null) { AioMetadataHelper.AddSearchResult(results, item); break; }
            }

            if (results.Count == 0 && !string.IsNullOrWhiteSpace(searchInfo.Name))
            {
                var item = await db.GetCatalogItemByTitleAsync(searchInfo.Name, searchInfo.Year, null).ConfigureAwait(false);
                if (item != null) AioMetadataHelper.AddSearchResult(results, item);
            }

            return results;
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
            => Task.FromResult<HttpResponseInfo>(null!);
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

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return Enumerable.Empty<RemoteSearchResult>();

            var results = new List<RemoteSearchResult>();

            foreach (var kvp in searchInfo.ProviderIds)
            {
                var item = await db.GetCatalogItemByProviderIdAsync(kvp.Key, kvp.Value).ConfigureAwait(false);
                if (item != null) { AioMetadataHelper.AddSearchResult(results, item); break; }
            }

            if (results.Count == 0 && !string.IsNullOrWhiteSpace(searchInfo.Name))
            {
                var item = await db.GetCatalogItemByTitleAsync(searchInfo.Name, searchInfo.Year, null).ConfigureAwait(false);
                if (item != null) AioMetadataHelper.AddSearchResult(results, item);
            }

            return results;
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
            => Task.FromResult<HttpResponseInfo>(null!);
    }
}
