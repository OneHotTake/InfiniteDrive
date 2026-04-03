using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// One row in the <c>playback_log</c> table.  Written after every play attempt.
    /// Capped at 500 rows; oldest rows are pruned by the background task.
    /// </summary>
    public class PlaybackEntry
    {
        /// <summary>UUID primary key.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>IMDB ID of the played item.</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Display title (for the dashboard "Recent Activity" panel).</summary>
        public string? Title { get; set; }

        /// <summary>Season number; null for movies.</summary>
        public int? Season { get; set; }

        /// <summary>Episode number; null for movies.</summary>
        public int? Episode { get; set; }

        /// <summary>
        /// How the stream was served:
        /// <c>cached</c>, <c>fallback_1</c>, <c>fallback_2</c>,
        /// <c>sync_resolve</c>, or <c>failed</c>.
        /// </summary>
        public string ResolutionMode { get; set; } = string.Empty;

        /// <summary>Quality tier actually delivered to the client.</summary>
        public string? QualityServed { get; set; }

        /// <summary>Normalised client identifier (e.g. <c>emby_atv</c>).</summary>
        public string? ClientType { get; set; }

        /// <summary><c>proxy</c> or <c>redirect</c>.</summary>
        public string? ProxyMode { get; set; }

        /// <summary>Milliseconds from request receipt to first byte served.</summary>
        public int? LatencyMs { get; set; }

        /// <summary>Average sustained bitrate in kbps (proxy mode only).</summary>
        public int? BitrateSustained { get; set; }

        /// <summary>1 if quality had to be stepped down to a fallback; 0 otherwise.</summary>
        public int QualityDowngrade { get; set; } = 0;

        /// <summary>Error details when <see cref="ResolutionMode"/> is <c>failed</c>.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>UTC timestamp of the play event.</summary>
        public string PlayedAt { get; set; } = DateTime.UtcNow.ToString("o");
    }
}
