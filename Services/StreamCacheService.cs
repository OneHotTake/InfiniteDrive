using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Service interface for the cached_streams pre-cache system.
    /// </summary>
    public interface IStreamCacheService
    {
        Task<CachedStreamEntry?> GetAsync(string tmdbKey);
        Task<CachedStreamEntry?> GetByImdbAsync(string imdbId, int? season, int? episode);
        Task StoreAsync(CachedStreamEntry entry);
        List<MediaSourceInfo> BuildMediaSources(CachedStreamEntry entry);
        Task<List<UncachedItem>> GetUncachedAsync(int limit, CancellationToken ct);
        string BuildPrimaryKey(string? tmdbId, string imdbId, string mediaType, int? season, int? episode);
        Task<string?> ResolveTmdbIdAsync(string imdbId);
        Task InvalidateAsync(string imdbId, int? season, int? episode);
        Task PreCacheSingleAsync(string imdbId, string mediaType, int? season, int? episode);
    }

    /// <summary>
    /// Singleton service for reading/writing the <c>cached_streams</c> table
    /// and converting cached variants into Emby <see cref="MediaSourceInfo"/> objects.
    /// </summary>
    public class StreamCacheService : IStreamCacheService
    {
        private readonly ILogger<StreamCacheService> _logger;
        private readonly ILogManager _logManager;

        public StreamCacheService(ILogger<StreamCacheService> logger, ILogManager? logManager = null)
        {
            _logger = logger;
            _logManager = logManager!;
        }

        /// <inheritdoc/>
        public async Task<CachedStreamEntry?> GetAsync(string tmdbKey)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null || string.IsNullOrEmpty(tmdbKey)) return null;

            try
            {
                return await db.GetCachedStreamsAsync(tmdbKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] GetAsync failed for {Key}", tmdbKey);
                return null;
            }
        }

        /// <inheritdoc/>
        public async Task<CachedStreamEntry?> GetByImdbAsync(string imdbId, int? season, int? episode)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null || string.IsNullOrEmpty(imdbId)) return null;

            try
            {
                return await db.GetCachedStreamsByImdbAsync(imdbId, season, episode).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] GetByImdbAsync failed for {Imdb}", imdbId);
                return null;
            }
        }

        /// <inheritdoc/>
        public async Task StoreAsync(CachedStreamEntry entry)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null || entry == null) return;

            try
            {
                await db.UpsertCachedStreamAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] StoreAsync failed for {Key}", entry.TmdbKey);
            }
        }

        /// <inheritdoc/>
        public async Task<List<UncachedItem>> GetUncachedAsync(int limit, CancellationToken ct)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new List<UncachedItem>();

            try
            {
                return await db.GetUncachedItemsAsync(limit, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] GetUncachedAsync failed");
                return new List<UncachedItem>();
            }
        }

        /// <inheritdoc/>
        public string BuildPrimaryKey(string? tmdbId, string imdbId, string mediaType, int? season, int? episode)
        {
            // Prefer TMDB when available, fallback to IMDB (always available)
            var id = !string.IsNullOrEmpty(tmdbId) ? $"tmdb-{tmdbId}" : $"imdb-{imdbId}";
            if (mediaType == "series" && season.HasValue && episode.HasValue)
                return $"{id}-s{season.Value}e{episode.Value}";
            return $"{id}-{mediaType}";
        }

        /// <inheritdoc/>
        public async Task<string?> ResolveTmdbIdAsync(string imdbId)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null || string.IsNullOrEmpty(imdbId)) return null;
            try
            {
                return await db.GetTmdbIdForImdbAsync(imdbId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] ResolveTmdbIdAsync failed for {Imdb}", imdbId);
                return null;
            }
        }

        /// <summary>
        /// Marks cached stream entries as expired for a specific item.
        /// Called by EmbyEventHandler on metadata refresh.
        /// </summary>
        public async Task InvalidateAsync(string imdbId, int? season, int? episode)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null || string.IsNullOrEmpty(imdbId)) return;

            try
            {
                await db.InvalidateCachedStreamAsync(imdbId, season, episode).ConfigureAwait(false);
                _logger.LogDebug("[StreamCache] Invalidated cache for {Imdb} S{S}E{E}", imdbId, season, episode);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] InvalidateAsync failed for {Imdb}", imdbId);
            }
        }

        /// <summary>
        /// Lightweight single-item pre-cache refresh. Called by EmbyEventHandler
        /// after invalidation on metadata refresh.
        /// </summary>
        public async Task PreCacheSingleAsync(string imdbId, string mediaType, int? season, int? episode)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnablePreCache) return;
            if (_logManager == null) return;

            var providers = ProviderHelper.GetProviders(config);
            if (providers.Count == 0) return;

            var uncachedItem = new UncachedItem
            {
                ImdbId = imdbId,
                MediaType = mediaType,
                Season = season,
                Episode = episode,
            };

            var preCacheTask = new Tasks.PreCacheAioStreamsTask(_logManager);

            try
            {
                var ttlDays = config.PreCacheTTLDays > 0 ? config.PreCacheTTLDays : 14;
                var entry = await preCacheTask.ResolveItemAsync(
                    uncachedItem, providers, config, this, ttlDays, CancellationToken.None).ConfigureAwait(false);

                if (entry != null)
                {
                    await StoreAsync(entry).ConfigureAwait(false);
                    _logger.LogInformation(
                        "[PreCache] Single item refresh completed for {Imdb} S{S}E{E}",
                        imdbId, season, episode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] PreCacheSingleAsync failed for {Imdb}", imdbId);
            }
        }

        /// <summary>
        /// Converts cached variants into Emby MediaSourceInfo[] with RequiresOpening=true.
        /// Open tokens encode infoHash+fileIdx so OpenMediaSource can resolve fresh CDN URLs.
        /// </summary>
        public List<MediaSourceInfo> BuildMediaSources(CachedStreamEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.VariantsJson))
                return new List<MediaSourceInfo>();

            List<StreamVariant>? variants;
            try
            {
                variants = JsonSerializer.Deserialize<List<StreamVariant>>(entry.VariantsJson);
            }
            catch
            {
                return new List<MediaSourceInfo>();
            }

            if (variants == null || variants.Count == 0)
                return new List<MediaSourceInfo>();

            var config = Plugin.Instance?.Configuration;
            var prefix = config?.VersionLabelPrefix ?? "";
            var sources = new List<MediaSourceInfo>();

            for (int idx = 0; idx < variants.Count; idx++)
            {
                var v = variants[idx];

                // Build display name: "1080p · HEVC · English" style
                var name = prefix + BuildVariantDisplayName(v, idx);

                // Stable ID from infoHash:fileIdx or position-based
                var stableId = !string.IsNullOrEmpty(v.InfoHash) && v.FileIdx.HasValue
                    ? $"{v.InfoHash}:{v.FileIdx.Value}".GetHashCode().ToString("x8")
                    : $"precache-{entry.ImdbId}-{idx}";

                var source = new MediaSourceInfo
                {
                    Id = stableId,
                    Name = name,
                    Path = v.Url ?? "",
                    Protocol = MediaProtocol.Http,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    IsInfiniteStream = false,
                    RequiresOpening = true, // Always true for pre-cached: need fresh CDN URL
                };

                if (v.Bitrate.HasValue && v.Bitrate.Value > 0)
                    source.Bitrate = v.Bitrate.Value * 1000; // kbps → bps

                if (v.SizeBytes.HasValue && v.SizeBytes.Value > 0)
                    source.Size = v.SizeBytes.Value;

                if (!string.IsNullOrEmpty(v.FileName))
                {
                    var ext = System.IO.Path.GetExtension(v.FileName)?.TrimStart('.');
                    if (!string.IsNullOrEmpty(ext))
                        source.Container = ext;
                }

                if (!string.IsNullOrEmpty(v.HeadersJson))
                {
                    try
                    {
                        source.RequiredHttpHeaders =
                            JsonSerializer.Deserialize<Dictionary<string, string>>(v.HeadersJson);
                    }
                    catch { /* non-fatal */ }
                }

                // Build MediaStreams from variant metadata
                var streams = new List<MediaStream>();

                // Video stream
                var (width, height) = ParseResolution(v.Resolution ?? v.QualityTier);
                if (width > 0 || !string.IsNullOrEmpty(v.VideoCodec))
                {
                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Codec = v.VideoCodec ?? "",
                        Width = width,
                        Height = height,
                        BitRate = v.Bitrate.HasValue ? v.Bitrate.Value * 1000 : 0,
                        Index = streams.Count,
                    });
                }

                // Audio streams
                if (v.AudioStreams != null)
                {
                    foreach (var audio in v.AudioStreams)
                    {
                        streams.Add(new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Language = audio.Language ?? "",
                            Title = audio.Language ?? "",
                            DisplayTitle = audio.Language ?? "",
                            Codec = audio.Codec ?? "",
                            Channels = audio.Channels ?? 2,
                            IsDefault = audio.IsDefault,
                            Index = streams.Count,
                        });
                    }
                }

                // Subtitle streams
                if (v.SubtitleStreams != null)
                {
                    foreach (var sub in v.SubtitleStreams)
                    {
                        streams.Add(new MediaStream
                        {
                            Type = MediaStreamType.Subtitle,
                            Language = sub.Language ?? "",
                            Title = sub.Language ?? "",
                            DisplayTitle = sub.Language ?? "",
                            IsDefault = sub.IsDefault,
                            Index = streams.Count,
                        });
                    }
                }

                source.MediaStreams = streams;

                // Open token: encode infoHash+fileIdx+imdbId for OpenMediaSource CDN resolution
                var tokenData = new CachedStreamOpenToken
                {
                    InfoHash = v.InfoHash,
                    FileIdx = v.FileIdx,
                    ImdbId = entry.ImdbId,
                    Season = entry.Season,
                    Episode = entry.Episode,
                    MediaType = entry.MediaType,
                    Url = v.Url,
                    HeadersJson = v.HeadersJson,
                    ProviderName = v.ProviderName,
                };
                source.OpenToken = JsonSerializer.Serialize(tokenData);

                sources.Add(source);
            }

            return sources;
        }

        /// <summary>
        /// Builds a human-readable display name like "1080p · HEVC · English".
        /// </summary>
        private static string BuildVariantDisplayName(StreamVariant v, int fallbackIndex)
        {
            var parts = new List<string>();

            // Resolution
            var res = (v.Resolution ?? v.QualityTier ?? "") switch
            {
                "remux" => "4K Remux",
                "2160p" => "4K",
                "1080p" => "1080p",
                "720p" => "720p",
                "480p" => "480p",
                var r when !string.IsNullOrEmpty(r) => r,
                _ => ""
            };
            if (!string.IsNullOrEmpty(res)) parts.Add(res);

            // Codec
            var codec = v.VideoCodec?.ToUpperInvariant() switch
            {
                "HEVC" => "HEVC",
                "H264" => "AVC",
                "AV1" => "AV1",
                var c when !string.IsNullOrEmpty(c) => c.ToUpperInvariant(),
                _ => ""
            };
            if (!string.IsNullOrEmpty(codec)) parts.Add(codec);

            // Primary audio language
            var lang = v.AudioStreams?.FirstOrDefault(a => a.IsDefault)?.Language
                ?? v.AudioStreams?.FirstOrDefault()?.Language;
            if (!string.IsNullOrEmpty(lang)) parts.Add(lang.ToUpperInvariant());

            // Provider
            if (!string.IsNullOrEmpty(v.ProviderName) && v.ProviderName != "unknown")
                parts.Add(v.ProviderName);

            return parts.Count > 0 ? string.Join(" · ", parts) : $"Stream #{fallbackIndex + 1}";
        }

        private static (int width, int height) ParseResolution(string? resolution)
        {
            return resolution?.ToLowerInvariant() switch
            {
                "2160p" or "4k" or "remux" => (3840, 2160),
                "1080p" => (1920, 1080),
                "720p" => (1280, 720),
                "480p" => (854, 480),
                _ => (0, 0),
            };
        }
    }

    /// <summary>
    /// Open token DTO for pre-cached streams. Carries infoHash+fileIdx
    /// so OpenMediaSource can resolve a fresh CDN URL without M3U8.
    /// </summary>
    public class CachedStreamOpenToken
    {
        public string? InfoHash { get; set; }
        public int? FileIdx { get; set; }
        public string ImdbId { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string? MediaType { get; set; }
        public string? Url { get; set; }
        public string? HeadersJson { get; set; }
        public string? ProviderName { get; set; }
    }
}
