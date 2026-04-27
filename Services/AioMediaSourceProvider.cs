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
using InfiniteDrive.Services.Scoring;
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
    public class AioMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<AioMediaSourceProvider> _logger;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ILibraryManager _libraryManager;

        // In-memory cache: key = "imdbId" or "imdbId:S{season}E{episode}", value = (sources, expiry)
        private static readonly ConcurrentDictionary<string, (List<MediaSourceInfo> Sources, DateTime Expires)> _cache
            = new();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

        // Single-flight lock: prevents duplicate AIOStreams fetches for the same item
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

        private static readonly HttpClient _headClient = new()
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        public AioMediaSourceProvider(
            ILogManager logManager,
            IMediaSourceManager mediaSourceManager,
            ILibraryManager libraryManager)
        {
            _logger = new EmbyLoggerAdapter<AioMediaSourceProvider>(logManager.GetLogger("InfiniteDrive"));
            _mediaSourceManager = mediaSourceManager;
            _libraryManager = libraryManager;
        }

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

        public async Task<ILiveStream> OpenMediaSource(
            string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            OpenTokenData? tokenData;
            try
            {
                tokenData = JsonSerializer.Deserialize<OpenTokenData>(openToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AioMediaSourceProvider] Invalid open token");
                throw new InvalidOperationException("Invalid open token");
            }

            if (tokenData?.Candidates == null || tokenData.Candidates.Count == 0)
                throw new InvalidOperationException("No candidates in open token");

            // Try candidates in rank order until one works
            for (var i = 0; i < tokenData.Candidates.Count; i++)
            {
                var cand = tokenData.Candidates[i];
                if (string.IsNullOrEmpty(cand.Url)) continue;

                try
                {
                    // Quick HEAD check to verify CDN URL is reachable
                    using var req = new HttpRequestMessage(HttpMethod.Head, cand.Url);
                    if (cand.Headers != null)
                    {
                        foreach (var h in cand.Headers)
                            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }

                    using var resp = await _headClient.SendAsync(req, cancellationToken).ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "[AioMediaSourceProvider] CDN HEAD failed for rank {Rank} (HTTP {Status}), trying next",
                            cand.Rank, (int)resp.StatusCode);
                        continue;
                    }

                    // Build the MediaSourceInfo for this candidate
                    var source = BuildSourceFromCandidate(cand, tokenData.ImdbId);

                    var liveStream = new InfiniteDriveLiveStream(source, _logger);
                    await liveStream.Open(cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "[AioMediaSourceProvider] Opened rank {Rank} for {ImdbId} via {Provider}",
                        cand.Rank, tokenData.ImdbId, cand.ProviderKey);

                    return liveStream;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[AioMediaSourceProvider] Candidate rank {Rank} failed, trying next", cand.Rank);
                }
            }

            throw new InvalidOperationException("All candidates failed HEAD check");
        }

        private async Task<List<MediaSourceInfo>> GetMediaSourcesCoreAsync(BaseItem item, CancellationToken ct)
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

            // Single-flight: ensure only one in-flight resolve per cache key
            var slim = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await slim.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check cache after acquiring lock
                if (_cache.TryGetValue(cacheKey, out cached) && cached.Expires > DateTime.UtcNow)
                    return cached.Sources;

                return await ResolveWithCacheAsync(cacheKey, imdbId, mediaType, season, episode, config, ct).ConfigureAwait(false);
            }
            finally
            {
                slim.Release();
            }
        }

        private async Task<List<MediaSourceInfo>> ResolveWithCacheAsync(
            string cacheKey, string imdbId, string mediaType, int? season, int? episode,
            PluginConfiguration config, CancellationToken ct)
        {
            var db = Plugin.Instance?.DatabaseManager;

            // DB cache check
            if (db != null)
            {
                try
                {
                    var dbCandidates = await db.GetStreamCandidatesAsync(imdbId, season, episode).ConfigureAwait(false);

                    if (dbCandidates?.Count > 0)
                    {
                        // Apply scoring to DB candidates
                        var scoringService = new StreamScoringService(_logger, config);
                        var scored = scoringService.SelectBest(
                            dbCandidates.Where(c => c.Status == "valid" && !string.IsNullOrEmpty(c.Url)).ToList());

                        if (scored.Count > 0)
                        {
                            var sources = BuildSourcesFromCandidates(scored, imdbId);

                            SortByLanguagePreference(sources, config, null);
                            SetOpenTokens(sources, imdbId);
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

            // Live resolve: fetch from AIOStreams, rank, dedup, cap
            var candidates = ResolveFromAioStreams(imdbId, mediaType, season, episode, config);
            if (candidates.Count == 0) return new List<MediaSourceInfo>();

            var liveSources = BuildSourcesFromCandidates(candidates, imdbId);

            if (liveSources.Count > 0)
            {
                SortByLanguagePreference(liveSources, config, null);
                SetOpenTokens(liveSources, imdbId);
                _cache[cacheKey] = (liveSources, DateTime.UtcNow.Add(CacheTtl));

                // Cache candidates to DB (async, non-blocking)
                CacheCandidatesToDb(db, imdbId, season, episode, candidates);

                // Fire-and-forget background probe for top candidates
                _ = BackgroundProbeAsync(candidates.Take(3).ToList());

                // Binge prefetch for series
                if (season.HasValue && episode.HasValue)
                {
                    _ = BingePrefetchService.PrefetchNextEpisodeAsync(
                        imdbId, season.Value, episode.Value, _logger);
                }
            }

            return liveSources;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Live resolve: AIOStreams → RankAndFilterStreams → tier dedup → cap
        // ═══════════════════════════════════════════════════════════════════════════

        private List<StreamCandidate> ResolveFromAioStreams(
            string imdbId, string mediaType, int? season, int? episode, PluginConfiguration config)
        {
            var providers = GetProviders(config);
            if (providers.Count == 0) return new List<StreamCandidate>();

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

                    // Run through RankAndFilterStreams (existing scoring logic)
                    var ranked = StreamHelpers.RankAndFilterStreams(
                        response!, imdbId, season, episode,
                        config.ProviderPriorityOrder ?? "",
                        config.CandidatesPerProvider > 0 ? config.CandidatesPerProvider : 5,
                        config.CacheLifetimeMinutes > 0 ? config.CacheLifetimeMinutes : 360);

                    // Bucket-based scoring
                    var scoringService = new StreamScoringService(_logger, config);
                    return scoringService.SelectBest(ranked);
                }
                catch (Exception ex)
                {
                    if (healthTracker != null) healthTracker.RecordFailure(provider.DisplayName);
                    _logger.LogError(ex, "[AioMediaSourceProvider] {Name} failed for {Id}", provider.DisplayName, imdbId);
                }
            }

            return new List<StreamCandidate>();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Source building (unified for both live and DB paths)
        // ═══════════════════════════════════════════════════════════════════════════

        private List<MediaSourceInfo> BuildSourcesFromCandidates(
            IEnumerable<StreamCandidate> candidates, string imdbId)
        {
            return candidates
                .Select(c => MapCandidateToSource(c))
                .Where(s => s != null)
                .Cast<MediaSourceInfo>()
                .ToList();
        }

        private MediaSourceInfo? MapCandidateToSource(StreamCandidate candidate)
        {
            if (string.IsNullOrEmpty(candidate.Url)) return null;

            var config = Plugin.Instance?.Configuration;
            var prefix = config?.VersionLabelPrefix ?? "";
            var name = prefix + BuildCandidateSourceName(candidate);
            var source = new MediaSourceInfo
            {
                Id = candidate.Id ?? Guid.NewGuid().ToString("N"),
                Name = name,
                Path = candidate.Url,
                Protocol = MediaProtocol.Http,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsInfiniteStream = false,
                RequiresOpening = config?.UseRequiresOpening ?? false,
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

            // Build MediaStreams from stored languages + subtitles
            var streams = BuildMediaStreamsFromLanguages(
                candidate.Languages, candidate.QualityTier, candidate.BitrateKbps);
            AppendSubtitlesFromJson(streams, candidate.SubtitlesJson);

            // Use cached probe data if available (replaces synthetic streams)
            if (!string.IsNullOrEmpty(candidate.ProbeJson))
            {
                try
                {
                    var probed = DeserializeProbeStreams(candidate.ProbeJson);
                    if (probed != null && probed.Count > 1)
                        streams = probed;
                }
                catch { /* fallback to synthetic */ }
            }

            source.MediaStreams = streams;
            return source;
        }

        private static string BuildCandidateSourceName(StreamCandidate c)
        {
            // Resolution (derive from QualityTier)
            var res = (c.QualityTier ?? "") switch
            {
                "remux" => GetResolutionFromFilename(c.FileName) ?? "4K",
                "2160p" => "4K",
                "1080p" => "1080p",
                "720p"  => "720p",
                "480p"  => "480p",
                _ => GetResolutionFromFilename(c.FileName) ?? c.QualityTier ?? ""
            };

            // Source type (derive from QualityTier and filename)
            var src = GetSourceTypeFromQualityTier(c.QualityTier, c.FileName);

            // Audio label: extract from filename
            var audioLabel = BuildAudioLabelFromFilename(c.FileName);

            // File size
            var sizeLabel = "";
            if (c.FileSize.HasValue && c.FileSize.Value > 0)
            {
                var gb = c.FileSize.Value / (1024.0 * 1024.0 * 1024.0);
                sizeLabel = gb >= 1 ? $"{gb:F0} GiB" : $"{c.FileSize.Value / (1024.0 * 1024.0):F0} MiB";
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(res))       parts.Add(res);
            if (!string.IsNullOrEmpty(src))       parts.Add(src);
            if (!string.IsNullOrEmpty(audioLabel))parts.Add(audioLabel);
            if (!string.IsNullOrEmpty(sizeLabel)) parts.Add(sizeLabel);

            return parts.Count > 0 ? string.Join(" · ", parts) : "Stream";
        }

        private static string? GetResolutionFromFilename(string? filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            var fn = filename.ToUpperInvariant();
            if (fn.Contains("2160") || fn.Contains("4K")) return "4K";
            if (fn.Contains("1080")) return "1080p";
            if (fn.Contains("720"))  return "720p";
            if (fn.Contains("480"))  return "480p";
            return null;
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
            if (fn.Contains("WEBDL") || fn.Contains("WEB-DL"))return "WEB-DL";
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
        private void SetOpenTokens(List<MediaSourceInfo> sources, string imdbId)
        {
            // Build a shared token with ALL candidates, attach to each source
            var candidates = sources.Select((s, i) => new OpenTokenCandidate
            {
                Rank = i,
                Url = s.Path,
                Headers = s.RequiredHttpHeaders,
                ProviderKey = ExtractProviderFromName(s.Name),
                Size = s.Size,
            }).ToList();

            var tokenJson = JsonSerializer.Serialize(new OpenTokenData
            {
                ImdbId = imdbId,
                Candidates = candidates,
            });

            foreach (var source in sources)
                source.OpenToken = tokenJson;
        }

        private MediaSourceInfo BuildSourceFromCandidate(OpenTokenCandidate cand, string imdbId)
        {
            var config = Plugin.Instance?.Configuration;
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
                RequiresOpening = config?.UseRequiresOpening ?? false,
                IsInfiniteStream = false,
            };

            if (cand.Size.HasValue) source.Size = cand.Size.Value;
            if (cand.Headers != null) source.RequiredHttpHeaders = cand.Headers;

            return source;
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
        //  Background probing (fire-and-forget after PlaybackInfo)
        // ═══════════════════════════════════════════════════════════════════════════

        private async Task BackgroundProbeAsync(List<StreamCandidate> candidates)
        {
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return;

            foreach (var cand in candidates)
            {
                try
                {
                    if (string.IsNullOrEmpty(cand.Url) || string.IsNullOrEmpty(cand.StreamKey)) continue;

                    var probeStreams = await CdnProber.ProbeAsync(cand.Url, _logger, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (probeStreams != null && probeStreams.Count > 1)
                    {
                        var probeJson = JsonSerializer.Serialize(
                            probeStreams.Select(ms => new
                            {
                                type = ms.Type.ToString(),
                                codec = ms.Codec,
                                language = ms.Language,
                                title = ms.Title,
                                channels = ms.Channels,
                                channelLayout = ms.ChannelLayout,
                                width = ms.Width,
                                height = ms.Height,
                                bitRate = ms.BitRate,
                                isDefault = ms.IsDefault,
                            }));

                        await db.SaveProbeJsonAsync(cand.StreamKey, probeJson).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AioMediaSourceProvider] Background probe failed for {Url} (non-fatal)",
                        cand.Url?[..Math.Min(cand.Url.Length, 60)]);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  DB caching
        // ═══════════════════════════════════════════════════════════════════════════

        private void CacheCandidatesToDb(
            Data.DatabaseManager? db, string imdbId, int? season, int? episode,
            List<StreamCandidate> candidates)
        {
            if (db == null || candidates.Count == 0) return;

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
                        StreamUrl = candidates[0].Url,
                        QualityTier = "all",
                        FileName = null,
                        Status = "valid",
                        ResolvedAt = now.ToString("o"),
                        ExpiresAt = now.Add(CacheTtl).ToString("o"),
                        ResolutionTier = "media_source_provider"
                    };

                    await db.UpsertResolutionResultAsync(entry, candidates);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AioMediaSourceProvider] DB cache write failed for {Id} (non-fatal)", imdbId);
                }
            });
        }

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

            // 4. Kitsu/AniList/MAL provider IDs (anime without IMDB)
            if (string.IsNullOrEmpty(imdbId) && item.ProviderIds != null)
            {
                foreach (var kvp in item.ProviderIds)
                {
                    if (string.Equals(kvp.Key, "Kitsu", StringComparison.OrdinalIgnoreCase))
                        imdbId = $"kitsu:{kvp.Value}";
                    else if (string.Equals(kvp.Key, "AniList", StringComparison.OrdinalIgnoreCase))
                        imdbId = $"anilist:{kvp.Value}";
                    else if (string.Equals(kvp.Key, "MAL", StringComparison.OrdinalIgnoreCase))
                        imdbId = $"mal:{kvp.Value}";
                    if (!string.IsNullOrEmpty(imdbId)) break;
                }
            }

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

        private static bool HasLanguageMatch(MediaSourceInfo source, string lang)
        {
            if (source.MediaStreams == null) return false;
            return source.MediaStreams.Any(ms =>
                ms.Type == MediaStreamType.Audio &&
                !string.IsNullOrEmpty(ms.Language) &&
                (string.Equals(ms.Language, lang, StringComparison.OrdinalIgnoreCase) ||
                 ms.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase)));
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  MediaStreams builders (unchanged helpers)
        // ═══════════════════════════════════════════════════════════════════════════

        private static List<MediaStream> BuildMediaStreamsFromLanguages(string? languages,
            string? qualityTier = null, int? bitrateKbps = null)
        {
            var streams = new List<MediaStream>();

            streams.Add(new MediaStream
            {
                Type      = MediaStreamType.Video,
                Index     = 0,
                Codec     = qualityTier != null && (qualityTier.Contains("remux") || qualityTier.Contains("2160") || qualityTier.Contains("4k"))
                                ? "hevc" : "h264",
                Language  = "und",
                IsDefault = true,
            });
            if (bitrateKbps.HasValue)
                streams[0].BitRate = bitrateKbps.Value;

            if (string.IsNullOrEmpty(languages)) return streams;

            foreach (var lang in languages.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = lang.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                streams.Add(new MediaStream
                {
                    Type         = MediaStreamType.Audio,
                    Language     = trimmed,
                    Title        = trimmed,
                    DisplayTitle = trimmed,
                    IsDefault    = streams.Count == 1,
                    Index        = streams.Count,
                });
            }

            return streams;
        }

        private static List<MediaStream>? DeserializeProbeStreams(string probeJson)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(probeJson);
            var result = new List<MediaStream>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var typeStr = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (typeStr == null) continue;

                var ms = new MediaStream
                {
                    Type = Enum.TryParse<MediaStreamType>(typeStr, out var mst) ? mst : MediaStreamType.Video,
                    Codec = el.TryGetProperty("codec", out var c) ? c.GetString() : null,
                    Language = el.TryGetProperty("language", out var l) ? l.GetString() : null,
                    Title = el.TryGetProperty("title", out var ti) ? ti.GetString() : null,
                    DisplayTitle = el.TryGetProperty("title", out var dt) ? dt.GetString() : null,
                    Channels = el.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var chVal) ? chVal : 0,
                    ChannelLayout = el.TryGetProperty("channelLayout", out var cl) ? cl.GetString() : null,
                    Width = el.TryGetProperty("width", out var w) && w.TryGetInt32(out var wVal) ? wVal : 0,
                    Height = el.TryGetProperty("height", out var h) && h.TryGetInt32(out var hVal) ? hVal : 0,
                    BitRate = el.TryGetProperty("bitRate", out var br) && br.TryGetInt32(out var brVal) ? brVal : 0,
                    IsDefault = el.TryGetProperty("isDefault", out var d) && d.GetBoolean(),
                    Index = result.Count,
                };

                if (ms.Type == MediaStreamType.Subtitle)
                    ms.IsExternal = false;

                result.Add(ms);
            }
            return result.Count > 0 ? result : null;
        }

        private static void AppendSubtitlesFromJson(List<MediaStream> streams, string? subtitlesJson)
        {
            if (string.IsNullOrEmpty(subtitlesJson)) return;
            try
            {
                var subs = JsonSerializer.Deserialize<List<AioStreamsSubtitle>>(subtitlesJson);
                if (subs == null) return;
                foreach (var sub in subs)
                {
                    if (string.IsNullOrEmpty(sub.Url)) continue;
                    streams.Add(new MediaStream
                    {
                        Type               = MediaStreamType.Subtitle,
                        Language           = sub.Lang ?? "und",
                        Title              = sub.Lang ?? "und",
                        DisplayTitle       = sub.Lang ?? "und",
                        IsExternal         = true,
                        Codec              = InferSubtitleCodec(sub.Url),
                        DeliveryUrl        = sub.Url,
                        Path               = sub.Url,
                        DeliveryMethod     = SubtitleDeliveryMethod.External,
                        SupportsExternalStream = true,
                        IsDefault          = false,
                        Index              = streams.Count,
                    });
                }
            }
            catch { /* non-fatal */ }
        }

        private static string InferSubtitleCodec(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "srt";
            if (url.Contains(".vtt", StringComparison.OrdinalIgnoreCase)) return "vtt";
            if (url.Contains(".ass", StringComparison.OrdinalIgnoreCase) || url.Contains(".ssa", StringComparison.OrdinalIgnoreCase)) return "ass";
            return "srt";
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Config helpers
        // ═══════════════════════════════════════════════════════════════════════════

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

        // ═══════════════════════════════════════════════════════════════════════════
        //  Inner types
        // ═══════════════════════════════════════════════════════════════════════════

        private class ProviderInfo
        {
            public string DisplayName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Uuid { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
        }
    }

    // ── Open token DTOs (shared between GetMediaSources and OpenMediaSource) ──

    internal class OpenTokenData
    {
        public string ImdbId { get; set; } = string.Empty;
        public List<OpenTokenCandidate> Candidates { get; set; } = new();
    }

    internal class OpenTokenCandidate
    {
        public int Rank { get; set; }
        public string? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string ProviderKey { get; set; } = "unknown";
        public long? Size { get; set; }
    }
}
