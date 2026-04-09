namespace EmbyStreams.Models
{
    /// <summary>
    /// Tracks per-source watermark and timing for incremental polling.
    /// Used by Library Worker to support delta pulls.
    /// </summary>
    public class IngestionState
    {
        /// <summary>Source identifier (e.g., "torrentio", "mediafusion").</summary>
        public string SourceId { get; set; } = string.Empty;

        /// <summary>UTC timestamp of last poll attempt (ISO8601).</summary>
        public string LastPollAt { get; set; } = string.Empty;

        /// <summary>UTC timestamp of last successful item discovery (ISO8601).</summary>
        public string LastFoundAt { get; set; } = string.Empty;

        /// <summary>Cursor / ETag / last item ID for delta pulls.</summary>
        public string Watermark { get; set; } = string.Empty;
    }
}
