using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Structured result from stream resolution, distinguishing WHY resolution
    /// failed so callers (validation tasks, ResolverService) can make correct
    /// decisions about retries, deletion, and failover.
    /// </summary>
    public class ResolutionResult
    {
        /// <summary>The resolved stream URL, or null if resolution failed.</summary>
        public string? StreamUrl { get; set; }

        /// <summary>Why resolution succeeded or failed.</summary>
        public ResolutionStatus Status { get; set; }

        /// <summary>For Throttled results: how long to wait before retrying.</summary>
        public TimeSpan? RetryAfter { get; set; }

        /// <summary>The cached ResolutionEntry, if one was produced.</summary>
        public ResolutionEntry? Entry { get; set; }
    }

    public enum ResolutionStatus
    {
        /// <summary>Stream URL resolved successfully.</summary>
        Success,

        /// <summary>Provider returned 429 — skip this cycle, don't delete .strm.</summary>
        Throttled,

        /// <summary>Both providers returned 404/no content — safe to delete in Pessimistic phase.</summary>
        ContentMissing,

        /// <summary>Provider timeout or connection failure — try next provider, don't delete.</summary>
        ProviderDown
    }
}
