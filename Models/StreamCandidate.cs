using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// One ranked, playable stream URL for a given (imdb_id, season, episode).
    ///
    /// Multiple candidates exist per item, stored in <c>stream_candidates</c> and
    /// ordered by <c>rank</c> (0 = best).  Rank is determined by quality tier first,
    /// then the user's configured <see cref="PluginConfiguration.ProviderPriorityOrder"/>
    /// within the same quality tier.
    ///
    /// Replaces the flat <c>fallback_1</c>/<c>fallback_2</c> columns on
    /// <see cref="ResolutionEntry"/> for all newly resolved items.
    /// </summary>
    public class StreamCandidate
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // ── Item identity ───────────────────────────────────────────────────────

        /// <summary>IMDB ID.</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Season number; null for movies.</summary>
        public int? Season { get; set; }

        /// <summary>Episode number; null for movies.</summary>
        public int? Episode { get; set; }

        // ── Ranking ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Zero-based rank within this item's candidate list.
        /// 0 = primary (best quality + highest priority provider).
        /// PlaybackService tries candidates in ascending rank order.
        /// </summary>
        public int Rank { get; set; }

        // ── Provider identity ───────────────────────────────────────────────────

        /// <summary>
        /// Provider service key as returned by AIOStreams <c>service.id</c>.
        /// Known values: <c>realdebrid</c>, <c>torbox</c>, <c>premiumize</c>,
        /// <c>alldebrid</c>, <c>debridlink</c>, <c>stremthru</c>,
        /// <c>nzbdav</c>, <c>altmount</c>, <c>easynews</c>, <c>unknown</c>.
        /// </summary>
        public string ProviderKey { get; set; } = "unknown";

        /// <summary>
        /// Stream type as classified by AIOStreams.
        /// Drives <see cref="Services.StreamTypePolicy"/> lookup for cache lifetime,
        /// HEAD-check behaviour, and header forwarding.
        /// Known values: <c>debrid</c>, <c>usenet</c>, <c>http</c>, <c>live</c>.
        /// </summary>
        public string StreamType { get; set; } = "debrid";

        // ── Stream URL ──────────────────────────────────────────────────────────

        /// <summary>Direct playable HTTP URL.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// JSON-serialised <c>Dictionary&lt;string,string&gt;</c> of HTTP headers
        /// required when proxying this stream (e.g. StremThru auth tokens).
        /// Null when no special headers are needed.
        /// </summary>
        public string? HeadersJson { get; set; }

        // ── Quality metadata ────────────────────────────────────────────────────

        /// <summary>Quality tier: <c>remux</c>, <c>2160p</c>, <c>1080p</c>, <c>720p</c>, or <c>unknown</c>.</summary>
        public string? QualityTier { get; set; }

        /// <summary>Original filename from AIOStreams <c>behaviorHints.filename</c>.</summary>
        public string? FileName { get; set; }

        /// <summary>File size in bytes.</summary>
        public long? FileSize { get; set; }

        /// <summary>Bitrate in kbps (from AIOStreams <c>bitrate</c> field, converted from bps).</summary>
        public int? BitrateKbps { get; set; }

        /// <summary>True when the content is already cached at the provider's CDN.</summary>
        public bool IsCached { get; set; } = true;

        // ── Torrent identity ────────────────────────────────────────────────────

        /// <summary>
        /// SHA1 info-hash of the source torrent (40-char hex), when available.
        /// Populated from <c>AioStreamsStream.InfoHash</c> for debrid streams.
        /// Null for usenet, HTTP, or streams where AIOStreams does not supply a hash.
        /// </summary>
        public string? InfoHash { get; set; }

        /// <summary>
        /// File index within the torrent archive.
        /// Required when the torrent is a season pack or multi-file bundle so the
        /// direct fallback path selects the correct episode file.
        /// Null for single-file torrents.
        /// </summary>
        public int? FileIdx { get; set; }

        // ── Stable identity ────────────────────────────────────────────────────

        /// <summary>
        /// Stable deduplication key derived from torrent identity or URL.
        /// <c>info_hash:file_idx</c> for debrid streams; raw URL otherwise.
        /// Survives CDN URL rotation — two rows with different <see cref="Url"/>
        /// but the same <c>StreamKey</c> represent the same underlying file.
        /// </summary>
        public string? StreamKey { get; set; }

        /// <summary>
        /// Binge-group identifier from AIOStreams.  When present, episodes sharing
        /// the same bingeGroup are likely from the same release source and can be
        /// pre-warmed together.
        /// </summary>
        public string? BingeGroup { get; set; }

        // ── Language ───────────────────────────────────────────────────────────

        /// <summary>
        /// Comma-separated ISO 639-1 audio language codes (e.g. "ja,en").
        /// Populated from AIOStreams <c>parsedFile.languages</c>.
        /// </summary>
        public string? Languages { get; set; }

        /// <summary>JSON-serialised list of subtitle tracks (url + lang).</summary>
        public string? SubtitlesJson { get; set; }

        /// <summary>Cached ffprobe JSON output for this stream's CDN URL.</summary>
        public string? ProbeJson { get; set; }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        /// <summary>UTC timestamp when this candidate was resolved.</summary>
        public string ResolvedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>UTC timestamp after which this URL should be re-validated.</summary>
        public string ExpiresAt { get; set; } = string.Empty;

        /// <summary>
        /// Candidate status.
        /// <list type="bullet">
        ///   <item><c>valid</c> — URL is believed good.</item>
        ///   <item><c>suspect</c> — HEAD check failed; try next rank before rescaping.</item>
        ///   <item><c>failed</c> — Confirmed dead; skip this rank.</item>
        /// </list>
        /// </summary>
        public string Status { get; set; } = "valid";
    }
}
