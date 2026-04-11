using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Single catalog entry from a manifest source.
    /// </summary>
    public class ManifestEntry
    {
        /// <summary>
        /// Unique identifier for this item (e.g., "imdb:tt123456", "tmdb:1160419").
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Display title.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Media type: "movie" or "series".
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Release year (optional).
        /// </summary>
        public int? Year { get; set; }

        /// <summary>
        /// Source addon ID (who provided this entry).
        /// </summary>
        public string? SourceId { get; set; }
    }
}
