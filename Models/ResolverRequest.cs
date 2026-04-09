using MediaBrowser.Model.Services;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Request model for /EmbyStreams/Resolve endpoint.
    /// </summary>
    [Route("/EmbyStreams/Resolve", "GET", Summary = "Resolve AIOStreams stream and return M3U8 manifest")]
    public class ResolverRequest : IReturn<object>
    {
        /// <summary>
        /// Content identifier (IMDB ID for movies/series).
        /// Format: tt1234567
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Quality tier for stream selection.
        /// Supported values: "4k_hdr", "4k_sdr", "hd_broad", "sd_broad"
        /// </summary>
        public string? Quality { get; set; }

        /// <summary>
        /// Content type identifier.
        /// Values: "movie" or "series"
        /// </summary>
        public string? IdType { get; set; }

        /// <summary>
        /// Season number for series episodes.
        /// Not used for movies.
        /// </summary>
        public int? Season { get; set; }

        /// <summary>
        /// Episode number for series episodes.
        /// Not used for movies.
        /// </summary>
        public int? Episode { get; set; }
    }
}
