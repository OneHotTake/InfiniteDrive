using System;
using System.Security.Cryptography;
using System.Text;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Generates and validates HMAC-SHA256 signed URLs for the /EmbyStreams/Stream endpoint.
    ///
    /// Signed URLs embed an expiry timestamp and a signature derived from the
    /// PluginSecret stored in plugin configuration.  This allows bare HTTP clients
    /// (VLC, ffmpeg, Roku firmware, Apple TV) to play streams without sending
    /// Emby authentication headers.
    ///
    /// Security model:
    ///   - The signature covers: id, type, season, episode, exp
    ///   - Expiry prevents indefinite replay of captured URLs
    ///   - Constant-time comparison prevents timing oracle attacks
    ///   - Rotating PluginSecret invalidates all existing .strm files
    /// </summary>
    public static class StreamUrlSigner
    {
        /// <summary>
        /// Generates a complete signed URL for the /EmbyStreams/Stream endpoint.
        /// </summary>
        /// <param name="embyBaseUrl">Public Emby base URL (e.g. http://192.168.1.50:8096). Trailing slash stripped.</param>
        /// <param name="imdbId">IMDB ID (e.g. tt0133093).</param>
        /// <param name="mediaType">"movie" or "series".</param>
        /// <param name="season">Season number for series; null for movies.</param>
        /// <param name="episode">Episode number for series; null for movies.</param>
        /// <param name="pluginSecret">HMAC key — from PluginConfiguration.PluginSecret.</param>
        /// <param name="validity">How long the URL stays valid. Use TimeSpan.FromDays(365) for .strm files.</param>
        /// <returns>Fully formed signed URL string.</returns>
        public static string GenerateSignedUrl(
            string embyBaseUrl,
            string imdbId,
            string mediaType,
            int? season,
            int? episode,
            string pluginSecret,
            TimeSpan validity)
        {
            var exp = DateTimeOffset.UtcNow.Add(validity).ToUnixTimeSeconds();
            var sig = ComputeHmac(imdbId, mediaType, season, episode, exp, pluginSecret);

            var sb = new StringBuilder();
            sb.Append(embyBaseUrl.TrimEnd('/'));
            sb.Append("/EmbyStreams/Stream?id=");
            sb.Append(Uri.EscapeDataString(imdbId));
            sb.Append("&type=");
            sb.Append(Uri.EscapeDataString(mediaType));
            sb.Append("&exp=");
            sb.Append(exp);
            if (season.HasValue)
            {
                sb.Append("&season=");
                sb.Append(season.Value);
            }
            if (episode.HasValue)
            {
                sb.Append("&episode=");
                sb.Append(episode.Value);
            }
            sb.Append("&sig=");
            sb.Append(sig);

            return sb.ToString();
        }

        /// <summary>
        /// Validates an HMAC signature and checks the expiry timestamp.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the signature is valid and the URL has not expired.
        /// <c>false</c> if expired or signature mismatch.
        /// </returns>
        public static bool ValidateSignature(
            string id,
            string type,
            int? season,
            int? episode,
            long exp,
            string sig,
            string pluginSecret)
        {
            // Check expiry first (cheap operation)
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
                return false;

            // Recompute expected signature
            var expected = ComputeHmac(id, type, season, episode, exp, pluginSecret);

            // Constant-time byte comparison — prevents timing oracle attacks
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes   = Encoding.UTF8.GetBytes(sig ?? string.Empty);

            // CryptographicOperations.FixedTimeEquals requires equal-length inputs;
            // a length mismatch is already a definitive mismatch, but we must not
            // short-circuit in a way that leaks length via timing.
            if (expectedBytes.Length != actualBytes.Length)
                return false;

            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        /// <summary>
        /// Generates a cryptographically random base64-encoded secret (32 random bytes).
        /// Call this once to populate PluginConfiguration.PluginSecret on first run.
        /// </summary>
        public static string GenerateSecret()
        {
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        // ── Private ──────────────────────────────────────────────────────────────

        /// <summary>
        /// HMAC message format: "{id}:{type}:{season}:{episode}:{exp}"
        /// season and episode are empty strings when null to keep the format stable.
        /// </summary>
        private static string ComputeHmac(
            string id, string type, int? season, int? episode, long exp, string secret)
        {
            var message = string.Concat(
                id, ":",
                type, ":",
                season?.ToString()   ?? string.Empty, ":",
                episode?.ToString()  ?? string.Empty, ":",
                exp.ToString());

            var keyBytes  = Encoding.UTF8.GetBytes(secret);
            var msgBytes  = Encoding.UTF8.GetBytes(message);

            using var hmac = new HMACSHA256(keyBytes);
            var hash = hmac.ComputeHash(msgBytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
