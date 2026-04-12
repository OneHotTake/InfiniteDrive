namespace InfiniteDrive.Models
{
    /// <summary>
    /// Represents an available item in the Discover catalog (cached from AIOStreams).
    /// This is the "what's available" cache, not items the user has added.
    /// </summary>
    public class DiscoverCatalogEntry
    {
        public string Id { get; set; } = string.Empty;                    // Unique ID (aio:{type}:{imdbid})
        public string ImdbId { get; set; } = string.Empty;                // IMDb ID (tt...)
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
        public string MediaType { get; set; } = "movie";                  // "movie" or "series"
        public string? PosterUrl { get; set; }
        public string? BackdropUrl { get; set; }
        public string? Overview { get; set; }
        public string? Genres { get; set; }                               // Comma-separated genres
        public double? ImdbRating { get; set; }                           // IMDb rating (0-10)
        public string? Certification { get; set; }                        // MPAA/TV rating (e.g., "PG-13", "R", "TV-MA")
        public string CatalogSource { get; set; } = string.Empty;         // Source catalog ID
        public string AddedAt { get; set; } = string.Empty;               // When added to cache (ISO 8601)
        public bool IsInUserLibrary { get; set; }                         // True if user already has this item
    }
}
