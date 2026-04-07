using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Normalized stream candidate for a specific title + slot combination.
    /// Stored in the <c>candidates</c> table.
    ///
    /// Unlike <see cref="StreamCandidate"/>, this model intentionally omits
    /// <c>StreamUrl</c>. Debrid URLs are reconstructed at play time from
    /// <see cref="InfoHash"/> + <see cref="FileIdx"/> via AIOStreams.
    /// </summary>
    public class Candidate
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Foreign key to media_items.id.</summary>
        public string MediaItemId { get; set; } = string.Empty;

        /// <summary>Slot key this candidate belongs to.</summary>
        public string SlotKey { get; set; } = string.Empty;

        /// <summary>Zero-based rank within the slot ladder. 0 = best.</summary>
        public int Rank { get; set; }

        // ── Provider identity ───────────────────────────────────────────────────

        /// <summary>Debrid service key (realdebrid, torbox, etc.).</summary>
        public string? Service { get; set; }

        /// <summary>Stream type: debrid, usenet, http, live.</summary>
        public string StreamType { get; set; } = "debrid";

        // ── Normalized technical metadata ───────────────────────────────────────

        /// <summary>Normalized resolution: 2160p, 1080p, 720p, etc.</summary>
        public string? Resolution { get; set; }

        /// <summary>Normalized video codec: h264, hevc, av1.</summary>
        public string? VideoCodec { get; set; }

        /// <summary>Normalized HDR class: dv, hdr10, hdr10_plus, or empty for SDR.</summary>
        public string? HdrClass { get; set; }

        /// <summary>Normalized audio codec: atmos, dd_plus, dd, aac.</summary>
        public string? AudioCodec { get; set; }

        /// <summary>Normalized audio channels: 7.1, 5.1, stereo.</summary>
        public string? AudioChannels { get; set; }

        // ── Source metadata ─────────────────────────────────────────────────────

        /// <summary>Original filename from AIOStreams.</summary>
        public string? FileName { get; set; }

        /// <summary>File size in bytes.</summary>
        public long? FileSize { get; set; }

        /// <summary>Bitrate in kbps.</summary>
        public int? BitrateKbps { get; set; }

        /// <summary>Language codes (comma-separated).</summary>
        public string? Languages { get; set; }

        /// <summary>Source type: remux, bluray, web, etc.</summary>
        public string? SourceType { get; set; }

        /// <summary>Whether the content is cached at the provider CDN.</summary>
        public bool IsCached { get; set; }

        // ── Stable identity (no debrid URL — hard constraint) ───────────────────

        /// <summary>
        /// SHA1 fingerprint for deduplication.
        /// Derived from stream identity fields, not CDN URL.
        /// </summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>Binge-group identifier from AIOStreams.</summary>
        public string? BingeGroup { get; set; }

        /// <summary>SHA1 info-hash of the source torrent.</summary>
        public string? InfoHash { get; set; }

        /// <summary>File index within a multi-file torrent.</summary>
        public int? FileIdx { get; set; }

        // ── Scoring ─────────────────────────────────────────────────────────────

        /// <summary>Confidence score (0.0–1.0) for how well this candidate matches the slot.</summary>
        public double ConfidenceScore { get; set; }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        /// <summary>UTC timestamp when this candidate was created.</summary>
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>UTC timestamp when this candidate expires.</summary>
        public string ExpiresAt { get; set; } = string.Empty;
    }
}
