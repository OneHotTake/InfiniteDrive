namespace EmbyStreams.Models
{
    /// <summary>
    /// Represents a user's pin on a catalog item via playback, discover, or admin.
    /// Maps to <c>user_item_pins</c> table.
    /// </summary>
    public class UserItemPin
    {
        /// <summary>Auto-incremented primary key.</summary>
        public long Id { get; set; }

        /// <summary>Emby user ID who owns this pin.</summary>
        public string EmbyUserId { get; set; } = string.Empty;

        /// <summary>Catalog item ID being pinned.</summary>
        public string CatalogItemId { get; set; } = string.Empty;

        /// <summary>UTC timestamp when pin was created (ISO8601).</summary>
        public string PinnedAt { get; set; } = string.Empty;

        /// <summary>Source of the pin (playback, discover, admin).</summary>
        public string PinSource { get; set; } = string.Empty;
    }
}
