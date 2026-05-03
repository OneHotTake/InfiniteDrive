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
                var name = prefix + FormatVariantName(v, idx);

                // Stable ID from infoHash:fileIdx or position-based
                var stableId = !string.IsNullOrEmpty(v.InfoHash) && v.FileIdx.HasValue
                    ? $"{v.InfoHash}:{v.FileIdx.Value}".GetHashCode().ToString("x8")
                    : $"precache-{entry.ImdbId}-{idx}";

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

                // Minimal MediaStreams — Emby probes the CDN URL via SupportsProbing.
                // For pre-cache sources, we don't have background probe support (no logger).
                // The AioMediaSourceProvider live/DB paths use ApplyProbes for caching.
                source.MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video, Index = -1 },
                    new MediaStream { Type = MediaStreamType.Audio, Index = -1 },
                };

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
                ImdbId = entry.ImdbId,
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
        public string ImdbId { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string? MediaType { get; set; }
        public string? Url { get; set; }
        public string? HeadersJson { get; set; }
        public string? ProviderName { get; set; }
        public string? FileName { get; set; }
    }
}
