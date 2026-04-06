using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Core entity representing a media item in the v3.3 system.
    /// Tracks lifecycle state, saved/blocked user states, and Emby integration.
    /// </summary>
    public class MediaItem
    {
        // ── Primary Key ───────────────────────────────────────────────────────

        /// <summary>
        /// Primary key - TEXT UUID (not int).
        /// </summary>
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        // ── Identification ───────────────────────────────────────────────────────

        /// <summary>
        /// Primary ID with type (e.g., "imdb:tt123456", "tmdb:1160419").
        /// </summary>
        public MediaId PrimaryId { get; init; } = default!;

        /// <summary>
        /// Media type: "movie" or "series".
        /// </summary>
        public string MediaType { get; init; } = null!;

        /// <summary>
        /// Item title.
        /// </summary>
        public string Title { get; init; } = null!;

        /// <summary>
        /// Release year (optional).
        /// </summary>
        public int? Year { get; init; }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// Current lifecycle state.
        /// </summary>
        public ItemStatus Status { get; set; } = ItemStatus.Known;

        /// <summary>
        /// Reason for failure if Status is Failed.
        /// </summary>
        public FailureReason FailureReason { get; set; } = FailureReason.None;

        // ── Saved State ────────────────────────────────────────────────────────

        /// <summary>
        /// Whether this item is saved by a user (boolean column, not status value).
        /// </summary>
        public bool Saved { get; set; }

        /// <summary>
        /// When the item was saved.
        /// </summary>
        public DateTimeOffset? SavedAt { get; set; }

        /// <summary>
        /// Who saved the item: "system:watch", "user", or "admin".
        /// </summary>
        public string? SavedBy { get; set; }

        /// <summary>
        /// Reason for saving.
        /// </summary>
        public SaveReason? SaveReason { get; set; }

        /// <summary>
        /// For series: the season number that was saved.
        /// </summary>
        public int? SavedSeason { get; set; }

        // ── Blocked State ───────────────────────────────────────────────────────

        /// <summary>
        /// Whether this item is blocked (boolean column, not status value).
        /// </summary>
        public bool Blocked { get; set; }

        /// <summary>
        /// When the item was blocked.
        /// </summary>
        public DateTimeOffset? BlockedAt { get; set; }

        // ── Timestamps ────────────────────────────────────────────────────────

        /// <summary>
        /// When the item was first created in the system.
        /// </summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Last updated timestamp (updated on each state change).
        /// </summary>
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// When the grace period for removal started (for removed items).
        /// </summary>
        public DateTimeOffset? GraceStartedAt { get; set; }

        // ── Superseded / Conflict Handling ───────────────────────────────────

        /// <summary>
        /// Whether this item has been superseded by another source.
        /// </summary>
        public bool Superseded { get; set; }

        /// <summary>
        /// Whether there's a supersession conflict with another item.
        /// </summary>
        public bool SupersededConflict { get; set; }

        /// <summary>
        /// When the item was marked as superseded.
        /// </summary>
        public DateTimeOffset? SupersededAt { get; set; }

        // ── Emby Integration ──────────────────────────────────────────────────

        /// <summary>
        /// Emby library item ID (TEXT GUID, not int).
        /// </summary>
        public string? EmbyItemId { get; set; }

        /// <summary>
        /// When Emby indexed the item.
        /// </summary>
        public DateTimeOffset? EmbyIndexedAt { get; set; }

        /// <summary>
        /// Path to the .strm file on disk.
        /// </summary>
        public string? StrmPath { get; set; }

        /// <summary>
        /// Path to the .nfo file on disk.
        /// </summary>
        public string? NfoPath { get; set; }

        /// <summary>
        /// Watch progress percentage (0-100).
        /// </summary>
        public int WatchProgressPct { get; set; }

        /// <summary>
        /// Whether the user favorited this item.
        /// </summary>
        public bool Favorited { get; set; }

        // ── Derived State ──────────────────────────────────────────────────────

        /// <summary>
        /// Whether this item is saved.
        /// </summary>
        public bool IsSaved => Saved;

        /// <summary>
        /// Whether this item is blocked.
        /// </summary>
        public bool IsBlocked => Blocked;

        /// <summary>
        /// Whether this item is playable (Active or Saved).
        /// </summary>
        public bool IsPlayable => Status == ItemStatus.Active || Saved;

        /// <summary>
        /// Primary ID value component (without type prefix).
        /// </summary>
        public string PrimaryIdValue => PrimaryId.Value;

        /// <summary>
        /// Primary ID type component.
        /// </summary>
        public string PrimaryIdType => PrimaryId.Type.ToLowerString();

        /// <summary>
        /// Whether this item is a series.
        /// </summary>
        public bool IsSeries => MediaType.Equals("series", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this item is a movie.
        /// </summary>
        public bool IsMovie => MediaType.Equals("movie", StringComparison.OrdinalIgnoreCase);

        // ── Methods ───────────────────────────────────────────────────────────

        /// <summary>
        /// Marks this item as saved with the specified reason.
        /// </summary>
        public void MarkSaved(string savedBy, SaveReason reason, int? season = null)
        {
            Saved = true;
            SavedAt = DateTimeOffset.UtcNow;
            SavedBy = savedBy;
            SaveReason = reason;
            SavedSeason = season;
            Blocked = false;
            BlockedAt = null;
        }

        /// <summary>
        /// Marks this item as blocked.
        /// </summary>
        public void MarkBlocked()
        {
            Blocked = true;
            BlockedAt = DateTimeOffset.UtcNow;
            Saved = false;
            SavedAt = null;
            SavedBy = null;
            SaveReason = null;
        }

        /// <summary>
        /// Marks this item as failed with the specified reason.
        /// </summary>
        public void MarkFailed(FailureReason reason)
        {
            Status = ItemStatus.Failed;
            FailureReason = reason;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Updates the status with validation.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the transition is invalid.</exception>
        public void SetStatus(ItemStatus newStatus)
        {
            if (!Status.CanTransitionTo(newStatus))
                throw new InvalidOperationException(
                    $"Invalid status transition: {Status} → {newStatus}");

            Status = newStatus;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
