namespace InfiniteDrive.Models
{
    /// <summary>
    /// A fully parsed, scored stream ready for version selection.
    /// Extracted from raw AioStreamsStream by StreamParser.
    /// </summary>
    public class ParsedStream
    {
        /// <summary>Normalised resolution: "4K", "1080p", "720p", "SD", or "Unknown".</summary>
        public string Resolution { get; set; } = "Unknown";

        /// <summary>
        /// Audio group classification for bucket matching:
        /// "Lossless/Premium", "5.1/7.1 (Surround)", "DD/DTS (Compressed)", "Stereo/2.0", "Any".
        /// </summary>
        public string AudioGroup { get; set; } = "Any";

        /// <summary>Human-readable audio label, e.g. "5.1 DD+ Atmos", "DTS-HD MA 7.1".</summary>
        public string AudioPretty { get; set; } = "Unknown Audio";

        /// <summary>Source type tag: "BluRay Remux", "BluRay", "WEB-DL", "WEBRip", etc.</summary>
        public string SourceTag { get; set; } = "Unknown";

        /// <summary>File size in GiB.</summary>
        public double SizeGiB { get; set; }

        /// <summary>Direct durable CDN URL from the debrid provider.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Composite rank score from StreamHelpers.TierScore + audio + source.</summary>
        public int RankScore { get; set; }

        /// <summary>Original raw stream reference for metadata recovery.</summary>
        public string? RawStreamJson { get; set; }

        /// <summary>StreamKey for dedup (infoHash:fileIdx or URL).</summary>
        public string? StreamKey { get; set; }
    }

    /// <summary>
    /// A version selected by VersionSelectorService, ready for .strm file writing.
    /// </summary>
    public class SelectedVersion
    {
        /// <summary>Parsed stream that was selected.</summary>
        public ParsedStream Stream { get; set; } = new();

        /// <summary>
        /// Pre-cached secondary CDN URL for instant failover when primary dies.
        /// Assigned by <see cref="VersionSelectorService.AssignSecondaryUrls"/>.
        /// </summary>
        public string? SecondaryUrl { get; set; }

        /// <summary>
        /// Emby-compliant version filename (without .strm extension).
        /// Format: "{Resolution} - {AudioPretty} - {SizeGiB:F1}GiB"
        /// </summary>
        public string VersionLabel { get; set; } = string.Empty;

        /// <summary>Score at time of selection (for later comparison/upgrade).</summary>
        public int SelectedScore { get; set; }

        /// <summary>Which bucket matched this stream (empty for Phase 2 fill).</summary>
        public string MatchedBucket { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lightweight DTO for serialising selected versions to the database.
    /// Does not carry the full ParsedStream — only what's needed for comparison
    /// and reconstruction during Marvin version refresh cycles.
    /// </summary>
    public class StoredVersion
    {
        public string Url { get; set; } = string.Empty;
        public string? SecondaryUrl { get; set; }
        public string Resolution { get; set; } = "Unknown";
        public string AudioPretty { get; set; } = "Unknown Audio";
        public string SourceTag { get; set; } = "Unknown";
        public double SizeGiB { get; set; }
        public int RankScore { get; set; }
        public string VersionLabel { get; set; } = string.Empty;
        public string? StreamKey { get; set; }
    }
}
