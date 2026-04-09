using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Tracks which slots have been materialized as .strm/.nfo pairs per title.
    /// Stored in the <c>materialized_versions</c> table.
    /// </summary>
    public class MaterializedVersion
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Foreign key to media_items.id.</summary>
        public string MediaItemId { get; set; } = string.Empty;

        /// <summary>Slot key that was materialized.</summary>
        public string SlotKey { get; set; } = string.Empty;

        /// <summary>Path to the .strm file on disk.</summary>
        public string StrmPath { get; set; } = string.Empty;

        /// <summary>Path to the .nfo file on disk.</summary>
        public string NfoPath { get; set; } = string.Empty;

        /// <summary>SHA1 hash of the .strm content URL for change detection.</summary>
        public string StrmUrlHash { get; set; } = string.Empty;

        /// <summary>1 if this slot holds the base (unsuffixed) filename.</summary>
        public bool IsBase { get; set; }

        /// <summary>UTC timestamp when first materialized.</summary>
        public string MaterializedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>UTC timestamp when last updated.</summary>
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>Unix timestamp when the .strm token expires (365 days after write).</summary>
        public long? StrmTokenExpiresAt { get; set; }
    }
}
