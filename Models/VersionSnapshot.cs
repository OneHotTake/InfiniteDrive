using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Tracks the selected top candidate per title per slot,
    /// plus an ephemeral playback URL cache.
    ///
    /// The snapshot stores only the top candidate ID. The full fallback ladder
    /// (ranked candidates 0..N) lives in the <c>candidates</c> table.
    /// </summary>
    public class VersionSnapshot
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Foreign key to media_items.id.</summary>
        public string MediaItemId { get; set; } = string.Empty;

        /// <summary>Slot key this snapshot belongs to.</summary>
        public string SlotKey { get; set; } = string.Empty;

        /// <summary>Foreign key to the top candidate.id.</summary>
        public string CandidateId { get; set; } = string.Empty;

        /// <summary>UTC timestamp when this snapshot was taken.</summary>
        public string SnapshotAt { get; set; } = DateTime.UtcNow.ToString("o");

        // ── Ephemeral playback URL cache ────────────────────────────────────────

        /// <summary>Cached playback URL (short-lived).</summary>
        public string? PlaybackUrl { get; set; }

        /// <summary>When the cached URL was stored.</summary>
        public string? PlaybackUrlCachedAt { get; set; }

        /// <summary>When the cached URL expires.</summary>
        public string? PlaybackUrlExpiresAt { get; set; }

        // ── Derived ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether the cached playback URL is still valid.
        /// Checks non-empty URL and not-yet-expired timestamp.
        /// </summary>
        public bool HasValidPlaybackUrl
        {
            get
            {
                if (string.IsNullOrEmpty(PlaybackUrl)) return false;
                if (string.IsNullOrEmpty(PlaybackUrlExpiresAt)) return false;
                if (DateTime.TryParse(PlaybackUrlExpiresAt, out var exp))
                    return exp > DateTime.UtcNow;
                return false;
            }
        }
    }
}
