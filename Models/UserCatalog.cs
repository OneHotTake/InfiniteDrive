using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Represents an external list catalog (MDBList, Trakt, TMDB, AniList).
    /// Maps to the <c>user_catalogs</c> SQLite table.
    /// </summary>
    public class UserCatalog
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Emby user ID who owns this catalog, or "SERVER" for admin lists.</summary>
        public string OwnerUserId { get; set; } = string.Empty;

        /// <summary>"external_list" — discriminator for future extension.</summary>
        public string SourceType { get; set; } = "external_list";

        /// <summary>"trakt", "mdblist", "tmdb", or "anilist".</summary>
        public string Service { get; set; } = string.Empty;

        /// <summary>List URL as entered by the user.</summary>
        public string ListUrl { get; set; } = string.Empty;

        /// <summary>Human-readable name; auto-seeded from list metadata or user-provided.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Whether this catalog participates in syncs. Soft-delete flag.</summary>
        public bool Active { get; set; } = true;

        /// <summary>ISO-8601 UTC timestamp of the last successful sync, or null.</summary>
        public string? LastSyncedAt { get; set; }

        /// <summary>"ok" or error message from the last sync attempt.</summary>
        public string? LastSyncStatus { get; set; }

        /// <summary>ISO-8601 UTC creation timestamp.</summary>
        public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("o");
    }
}
