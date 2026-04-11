using System;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Manages stream URL cache with TTL support.
    /// </summary>
    public class StreamCache
    {
        private readonly DatabaseManager _db;
        private readonly ILogger<StreamCache> _logger;

        private readonly TimeSpan _defaultTtl = TimeSpan.FromHours(24);

        public StreamCache(DatabaseManager db, ILogger<StreamCache> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Gets primary cached URL for a media ID.
        /// </summary>
        public async Task<string?> GetPrimaryAsync(string mediaId, CancellationToken ct = default)
        {
            var (url, _) = await _db.GetCachedStreamAsync(mediaId, ct);
            return url;
        }

        /// <summary>
        /// Gets secondary cached URL for a media ID.
        /// </summary>
        public async Task<string?> GetSecondaryAsync(string mediaId, CancellationToken ct = default)
        {
            var (_, urlSecondary) = await _db.GetCachedStreamAsync(mediaId, ct);
            return urlSecondary;
        }

        /// <summary>
        /// Sets primary cached URL for a media ID.
        /// </summary>
        public async Task SetPrimaryAsync(string mediaId, string url, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            await _db.SetCachedStreamPrimaryAsync(mediaId, url, ttl, ct);
            _logger.LogDebug("[StreamCache] Cached primary URL for {MediaId}, expires at {ExpiresAt}",
                mediaId, DateTimeOffset.UtcNow.Add(ttl ?? _defaultTtl));
        }

        /// <summary>
        /// Sets secondary cached URL for a media ID.
        /// </summary>
        public async Task SetSecondaryAsync(string mediaId, string url, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            await _db.SetCachedStreamSecondaryAsync(mediaId, url, ttl, ct);
            _logger.LogDebug("[StreamCache] Cached secondary URL for {MediaId}, expires at {ExpiresAt}",
                mediaId, DateTimeOffset.UtcNow.Add(ttl ?? _defaultTtl));
        }

        /// <summary>
        /// Invalidates cache entry for a media ID.
        /// </summary>
        public async Task InvalidateAsync(string mediaId, CancellationToken ct = default)
        {
            await _db.DeleteCachedStreamAsync(mediaId, ct);
            _logger.LogDebug("[StreamCache] Invalidated cache for {MediaId}", mediaId);
        }

        /// <summary>
        /// Purges all expired cache entries.
        /// </summary>
        public async Task PurgeExpiredAsync(CancellationToken ct = default)
        {
            await _db.PurgeExpiredCacheAsync(ct);
        }
    }
}
