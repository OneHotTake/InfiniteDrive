using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Represents a quality slot definition for versioned playback.
    /// Stored in the <c>version_slots</c> table.
    /// </summary>
    public class VersionSlot
    {
        /// <summary>Primary key: e.g. "hd_broad", "4k_hdr".</summary>
        public string SlotKey { get; set; } = string.Empty;

        /// <summary>Human-readable label, e.g. "HD · Broad".</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>Target resolution: "1080p", "2160p", "720p", or "highest".</summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>Comma-separated video codec allowlist, or "any".</summary>
        public string VideoCodecs { get; set; } = "any";

        /// <summary>Comma-separated HDR class allowlist. Empty = SDR only, "any" = accept all.</summary>
        public string HdrClasses { get; set; } = string.Empty;

        /// <summary>Comma-separated audio codecs in preference order.</summary>
        public string AudioPreferences { get; set; } = string.Empty;

        /// <summary>Whether this slot is enabled for materialization.</summary>
        public bool Enabled { get; set; }

        /// <summary>Whether this slot is the default auto-play version.</summary>
        public bool IsDefault { get; set; }

        /// <summary>Display sort order.</summary>
        public int SortOrder { get; set; }

        /// <summary>UTC creation timestamp.</summary>
        public string CreatedAt { get; set; } = string.Empty;

        /// <summary>UTC last-update timestamp.</summary>
        public string UpdatedAt { get; set; } = string.Empty;

        // ── Derived helpers ─────────────────────────────────────────────────────

        /// <summary>Parsed video codec list from <see cref="VideoCodecs"/>.</summary>
        public List<string> VideoCodecList =>
            VideoCodecs == "any"
                ? new List<string>()
                : VideoCodecs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant()).ToList();

        /// <summary>Parsed HDR class list from <see cref="HdrClasses"/>.</summary>
        public List<string> HdrClassList =>
            HdrClasses == "any"
                ? new List<string> { "any" }
                : HdrClasses.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant()).ToList();

        /// <summary>Parsed audio preference list from <see cref="AudioPreferences"/>.</summary>
        public List<string> AudioPreferenceList =>
            AudioPreferences.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant()).ToList();

        /// <summary>
        /// File naming suffix derived from the label.
        /// "4K · HDR" → "4K HDR" (replace · with space, trim).
        /// </summary>
        public string FileSuffix => Label.Replace("·", " ").Trim();

        /// <summary>Whether this is the built-in HD Broad slot (permanent floor).</summary>
        public bool IsHdBroad => SlotKey == "hd_broad";

        /// <summary>Whether the video codec policy accepts all codecs.</summary>
        public bool AcceptsAnyCodec => VideoCodecs == "any";

        /// <summary>Whether the HDR policy accepts all HDR classes.</summary>
        public bool AcceptsAnyHdr => HdrClasses == "any";

        /// <summary>Whether the HDR policy is SDR-only (empty list).</summary>
        public bool IsSdrOnly => string.IsNullOrEmpty(HdrClasses) || HdrClasses == "";
    }
}
