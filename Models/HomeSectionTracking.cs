using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Tracks per-user per-rail state for home screen sections.
    /// </summary>
    public class HomeSectionTracking
    {
        /// <summary>
        /// Primary key - TEXT UUID.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Emby user ID.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Rail type (saved, trending_movies, trending_series, new_this_week, admin_chosen).
        /// </summary>
        public string RailType { get; set; } = string.Empty;

        /// <summary>
        /// Emby-assigned section ID.
        /// </summary>
        public string? EmbySectionId { get; set; }

        /// <summary>
        /// Section marker for stable identity (via Subtitle field).
        /// </summary>
        public string SectionMarker { get; set; } = string.Empty;

        /// <summary>
        /// When this tracking was created.
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// When this tracking was last updated.
        /// </summary>
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
