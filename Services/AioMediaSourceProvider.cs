using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Populates Emby's version picker with live AIOStreams streams.
    /// Called when user browses/long-presses a media item.
    /// ONE .strm per item handles default play; this handles the version picker.
    /// </summary>
    public class AioMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<AioMediaSourceProvider> _logger;
        private readonly IMediaSourceManager _mediaSourceManager;

        // In-memory cache: key = "imdbId" or "imdbId:S{season}E{episode}", value = (sources, expiry)
        private static readonly ConcurrentDictionary<string, (List<MediaSourceInfo> Sources, DateTime Expires)> _cache
            = new();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

        public AioMediaSourceProvider(
            ILogManager logManager,
            IMediaSourceManager mediaSourceManager)
        {
            _logger = new EmbyLoggerAdapter<AioMediaSourceProvider>(logManager.GetLogger("InfiniteDrive"));
            _mediaSourceManager = mediaSourceManager;
        }

        public Task<List<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(GetMediaSourcesCore(item));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AioMediaSourceProvider] Error getting sources for {Item}", item?.Name);
                return Task.FromResult(new List<MediaSourceInfo>());
            }
        }

        public Task<ILiveStream> OpenMediaSource(
            string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("AioMediaSourceProvider does not open live streams");
        }

        private List<MediaSourceInfo> GetMediaSourcesCore(BaseItem item)
        {
            if (item == null) return new List<MediaSourceInfo>();

            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.PluginSecret))
                return new List<MediaSourceInfo>();

            // Only handle items in configured media paths
            if (!IsInConfiguredPath(item, config))
                return new List<MediaSourceInfo>();

            // Identify item
            var (imdbId, mediaType, season, episode) = IdentifyItem(item);
            if (string.IsNullOrEmpty(imdbId))
            {
                _logger.LogDebug("[AioMediaSourceProvider] No IMDB ID for {Name}", item.Name);
                return new List<MediaSourceInfo>();
            }

            // In-memory cache check
            var cacheKey = season.HasValue
                ? $"{imdbId}:S{season}E{episode}"
                : imdbId;

            if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
            {
                _logger.LogDebug("[AioMediaSourceProvider] Cache hit for {Key}", cacheKey);
                return cached.Sources;
            }

            // DB cache check
            var db = Plugin.Instance?.DatabaseManager;
            if (db != null)
            {
                try
                {
                    var dbCandidates = db.GetStreamCandidatesAsync(imdbId, season, episode)
                        .GetAwaiter().GetResult();

                    if (dbCandidates?.Count > 0)
                    {
                        // Build sources from cached candidates
                        var sources = dbCandidates
                            .Where(c => c.Status == "valid" && !string.IsNullOrEmpty(c.Url))
                            .Select(MapCandidateToSource)
                            .Where(s => s != null)
                            .Cast<MediaSourceInfo>()
                            .ToList();

                        if (sources.Count > 0)
                        {
                            SortByLanguagePreference(sources, config);
                            _cache[cacheKey] = (sources, DateTime.UtcNow.Add(CacheTtl));
                            return sources;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AioMediaSourceProvider] DB cache lookup failed for {Key}", cacheKey);
                }
            }

            // Live resolve: fetch all streams from AIOStreams (no SEL filter)
            var liveSources = ResolveFromAioStreams(imdbId, mediaType, season, episode, config);
            if (liveSources.Count > 0)
            {
                SortByLanguagePreference(liveSources, config);
                _cache[cacheKey] = (liveSources, DateTime.UtcNow.Add(CacheTtl));
                CacheToDb(db, imdbId, season, episode, liveSources);

                // Binge prefetch for series
                if (season.HasValue && episode.HasValue)
                {
                    _ = BingePrefetchService.PrefetchNextEpisodeAsync(
                        imdbId, season.Value, episode.Value, _logger);
                }
            }

            return liveSources;
        }

        private bool IsInConfiguredPath(BaseItem item, PluginConfiguration config)
        {
            var path = item.Path;
            if (string.IsNullOrEmpty(path)) return false;

            var paths = new[] { config.SyncPathMovies, config.SyncPathShows, config.SyncPathAnime };
            return paths.Any(p => !string.IsNullOrEmpty(p) && path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        private (string? imdbId, string mediaType, int? season, int? episode) IdentifyItem(BaseItem item)
        {
            string? imdbId = null;
            var mediaType = "movie";
            int? season = null;
            int? episode = null;

            // 1. ProviderIds["imdb"] — Emby native
            if (item.ProviderIds != null && item.ProviderIds.TryGetValue("imdb", out var imdb))
                imdbId = imdb;

            // 2. Parse from .strm path
            if (string.IsNullOrEmpty(imdbId) && !string.IsNullOrEmpty(item.Path))
            {
                imdbId = ParseImdbFromPath(item.Path);
            }

            // 3. ProviderIds["AIO"] (last resort)
            if (string.IsNullOrEmpty(imdbId) && item.ProviderIds != null && item.ProviderIds.TryGetValue("AIO", out var aioId))
                imdbId = aioId;

            if (string.IsNullOrEmpty(imdbId)) return (null, mediaType, null, null);

            // Detect series
            if (item is MediaBrowser.Controller.Entities.TV.Episode ep)
            {
                mediaType = "series";
                season = ep.ParentIndexNumber ?? 0;
                episode = ep.IndexNumber ?? 0;
            }
            else if (!string.IsNullOrEmpty(item.Path) && item.Path.Contains("Season ", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = "series";
                (season, episode) = ParseSeasonEpisodeFromPath(item.Path);
            }
            else if (item is MediaBrowser.Controller.Entities.TV.Series)
            {
                mediaType = "series";
            }

            return (imdbId, mediaType, season, episode);
        }

        private static readonly Regex ImdbRegex = new(@"tt\d{7,8}", RegexOptions.Compiled);
        private static readonly Regex SeasonEpisodeRegex = new(@"S(\d{1,2})E(\d{1,2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string? ParseImdbFromPath(string path)
        {
            var match = ImdbRegex.Match(path);
            return match.Success ? match.Value : null;
        }

        private static (int? season, int? episode) ParseSeasonEpisodeFromPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return (null, null);
            var match = SeasonEpisodeRegex.Match(path);
            if (!match.Success) return (null, null);
            return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }

        private List<MediaSourceInfo> ResolveFromAioStreams(
            string imdbId, string mediaType, int? season, int? episode, PluginConfiguration config)
        {
            var providers = GetProviders(config);
            if (providers.Count == 0) return new List<MediaSourceInfo>();

            var healthTracker = Plugin.Instance?.ResolverHealthTracker;

            foreach (var provider in providers)
            {
                if (healthTracker != null && healthTracker.ShouldSkip(provider.DisplayName))
                {
                    _logger.LogDebug("[AioMediaSourceProvider] Skipping {Name} — circuit open", provider.DisplayName);
                    continue;
                }

                try
                {
                    using var client = new AioStreamsClient(
                        provider.Url, provider.Uuid, provider.Token, _logger);

                    AioStreamsStreamResponse? response;

                    if (mediaType == "series" && season.HasValue && episode.HasValue)
                        response = client.GetSeriesStreamsAsync(imdbId, season.Value, episode.Value).GetAwaiter().GetResult();
                    else
                        response = client.GetMovieStreamsAsync(imdbId).GetAwaiter().GetResult();

                    var streams = response?.Streams;
                    if (streams == null || streams.Count == 0)
                    {
                        _logger.LogDebug("[AioMediaSourceProvider] No streams from {Name} for {Id}", provider.DisplayName, imdbId);
                        continue;
                    }

                    if (healthTracker != null) healthTracker.RecordSuccess(provider.DisplayName);

                    _logger.LogInformation(
                        "[AioMediaSourceProvider] Got {Count} streams for {Id} from {Provider}",
                        streams.Count, imdbId, provider.DisplayName);

                    return streams
                        .Where(s => !string.IsNullOrEmpty(s.Url))
                        .Select(MapStreamToSource)
                        .Where(s => s != null)
                        .Cast<MediaSourceInfo>()
                        .ToList();
                }
                catch (Exception ex)
                {
                    if (healthTracker != null) healthTracker.RecordFailure(provider.DisplayName);
                    _logger.LogError(ex, "[AioMediaSourceProvider] {Name} failed for {Id}", provider.DisplayName, imdbId);
                }
            }

            return new List<MediaSourceInfo>();
        }

        private static MediaSourceInfo? MapStreamToSource(AioStreamsStream stream)
        {
            if (string.IsNullOrEmpty(stream.Url)) return null;

            var name = stream.Name ?? stream.Title ?? "Stream";

            var source = new MediaSourceInfo
            {
                Id = stream.Id ?? Guid.NewGuid().ToString("N"),
                Name = name,
                Path = stream.Url,
                Protocol = MediaProtocol.Http,
                SupportsDirectStream = true,
                SupportsTranscoding = false,
                IsInfiniteStream = false,
            };

            if (stream.Bitrate.HasValue && stream.Bitrate.Value > 0)
                source.Bitrate = (int)(stream.Bitrate.Value / 1000); // Convert bps to kbps

            if (stream.Size.HasValue && stream.Size.Value > 0)
                source.Size = stream.Size.Value;

            // Container from filename
            if (stream.BehaviorHints?.Filename != null)
            {
                var ext = Path.GetExtension(stream.BehaviorHints.Filename)?.TrimStart('.');
                if (!string.IsNullOrEmpty(ext))
                    source.Container = ext;
            }

            if (stream.Headers != null && stream.Headers.Count > 0)
                source.RequiredHttpHeaders = new Dictionary<string, string>(stream.Headers);

            // Build MediaStreams from parsed language/subtitle data
            source.MediaStreams = BuildMediaStreams(stream);

            return source;
        }

        private static MediaSourceInfo? MapCandidateToSource(StreamCandidate candidate)
        {
            if (string.IsNullOrEmpty(candidate.Url)) return null;

            var source = new MediaSourceInfo
            {
                Id = candidate.Url.GetHashCode(StringComparison.Ordinal).ToString("x"),
                Name = $"[{candidate.ProviderKey}] {candidate.QualityTier}",
                Path = candidate.Url,
                Protocol = MediaProtocol.Http,
                SupportsDirectStream = true,
                SupportsTranscoding = false,
                IsInfiniteStream = false,
            };

            // Build audio MediaStreams from stored languages
            source.MediaStreams = BuildMediaStreamsFromLanguages(candidate.Languages);

            return source;
        }

        private static List<MediaStream> BuildMediaStreams(AioStreamsStream stream)
        {
            var streams = new List<MediaStream>();

            // Audio streams from parsed languages
            if (stream.ParsedFile?.Languages?.Count > 0)
            {
                for (int i = 0; i < stream.ParsedFile.Languages.Count; i++)
                {
                    var lang = stream.ParsedFile.Languages[i];
                    var channels = stream.ParsedFile.Channels;
                    var audioTags = stream.ParsedFile.AudioTags;

                    var title = lang;
                    if (!string.IsNullOrEmpty(channels))
                        title += $" - {channels}";
                    if (audioTags?.Count > 0)
                        title += $" {string.Join(" ", audioTags)}";

                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Language = lang,
                        Title = title.Trim(),
                        IsDefault = i == 0,
                        Index = streams.Count,
                    });
                }
            }

            // Subtitle streams
            if (stream.Subtitles?.Count > 0)
            {
                foreach (var sub in stream.Subtitles)
                {
                    if (string.IsNullOrEmpty(sub.Url)) continue;

                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Language = sub.Lang ?? "und",
                        IsExternal = true,
                        DeliveryUrl = sub.Url,
                        Path = sub.Url,
                        IsDefault = false,
                        Index = streams.Count,
                    });
                }
            }

            return streams;
        }

        private static List<MediaStream> BuildMediaStreamsFromLanguages(string? languages)
        {
            var streams = new List<MediaStream>();
            if (string.IsNullOrEmpty(languages)) return streams;

            foreach (var lang in languages.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = lang.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Language = trimmed,
                    Title = trimmed,
                    IsDefault = streams.Count == 0,
                    Index = streams.Count,
                });
            }

            return streams;
        }

        private static string? ExtractLanguagesFromMediaStreams(List<MediaStream>? mediaStreams)
        {
            if (mediaStreams == null || mediaStreams.Count == 0) return null;
            var langs = mediaStreams
                .Where(ms => ms.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(ms.Language))
                .Select(ms => ms.Language)
                .Distinct()
                .ToList();
            return langs.Count > 0 ? string.Join(",", langs) : null;
        }

        private static void SortByLanguagePreference(List<MediaSourceInfo> sources, PluginConfiguration config)
        {
            var prefLang = config.MetadataLanguage;
            if (string.IsNullOrEmpty(prefLang) || sources.Count <= 1) return;

            sources.Sort((a, b) =>
            {
                var aMatch = HasLanguageMatch(a, prefLang) ? 0 : 1;
                var bMatch = HasLanguageMatch(b, prefLang) ? 0 : 1;
                return aMatch.CompareTo(bMatch);
            });

            // Mark preferred language stream as default
            foreach (var source in sources)
            {
                if (source.MediaStreams == null) continue;
                var hasMatch = false;
                foreach (var ms in source.MediaStreams.Where(ms => ms.Type == MediaStreamType.Audio))
                {
                    ms.IsDefault = !hasMatch && string.Equals(ms.Language, prefLang, StringComparison.OrdinalIgnoreCase);
                    if (ms.IsDefault) hasMatch = true;
                }
            }
        }

        private static bool HasLanguageMatch(MediaSourceInfo source, string lang)
        {
            if (source.MediaStreams == null) return false;
            return source.MediaStreams.Any(ms =>
                ms.Type == MediaStreamType.Audio &&
                string.Equals(ms.Language, lang, StringComparison.OrdinalIgnoreCase));
        }

        private void CacheToDb(
            Data.DatabaseManager? db, string imdbId, int? season, int? episode,
            List<MediaSourceInfo> sources)
        {
            if (db == null || sources.Count == 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var entry = new ResolutionEntry
                    {
                        ImdbId = imdbId,
                        Season = season,
                        Episode = episode,
                        StreamUrl = sources[0].Path,
                        QualityTier = "all",
                        FileName = null,
                        Status = "valid",
                        ResolvedAt = now.ToString("o"),
                        ExpiresAt = now.Add(CacheTtl).ToString("o"),
                        ResolutionTier = "media_source_provider"
                    };

                    var candidates = sources.Select((s, i) => new StreamCandidate
                    {
                        ImdbId = imdbId,
                        Season = season,
                        Episode = episode,
                        Rank = i,
                        ProviderKey = "aio",
                        StreamType = "debrid",
                        Url = s.Path,
                        QualityTier = "all",
                        FileName = s.Name,
                        Status = "valid",
                        ResolvedAt = now.ToString("o"),
                        ExpiresAt = now.Add(CacheTtl).ToString("o"),
                        Languages = ExtractLanguagesFromMediaStreams(s.MediaStreams),
                    }).ToList();

                    await db.UpsertResolutionResultAsync(entry, candidates);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AioMediaSourceProvider] DB cache write failed for {Id} (non-fatal)", imdbId);
                }
            });
        }

        private static List<ProviderInfo> GetProviders(PluginConfiguration config)
        {
            var providers = new List<ProviderInfo>();

            if (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                    providers.Add(new ProviderInfo { DisplayName = "Primary", Url = url, Uuid = uuid ?? "", Token = token ?? "" });
            }

            if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.SecondaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                    providers.Add(new ProviderInfo { DisplayName = "Secondary", Url = url, Uuid = uuid ?? "", Token = token ?? "" });
            }

            return providers;
        }

        private class ProviderInfo
        {
            public string DisplayName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Uuid { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
        }
    }
}
