using System;
using System.Security.Cryptography;

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
    public static class PlaybackTokenService
    {
        private const int DefaultExpirationHours = 1;

        /// <summary>
        /// Signs a raw stream URL with HMAC-SHA256 for short-term playback.
        /// Adds timestamp and signature to prevent replay attacks.
        /// </summary>
        /// <param name="url">The raw stream URL to sign.</param>
        /// <param name="pluginSecret">HMAC key from PluginConfiguration.PluginSecret.</param>
        /// <param name="validityHours">Optional validity period; defaults to 1 hour.</param>
        /// <returns>Signed URL in format: {url}|{timestamp}|{signature}</returns>
        public static string Sign(
            string url,
            string pluginSecret,
            int validityHours = DefaultExpirationHours)
        {
            if (string.IsNullOrEmpty(pluginSecret))
            {
                return url; // Return unsigned if no secret configured
            }

            var timestamp = DateTimeOffset.UtcNow.AddHours(validityHours).ToUnixTimeSeconds();
            var message = $"{url}|{timestamp}";
            var signature = ComputeHmacSimple(message, pluginSecret);

            return $"{url}|{timestamp}|{signature}";
        }

        /// <summary>
        /// Verifies a signed URL returned by Sign().
        /// Checks both HMAC signature and timestamp expiration.
        /// </summary>
        /// <param name="signedUrl">The signed URL in format: {url}|{timestamp}|{signature}</param>
        /// <param name="pluginSecret">HMAC key from PluginConfiguration.PluginSecret.</param>
        /// <returns>True if signature valid and not expired; false otherwise.</returns>
        public static bool Verify(string signedUrl, string pluginSecret)
        {
            if (string.IsNullOrEmpty(pluginSecret))
                return true; // Allow unsigned if no secret configured

            var parts = signedUrl.Split('|');
            if (parts.Length != 3) return false;

            var url = parts[0];
            if (!long.TryParse(parts[1], out long timestamp)) return false;

            // Check timestamp expiration
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > timestamp)
                return false;

            // Verify signature
            var message = $"{url}|{timestamp}";
            var expectedSignature = ComputeHmacSimple(message, pluginSecret);

            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(parts[2] ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(expectedSignature));
        }

        /// <summary>
        /// Generates a complete signed URL for the /EmbyStreams/Stream endpoint.
        /// </summary>
        /// <param name="embyBaseUrl">Public Emby base URL (e.g. http://192.168.1.50:8096). Trailing slash stripped.</param>
        /// <param name="imdbId">IMDB ID (e.g. tt0133093).</param>
        /// <param name="mediaType">"movie" or "series".</param>
        /// <param name="season">Season number for series; null for movies.</param>
        /// <param name="episode">Episode number for series; null for movies.</param>
        /// <param name="pluginSecret">HMAC key — from PluginConfiguration.PluginSecret.</param>
        /// <param name="validity">How long to URL stays valid. Use TimeSpan.FromDays(365) for .strm files.</param>
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

            var sb = new System.Text.StringBuilder();
            sb.Append(embyBaseUrl.TrimEnd('/'));
            sb.Append("/EmbyStreams/Stream?id=");
            sb.Append(System.Uri.EscapeDataString(imdbId));
            sb.Append("&type=");
            sb.Append(System.Uri.EscapeDataString(mediaType));
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
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
            var actualBytes = System.Text.Encoding.UTF8.GetBytes(sig ?? string.Empty);

            // CryptographicOperations.FixedTimeEquals requires equal-length inputs;
            // a length mismatch is already a definitive mismatch, but we must not
            // short-circuit in a way that leaks length via timing.
            if (expectedBytes.Length != actualBytes.Length)
                return false;

            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        /// <summary>
        /// Generates a short-lived resolve token for /EmbyStreams/Resolve endpoint.
        /// Format: {quality}:{imdbId}:{exp}:{signature}
        /// </summary>
        /// <param name="quality">Quality label (e.g. "4k", "1080p", "720p").</param>
        /// <param name="imdbId">IMDB ID of the item to resolve.</param>
        /// <param name="pluginSecret">HMAC key from PluginConfiguration.PluginSecret.</param>
        /// <param name="validityHours">Optional validity period; defaults to 1 hour.</param>
        /// <returns>Resolve token string.</returns>
        public static string GenerateResolveToken(
            string quality,
            string imdbId,
            string pluginSecret,
            int validityHours = 1)
        {
            var exp = DateTimeOffset.UtcNow.AddHours(validityHours).ToUnixTimeSeconds();
            var message = $"{quality}:{imdbId}:{exp}";
            var sig = ComputeHmacSimple(message, pluginSecret);
            return $"{message}:{sig}";
        }

        /// <summary>
        /// Validates a stream token returned by GenerateResolveToken().
        /// Format: {quality}:{imdbId}:{exp}:{signature}
        /// </summary>
        /// <param name="token">The stream token to validate.</param>
        /// <param name="pluginSecret">HMAC key from PluginConfiguration.PluginSecret.</param>
        /// <returns>True if token format is valid, signature matches, and not expired.</returns>
        public static bool ValidateStreamToken(string token, string pluginSecret)
        {
            if (string.IsNullOrEmpty(pluginSecret) || string.IsNullOrEmpty(token))
                return false;

            var parts = token.Split(':');
            if (parts.Length != 4)
                return false;

            if (!long.TryParse(parts[2], out long exp))
                return false;

            // Check expiration
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
                return false;

            // Verify signature
            var message = $"{parts[0]}:{parts[1]}:{parts[2]}";
            var expectedSignature = ComputeHmacSimple(message, pluginSecret);

            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(parts[3] ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(expectedSignature));
        }

        /// <summary>
        /// Generates a cryptographically random base64-encoded secret (32 random bytes).
        /// Call this once to populate PluginConfiguration.PluginSecret on first run.
        /// </summary>
        public static string GenerateSecret()
        {
            var bytes = new byte[32];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        // ── Private ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes HMAC-SHA256 hash for the simple URL signing format.
        /// </summary>
        private static string ComputeHmacSimple(string message, string secret)
        {
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(secret);
            var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);

            using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
            var hash = hmac.ComputeHash(msgBytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

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
                season?.ToString() ?? string.Empty, ":",
                episode?.ToString() ?? string.Empty, ":",
                exp.ToString());

            var keyBytes = System.Text.Encoding.UTF8.GetBytes(secret);
            var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);

            using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
            var hash = hmac.ComputeHash(msgBytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
