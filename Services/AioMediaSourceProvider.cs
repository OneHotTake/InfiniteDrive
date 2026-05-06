using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Tasks;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Populates Emby's version picker with live AIOStreams streams.
    /// Called when user browses/long-presses a media item.
    /// ONE .strm per item handles default play; this handles the version picker.
    ///
    /// Architecture: RankAndFilterStreams → tier dedup → cap at 7 sources.
    /// Never probes during PlaybackInfo. OpenMediaSource proxies with failover.
    /// </summary>
    public partial class AioMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<AioMediaSourceProvider> _logger;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ILibraryManager _libraryManager;

        // In-memory cache: key = "aioId" or "aioId:S{season}E{episode}", value = (sources, expiry)
        private static readonly ConcurrentDictionary<string, (List<MediaSourceInfo> Sources, DateTime Expires)> _cache
            = new();

        // Single-flight lock: prevents duplicate AIOStreams fetches for the same item
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

        /// <summary>
        /// Lazy-resolved stream cache service from Plugin.Instance singleton.
        /// Avoids constructor injection issues with Emby's DI container.
        /// </summary>
        private IStreamCacheService StreamCache => Plugin.Instance?.StreamCacheService
            ?? new StreamCacheService(Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamCacheService>.Instance);

        public AioMediaSourceProvider(
            ILogManager logManager,
            IMediaSourceManager mediaSourceManager,
            ILibraryManager libraryManager)
        {
            _logger = new EmbyLoggerAdapter<AioMediaSourceProvider>(logManager.GetLogger("InfiniteDrive"));
            _mediaSourceManager = mediaSourceManager;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Invalidates the in-memory source cache for an item so the next GetMediaSources
        /// picks up probe data from the DB.
        /// </summary>
        internal static void InvalidateCache(string aioId, int? season, int? episode)
        {
            var key = season.HasValue
                ? $"{aioId}:S{season}E{episode}"
                : aioId;
            _cache.TryRemove(key, out _);
        }

        [Obsolete("Legacy resolution path. Versioned multi-CDN flow is preferred.")]
        public Task<List<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                return GetMediaSourcesCoreAsync(item, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AioMediaSourceProvider] Error getting sources for {Item}", item?.Name);
                return Task.FromResult(new List<MediaSourceInfo>());
            }
        }


        private async Task<List<MediaSourceInfo>> GetMediaSourcesCoreAsync(BaseItem item, CancellationToken ct)
        {
            if (item == null) return new List<MediaSourceInfo>();

            _logger.LogInformation("[AioMediaSourceProvider] GetMediaSources called for {Name} (Type={Type}, Path={Path})",
                item.Name, item.GetType().Name, item.Path);

            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.PluginSecret))
            {
                _logger.LogWarning("[AioMediaSourceProvider] Skipping {Name} — config={Cfg} secret={Secret}",
                    item.Name, config != null ? "ok" : "null", !string.IsNullOrEmpty(config?.PluginSecret) ? "set" : "empty");
                return new List<MediaSourceInfo>();
            }

            // Only handle items in configured media paths
            if (!IsInConfiguredPath(item, config))
            {
                _logger.LogDebug("[AioMediaSourceProvider] Skipping {Name} — not in configured path (Path={Path})", item.Name, item.Path);
                return new List<MediaSourceInfo>();
            }

            // Identify item
            var (aioId, mediaType, season, episode) = IdentifyItem(item);
            if (string.IsNullOrEmpty(aioId))
            {
                _logger.LogWarning("[AioMediaSourceProvider] No AIO ID for {Name} (Path={Path}, Providers={Providers})",
                    item.Name, item.Path,
                    item.ProviderIds != null ? string.Join(",", item.ProviderIds.Keys) : "none");
                return new List<MediaSourceInfo>();
            }

            // If this item is a .strm file in a configured path, let Emby play it natively.
            // The .strm files contain direct CDN URLs — no need to inject MediaSourceInfo.
            if (!string.IsNullOrEmpty(item.Path) && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("[AioMediaSourceProvider] Skipping .strm item {Name} — plays natively", item.Name);
                return new List<MediaSourceInfo>();
            }

            // In-memory cache check
            var cacheKey = season.HasValue
                ? $"{aioId}:S{season}E{episode}"
                : aioId;

            if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
            {
                _logger.LogDebug("[AioMediaSourceProvider] Cache hit for {Key}", cacheKey);
                return cached.Sources;
            }

            // Single-flight: ensure only one in-flight resolve per cache key
            var slim = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await slim.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check cache after acquiring lock
                if (_cache.TryGetValue(cacheKey, out cached) && cached.Expires > DateTime.UtcNow)
                    return cached.Sources;

                return await ResolveWithCacheAsync(cacheKey, aioId, mediaType, season, episode, config, ct).ConfigureAwait(false);
            }
            finally
            {
                slim.Release();
                _keyLocks.TryRemove(cacheKey, out _);
            }
        }

        private async Task<List<MediaSourceInfo>> ResolveWithCacheAsync(
            string cacheKey, string aioId, string mediaType, int? season, int? episode,
            PluginConfiguration config, CancellationToken ct)
        {
            var db = Plugin.Instance?.DatabaseManager;
            var cacheTtl = TimeSpan.FromMinutes(
                config.CacheLifetimeMinutes > 0 ? config.CacheLifetimeMinutes : 360);

            // ── Pre-cache lookup (cached_streams table) ──────────────────────────
            try
            {
                var cached = await StreamCache.GetByAioIdAsync(aioId, season, episode).ConfigureAwait(false);
                if (cached != null)
                {
                    var preSources = StreamCache.BuildMediaSources(cached);
                    if (preSources.Count > 0)
                    {
                        _logger.LogDebug("[AioMediaSourceProvider] Pre-cache hit for {AioId}", aioId);
                        _cache[cacheKey] = (preSources, DateTime.UtcNow.Add(cacheTtl));
                        return preSources;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AioMediaSourceProvider] Pre-cache lookup failed for {Key}", cacheKey);
            }

            // ── Legacy DB cache check (stream_candidates) ─────────────────────────
            if (db != null)
            {
                try
                {
                    var dbCandidates = await db.GetStreamCandidatesAsync(aioId, season, episode).ConfigureAwait(false);

                    if (dbCandidates?.Count > 0)
                    {
                        // Inline ranking replaces StreamScoringService
                        var valid = dbCandidates
                            .Where(c => c.Status == "valid" && !string.IsNullOrEmpty(c.Url))
                            .ToList();
                        var scored = StreamHelpers.RankCandidates(valid);

                        if (scored.Count > 0)
                        {
                            var sources = BuildSourcesFromCandidates(scored, aioId);
                            SetOpenTokens(sources, aioId, scored, mediaType);

                            SortByLanguagePreference(sources, config, null);
                            _cache[cacheKey] = (sources, DateTime.UtcNow.Add(cacheTtl));
                            return sources;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AioMediaSourceProvider] DB cache lookup failed for {Key}", cacheKey);
                }
            }

            // Live resolve: fetch from AIOStreams, rank, dedup, cap
            _logger.LogInformation("[AioMediaSourceProvider] Live resolve for {AioId} ({MediaType}) S{Season}E{Episode}",
                aioId, mediaType, season, episode);
            var candidates = await ResolveFromAioStreams(aioId, mediaType, season, episode, config).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                _logger.LogWarning("[AioMediaSourceProvider] Live resolve returned 0 candidates for {AioId}", aioId);
                return new List<MediaSourceInfo>();
            }

            var liveSources = BuildSourcesFromCandidates(candidates, aioId);

            if (liveSources.Count > 0)
            {
                SetOpenTokens(liveSources, aioId, candidates, mediaType);
                SortByLanguagePreference(liveSources, config, null);
                _cache[cacheKey] = (liveSources, DateTime.UtcNow.Add(cacheTtl));

                // Cache candidates to DB (await to ensure ResolverService can use them immediately)
                await CacheCandidatesToDbAsync(db, aioId, season, episode, candidates, cacheTtl).ConfigureAwait(false);

                // Also write to cached_streams for pre-cache (fire-and-forget)
                _ = WriteToStreamCacheAsync(aioId, mediaType, season, episode, candidates);

                // Binge prefetch for series
                if (season.HasValue && episode.HasValue)
                {
                    _ = BingePrefetchService.PrefetchNextEpisodeAsync(
                        aioId, season.Value, episode.Value, _logger);
                }
            }

            return liveSources;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Live resolve: AIOStreams → RankAndFilterStreams → tier dedup → cap
        // ═══════════════════════════════════════════════════════════════════════════

        private async Task<List<StreamCandidate>> ResolveFromAioStreams(
            string aioId, string mediaType, int? season, int? episode, PluginConfiguration config)
        {
            var providers = ProviderHelper.GetProviders(config);
            if (providers.Count == 0) return new List<StreamCandidate>();

            var response = await AioStreamsClient.FetchAioStreamsAsync(
                providers, aioId, mediaType, season, episode,
                _logger, Plugin.Instance?.ResolverHealthTracker,
                cooldown: null, ct: CancellationToken.None).ConfigureAwait(false);

            if (response == null) return new List<StreamCandidate>();

            // Pass 0 for candidatesPerProvider to disable the per-provider cap.
            // The bucket algorithm in SelectBest handles curation; capping here
            // means all streams from a single-provider setup get cut to 3.
            var ranked = StreamHelpers.RankAndFilterStreams(
                response, aioId, season, episode,
                config.ProviderPriorityOrder ?? "",
                0, // unlimited — let SelectBest's bucket algorithm curate
                config.CacheLifetimeMinutes > 0 ? config.CacheLifetimeMinutes : 360);

            return StreamHelpers.RankCandidates(ranked);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Source building (unified for both live and DB paths)
        // ═══════════════════════════════════════════════════════════════════════════

        private List<MediaSourceInfo> BuildSourcesFromCandidates(
            IEnumerable<StreamCandidate> candidates, string aioId)
        {
            var config = Plugin.Instance?.Configuration;
            var tierLimits = config != null
                ? BuildTierLimits(config)
                : new Dictionary<string, int>();

            // Group candidates by UI tier, cap per tier, preserve rank order
            var capped = candidates
                .GroupBy(c => MapCandidateToUiTier(c))
                .SelectMany(g => tierLimits.TryGetValue(g.Key, out var limit) && limit > 0
                    ? g.Take(limit)
                    : limit == 0 ? Enumerable.Empty<StreamCandidate>() : g)
                .OrderBy(c => c.Rank)
                .ToList();

            return capped
                .Select(c => MapCandidateToSource(c))
                .Where(s => s != null)
                .Cast<MediaSourceInfo>()
                .ToList();
        }

        /// <summary>Maps a candidate's QualityTier + filename audio to a UI tier name.</summary>
        private static string MapCandidateToUiTier(StreamCandidate c)
        {
            var fn = (c.FileName ?? "").ToUpperInvariant();
            var has51 = fn.Contains("5.1") || fn.Contains("7.1") || fn.Contains("6CH") || fn.Contains("8CH")
                     || fn.Contains("DTS") || fn.Contains("TRUEHD") || fn.Contains("ATMOS")
                     || fn.Contains("DOLBY") && !fn.Contains("DOLBY DIGITAL"); // DTS/Atmos = 5.1+

            return c.QualityTier switch
            {
                "2160p" => has51 ? "4K 5.1 / DTS" : "4K (any)",
                "1080p" => has51 ? "1080p 5.1" : "1080p (any)",
                "720p"  => "720p",
                _       => "SD / Unknown / Low-bandwidth" // 480p, unknown, etc.
            };
        }

        private static Dictionary<string, int> BuildTierLimits(PluginConfiguration cfg) => new()
        {
            { "4K 5.1 / DTS",                cfg.MaxStreams4k51 },
            { "4K (any)",                     cfg.MaxStreams4kAny },
            { "1080p 5.1",                    cfg.MaxStreams1080p51 },
            { "1080p (any)",                  cfg.MaxStreams1080pAny },
            { "720p",                         cfg.MaxStreams720p },
            { "SD / Unknown / Low-bandwidth", cfg.MaxStreamsSd },
        };

        private MediaSourceInfo? MapCandidateToSource(StreamCandidate candidate)
        {
            if (string.IsNullOrEmpty(candidate.Url)) return null;

            var config = Plugin.Instance?.Configuration;
            var name = FormatCandidateName(candidate);
            var source = new MediaSourceInfo
            {
                Id = candidate.Id ?? Guid.NewGuid().ToString("N"),
                Name = name,
                Path = "", // No URL — prevents Emby ffprobe storm. OpenMediaSource provides URL on play.
                Protocol = MediaProtocol.Http,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsInfiniteStream = false,
                RequiresOpening = true,
                SupportsProbing = false,
            };

            if (candidate.BitrateKbps.HasValue && candidate.BitrateKbps.Value > 0)
                source.Bitrate = candidate.BitrateKbps.Value;

            if (candidate.FileSize.HasValue && candidate.FileSize.Value > 0)
                source.Size = candidate.FileSize.Value;

            // Container from filename
            if (!string.IsNullOrEmpty(candidate.FileName))
            {
                var ext = Path.GetExtension(candidate.FileName)?.TrimStart('.');
                if (!string.IsNullOrEmpty(ext))
                    source.Container = ext;
            }

            // Required headers (StremThru, nzbDAV auth tokens)
            if (!string.IsNullOrEmpty(candidate.HeadersJson))
            {
                try
                {
                    source.RequiredHttpHeaders =
                        JsonSerializer.Deserialize<Dictionary<string, string>>(candidate.HeadersJson);
                }
                catch { /* non-fatal */ }
            }

            // MediaStreams: prefer actual ffprobe data, fall back to filename/metadata parsing
            if (!string.IsNullOrEmpty(candidate.ProbeJson))
            {
                try
                {
                    var probed = DeserializeProbeStreams(candidate.ProbeJson);
                    if (probed != null && probed.Count > 1)
                        source.MediaStreams = FilterKnownStreams(probed);
                }
                catch { /* non-fatal */ }
            }

            // Fallback: build streams from candidate metadata (filename parsing, raw AioStreams data)
            if (source.MediaStreams == null || source.MediaStreams.Count == 0)
                source.MediaStreams = BuildRichMediaStreams(candidate);

            return source;
        }

        private static string FormatCandidateName(StreamCandidate c)
        {
            var res        = StreamHelpers.ResolutionToLabel(c.QualityTier, c.FileName);
            var src        = GetSourceTypeFromQualityTier(c.QualityTier, c.FileName);
            var audioLabel = BuildAudioLabelFromFilename(c.FileName);

            var sizeLabel = "";
            if (c.FileSize.HasValue && c.FileSize.Value > 0)
            {
                var gb = c.FileSize.Value / (1024.0 * 1024.0 * 1024.0);
                sizeLabel = gb >= 1 ? $"{gb:F0} GiB" : $"{c.FileSize.Value / (1024.0 * 1024.0):F0} MiB";
            }

            var parts = new[] { res, src, audioLabel, sizeLabel }
                .Where(p => !string.IsNullOrEmpty(p));
            return parts.Any() ? string.Join(" · ", parts) : "Stream";
        }

        private static string GetSourceTypeFromQualityTier(string? qualityTier, string? filename)
        {
            // If qualityTier is explicitly "remux", return Remux
            if (string.Equals(qualityTier, "remux", StringComparison.OrdinalIgnoreCase))
                return "Remux";

            // Otherwise derive from filename
            if (string.IsNullOrEmpty(filename)) return qualityTier ?? "Stream";

            var fn = filename.ToUpperInvariant();
            if (fn.Contains("REMUX"))                          return "Remux";
            if (fn.Contains("BLURAY") || fn.Contains("BDRIP") || fn.Contains("BLU-RAY"))
                                                        return "BluRay";
            if (fn.Contains("WEBDL") || fn.Contains("WEB-DL") ||
                fn.Contains("WEB.DL") || fn.Contains("WEB DL"))return "WEB-DL";
            if (fn.Contains("WEB"))                           return "WEB";

            // Fallback to qualityTier
            return qualityTier switch
            {
                "2160p" => "BluRay",
                "1080p" => "BluRay",
                "720p"  => "WEB-DL",
                _ => "Stream"
            };
        }

        private static string BuildAudioLabelFromFilename(string? filename)
        {
            if (string.IsNullOrEmpty(filename)) return "";

            var fn = filename.ToUpperInvariant();
            var channels = "";

            // Extract channels
            if (fn.Contains("7.1") || fn.Contains("8 CH")) channels = "7.1";
            else if (fn.Contains("5.1") || fn.Contains("6 CH")) channels = "5.1";
            else if (fn.Contains("2.0") || fn.Contains("STEREO")) channels = "Stereo";

            // Check for Atmos
            if (fn.Contains("ATMOS") && channels == "7.1") return "Atmos";

            // Check for specific codecs
            if (fn.Contains("TRUEHD"))
                return $"TrueHD{(string.IsNullOrEmpty(channels) ? "" : " " + channels)}";
            if (fn.Contains("DTS-HD") || fn.Contains("DTSHD"))
                return $"DTS-HD MA{(string.IsNullOrEmpty(channels) ? "" : " " + channels)}";
            if (fn.Contains("DTS-X"))
                return $"DTS:X{(string.IsNullOrEmpty(channels) ? "" : " " + channels)}";
            if (fn.Contains("EAC3") || fn.Contains("DDP") || fn.Contains("DD+"))
                return $"DD+{(string.IsNullOrEmpty(channels) ? "" : " " + channels)}";
            if (fn.Contains("DTS"))
                return $"DTS{(string.IsNullOrEmpty(channels) ? "" : " " + channels)}";
            if (fn.Contains("AC3") || fn.Contains("DD "))
                return $"DD{(string.IsNullOrEmpty(channels) ? "" : " " + channels)}";
            if (fn.Contains("AAC")) return "AAC";
            if (fn.Contains("FLAC")) return "FLAC";
            if (fn.Contains("OPUS")) return "OPUS";

            // Return just channels if nothing else found
            return channels;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  OpenMediaSource helpers
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Serializes all candidates into the OpenToken so OpenMediaSource can failover.
        /// </summary>
        private void SetOpenTokens(List<MediaSourceInfo> sources, string aioId,
            List<StreamCandidate>? originalCandidates = null, string? mediaType = null)
        {
            // Build all candidate entries
            var allCandidates = sources.Select((s, i) =>
            {
                var orig = originalCandidates != null && i < originalCandidates.Count
                    ? originalCandidates[i] : null;
                return new OpenTokenCandidate
                {
                    Rank = i,
                    Url = orig?.Url ?? s.Path,
                    Headers = s.RequiredHttpHeaders,
                    ProviderKey = ExtractProviderFromName(s.Name),
                    Size = s.Size,
                    StreamKey = orig?.StreamKey,
                    InfoHash = orig?.InfoHash,
                    FileIdx = orig?.FileIdx,
                    FileName = orig?.FileName,
                };
            }).ToList();

            // Each source gets its OWN token with its candidate FIRST (for failover)
            for (var idx = 0; idx < sources.Count; idx++)
            {
                var ordered = new List<OpenTokenCandidate> { allCandidates[idx] };
                for (var j = 0; j < allCandidates.Count; j++)
                {
                    if (j != idx) ordered.Add(allCandidates[j]);
                }

                sources[idx].OpenToken = JsonSerializer.Serialize(new OpenTokenData
                {
                    AioId = aioId,
                    MediaType = mediaType,
                    Candidates = ordered,
                });
            }
        }

        private async Task<MediaSourceInfo> BuildSourceFromCandidateAsync(
            OpenTokenCandidate cand, string aioId, CancellationToken ct)
        {
            var name = $"Stream #{cand.Rank + 1}";
            var source = new MediaSourceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Path = cand.Url ?? "",
                Protocol = MediaProtocol.Http,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                RequiresOpening = false,
                IsInfiniteStream = false,
            };

            if (cand.Size.HasValue) source.Size = cand.Size.Value;
            if (cand.Headers != null) source.RequiredHttpHeaders = cand.Headers;

            // Container from filename
            if (!string.IsNullOrEmpty(cand.FileName))
            {
                var ext = System.IO.Path.GetExtension(cand.FileName)?.TrimStart('.');
                if (!string.IsNullOrEmpty(ext))
                    source.Container = ext;
            }

            // Emby does NOT probe ILiveStream sources.
            // Run ffprobe on the selected CDN URL to get real stream metadata.
            // Only probes the ONE stream the user selected — no storm.
            if (!string.IsNullOrEmpty(cand.Url))
            {
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(8));
                    var probed = await CdnProber.ProbeAsync(cand.Url, _logger, probeCts.Token).ConfigureAwait(false);
                    if (probed != null && probed.Count > 1)
                    {
                        source.MediaStreams = FilterKnownStreams(probed);
                        _logger.LogInformation("[AioMediaSourceProvider] ffprobe got {Count} streams for rank {Rank}",
                            probed.Count, cand.Rank);
                        // Cache probe result for dropdown display
                        if (!string.IsNullOrEmpty(cand.StreamKey))
                        {
                            var json = SerializeProbeStreams(probed);
                            var db = Plugin.Instance?.DatabaseManager;
                            if (db != null)
                                _ = db.SaveProbeJsonAsync(cand.StreamKey, json);
                        }
                        return source;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AioMediaSourceProvider] ffprobe failed for rank {Rank}, using filename fallback", cand.Rank);
                }
            }

            // Fallback: parse what we can from filename
            source.MediaStreams = BuildStreamsFromFilename(cand.FileName);

            return source;
        }

        /// <summary>
        /// Builds minimal MediaStreams from filename so Emby can decide direct play vs transcode.
        /// ILiveStream sources are NOT probed by Emby — this is the only stream info it gets.
        /// </summary>
        private static List<MediaStream> BuildStreamsFromFilename(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return new List<MediaStream>
            {
                new MediaStream { Type = MediaStreamType.Video, Index = 0 },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1 },
            };

            var fn = fileName.ToUpperInvariant();

            // Video codec from filename
            var videoCodec = "";
            if (fn.Contains("H265") || fn.Contains("H.265") || fn.Contains("HEVC") || fn.Contains("X265") || fn.Contains("X.265"))
                videoCodec = "hevc";
            else if (fn.Contains("H264") || fn.Contains("H.264") || fn.Contains("AVC") || fn.Contains("X264") || fn.Contains("X.264"))
                videoCodec = "h264";
            else if (fn.Contains("AV1"))
                videoCodec = "av1";
            else if (fn.Contains("VP9"))
                videoCodec = "vp9";
            else if (fn.Contains("VP8"))
                videoCodec = "vp8";

            // Audio codec from filename
            var audioCodec = "";
            if (fn.Contains("EAC3") || fn.Contains("E-AC3") || fn.Contains("DDP") || fn.Contains("DD+"))
                audioCodec = "eac3";
            else if (fn.Contains("AC3") || fn.Contains("AC-3") || fn.Contains("DD5") || fn.Contains("DD "))
                audioCodec = "ac3";
            else if (fn.Contains("DTS-HD") || fn.Contains("DTSHD"))
                audioCodec = "dtshd";
            else if (fn.Contains("DTS"))
                audioCodec = "dts";
            else if (fn.Contains("TRUEHD") || fn.Contains("ATMOS"))
                audioCodec = "truehd";
            else if (fn.Contains("AAC"))
                audioCodec = "aac";
            else if (fn.Contains("FLAC"))
                audioCodec = "flac";
            else if (fn.Contains("OPUS"))
                audioCodec = "opus";

            // Channels
            int? channels = null;
            if (fn.Contains("7.1")) channels = 8;
            else if (fn.Contains("5.1")) channels = 6;
            else if (fn.Contains("2.0") || fn.Contains("STEREO")) channels = 2;

            var streams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = videoCodec,
                    IsDefault = true,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = audioCodec,
                    Channels = channels,
                    IsDefault = true,
                },
            };

            return streams;
        }

        private static string ExtractProviderFromName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            var providers = new[] { "Real-Debrid", "TorBox", "AllDebrid", "Premiumize", "DebridLink", "StremThru" };
            foreach (var p in providers)
            {
                if (name.Contains(p, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return "unknown";
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Probing (cache → live probe, never overwrites with worse data)
        // ═══════════════════════════════════════════════════════════════════════════
        //  DB caching
        // ═══════════════════════════════════════════════════════════════════════════

        private async Task CacheCandidatesToDbAsync(
            Data.DatabaseManager? db, string aioId, int? season, int? episode,
            List<StreamCandidate> candidates, TimeSpan cacheTtl)
        {
            if (db == null || candidates.Count == 0) return;

            try
            {
                var now = DateTime.UtcNow;
                var entry = new ResolutionEntry
                {
                    AioId = aioId,
                    Season = season,
                    Episode = episode,
                    StreamUrl = candidates[0].Url,
                    QualityTier = candidates[0].QualityTier ?? "4k_any",
                    FileName = candidates[0].FileName,
                    Status = "valid",
                    ResolvedAt = now.ToString("o"),
                    ExpiresAt = now.Add(cacheTtl).ToString("o"),
                    ResolutionTier = "media_source_provider"
                };

                // Preserve each candidate's QualityTier for ResolverService filtering
                var candidatesWithTier = candidates.Select((c, i) => new StreamCandidate
                {
                    AioId = c.AioId,
                    Season = c.Season,
                    Episode = c.Episode,
                    Rank = c.Rank,
                    ProviderKey = c.ProviderKey,
                    StreamType = c.StreamType,
                    Url = c.Url,
                    QualityTier = c.QualityTier ?? "4k_any",
                    FileName = c.FileName,
                    Status = c.Status,
                    ResolvedAt = c.ResolvedAt,
                    ExpiresAt = c.ExpiresAt
                }).ToList();

                await db.UpsertResolutionResultAsync(entry, candidatesWithTier).ConfigureAwait(false);
                _logger.LogDebug("[AioMediaSourceProvider] Cached {Count} candidates for {AioId}", candidates.Count, aioId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AioMediaSourceProvider] DB cache write failed for {Id} (non-fatal)", aioId);
            }
        }


        // ═══════════════════════════════════════════════════════════════════════════
        //  Versioned multi-CDN sources
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Looks up stored multi-version data and returns direct-play MediaSourceInfo
        /// for each version. Returns null if no stored versions exist (fall through
        /// to legacy resolution).
        /// </summary>


        // ═══════════════════════════════════════════════════════════════════════════
        //  Item identification (unchanged)
        // ═══════════════════════════════════════════════════════════════════════════

        private bool IsInConfiguredPath(BaseItem item, PluginConfiguration config)
        {
            // Virtual items (Path=null) identified by INFINITEDRIVE provider ID
            if (item.ProviderIds != null && item.ProviderIds.ContainsKey("INFINITEDRIVE"))
                return true;

            var path = item.Path;
            if (string.IsNullOrEmpty(path)) return false;

            var paths = new[] { config.SyncPathMovies, config.SyncPathShows, config.SyncPathAnime };
            return paths.Any(p => !string.IsNullOrEmpty(p) && path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        private (string? aioId, string mediaType, int? season, int? episode) IdentifyItem(BaseItem item)
        {
            string? aioId = null;
            var mediaType = "movie";
            int? season = null;
            int? episode = null;

            // 1. ProviderIds["imdb"] — Emby native
            if (item.ProviderIds != null && item.ProviderIds.TryGetValue("imdb", out var imdb))
                aioId = imdb;

            // 2. Parse from .strm path
            if (string.IsNullOrEmpty(aioId) && !string.IsNullOrEmpty(item.Path))
            {
                aioId = ParseImdbFromPath(item.Path);
            }

            // 3. ProviderIds["AIO"] (last resort)
            if (string.IsNullOrEmpty(aioId) && item.ProviderIds != null && item.ProviderIds.TryGetValue("AIO", out var aioFromProvider))
                aioId = aioFromProvider;

            // 4. Kitsu/AniList/MAL provider IDs (anime without IMDB)
            if (string.IsNullOrEmpty(aioId) && item.ProviderIds != null)
            {
                foreach (var kvp in item.ProviderIds)
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

            if (string.IsNullOrEmpty(aioId)) return (null, mediaType, null, null);

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

            return (aioId, mediaType, season, episode);
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

        // ═══════════════════════════════════════════════════════════════════════════
        //  Language preference sorting (unchanged)
        // ═══════════════════════════════════════════════════════════════════════════

        private void SortByLanguagePreference(List<MediaSourceInfo> sources, PluginConfiguration config, string? itemPath)
        {
            // Priority: config.MetadataLanguage → library language → no sort
            var prefLang = config.MetadataLanguage;
            if (string.IsNullOrEmpty(prefLang))
                prefLang = GetLibraryLanguage(itemPath);
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

        private string? GetLibraryLanguage(string? itemPath)
        {
            if (string.IsNullOrEmpty(itemPath)) return null;
            try
            {
                var folders = _libraryManager.GetVirtualFolders();
                if (folders == null) return null;

                foreach (var folder in folders)
                {
                    if (folder.Locations == null) continue;
                    foreach (var location in folder.Locations)
                    {
                        if (!string.IsNullOrEmpty(location) &&
                            itemPath.StartsWith(location, StringComparison.OrdinalIgnoreCase))
                        {
                            return folder.LibraryOptions?.PreferredMetadataLanguage;
                        }
                    }
                }
            }
            catch { /* non-critical */ }

            return null;
        }

        // ═══════════════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════════════
        //  Pre-cache write-through
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Fire-and-forget: writes live-resolved candidates to cached_streams
        /// so future lookups are instant.
        /// </summary>
        private async Task WriteToStreamCacheAsync(
            string aioId, string mediaType, int? season, int? episode,
            List<StreamCandidate> candidates)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return;

                var cacheService = StreamCache;

                var variants = candidates.Take(6).Select(c => new StreamVariant
                {
                    InfoHash = c.InfoHash,
                    FileIdx = c.FileIdx,
                    FileName = c.FileName,
                    Resolution = c.QualityTier,
                    QualityTier = c.QualityTier,
                    SizeBytes = c.FileSize,
                    Bitrate = c.BitrateKbps,
                    VideoCodec = InferCodec(c.FileName),
                    AudioStreams = !string.IsNullOrEmpty(c.Languages)
                        ? c.Languages.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select((lang, i) => new AudioStreamInfo { Language = lang.Trim(), IsDefault = i == 0 })
                            .ToList()
                        : null,
                    ProviderName = c.ProviderKey,
                    StreamType = c.StreamType,
                    SourceName = c.FileName ?? c.StreamKey ?? $"Stream #{c.Rank + 1}",
                    BingeGroup = c.BingeGroup,
                    StreamKey = c.StreamKey,
                    Url = c.Url,
                    HeadersJson = c.HeadersJson,
                }).ToList();

                var ttlDays = config.PreCacheTTLDays > 0 ? config.PreCacheTTLDays : 14;

                // Build primary key: prefer TMDB, fallback to IMDB
                var tmdbId = await cacheService.ResolveTmdbIdForAioIdAsync(aioId).ConfigureAwait(false);
                var primaryKey = cacheService.BuildPrimaryKey(tmdbId, aioId, mediaType, season, episode);

                var entry = new CachedStreamEntry
                {
                    TmdbKey = primaryKey,
                    AioId = aioId,
                    MediaType = mediaType,
                    Season = season,
                    Episode = episode,
                    VariantsJson = System.Text.Json.JsonSerializer.Serialize(variants),
                    CachedAt = DateTime.UtcNow.ToString("o"),
                    ExpiresAt = DateTime.UtcNow.AddDays(ttlDays).ToString("o"),
                    Status = "valid",
                };

                // Fetch subtitles alongside live-resolved streams
                try
                {
                    var providers = ProviderHelper.GetProviders(config);
                    var subs = await AioStreamsClient.FetchSubtitlesAsync(
                        providers, aioId, mediaType, season, episode,
                        _logger, Plugin.Instance?.ResolverHealthTracker, CancellationToken.None).ConfigureAwait(false);
                    if (subs != null && subs.Count > 0)
                    {
                        var releaseName = candidates.FirstOrDefault()?.FileName ?? candidates.FirstOrDefault()?.StreamKey;
                        var scored = PreCacheAioStreamsTask.ScoreAndRankSubtitles(subs, releaseName, 10);
                        entry.SubtitlesJson = System.Text.Json.JsonSerializer.Serialize(scored);
                    }
                }
                catch (Exception subEx)
                {
                    _logger.LogDebug(subEx, "[AioMediaSourceProvider] Subtitle fetch failed for {AioId}", aioId);
                }

                await cacheService.StoreAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AioMediaSourceProvider] WriteToStreamCache failed for {AioId}", aioId);
            }
        }

        private static string? InferCodec(string? filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            var lower = filename.ToLowerInvariant();
            if (lower.Contains("x265") || lower.Contains("hevc")) return "hevc";
            if (lower.Contains("x264") || lower.Contains("h264") || lower.Contains("h.264")) return "h264";
            if (lower.Contains("av1")) return "av1";
            if (lower.Contains("xvid")) return "xvid";
            return null;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Config helpers
        // ═══════════════════════════════════════════════════════════════════════════

    }

    // ── Open token DTOs (shared between GetMediaSources and OpenMediaSource) ──

    /// <summary>
    /// Token for versioned multi-CDN playback. Carries primary + secondary URLs
    /// so OpenMediaSource can failover instantly without re-resolving.
    /// </summary>
    internal class VersionedOpenToken
    {
        public string PrimaryUrl { get; set; } = string.Empty;
        public string? SecondaryUrl { get; set; }
        public string AioId { get; set; } = string.Empty;
        public string StrmPath { get; set; } = string.Empty;
        public string VersionLabel { get; set; } = string.Empty;
        public string? StreamKey { get; set; }
    }

    internal class OpenTokenData
    {
        public string AioId { get; set; } = string.Empty;
        public string? MediaType { get; set; }
        public List<OpenTokenCandidate> Candidates { get; set; } = new();
    }

    internal class OpenTokenCandidate
    {
        public int Rank { get; set; }
        public string? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string ProviderKey { get; set; } = "unknown";
        public long? Size { get; set; }
        public string? StreamKey { get; set; }
        public string? InfoHash { get; set; }
        public int? FileIdx { get; set; }
        public string? FileName { get; set; }
    }
}
