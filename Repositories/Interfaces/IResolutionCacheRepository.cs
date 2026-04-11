using System;
using System.Threading;
using System.Threading.Tasks;

namespace InfiniteDrive.Repositories.Interfaces
{
    /// <summary>
    /// Repository interface for stream URL caching.
    /// Provides cached stream URL storage and invalidation for playback service.
    /// One row per (imdb_id, season, episode) in resolution_cache table.
    /// Implemented by DatabaseManager (delegates to resolution cache methods).
    /// </summary>
    public interface IResolutionCacheRepository
    {
        /// <summary>
        /// Retrieves cached stream URL for a specific episode.
        /// Returns null if no cached entry exists or URL is expired.
        /// </summary>
        Task<string?> GetCachedUrlAsync(
            string imdbId,
            int? season,
            int episode,
            CancellationToken ct = default);

        /// <summary>
        /// Stores a resolved stream URL in cache with expiration time.
        /// Used by LinkResolverTask (tiered pre-resolution) and PlaybackService (cache hit).
        /// </summary>
        Task SetCachedUrlAsync(
            string imdbId,
            int? season,
            int episode,
            string resolvedUrl,
            DateTime expiresAt,
            CancellationToken ct = default);

        /// <summary>
        /// Marks a cached URL as invalid (status = 'stale').
        /// Used when HEAD validation fails or URL age exceeds TTL threshold.
        /// Triggers re-resolution in next LinkResolverTask run.
        /// </summary>
        Task InvalidateAsync(
            string imdbId,
            CancellationToken ct = default);

        /// <summary>
        /// Removes all expired cache entries (where expires_at < now).
        /// Runs periodically as maintenance.
        /// </summary>
        Task PurgeExpiredAsync(CancellationToken ct = default);
    }
}
