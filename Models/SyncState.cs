namespace InfiniteDrive.Models
{
    /// <summary>
    /// Incremental sync cursor for a single catalog source.
    /// Stored in the <c>sync_state</c> table; prevents redundant re-fetches.
    /// </summary>
    public class SyncState
    {
        /// <summary>
        /// Source key (primary key).
        /// Format: <c>trakt:{username}</c>, <c>aiostreams</c>, etc.
        /// </summary>
        public string SourceKey { get; set; } = string.Empty;

        /// <summary>UTC timestamp of the last successful sync.</summary>
        public string? LastSyncAt { get; set; }

        /// <summary>HTTP ETag received on the last response (used for conditional GET).</summary>
        public string? LastEtag { get; set; }

        /// <summary>Pagination cursor for sources that support it.</summary>
        public string? LastCursor { get; set; }

        /// <summary>Number of items returned by the last sync.</summary>
        public int ItemCount { get; set; } = 0;

        /// <summary>Current source status: <c>ok</c>, <c>warn</c>, or <c>error</c>.</summary>
        public string Status { get; set; } = "ok";

        /// <summary>
        /// Number of consecutive failed sync attempts since the last success.
        /// Resets to 0 on any successful fetch.
        /// </summary>
        public int ConsecutiveFailures { get; set; } = 0;

        /// <summary>
        /// Human-readable message from the most recent failure, or null if none.
        /// Cleared on the next successful sync.
        /// </summary>
        public string? LastError { get; set; }

        /// <summary>
        /// UTC timestamp of the last time this source was successfully reached
        /// (manifest / endpoint responded 2xx), regardless of item count.
        /// Distinct from <see cref="LastSyncAt"/>, which is only updated when
        /// items were actually processed.
        /// </summary>
        public string? LastReachedAt { get; set; }

        /// <summary>
        /// Human-readable catalog name (e.g. "Netflix", "Popular").
        /// Null for provider-level rows (trakt, mdblist).
        /// </summary>
        public string? CatalogName { get; set; }

        /// <summary>Media type of this catalog (movie, series, Marvel, DC, etc.).</summary>
        public string? CatalogType { get; set; }

        /// <summary>Configured item limit for this catalog.  0 = not yet set.</summary>
        public int ItemsTarget { get; set; } = 0;

        /// <summary>
        /// Items fetched in the current (or most recent) sync run.
        /// Updated in real time during an active sync so the dashboard can
        /// show a live progress bar.
        /// </summary>
        public int ItemsRunning { get; set; } = 0;
    }
}
