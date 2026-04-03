namespace EmbyStreams.Models
{
    /// <summary>
    /// Snapshot of <c>resolution_cache</c> row counts returned by
    /// <see cref="Data.DatabaseManager.GetResolutionCacheStatsAsync"/>.
    /// </summary>
    public class ResolutionCacheStats
    {
        /// <summary>Total rows in the resolution cache.</summary>
        public int Total { get; set; }

        /// <summary>Rows with <c>status='valid'</c> and <c>expires_at</c> in the future.</summary>
        public int ValidUnexpired { get; set; }

        /// <summary>Rows that are stale or expired.</summary>
        public int Stale { get; set; }

        /// <summary>Rows with <c>status='failed'</c>.</summary>
        public int Failed { get; set; }
    }
}
