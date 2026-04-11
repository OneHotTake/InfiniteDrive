using System;
using System.Collections.Generic;
using System.Linq;
using InfiniteDrive.Models;
using System.Text.Json;

namespace InfiniteDrive.Services
{
    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  STREAM TYPE POLICY                                                      ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Behaviour policy for a given AIOStreams stream type.
    ///
    /// Add a new entry to <see cref="Policies"/> when AIOStreams introduces a new
    /// stream type — no other code changes are needed.  Unknown types fall through
    /// to the <c>http</c> policy as a safe conservative default.
    /// </summary>
    public class StreamTypePolicy
    {
        /// <summary>How long (minutes) a resolved URL of this type remains valid before re-validation.</summary>
        public int CacheLifetimeMinutes { get; }

        /// <summary>Whether to send a HEAD request to verify the URL before serving.</summary>
        public bool SupportsHeadCheck { get; }

        /// <summary>
        /// Whether to forward custom HTTP headers stored in <c>headers_json</c>
        /// when proxying.  Required for StremThru / nzbDAV auth tokens.
        /// </summary>
        public bool ForwardHeaders { get; }

        /// <summary>
        /// True for live/broadcast streams that must never be cached.
        /// Live candidates are skipped during ranking.
        /// </summary>
        public bool IsLive { get; }

        private StreamTypePolicy(int cacheMinutes, bool headCheck, bool forwardHeaders, bool isLive)
        {
            CacheLifetimeMinutes = cacheMinutes;
            SupportsHeadCheck    = headCheck;
            ForwardHeaders       = forwardHeaders;
            IsLive               = isLive;
        }

        // ── Policy table ────────────────────────────────────────────────────────
        // Add a row here for each new AIOStreams stream type.  Do not change existing
        // entries without considering backward-compat effects on cached entries.

        private static readonly Dictionary<string, StreamTypePolicy> Policies =
            new Dictionary<string, StreamTypePolicy>(StringComparer.OrdinalIgnoreCase)
        {
            // Debrid CDN links (RD, TorBox, AllDebrid, Premiumize, etc.)
            // Expire in 6h by default; configured by CacheLifetimeMinutes.
            ["debrid"]  = new StreamTypePolicy(360,   headCheck: true,  forwardHeaders: false, isLive: false),

            // Usenet via TorBox Pro, StremThru, nzbDAV, AltMount, Easynews.
            // Longer expiry — assembled HTTP links are more stable than debrid CDN URLs.
            // Forward headers because StremThru / nzbDAV use auth tokens.
            ["usenet"]  = new StreamTypePolicy(1440,  headCheck: true,  forwardHeaders: true,  isLive: false),

            // Direct HTTP streams (custom addons, Easynews direct-play, etc.)
            ["http"]    = new StreamTypePolicy(60,    headCheck: true,  forwardHeaders: false, isLive: false),

            // Live / broadcast streams — never cached; no HEAD check useful.
            // IPTV/Xtream VOD will appear here when AIOStreams adds it.
            ["live"]    = new StreamTypePolicy(0,     headCheck: false, forwardHeaders: false, isLive: true),

            // Raw torrent infoHash streams — no direct URL; always skipped.
            ["torrent"] = new StreamTypePolicy(0,     headCheck: false, forwardHeaders: false, isLive: false),
        };

        /// <summary>
        /// Returns the policy for <paramref name="streamType"/>.
        /// Falls back to the <c>http</c> policy for unknown or null types.
        /// </summary>
        public static StreamTypePolicy Get(string? streamType)
        {
            if (!string.IsNullOrEmpty(streamType)
                && Policies.TryGetValue(streamType, out var policy))
                return policy;

            return Policies["http"]; // safe conservative default
        }
    }
    /// <summary>
    /// Static helpers shared across PlaybackService, LinkResolverTask, and StreamProxyService.
    /// </summary>
    public static class StreamHelpers
    {
        // ── Quality tier parsing ────────────────────────────────────────────────

        /// <summary>
        /// Infers a quality tier string from an AIOStreams filename.
        /// Priority order is defined in QUICKREF.md.
        /// </summary>
        /// <param name="filename">
        /// Original filename from <c>behaviorHints.filename</c>, e.g.
        /// <c>Movie.2021.2160p.REMUX.DTS-HD.mkv</c>.
        /// </param>
        /// <returns>
        /// One of: <c>remux</c>, <c>2160p</c>, <c>1080p</c>, <c>720p</c>, <c>unknown</c>.
        /// </returns>
        public static string ParseQualityTier(string? filename)
        {
            if (string.IsNullOrEmpty(filename))
                return "unknown";

            var f = filename;
            if (ContainsI(f, "remux"))                                            return "remux";
            if (ContainsI(f, "2160p") || ContainsI(f, "4K")
                                      || ContainsI(f, "UHD"))                     return "2160p";
            // 1440p is between 1080p and 4K; map to 1080p tier (no native tier exists)
            if (ContainsI(f, "1440p"))                                            return "1080p";
            if (ContainsI(f, "1080p"))                                            return "1080p";
            if (ContainsI(f, "720p"))                                             return "720p";
            // 480p and lower map to a dedicated tier ranked below 720p
            if (ContainsI(f, "480p") || ContainsI(f, "360p") || ContainsI(f, "240p")) return "480p";
            return "unknown";
        }

        /// <summary>
        /// Returns a secondary codec score used for tie-breaking within the same quality tier.
        /// Higher score = preferred (Dolby Vision &gt; HDR10+ &gt; HDR10 &gt; HLG &gt; HEVC &gt; H.264).
        /// </summary>
        public static int ParseCodecScore(string? filename)
        {
            if (string.IsNullOrEmpty(filename)) return 0;
            var f = filename;
            // Dolby Vision (DV) — highest codec preference
            if (ContainsI(f, "DV") || ContainsI(f, "DoVi") || ContainsI(f, "Dolby.Vision"))
                return 5;
            // HDR10+ — next best
            if (ContainsI(f, "HDR10+") || ContainsI(f, "HDR10Plus"))
                return 4;
            // HDR10 / generic HDR
            if (ContainsI(f, "HDR"))
                return 3;
            // HLG (broadcast HDR)
            if (ContainsI(f, "HLG"))
                return 2;
            // HEVC / x265 without HDR
            if (ContainsI(f, "HEVC") || ContainsI(f, "x265") || ContainsI(f, "h265"))
                return 1;
            return 0;
        }

        // ── Client type normalization ───────────────────────────────────────────

        /// <summary>
        /// Normalises the raw Emby client string from the <c>X-Emby-Client</c> header
        /// or User-Agent to one of the known client type tokens.
        /// </summary>
        /// <param name="raw">Raw header value, may be null or empty.</param>
        /// <returns>
        /// One of: <c>emby_atv</c>, <c>emby_android</c>, <c>infuse</c>,
        /// <c>emby_web</c>, <c>emby_ios</c>, <c>other</c>.
        /// </returns>
        public static string NormalizeClientType(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "other";

            var s = raw!.ToLowerInvariant();
            if (s.Contains("apple tv"))  return "emby_atv";
            if (s.Contains("android"))   return "emby_android";
            if (s.Contains("infuse"))    return "infuse";
            if (s.Contains("web"))       return "emby_web";
            if (s.Contains("ios"))       return "emby_ios";
            return "other";
        }

        // ── Expiry calculation ──────────────────────────────────────────────────

        /// <summary>
        /// Calculates the <c>expires_at</c> ISO-8601 string for a new resolution
        /// cache entry based on the configured cache lifetime.
        /// </summary>
        public static string CalculateExpiresAt(int cacheLifetimeMinutes)
            => DateTime.UtcNow.AddMinutes(cacheLifetimeMinutes).ToString("o");

        // ── Season pack detection ───────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when a filename appears to be from a season pack
        /// rather than a single-episode release.
        /// Heuristics: "S01" without following "E01", ".Season.", ".Complete."
        /// </summary>
        public static bool IsSeasonPack(string? filename)
        {
            if (string.IsNullOrEmpty(filename)) return false;
            var f = filename;
            if (ContainsI(f, ".Season.") || ContainsI(f, ".Complete.")) return true;

            // Multi-season patterns: S01S02, S01-S02, Season.01-02, Seasons.1-3
            if (System.Text.RegularExpressions.Regex.IsMatch(f,
                @"S\d{2}[-_]?S\d{2}|Season[s]?[._\s-]+\d{1,2}[-]\d{1,2}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;

            // S\d{2} not followed by E\d{2}
            var idx = f.IndexOf("S", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0 && idx + 2 < f.Length)
            {
                if (char.IsDigit(f[idx + 1]) && char.IsDigit(f[idx + 2]))
                {
                    var afterSeason = idx + 3;
                    if (afterSeason >= f.Length
                        || !char.ToUpperInvariant(f[afterSeason]).Equals('E'))
                        return true;
                }
                idx = f.IndexOf("S", idx + 1, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        // ── Bitrate estimation ─────────────────────────────────────────────────

        /// <summary>
        /// Returns a conservative estimated sustained bitrate in kbps for a
        /// given quality tier.  Used by the throughput-learning proxy to decide
        /// whether a client is struggling to keep up.
        /// </summary>
        public static int EstimateBitrateKbps(string? qualityTier) =>
            qualityTier switch
            {
                "remux"   => 40_000,  // ~40 Mbps Blu-ray remux
                "2160p"   => 20_000,  // ~20 Mbps 4K encode
                "1080p"   =>  8_000,  // ~8 Mbps 1080p encode
                "720p"    =>  4_000,  // ~4 Mbps 720p encode
                "480p"    =>  1_500,  // ~1.5 Mbps 480p encode
                _         =>      0,  // unknown — skip tracking
            };

        // ── Backoff ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Calculates the next back-off delay in milliseconds using exponential
        /// back-off with jitter.  Base: 1 second.  Max: 64 seconds.
        /// </summary>
        public static int ExponentialBackoffMs(int attempt)
        {
            var baseMs  = Math.Min(1000 * (int)Math.Pow(2, attempt), 64_000);
            var jitter  = new Random().Next(0, Math.Max(1, baseMs / 4));
            return baseMs + jitter;
        }

        // ── Stream ranking ──────────────────────────────────────────────────────

        /// <summary>
        /// Converts an AIOStreams stream response into a ranked list of
        /// <see cref="StreamCandidate"/> objects ready for storage in
        /// <c>stream_candidates</c>.
        ///
        /// Ranking rules (in order of precedence):
        /// <list type="number">
        ///   <item>Quality tier (remux &gt; 2160p &gt; 1080p &gt; 720p &gt; unknown)</item>
        ///   <item>Provider priority from <paramref name="providerPriorityOrder"/>
        ///         within the same quality tier</item>
        ///   <item>Original AIOStreams order (AIOStreams already sorted by quality
        ///         within each provider)</item>
        /// </list>
        ///
        /// Streams with no <c>url</c> field (raw NZB-only, magnet-only),
        /// live streams, and torrent-type streams are silently skipped.
        /// </summary>
        /// <param name="response">Raw AIOStreams stream response.</param>
        /// <param name="imdbId">IMDB ID for the returned candidates.</param>
        /// <param name="season">Season number; null for movies.</param>
        /// <param name="episode">Episode number; null for movies.</param>
        /// <param name="providerPriorityOrder">
        /// Comma-separated provider key list, e.g. <c>realdebrid,torbox,stremthru</c>.
        /// </param>
        /// <param name="candidatesPerProvider">
        /// Maximum candidates to store <b>per debrid provider</b>.
        /// With 3 providers and <c>candidatesPerProvider = 3</c> up to 9 total
        /// candidates are stored.  A cap of 0 means unlimited (use with caution).
        /// </param>
        /// <param name="debridCacheLifetimeMinutes">
        /// Cache lifetime to apply to <c>debrid</c>-type streams (from plugin config).
        /// Other types use their own policy values.
        /// </param>
        /// <returns>Ranked candidate list, rank 0 = best.</returns>
        public static List<StreamCandidate> RankAndFilterStreams(
            AioStreamsStreamResponse response,
            string imdbId,
            int?   season,
            int?   episode,
            string providerPriorityOrder,
            int    candidatesPerProvider,
            int    debridCacheLifetimeMinutes)
        {
            var streams = response.Streams ?? new List<AioStreamsStream>();

            // Build provider priority map once (provider key → rank index, lower = better)
            var priorityMap = BuildProviderPriorityMap(providerPriorityOrder);

            // Filter to streams that have a direct playable URL
            var playable = streams
                .Where(s => !string.IsNullOrEmpty(s.Url)
                         && s.StreamType != "torrent")
                .ToList();

            // Sort: quality tier descending, then codec score descending (HDR/DV),
            // then provider priority ascending, then preserve original AIOStreams position.
            playable = playable
                .Select((s, idx) => (Stream: s, OrigIdx: idx))
                .OrderByDescending(x => QualityRank(
                    ParseQualityTier(
                        !string.IsNullOrEmpty(x.Stream.ParsedFile?.Resolution)
                            ? x.Stream.ParsedFile!.Resolution
                            : x.Stream.BehaviorHints?.Filename)))
                .ThenByDescending(x => ParseCodecScore(x.Stream.BehaviorHints?.Filename))
                .ThenBy(x => ProviderPriorityOf(x.Stream, priorityMap))
                .ThenBy(x => x.OrigIdx)
                .Select(x => x.Stream)
                .ToList();

            var results        = new List<StreamCandidate>();
            int rank           = 0;
            // Per-provider bucket counts — key = provider key (e.g. "realdebrid")
            // Ensures we store up to candidatesPerProvider from EACH provider so that
            // if all Real-Debrid URLs expire, TorBox and Premiumize candidates remain.
            var providerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in playable)
            {
                var streamType = s.StreamType ?? "debrid";
                var policy     = StreamTypePolicy.Get(streamType);

                // Skip uncacheable stream types at ranking time
                if (policy.IsLive) continue;

                // Always normalize through ParseQualityTier so that AIOStreams
                // parsedFile.Resolution values like "4K" map to our internal
                // tier keys ("2160p") rather than hitting the QualityRank fallthrough.
                var quality  = ParseQualityTier(
                    !string.IsNullOrEmpty(s.ParsedFile?.Resolution)
                        ? s.ParsedFile!.Resolution
                        : s.BehaviorHints?.Filename);
                var provider = ParseProviderKey(s);

                // Per-provider cap: skip if this provider's bucket is full.
                // candidatesPerProvider <= 0 means unlimited.
                if (candidatesPerProvider > 0)
                {
                    providerCounts.TryGetValue(provider, out var providerCount);
                    if (providerCount >= candidatesPerProvider) continue;
                    providerCounts[provider] = providerCount + 1;
                }

                // Cache lifetime: debrid uses config value; others use policy
                int cacheMinutes = string.Equals(streamType, "debrid",
                    StringComparison.OrdinalIgnoreCase)
                    ? debridCacheLifetimeMinutes
                    : policy.CacheLifetimeMinutes;

                // Merge headers from both locations AIOStreams may use
                var headers = s.BehaviorHints?.Headers ?? s.Headers;
                string? headersJson = (headers != null && headers.Count > 0)
                    ? JsonSerializer.Serialize(headers)
                    : null;

                // StreamKey: stable dedup key that survives CDN URL rotation
                var streamKey = !string.IsNullOrEmpty(s.InfoHash) && s.FileIdx.HasValue
                    ? $"{s.InfoHash}:{s.FileIdx}"
                    : s.Url;

                results.Add(new StreamCandidate
                {
                    ImdbId      = imdbId,
                    Season      = season,
                    Episode     = episode,
                    Rank        = rank,
                    ProviderKey = provider,
                    StreamType  = streamType,
                    Url         = s.Url!,
                    HeadersJson = headersJson,
                    QualityTier = quality,
                    FileName    = s.BehaviorHints?.Filename,
                    FileSize    = s.BehaviorHints?.VideoSize ?? s.Size,
                    BitrateKbps = s.Bitrate.HasValue ? (int)(s.Bitrate.Value / 1000) : null,
                    IsCached    = s.Service?.Cached ?? true,
                    // Store torrent identity for the direct debrid fallback path (Sprint 14).
                    // Present for debrid streams; null for usenet/HTTP.
                    InfoHash    = string.IsNullOrEmpty(s.InfoHash) ? null : s.InfoHash,
                    FileIdx     = s.FileIdx,
                    StreamKey   = streamKey,
                    BingeGroup  = s.BingeGroup ?? s.BehaviorHints?.BingeGroup,
                    ResolvedAt  = DateTime.UtcNow.ToString("o"),
                    ExpiresAt   = DateTime.UtcNow.AddMinutes(cacheMinutes).ToString("o"),
                    Status      = "valid",
                });

                rank++;
            }

            return results;
        }

        /// <summary>
        /// Extracts the provider key from a stream, preferring
        /// <c>service.id</c> then falling back to <c>StreamType</c>.
        /// </summary>
        public static string ParseProviderKey(AioStreamsStream stream)
        {
            var serviceId = stream.Service?.Id;
            if (!string.IsNullOrEmpty(serviceId))
                return serviceId!.ToLowerInvariant();

            var streamType = stream.StreamType;
            return string.IsNullOrEmpty(streamType)
                ? "unknown"
                : streamType!.ToLowerInvariant();
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private static int QualityRank(string? tier) =>
            tier switch
            {
                "remux"   => 6,
                "2160p"   => 5,
                "1080p"   => 4,
                "720p"    => 3,
                "480p"    => 2,
                "unknown" => 1,
                _         => 0,
            };

        private static int ProviderPriorityOf(
            AioStreamsStream stream, Dictionary<string, int> priorityMap)
        {
            var provider = ParseProviderKey(stream);
            if (priorityMap.TryGetValue(provider, out var p)) return p;

            // Also try stream type as a coarser fallback key (e.g. "usenet")
            if (!string.IsNullOrEmpty(stream.StreamType)
                && priorityMap.TryGetValue(stream.StreamType, out var pt)) return pt;

            return int.MaxValue; // unconfigured providers rank last
        }

        private static Dictionary<string, int> BuildProviderPriorityMap(string priorityOrder)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(priorityOrder)) return map;

            var parts = priorityOrder.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                var key = parts[i].Trim();
                if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
                    map[key] = i;
            }
            return map;
        }

        private static bool ContainsI(string s, string value)
            => s.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
