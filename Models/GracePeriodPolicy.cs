using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Shared grace period policy for removal logic.
    /// Used by both RemovalPipeline and RemovalService to avoid duplication.
    /// </summary>
    public static class GracePeriodPolicy
    {
        public static readonly TimeSpan Duration = TimeSpan.FromDays(7);

        /// <summary>
        /// Returns true if the item is protected from removal
        /// (has an enabled source, or is manually saved/blocked).
        /// </summary>
        public static bool IsProtected(MediaItem item, bool hasEnabledSource) =>
            hasEnabledSource || item.Saved || item.Blocked;
    }
}
