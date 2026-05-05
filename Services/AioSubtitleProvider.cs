using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Emby subtitle provider backed by AIOStreams /subtitles/ endpoint.
    /// Reads pre-decorated subtitles from cache on Search, falls back to live API call.
    /// Downloads actual subtitle file on GetSubtitles via base64-encoded URL.
    /// </summary>
    public class AioSubtitleProvider : ISubtitleProvider
    {
        private readonly ILogger<AioSubtitleProvider> _logger;
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public AioSubtitleProvider(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<AioSubtitleProvider>(logManager.GetLogger("InfiniteDrive"));
        }

        public string Name => "InfiniteDrive";

        public IEnumerable<VideoContentType> SupportedMediaTypes =>
            new[] { VideoContentType.Movie, VideoContentType.Episode };

        /// <summary>
        /// Searches for subtitles. Reads from cache first, falls back to live AIOStreams call.
        /// </summary>
        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            var (aioId, mediaType, season, episode) = ResolveFromRequest(request);
            if (string.IsNullOrEmpty(aioId))
            {
                _logger.LogDebug("[Subtitles] Could not resolve AIO ID from request");
                return Array.Empty<RemoteSubtitleInfo>();
            }

            // 1. Read from cache
            var subs = await ReadCachedSubtitlesAsync(aioId, season, episode).ConfigureAwait(false);

            // 2. Live fallback
            if (subs == null || subs.Count == 0)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return Array.Empty<RemoteSubtitleInfo>();

                var providers = ProviderHelper.GetProviders(config);
                if (providers.Count == 0) return Array.Empty<RemoteSubtitleInfo>();

                try
                {
                    subs = await AioStreamsClient.FetchSubtitlesAsync(
                        providers, aioId, mediaType, season, episode,
                        _logger, Plugin.Instance?.ResolverHealthTracker, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Subtitles] Live fetch failed for {AioId}", aioId);
                }
            }

            if (subs == null || subs.Count == 0)
                return Array.Empty<RemoteSubtitleInfo>();

            // Filter by language if specified
            var filtered = subs;
            if (!string.IsNullOrEmpty(request.Language))
            {
                var lang = request.Language.ToLowerInvariant();
                filtered = subs.Where(s =>
                {
                    var sLang = (s.Lang ?? s.LangCode ?? "").ToLowerInvariant();
                    return sLang == lang || sLang.StartsWith(lang + "-") || sLang.StartsWith(lang + "_");
                }).ToList();

                // If no matches after filtering, return all (better than nothing)
                if (filtered.Count == 0) filtered = subs;
            }

            return filtered.Select(MapToRemoteSubtitleInfo);
        }

        /// <summary>
        /// Downloads a subtitle file by decoding the base64-encoded URL from the subtitle ID.
        /// </summary>
        public async Task<SubtitleResponse> GetSubtitles(
            string id, CancellationToken cancellationToken)
        {
            string url;
            try
            {
                var bytes = Convert.FromBase64String(id);
                url = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                _logger.LogWarning("[Subtitles] Invalid subtitle id format");
                return new SubtitleResponse { Format = "srt" };
            }

            try
            {
                var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var format = InferFormat(url);

                return new SubtitleResponse
                {
                    Format = format,
                    Language = "und",
                    Stream = stream,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Subtitles] Download failed for subtitle URL");
                return new SubtitleResponse { Format = "srt" };
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private (string? aioId, string mediaType, int? season, int? episode) ResolveFromRequest(
            SubtitleSearchRequest request)
        {
            string? aioId = null;
            var mediaType = "movie";
            int? season = null;
            int? episode = null;

            // ProviderIds — same logic as AioMediaSourceProvider.IdentifyItem
            if (request.ProviderIds != null)
            {
                if (request.ProviderIds.TryGetValue("imdb", out var imdb))
                    aioId = imdb;
                if (string.IsNullOrEmpty(aioId) && request.ProviderIds.TryGetValue("AIO", out var aio))
                    aioId = aio;
                if (string.IsNullOrEmpty(aioId) && request.ProviderIds.TryGetValue("INFINITEDRIVE", out var inf))
                    aioId = inf;

                if (string.IsNullOrEmpty(aioId))
                {
                    foreach (var kvp in request.ProviderIds)
                    {
                        if (string.Equals(kvp.Key, "Kitsu", StringComparison.OrdinalIgnoreCase))
                            aioId = $"kitsu:{kvp.Value}";
                        else if (string.Equals(kvp.Key, "AniList", StringComparison.OrdinalIgnoreCase))
                            aioId = $"anilist:{kvp.Value}";
                        else if (string.Equals(kvp.Key, "MAL", StringComparison.OrdinalIgnoreCase))
                            aioId = $"mal:{kvp.Value}";
                        if (!string.IsNullOrEmpty(aioId)) break;
                    }
                }
            }

            if (string.IsNullOrEmpty(aioId)) return (null, mediaType, null, null);

            if (request.ContentType == VideoContentType.Episode)
            {
                mediaType = "series";
                season = request.ParentIndexNumber;
                episode = request.IndexNumber;
            }

            return (aioId, mediaType, season, episode);
        }

        private async Task<List<AioStreamsSubtitle>?> ReadCachedSubtitlesAsync(
            string aioId, int? season, int? episode)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return null;

            try
            {
                var json = await db.GetCachedSubtitlesAsync(aioId, season, episode).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json)) return null;
                return JsonSerializer.Deserialize<List<AioStreamsSubtitle>>(json);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Subtitles] Cache read failed for {AioId}", aioId);
                return null;
            }
        }

        private static RemoteSubtitleInfo MapToRemoteSubtitleInfo(AioStreamsSubtitle sub)
        {
            // ID = base64-encoded URL for download in GetSubtitles
            var id = !string.IsNullOrEmpty(sub.Url)
                ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sub.Url))
                : sub.Id ?? "";

            var lang = sub.Lang ?? sub.LangCode ?? "und";
            var title = sub.Title ?? lang;
            var format = InferFormat(sub.Url ?? "");

            return new RemoteSubtitleInfo
            {
                Id = id,
                ThreeLetterISOLanguageName = lang,
                ProviderName = "InfiniteDrive",
                Name = title,
                Format = format,
                IsForced = false,
                Comment = sub.AiTranslated == true ? "AI-translated" : null,
            };
        }

        private static string InferFormat(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "srt";
            var lower = url.ToLowerInvariant();
            if (lower.Contains(".vtt")) return "vtt";
            if (lower.Contains(".ass") || lower.Contains(".ssa")) return "ass";
            return "srt";
        }
    }
}
