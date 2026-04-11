using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Represents a single media item in the InfiniteDrive catalog.
    /// Maps to the <c>catalog_items</c> SQLite table.
    /// </summary>
    public class CatalogItem
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Canonical primary ID. May be a tt-prefixed IMDb ID when resolved,
        /// or a native provider ID (e.g., tmdb_260192) when resolution failed.
        /// Used as the deduplication key despite the column name.
        /// Updated in Sprint 160 to clarify it's not always an IMDb ID.
        /// </summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>TMDB numeric ID as string, or null if unknown.</summary>
        public string? TmdbId { get; set; }

        /// <summary>
        /// JSON array of provider IDs for multi-provider lookup.
        /// Format: <c>[{"provider":"imdb","id":"tt1160419"},{"provider":"kitsu","id":"48363"}]</c>.
        /// Null for existing items (migrated via sprint), populated for new items.
        /// Enables fallback episode count queries when IMDB ID is unavailable in Emby library.
        /// </summary>
        public string? UniqueIdsJson { get; set; }

        /// <summary>Display title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Release year (or first-air year for series).</summary>
        public int? Year { get; set; }

        /// <summary><c>movie</c> or <c>series</c>.</summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>Source identifier: <c>trakt</c>, <c>aiostreams</c>, or <c>mdblist</c>.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Trakt username, MDBList list ID, etc. Nullable.</summary>
        public string? SourceListId { get; set; }

        /// <summary>
        /// JSON-encoded season/episode map: <c>[{"season":1,"episodes":[1,2,3]}]</c>.
        /// Null for movies or when episode data is not yet known.
        /// </summary>
        public string? SeasonsJson { get; set; }

        /// <summary>Absolute path to the primary .strm file on disk.</summary>
        public string? StrmPath { get; set; }

        /// <summary>
        /// Absolute path to the item as it currently exists on this Emby server.
        /// Set to the real media file path when the item was found in an existing
        /// library (<see cref="LocalSource"/> = <c>library</c>), or to the .strm
        /// path when the plugin wrote it (<see cref="LocalSource"/> = <c>strm</c>).
        /// Null until the first sync run that processes this item.
        /// </summary>
        public string? LocalPath { get; set; }

        /// <summary>
        /// Describes the origin of <see cref="LocalPath"/>:
        /// <list type="bullet">
        ///   <item><c>library</c> — item exists in the user's Emby library as a real file.
        ///         The plugin will not create a .strm for it unless that file disappears
        ///         (see Sprint 3 File Resurrection).</item>
        ///   <item><c>strm</c> — item is managed by this plugin as a .strm file.</item>
        /// </list>
        /// Null until the first sync run resolves it.
        /// </summary>
        public string? LocalSource { get; set; }

        /// <summary>UTC timestamp when the item was first seen.</summary>
        public string AddedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>UTC timestamp of the most recent upsert.</summary>
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>Non-null when the item has been soft-deleted (removed from all sources).</summary>
        public string? RemovedAt { get; set; }

        /// <summary>
        /// Number of times this item has been resurrected by <c>FileResurrectionTask</c>.
        /// Incremented each time a missing library file is replaced by a new .strm.
        /// </summary>
        public int ResurrectionCount { get; set; }

        /// <summary>
        /// Raw catalog type from the source ("anime", "series", "movie").
        /// Persisted in Sprint 160 (catalog_type column).
        /// Used for NFO writing and ID resolution.
        /// </summary>
        public string? CatalogType { get; set; }

        /// <summary>
        /// TVDB ID for series Emby scanner hint ([tvdbid-xxx])
        /// and NFO <tvdbid> tag. Separate from UniqueIdsJson for direct access.
        /// Added in Sprint 160.
        /// </summary>
        public string? TvdbId { get; set; }

        /// <summary>
        /// Verbatim JSON response from the source addon's
        /// /meta/{type}/{id}.json call. Null if call was skipped or failed.
        /// Purpose: debugging ID resolution failures without re-fetching.
        /// Added in Sprint 160.
        /// </summary>
        public string? RawMetaJson { get; set; }

        // ── Sprint 66: Marvin Item State Machine ───────────────────────────────────

        /// <summary>
        /// Current state in the Marvin reconciliation lifecycle.
        /// See <see cref="ItemState"/> for state transitions.
        /// Default: Catalogued (0) for existing items.
        /// </summary>
        public ItemState ItemState { get; set; } = ItemState.Catalogued;

        /// <summary>
        /// Source of PIN state when <see cref="ItemState"/> = <see cref="ItemState.Pinned"/>.
        /// Format: "user:discover:ISO8601_timestamp" or similar.
        /// Null for non-pinned items.
        /// </summary>
        public string? PinSource { get; set; }

        /// <summary>
        /// UTC timestamp when the item was pinned.
        /// Null for non-pinned items.
        /// </summary>
        public string? PinnedAt { get; set; }

        /// <summary>
            /// Emby user ID of user who first added this item.
            /// Set when writing .strm via StrmWriterService (Sprint 156).
            /// Null for system-synced items.
            /// </summary>
            public string? FirstAddedByUserId { get; set; }

        // ── Sprint 142: Refresh Lifecycle Properties ─────────────────────────────

        /// <summary>NFO enrichment status for Refresh lifecycle.</summary>
        public string? NfoStatus { get; set; }

        /// <summary>Number of enrichment retries attempted.</summary>
        public int RetryCount { get; set; }

        /// <summary>Unix timestamp for next retry attempt.</summary>
        public long? NextRetryAt { get; set; }

        /// <summary>Unix timestamp when .strm token expires.</summary>
        public long? StrmTokenExpiresAt { get; set; }

        /// <summary>UTC timestamp when item was blocked (ISO8601).</summary>
        public string? BlockedAt { get; set; }

        /// <summary>Emby user ID of admin who blocked the item.</summary>
        public string? BlockedBy { get; set; }

        /// <summary>True if this item has been blocked by an admin.</summary>
        public bool Blocked => !string.IsNullOrEmpty(BlockedAt);
    }
}
