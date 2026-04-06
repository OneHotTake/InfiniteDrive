using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Represents a collection in EmbyStreams.
    /// </summary>
    public class Collection
    {
        /// <summary>
        /// Primary key - TEXT UUID.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Collection name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Emby collection ID (BoxSet ID).
        /// </summary>
        public string? EmbyCollectionId { get; set; }

        /// <summary>
        /// Associated source ID.
        /// </summary>
        public string? SourceId { get; set; }

        /// <summary>
        /// Whether this collection is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Collection name override from source.
        /// </summary>
        public string? CollectionName { get; set; }

        /// <summary>
        /// Last synced at timestamp.
        /// </summary>
        public DateTimeOffset? LastSyncedAt { get; set; }

        /// <summary>
        /// When this collection was created.
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// When this collection was last updated.
        /// </summary>
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
