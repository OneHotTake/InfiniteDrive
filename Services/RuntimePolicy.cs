namespace InfiniteDrive.Services
{
    /// <summary>
    /// Bounded implementation policy that is not user configuration. Runtime
    /// behavior is driven by observed state (configured manifests, server
    /// preferences, cache expiry and provider backoff) wherever that state exists.
    /// </summary>
    internal static class RuntimePolicy
    {
        public const int EmbyVersionLimit = 8;
        public const int FallbackStreamCacheMinutes = 360;
        public const int NextEpisodePrewarmCount = 1;
        public const int GlobalAbsentSyncThreshold = 3;
        public const int ResolutionBatchSize = 42;
        public const string FallbackLanguage = "en";
        public const string FallbackCountry = "US";
    }
}
