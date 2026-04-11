using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Represents a content source (catalog) in the v3.3 system.
    /// Sources provide media items and can be enabled/disabled individually.
    /// </summary>
    public class Source
    {
        // ── Primary Key ───────────────────────────────────────────────────────

        /// <summary>
        /// Primary key - TEXT UUID (not int).
        /// </summary>
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        // ── Basic Info ───────────────────────────────────────────────────────

        /// <summary>
        /// Display name for this source.
        /// </summary>
        public string Name { get; init; } = null!;

        /// <summary>
        /// URL for the source (if applicable to type).
        /// </summary>
        public string? Url { get; init; }

        /// <summary>
        /// Type of source (BuiltIn, Aio, Trakt, MdbList).
        /// </summary>
        public SourceType Type { get; init; }

        // ── State Flags ──────────────────────────────────────────────────────

        /// <summary>
        /// Whether this source is enabled for syncing.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Whether this source should be displayed as a collection in Emby.
        /// </summary>
        public bool ShowAsCollection { get; set; }

        // ── Sync Metadata ────────────────────────────────────────────────────

        /// <summary>
        /// Maximum number of items to sync from this source.
        /// </summary>
        public int MaxItems { get; set; } = 100;

        /// <summary>
        /// Sync interval in hours.
        /// </summary>
        public int SyncIntervalHours { get; set; } = 6;

        /// <summary>
        /// When the last sync completed.
        /// </summary>
        public DateTimeOffset? LastSyncedAt { get; set; }

        // ── Collection Metadata ───────────────────────────────────────────────

        /// <summary>
        /// Emby BoxSet InternalId (as string) for this source's collection.
        /// </summary>
        public string? EmbyCollectionId { get; set; }

        /// <summary>
        /// Display name for the collection (if ShowAsCollection is true).
        /// </summary>
        public string? CollectionName { get; set; }

        // ── Timestamps ────────────────────────────────────────────────────────

        /// <summary>
        /// When this source was created.
        /// </summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Last updated timestamp.
        /// </summary>
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        // ── Derived State ──────────────────────────────────────────────────────

        /// <summary>
        /// Whether this source is currently syncing.
        /// </summary>
        public bool IsSyncing =>
            LastSyncedAt.HasValue &&
            DateTimeOffset.UtcNow - LastSyncedAt.Value < TimeSpan.FromMinutes(5);

        /// <summary>
        /// Whether this source is due for a sync.
        /// </summary>
        public bool IsDueForSync =>
            !LastSyncedAt.HasValue ||
            DateTimeOffset.UtcNow - LastSyncedAt.Value > TimeSpan.FromHours(SyncIntervalHours);

        // ── Methods ───────────────────────────────────────────────────────────

        /// <summary>
        /// Marks this source as synced.
        /// </summary>
        public void MarkSynced()
        {
            LastSyncedAt = DateTimeOffset.UtcNow;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Toggles the enabled state.
        /// </summary>
        public void Toggle()
        {
            Enabled = !Enabled;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Enables this source.
        /// </summary>
        public void Enable()
        {
            Enabled = true;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Disables this source.
        /// </summary>
        public void Disable()
        {
            Enabled = false;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
