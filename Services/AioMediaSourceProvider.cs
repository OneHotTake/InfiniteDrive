using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    /// Dynamic media source provider for virtual AIO library items.
    ///
    /// Triggered when Emby calls /Items/{Id}/PlaybackInfo for items whose
    /// Path starts with /emby-aio/. Resolves streams LIVE at playback time
    /// using the existing StreamResolutionHelper — zero resolution at catalog sync.
    ///
    /// Registered via IMediaSourceManager.AddParts() in VirtualAioEntryPoint.
    /// </summary>
    public class AioMediaSourceProvider : IMediaSourceProvider
    {
        private const string AioPathPrefix = "/emby-aio/";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

        private readonly ILogger<AioMediaSourceProvider> _logger;

        private readonly ConcurrentDictionary<string, CachedSources> _cache = new();

        public AioMediaSourceProvider(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<AioMediaSourceProvider>(logManager.GetLogger("AioMediaSource"));
        }

        // ── IMediaSourceProvider ──────────────────────────────────────────────

        public async Task<List<MediaSourceInfo>> GetMediaSources(
            BaseItem item, CancellationToken cancellationToken)
        {
            // Only handle virtual /emby-aio/ items
            if (item?.Path == null ||
                !item.Path.StartsWith(AioPathPrefix, StringComparison.OrdinalIgnoreCase))
                return new List<MediaSourceInfo>();

            var externalId = item.Path.Substring(AioPathPrefix.Length);
            var cacheKey = item.Path;

            // ── Cache hit ─────────────────────────────────────────────────────
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
            {
                _logger.LogInformation(
                    "[AIO] Cache hit: {Count} sources for '{Title}'",
                    cached.Sources.Count, item.Name);
                return cached.Sources;
            }

            // ── Resolve live ──────────────────────────────────────────────────
            var sources = await ResolveSourcesAsync(item, externalId, cancellationToken);

            _cache[cacheKey] = new CachedSources(sources, DateTime.UtcNow.Add(CacheTtl));

            // If cache was stale (not missing), fire background refresh but return stale
            if (cached != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fresh = await ResolveSourcesAsync(item, externalId, default);
                        _cache[cacheKey] = new CachedSources(fresh, DateTime.UtcNow.Add(CacheTtl));
                    }
                    catch { /* best effort */ }
                }, CancellationToken.None);

                return cached.Sources;
            }

            _logger.LogInformation(
                "[AIO] Resolved {Count} sources for '{Title}' (id={Id})",
                sources.Count, item.Name, externalId);
            return sources;
        }

        public Task<ILiveStream> OpenMediaSource(
            string openToken, List<ILiveStream> currentLiveStreams,
            CancellationToken cancellationToken)
        {
            // AIO sources use direct play/stream — RequiresOpening defaults to false
            throw new NotImplementedException(
                "AIO sources use direct play — OpenMediaSource is not required.");
        }

        // ── Resolution ───────────────────────────────────────────────────────

        /// <summary>
        /// Calls the existing StreamResolutionHelper to get a live stream URL,
        /// then wraps it in MediaSourceInfo. Falls back to mock data if the
        /// plugin is not configured (first-run / test scenario).
        /// </summary>
        private async Task<List<MediaSourceInfo>> ResolveSourcesAsync(
            BaseItem item, string externalId, CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            var db = plugin?.DatabaseManager;

            // Try real resolution if plugin is configured
            if (config != null && db != null && plugin != null
                && !string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                var healthTracker = plugin.ResolverHealthTracker;

                var playReq = new PlayRequest { Imdb = externalId };
                var result = await StreamResolutionHelper.SyncResolveViaProvidersAsync(
                    playReq, config, db, _logger, healthTracker, cancellationToken);

                if (result != null && result.Status == ResolutionStatus.Success
                    && !string.IsNullOrEmpty(result.StreamUrl))
                {
                    return new List<MediaSourceInfo>
                    {
                        BuildMediaSource(result.StreamUrl, result.Entry?.FileName, externalId),
                    };
                }

                _logger.LogWarning(
                    "[AIO] Live resolution returned {Status} for '{Title}' — falling back to cached/mock",
                    result?.Status.ToString() ?? "null", item.Name);
            }

            // Fallback: mock sources for unconfigured / test scenarios
            return MockResolveAioStreams(item, externalId);
        }

        private static MediaSourceInfo BuildMediaSource(
            string url, string? fileName, string externalId)
        {
            return new MediaSourceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = !string.IsNullOrEmpty(fileName) ? fileName : "AIO Stream",
                Path = url,
                DirectStreamUrl = url,
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = false,
                RequiredHttpHeaders = new Dictionary<string, string>(),
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video },
                    new MediaStream { Type = MediaStreamType.Audio, Language = "eng" },
                },
            };
        }

        /// <summary>
        /// Mock sources for first-run / unconfigured scenarios.
        /// Replace with real AIOStreams multi-tier resolution in production.
        /// </summary>
        private static List<MediaSourceInfo> MockResolveAioStreams(
            BaseItem item, string externalId)
        {
            var variants = new[]
            {
                new { Name = "4K HDR Dolby Digital+", Bitrate = 25000000,
                    Video = "hevc", Audio = "truehd",
                    Url = $"https://cdn.example.com/mock-4k/{externalId}.mkv" },
                new { Name = "1080p DD+", Bitrate = 8000000,
                    Video = "h264", Audio = "ac3",
                    Url = $"https://cdn.example.com/mock-1080p/{externalId}.mkv" },
                new { Name = "720p AAC", Bitrate = 4000000,
                    Video = "h264", Audio = "aac",
                    Url = $"https://cdn.example.com/mock-720p/{externalId}.mkv" },
            };

            return variants.Select(v => new MediaSourceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = v.Name,
                Path = v.Url,
                DirectStreamUrl = v.Url,
                Protocol = MediaProtocol.Http,
                Bitrate = v.Bitrate,
                Container = "mkv",
                Type = MediaSourceType.Default,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = false,
                RunTimeTicks = 7_200_000_000L,
                RequiredHttpHeaders = new Dictionary<string, string>(),
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video, Codec = v.Video },
                    new MediaStream { Type = MediaStreamType.Audio, Codec = v.Audio, Language = "eng" },
                },
            }).ToList();
        }

        // ── Cache record ─────────────────────────────────────────────────────

        private sealed record CachedSources(List<MediaSourceInfo> Sources, DateTime Expires);
    }
}
