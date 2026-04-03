namespace EmbyStreams.Models
{
    /// <summary>
    /// Coverage summary: how many .strm catalog items have valid, stale, or
    /// no resolution cache entries.
    /// Returned by <see cref="Data.DatabaseManager.GetResolutionCoverageAsync"/>.
    /// </summary>
    public class ResolutionCoverageStats
    {
        /// <summary>Total active catalog items with <c>local_source='strm'</c>.</summary>
        public int TotalStrm { get; set; }

        /// <summary>Items with at least one unexpired valid cache entry.</summary>
        public int ValidCached { get; set; }

        /// <summary>Items that have cache entries but none are currently valid.</summary>
        public int StaleCached { get; set; }

        /// <summary>Items with no resolution cache entry at all.</summary>
        public int Uncached { get; set; }

        /// <summary>
        /// Percentage of .strm items that have a valid cached stream URL (0–100).
        /// </summary>
        public int CoveragePercent => TotalStrm > 0 ? ValidCached * 100 / TotalStrm : 0;
    }
}
