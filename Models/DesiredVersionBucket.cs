namespace InfiniteDrive.Models
{
    /// <summary>
    /// Defines a desired quality bucket for multi-version .strm selection.
    /// During version selection, buckets are matched in order — earlier
    /// buckets get priority when claiming streams from the pool.
    /// </summary>
    public class DesiredVersionBucket
    {
        /// <summary>
        /// Resolution to match: "4K", "1080p", "720p", "SD", or empty/null for any.
        /// Matched case-insensitively against ParsedStream.Resolution.
        /// </summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>
        /// Audio profile to match: "Lossless/Premium", "5.1/7.1 (Surround)",
        /// "DD/DTS (Compressed)", "Stereo/2.0", "Any Audio", or empty for any.
        /// </summary>
        public string Audio { get; set; } = "Any Audio";

        /// <summary>
        /// Maximum number of streams to select from this bucket.
        /// </summary>
        public int Count { get; set; } = 1;
    }
}
