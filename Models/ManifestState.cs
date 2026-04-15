using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Container for manifest fetch state — replaces scattered statics in Plugin.cs.
    /// Sprint 360: Single authority for manifest status, fetched timestamp, and staleness.
    /// </summary>
    public class ManifestState
    {
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(12);

        public ManifestStatusState Status { get; set; } = ManifestStatusState.Error;
        public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.MinValue;

        /// <summary>
        /// Checks if cached manifest is stale (> 12 hours old) and updates status.
        /// </summary>
        public void CheckStale()
        {
            if (FetchedAt != DateTimeOffset.MinValue)
            {
                var age = DateTimeOffset.UtcNow - FetchedAt;
                if (age > StaleThreshold)
                {
                    Status = ManifestStatusState.Stale;
                }
            }
        }
    }
}
