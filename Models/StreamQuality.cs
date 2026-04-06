using System.ComponentModel;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Stream quality tiers for ranking.
    /// </summary>
    [Description("Stream quality")]
    public enum StreamQuality
    {
        /// <summary>4K / 2160p (highest)</summary>
        [Description("4K")]
        FHD_4K = 5,

        /// <summary>1080p (high)</summary>
        [Description("1080p")]
        FHD = 4,

        /// <summary>720p (medium)</summary>
        [Description("720p")]
        HD = 3,

        /// <summary>480p / SD (low)</summary>
        [Description("480p")]
        SD = 2,

        /// <summary>Unknown quality</summary>
        [Description("Unknown")]
        Unknown = 1,

        /// <summary>No quality specified</summary>
        [Description("None")]
        None = 0
    }
}
