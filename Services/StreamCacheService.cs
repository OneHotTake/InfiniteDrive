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
        Task<CachedStreamEntry?> GetByAioIdAsync(string aioId, int? season, int? episode);
        Task StoreAsync(CachedStreamEntry entry);
        List<MediaSourceInfo> BuildMediaSources(CachedStreamEntry entry);
        Task<List<UncachedItem>> GetUncachedAsync(int limit, CancellationToken ct);
        string BuildPrimaryKey(string? tmdbId, string aioId, string mediaType, int? season, int? episode);
        Task<string?> ResolveTmdbIdForAioIdAsync(string aioId);
        Task InvalidateAsync(string aioId, int? season, int? episode);
        Task PreCacheSingleAsync(string aioId, string mediaType, int? season, int? episode);
    }

    // Cache stores FULL stream URLs (Sprint 502).
    // Proxy mode on AIOStreams = effectively infinite life.
    // Otherwise, Marvin refreshes after CacheRefreshIntervalDays.

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
                return await db.GetCachedStreamsByTmdbKeyAsync(tmdbKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] GetAsync failed for {Key}", tmdbKey);
                return null;
            }
        }

        /// <inheritdoc/>
        public async Task<CachedStreamEntry?> GetByAioIdAsync(string aioId, int? season, int? episode)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null || string.IsNullOrEmpty(aioId)) return null;

            try
            {
                return await db.GetCachedStreamsByAioIdAsync(aioId, season, episode).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] GetByAioIdAsync failed for {AioId}", aioId);
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
        public string BuildPrimaryKey(string? tmdbId, string aioId, string mediaType, int? season, int? episode)
        {
            // Prefer TMDB when available, fallback to AIO ID (always available)
            var id = !string.IsNullOrEmpty(tmdbId) ? $"tmdb-{tmdbId}" : $"aio-{aioId}";
            if (mediaType == "series" && season.HasValue && episode.HasValue)
                return $"{id}-s{season.Value}e{episode.Value}";
            return $"{id}-{mediaType}";
        }

        /// <inheritdoc/>
        public async Task<string?> ResolveTmdbIdForAioIdAsync(string aioId)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null || string.IsNullOrEmpty(aioId)) return null;
            try
            {
                return await db.GetTmdbIdForAioIdAsync(aioId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] ResolveTmdbIdForAioIdAsync failed for {AioId}", aioId);
                return null;
            }
        }

        /// <summary>
        /// Marks cached stream entries as expired for a specific item.
        /// Called by EmbyEventHandler on metadata refresh.
        /// </summary>
        public async Task InvalidateAsync(string aioId, int? season, int? episode)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null || string.IsNullOrEmpty(aioId)) return;

            try
            {
                await db.InvalidateCachedStreamAsync(aioId, season, episode).ConfigureAwait(false);
                _logger.LogDebug("[StreamCache] Invalidated cache for {AioId} S{S}E{E}", aioId, season, episode);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] InvalidateAsync failed for {AioId}", aioId);
            }
        }

        /// <summary>
        /// Lightweight single-item pre-cache refresh. Called by EmbyEventHandler
        /// after invalidation on metadata refresh.
        /// </summary>
        public async Task PreCacheSingleAsync(string aioId, string mediaType, int? season, int? episode)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnablePreCache) return;
            if (_logManager == null) return;

            var providers = ProviderHelper.GetProviders(config);
            if (providers.Count == 0) return;

            var uncachedItem = new UncachedItem
            {
                AioId = aioId,
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
                        "[PreCache] Single item refresh completed for {AioId} S{S}E{E}",
                        aioId, season, episode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamCache] PreCacheSingleAsync failed for {AioId}", aioId);
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
                var name = prefix + FormatVariantName(v, idx);

                // Stable ID from infoHash:fileIdx or position-based
                var stableId = !string.IsNullOrEmpty(v.InfoHash) && v.FileIdx.HasValue
                    ? $"{v.InfoHash}:{v.FileIdx.Value}".GetHashCode().ToString("x8")
                    : $"precache-{entry.AioId}-{idx}";

                var source = new MediaSourceInfo
                {
                    Id = stableId,
                    Name = name,
                    Path = "",                              // prevents Emby ffprobe storm
                    Protocol = MediaProtocol.Http,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    IsInfiniteStream = false,
                    RequiresOpening = true,                 // routes through OpenMediaSource
                    SupportsProbing = false,
                    OpenToken = BuildCachedOpenToken(v, entry),  // infoHash+fileIdx for fresh resolve
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

                // Build MediaStreams from variant metadata (codec, resolution, audio, subtitles)
                source.MediaStreams = BuildMediaStreamsFromVariant(v);

                sources.Add(source);
            }

            return sources;
        }

        private static string BuildCachedOpenToken(StreamVariant v, CachedStreamEntry entry)
        {
            var token = new CachedStreamOpenToken
            {
                InfoHash = v.InfoHash,
                FileIdx = v.FileIdx,
                AioId = entry.AioId,
                Season = entry.Season,
                Episode = entry.Episode,
                MediaType = entry.MediaType,
                Url = v.Url,
                HeadersJson = v.HeadersJson,
                ProviderName = v.ProviderName,
                FileName = v.FileName,
            };
            return JsonSerializer.Serialize(token);
        }

        /// <summary>
        /// Builds a human-readable display name like "1080p · HEVC · English".
        /// </summary>
        private static List<MediaStream> BuildMediaStreamsFromVariant(StreamVariant v)
        {
            var streams = new List<MediaStream>();
            var (w, h) = StreamHelpers.ResolutionToPixels(v.Resolution ?? v.QualityTier);

            // Video stream
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = v.VideoCodec ?? "h264",
                Width = w,
                Height = h,
                Language = "und",
                IsDefault = true,
                BitRate = (v.Bitrate ?? 0) * 1000,
            });

            // Audio streams from structured data
            if (v.AudioStreams?.Count > 0)
            {
                for (int i = 0; i < v.AudioStreams.Count; i++)
                {
                    var a = v.AudioStreams[i];
                    var title = !string.IsNullOrEmpty(a.Codec) && a.Channels > 0
                        ? $"{a.Codec.ToUpperInvariant()} {(a.Channels >= 6 ? "5.1" : a.Channels == 2 ? "Stereo" : "")}".Trim()
                        : a.Codec?.ToUpperInvariant() ?? "";
                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = streams.Count,
                        Codec = a.Codec ?? "",
                        Language = a.Language ?? "und",
                        Channels = a.Channels ?? 0,
                        Title = title,
                        DisplayTitle = title,
                        IsDefault = a.IsDefault || i == 0,
                    });
                }
            }
            else
            {
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = streams.Count,
                    Codec = "",
                    Language = "und",
                    IsDefault = true,
                });
            }

            // Subtitle streams
            if (v.SubtitleStreams?.Count > 0)
            {
                foreach (var s in v.SubtitleStreams)
                {
                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Index = streams.Count,
                        Language = s.Language ?? "und",
                        Title = s.Language ?? "und",
                        DisplayTitle = s.Language ?? "und",
                        IsDefault = s.IsDefault,
                    });
                }
            }

            return streams;
        }

        private static string FormatVariantName(StreamVariant v, int fallbackIndex)
        {
            var tier = v.Resolution ?? v.QualityTier ?? "";
            var res = string.Equals(tier, "remux", StringComparison.OrdinalIgnoreCase)
                ? "4K Remux"
                : StreamHelpers.ResolutionToLabel(tier, null);

            var codec = v.VideoCodec?.ToUpperInvariant() switch
            {
                "HEVC" => "HEVC",
                "H264" => "AVC",
                "AV1"  => "AV1",
                var c when !string.IsNullOrEmpty(c) => c.ToUpperInvariant(),
                _ => ""
            };

            var lang = v.AudioStreams?.FirstOrDefault(a => a.IsDefault)?.Language
                ?? v.AudioStreams?.FirstOrDefault()?.Language;

            var provider = (!string.IsNullOrEmpty(v.ProviderName) && v.ProviderName != "unknown")
                ? v.ProviderName : null;

            return StreamHelpers.BuildDisplayName(
                v.Description, $"Stream #{fallbackIndex + 1}",
                res, codec, lang?.ToUpperInvariant(), provider);
        }
    }

    /// <summary>
    /// Open token DTO for pre-cached streams. Carries infoHash+fileIdx
    /// so OpenMediaSource can resolve a fresh CDN URL without M3U8.
    /// </summary>
    public class CachedStreamOpenToken
    {
        public string TokenType { get; set; } = "cached";
        public string? InfoHash { get; set; }
        public int? FileIdx { get; set; }
        public string AioId { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string? MediaType { get; set; }
        public string? Url { get; set; }
        public string? HeadersJson { get; set; }
        public string? ProviderName { get; set; }
        public string? FileName { get; set; }
    }
}
