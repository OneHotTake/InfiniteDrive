using System;
using System.Collections.Concurrent;

namespace EmbyStreams.Services
{
    /// <summary>
    /// In-memory store for short-lived proxy session tokens.
    ///
    /// A proxy session is created by <see cref="PlaybackService"/> when it
    /// decides to serve a stream through the proxy rather than via HTTP redirect.
    /// <see cref="StreamProxyService"/> looks up the token to find the upstream
    /// URL and fallback chain.
    ///
    /// Token TTL: 4 hours.  Expired tokens are pruned lazily on every write.
    /// </summary>
    public static class ProxySessionStore
    {
        private const int TokenTtlHours = 4;

        private static readonly ConcurrentDictionary<string, ProxySession> Sessions
            = new ConcurrentDictionary<string, ProxySession>(StringComparer.Ordinal);

        // ── Write ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new proxy session and returns its token (a short GUID string).
        /// </summary>
        public static string Create(ProxySession session)
        {
            PruneExpired();

            var token = Guid.NewGuid().ToString("N"); // 32-char hex, no dashes
            Sessions[token] = session;
            return token;
        }

        // ── Read ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the <see cref="ProxySession"/> for the given token, or null
        /// if the token is unknown or has expired.
        /// </summary>
        public static ProxySession? TryGet(string token)
        {
            if (!Sessions.TryGetValue(token, out var session))
                return null;

            if (DateTime.UtcNow > session.ExpiresAt)
            {
                Sessions.TryRemove(token, out _);
                return null;
            }

            return session;
        }

        // ── Private ──────────────────────────────────────────────────────────────

        private static void PruneExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in Sessions)
            {
                if (now > kvp.Value.ExpiresAt)
                    Sessions.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Data associated with a single proxy session.
    /// </summary>
    public class ProxySession
    {
        /// <summary>Primary upstream Real-Debrid / CDN URL.</summary>
        public string StreamUrl { get; set; } = string.Empty;

        /// <summary>IMDB ID of the content being played (for fallback resolution).</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Season number; null for movies.</summary>
        public int? Season { get; set; }

        /// <summary>Episode number; null for movies.</summary>
        public int? Episode { get; set; }

        /// <summary>First fallback URL (next quality tier down).</summary>
        public string? Fallback1 { get; set; }

        /// <summary>Second fallback URL (lowest safe bitrate).</summary>
        public string? Fallback2 { get; set; }

        /// <summary>Torrent hash for season-pack bulk invalidation.</summary>
        public string? TorrentHash { get; set; }

        /// <summary>Quality tier of the primary stream (for logging).</summary>
        public string? QualityTier { get; set; }

        /// <summary>Binge-group identifier from AIOStreams for episode chaining.</summary>
        public string? BingeGroup { get; set; }

        /// <summary>
        /// Conservative estimated bitrate in kbps for the primary stream.
        /// Used by the throughput-learning proxy to detect struggling clients.
        /// 0 means unknown — skip throughput learning for this session.
        /// </summary>
        public int EstimatedBitrateKbps { get; set; }

        /// <summary>UTC timestamp after which this token is invalid.</summary>
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(4);
    }
}
