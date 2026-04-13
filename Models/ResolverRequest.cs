using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Request model for /InfiniteDrive/Resolve endpoint.
    /// </summary>
    [Route("/InfiniteDrive/Resolve", "GET", Summary = "Resolve AIOStreams stream and return M3U8 manifest")]
    [Unauthenticated]
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

        /// <summary>
        /// Resolve token for authentication.
        /// Format: {exp}:{signature} (opaque token, IMDB ID and quality are query params)
        /// </summary>
        public string? Token { get; set; }
    }
}
