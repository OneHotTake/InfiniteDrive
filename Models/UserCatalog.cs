using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Represents a user-owned public RSS catalog (Trakt or MDBList list).
    /// Maps to the <c>user_catalogs</c> SQLite table (Sprint 158).
    /// </summary>
    public class UserCatalog
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Emby user ID who owns this catalog.</summary>
        public string OwnerUserId { get; set; } = string.Empty;

        /// <summary>Always "user_rss" — discriminator for future extension.</summary>
        public string SourceType { get; set; } = "user_rss";

        /// <summary>"trakt" or "mdblist" — identifies the RSS provider.</summary>
        public string Service { get; set; } = string.Empty;

        /// <summary>Public RSS feed URL (https only).</summary>
        public string RssUrl { get; set; } = string.Empty;

        /// <summary>Human-readable name; auto-seeded from feed &lt;title&gt;.</summary>
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
