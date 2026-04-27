using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Fallback image provider from AIOStreams metadata.
    /// If an item has a TMDB ID, Emby's native TMDB provider handles images — this provider skips entirely.
    /// Only serves items with ProviderIds["INFINITEDRIVE"] that lack TMDB IDs.
    /// </summary>
    public class AioImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClient _httpClient;
        private readonly ILogger<AioImageProvider> _logger;

        public AioImageProvider(IHttpClient httpClient, ILogManager logManager)
        {
            _httpClient = httpClient;
            _logger = new EmbyLoggerAdapter<AioImageProvider>(logManager.GetLogger("InfiniteDrive"));
            _logger.LogInformation("[AioImageProvider] Constructed — fallback provider, skips items with TMDB ID");
        }

        public string Name => "InfiniteDrive";

        public bool Supports(BaseItem item) =>
            item is Movie || item is Series || item is Season || item is Episode;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) =>
            new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb, ImageType.Banner };

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            // Pure fallback: skip entirely if TMDB ID present — Emby's native TMDB provider handles these
            if (item.ProviderIds != null &&
                item.ProviderIds.TryGetValue("Tmdb", out var tmdbCheck) &&
                !string.IsNullOrEmpty(tmdbCheck))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var list = new List<RemoteImageInfo>();

            _logger.LogDebug("[AioImageProvider] GetImages for {Name} (Type={Type})", item.Name, item.GetType().Name);

            try
            {

                var meta = await ResolveAioMeta(item, cancellationToken).ConfigureAwait(false);
                if (meta == null)
                {
                    _logger.LogDebug("[AioImageProvider] No AioMeta found for {Name}", item.Name);
                    return list;
                }

                // Populate images from AioMeta
                if (!string.IsNullOrEmpty(meta.Poster))
                {
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Type = ImageType.Primary,
                        Url = meta.Poster,
                        CommunityRating = ParseRating(meta.ImdbRating)
                    });
                }

                if (!string.IsNullOrEmpty(meta.Background))
                {
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Type = ImageType.Backdrop,
                        Url = meta.Background
                    });
                }

                if (!string.IsNullOrEmpty(meta.Logo))
                {
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Type = ImageType.Logo,
                        Url = meta.Logo
                    });
                }

                // Thumb and Banner: use poster as thumb for seasons/episodes, backdrop as banner for series
                if (item is Season || item is Episode)
                {
                    if (!string.IsNullOrEmpty(meta.Poster))
                    {
                        list.Add(new RemoteImageInfo
                        {
                            ProviderName = Name,
                            Type = ImageType.Thumb,
                            Url = meta.Poster
                        });
                    }
                }

                if (item is Series)
                {
                    if (!string.IsNullOrEmpty(meta.Background))
                    {
                        list.Add(new RemoteImageInfo
                        {
                            ProviderName = Name,
                            Type = ImageType.Banner,
                            Url = meta.Background
                        });
                    }
                }

                _logger.LogDebug("[AioImageProvider] Returning {Count} images for {Name}", list.Count, item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AioImageProvider] Failed for {Name}", item.Name);
            }

            return list;
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetResponse(new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url
            });
        }

        private async Task<AioMeta?> ResolveAioMeta(BaseItem item, CancellationToken ct)
        {
            // Check if this is one of our items (INFINITEDRIVE provider ID)
            if (item.ProviderIds == null || !item.ProviderIds.ContainsKey("INFINITEDRIVE"))
                return null;

            // For Season/Episode items, try walking up to parent Series for images
            var targetItem = item;
            if (item is Season season && season.Series != null)
                targetItem = season.Series;
            else if (item is Episode episode && episode.Series != null)
                targetItem = episode.Series;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[AioImageProvider] No DatabaseManager available");
                return null;
            }

            // Just fetch the raw_meta_json directly — no need to map 33 columns
            string? rawMetaJson = null;

            foreach (var kvp in targetItem.ProviderIds)
            {
                if (kvp.Key == "INFINITEDRIVE") continue;
                rawMetaJson = await db.GetRawMetaJsonByProviderIdAsync(kvp.Key, kvp.Value).ConfigureAwait(false);
                if (rawMetaJson != null) break;
            }

            // Fallback: try the INFINITEDRIVE id directly
            if (rawMetaJson == null)
            {
                var infId = targetItem.ProviderIds["INFINITEDRIVE"];
                rawMetaJson = await db.GetRawMetaJsonByProviderIdAsync("imdb", infId).ConfigureAwait(false);
                if (rawMetaJson == null && infId.StartsWith("kitsu:"))
                    rawMetaJson = await db.GetRawMetaJsonByProviderIdAsync("kitsu", infId.Substring(6)).ConfigureAwait(false);
            }

            if (rawMetaJson == null)
            {
                // Title-based fallback: query catalog by item name
                try
                {
                    var catalogItem = await db.GetCatalogItemByTitleAsync(
                        targetItem.Name, targetItem.ProductionYear, null).ConfigureAwait(false);
                    if (catalogItem?.RawMetaJson != null)
                    {
                        rawMetaJson = catalogItem.RawMetaJson;
                        _logger.LogDebug("[AioImageProvider] Found meta via title lookup for {Name}", targetItem.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AioImageProvider] Title fallback failed for {Name}", targetItem.Name);
                }
            }

            if (rawMetaJson == null)
            {
                // Live fetch fallback: try AIOStreams /meta/ endpoint
                var liveMeta = await FetchLiveMetaAsync(targetItem, ct).ConfigureAwait(false);
                if (liveMeta != null)
                {
                    _logger.LogDebug("[AioImageProvider] Live meta fetch succeeded for {Name}", targetItem.Name);
                    return liveMeta;
                }

                _logger.LogDebug("[AioImageProvider] No meta found for {Name}", targetItem.Name);
                return null;
            }

            _logger.LogDebug("[AioImageProvider] Found meta for {Name}, length={Len}", targetItem.Name, rawMetaJson.Length);

            try
            {
                return JsonSerializer.Deserialize<AioMeta>(rawMetaJson);
            }
            catch
            {
                return null;
            }
        }

        private async Task<AioMeta?> FetchLiveMetaAsync(BaseItem item, CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            var (type, id) = ResolveMetaQuery(item);
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var (url, uuid, token, name) in GetConfiguredProviders(config))
            {
                try
                {
                    using var client = new AioStreamsClient(url, uuid, token, _logger);
                    var response = await client.GetMetaAsyncTyped(type, id, ct).ConfigureAwait(false);
                    if (response?.Meta != null)
                    {
                        _logger.LogDebug("[AioImageProvider] Live meta from {Provider} for {Name}", name, item.Name);
                        await CacheMetaToDbAsync(response.Meta, item, ct).ConfigureAwait(false);
                        return response.Meta;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AioImageProvider] Live meta failed from {Provider}", name);
                }
            }

            return null;
        }

        private static (string type, string? id) ResolveMetaQuery(BaseItem item)
        {
            var ids = item.ProviderIds;
            if (ids == null) return ("", null);

            var type = item is Movie ? "movie" : "series";

            if (ids.TryGetValue("imdb", out var imdbId) && !string.IsNullOrEmpty(imdbId))
                return (type, imdbId);
            if (ids.TryGetValue("Kitsu", out var kitsuId) && !string.IsNullOrEmpty(kitsuId))
                return ("series", $"kitsu:{kitsuId}");
            if (ids.TryGetValue("MAL", out var malId) && !string.IsNullOrEmpty(malId))
                return ("series", $"mal:{malId}");
            if (ids.TryGetValue("AniList", out var anilistId) && !string.IsNullOrEmpty(anilistId))
                return ("series", $"anilist:{anilistId}");

            return (type, null);
        }

        private async Task CacheMetaToDbAsync(AioMeta meta, BaseItem item, CancellationToken ct)
        {
            try
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                var catalogItem = new CatalogItem
                {
                    ImdbId = meta.ImdbId ?? meta.Id ?? "",
                    Title = meta.Name ?? item.Name,
                    MediaType = meta.Type == AioMetaType.Movie ? "movie" : "series",
                    Source = "image_live_fetch",
                    RawMetaJson = JsonSerializer.Serialize(meta),
                };

                if (meta.Year.HasValue) catalogItem.Year = meta.Year;
                if (!string.IsNullOrEmpty(meta.TmdbId)) catalogItem.TmdbId = meta.TmdbId;

                await db.UpsertCatalogItemAsync(catalogItem, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AioImageProvider] Cache write failed for {Name}", item.Name);
            }
        }

        private static List<(string url, string uuid, string token, string name)> GetConfiguredProviders(PluginConfiguration config)
        {
            var list = new List<(string, string, string, string)>();

            if (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                    list.Add((url, uuid ?? "", token ?? "", "Primary"));
            }

            if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.SecondaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                    list.Add((url, uuid ?? "", token ?? "", "Secondary"));
            }

            return list;
        }

        private static float? ParseRating(string? rating)
        {
            if (string.IsNullOrEmpty(rating)) return null;
            return float.TryParse(rating, out var r) ? (float?)(r / 2f) : null; // Normalize 10-scale to 5-scale
        }
    }
}
