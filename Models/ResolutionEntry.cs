using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// A pre-resolved stream URL row in the <c>resolution_cache</c> table.
    /// One row per (imdb_id, season, episode).  Movies use null season/episode.
    /// </summary>
    public class ResolutionEntry
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>IMDB ID.</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Season number; null for movies.</summary>
        public int? Season { get; set; }

        /// <summary>Episode number; null for movies.</summary>
        public int? Episode { get; set; }

        // ── Primary stream ──────────────────────────────────────────────────────

        /// <summary>Best-quality Real-Debrid download URL.</summary>
        public string StreamUrl { get; set; } = string.Empty;

        /// <summary>Quality tier: <c>remux</c>, <c>2160p</c>, <c>1080p</c>, <c>720p</c>, or <c>unknown</c>.</summary>
        public string? QualityTier { get; set; }

        /// <summary>Original filename from AIOStreams behaviorHints.</summary>
        public string? FileName { get; set; }

        /// <summary>File size in bytes (if provided by AIOStreams).</summary>
        public long? FileSize { get; set; }

        /// <summary>Estimated bitrate in kbps (computed from file size / runtime).</summary>
        public int? FileBitrateKbps { get; set; }

        // ── Fallbacks ───────────────────────────────────────────────────────────

        /// <summary>Next-tier-down fallback URL.</summary>
        public string? Fallback1 { get; set; }

        /// <summary>Quality tier of <see cref="Fallback1"/>.</summary>
        public string? Fallback1Quality { get; set; }

        /// <summary>Safe low-bitrate fallback URL (1080p or below).</summary>
        public string? Fallback2 { get; set; }

        /// <summary>Quality tier of <see cref="Fallback2"/>.</summary>
        public string? Fallback2Quality { get; set; }

        // ── Season pack ─────────────────────────────────────────────────────────

        /// <summary>
        /// Torrent hash that groups all episodes from the same season pack.
        /// Null when the episode is from a single-episode torrent.
        /// </summary>
        public string? TorrentHash { get; set; }

        /// <summary>1 = URL is confirmed cached at Real-Debrid; 0 = may need re-caching.</summary>
        public int RdCached { get; set; } = 1;

        // ── Resolution metadata ─────────────────────────────────────────────────

        /// <summary>Resolution tier that produced this entry.</summary>
        public string ResolutionTier { get; set; } = "tier3";

        /// <summary><c>valid</c>, <c>stale</c>, or <c>failed</c>.</summary>
        public string Status { get; set; } = "valid";

        /// <summary>UTC timestamp when this entry was resolved.</summary>
        public string ResolvedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>UTC timestamp after which the URL should be re-validated.</summary>
        public string ExpiresAt { get; set; } = string.Empty;

        // ── Usage tracking ──────────────────────────────────────────────────────

        /// <summary>Number of times this entry has been served to a client.</summary>
        public int PlayCount { get; set; } = 0;

        /// <summary>UTC timestamp of the most recent play.</summary>
        public string? LastPlayedAt { get; set; }

        /// <summary>Number of times resolution has been retried after failure.</summary>
        public int RetryCount { get; set; } = 0;
    }
}
