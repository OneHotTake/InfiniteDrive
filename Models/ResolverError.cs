namespace InfiniteDrive.Models
{
    /// <summary>
    /// Distinct error codes for resolver failures.
    /// Enables clients to distinguish between different failure types
    /// and provide appropriate user feedback.
    /// </summary>
    public enum ResolverError
    {
        /// <summary>No streams exist for this media item.</summary>
        NoStreamsExist = 1,

        /// <summary>Streams exist but none match the requested quality tier.</summary>
        QualityMismatch = 2,

        /// <summary>Primary resolver (AIOStreams) is down or unreachable.</summary>
        PrimaryResolverDown = 3,

        /// <summary>All resolvers have failed to return streams.</summary>
        AllResolversDown = 4,

        /// <summary>Rate limit exceeded on upstream service.</summary>
        RateLimited = 5,

        /// <summary>Resolve token is invalid or expired.</summary>
        InvalidToken = 6
    }

    /// <summary>
    /// Human-readable messages for each error type.
    /// </summary>
    public static class ResolverErrorMessages
    {
        public const string NoStreamsExist = "No streams available for this content";
        public const string QualityMismatch = "No streams available at the requested quality tier";
        public const string PrimaryResolverDown = "Primary resolver temporarily unavailable";
        public const string AllResolversDown = "All resolvers temporarily unavailable";
        public const string RateLimited = "Too many requests - please try again later";
        public const string InvalidToken = "Invalid or expired token";
    }
}
