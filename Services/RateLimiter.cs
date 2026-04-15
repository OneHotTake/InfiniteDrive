using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Tracks per-IP rate limiting state with sliding window.
    /// </summary>
    internal sealed class IpRateLimit
    {
        private int _count;
        private DateTimeOffset _windowStart;

        public int Count => _count;
        public DateTimeOffset WindowStart => _windowStart;

        public IpRateLimit()
        {
            _windowStart = DateTimeOffset.UtcNow;
        }

        public bool TryIncrement(int limit, TimeSpan window)
        {
            var now = DateTimeOffset.UtcNow;

            // Reset window if expired
            if (now - _windowStart >= window)
            {
                _windowStart = now;
                _count = 0;
            }

            if (_count >= limit)
                return false;

            Interlocked.Increment(ref _count);
            return true;
        }

        public int SecondsUntilReset(int limit, TimeSpan window) =>
            (int)(window - (DateTimeOffset.UtcNow - _windowStart)).TotalSeconds;
    }

    /// <summary>
    /// Simple in-memory rate limiter (sliding window per IP).
    /// Limits: 30 resolve/minute, 120 stream/minute per IP.
    /// Returns 429 with Retry-After: 60 when exceeded.
    /// Exempts localhost / configured trusted IPs.
    /// </summary>
    public sealed class RateLimiter
    {
        private readonly ILogger<RateLimiter> _logger;
        private readonly ConcurrentDictionary<string, IpRateLimit> _resolveLimits = new();
        private readonly ConcurrentDictionary<string, IpRateLimit> _streamLimits = new();
        private readonly string[] _trustedIps;

        public const int ResolveLimitPerMinute = 30;
        public const int StreamLimitPerMinute = 120;
        private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
        private static readonly int RetryAfterSeconds = 60;

        public RateLimiter(ILogger<RateLimiter> logger, string[] trustedIps)
        {
            _logger = logger;
            _trustedIps = trustedIps ?? Array.Empty<string>();
        }

        /// <summary>
        /// Checks if the IP is allowed to make a resolve request.
        /// Returns null if allowed, or 429 response if rate limited.
        /// </summary>
        public object? CheckResolveLimit(string? ipAddress)
        {
            if (IsTrusted(ipAddress))
                return null;

            if (string.IsNullOrEmpty(ipAddress))
                return CreateRateLimitResponse("resolve", RetryAfterSeconds);

            var limit = _resolveLimits.GetOrAdd(ipAddress, _ => new IpRateLimit());

            if (!limit.TryIncrement(ResolveLimitPerMinute, OneMinute))
            {
                var retryAfter = limit.SecondsUntilReset(ResolveLimitPerMinute, OneMinute);
                _logger.LogWarning(
                    "[RateLimiter] Resolve rate limit exceeded for {Ip} ({Count}/{Limit}), retry after {RetryAfter}s",
                    ipAddress, limit.Count, ResolveLimitPerMinute, retryAfter);
                return CreateRateLimitResponse("resolve", retryAfter);
            }

            return null;
        }

        /// <summary>
        /// Checks if the IP is allowed to make a stream request.
        /// Returns null if allowed, or 429 response if rate limited.
        /// </summary>
        public object? CheckStreamLimit(string? ipAddress)
        {
            if (IsTrusted(ipAddress))
                return null;

            if (string.IsNullOrEmpty(ipAddress))
                return CreateRateLimitResponse("stream", RetryAfterSeconds);

            var limit = _streamLimits.GetOrAdd(ipAddress, _ => new IpRateLimit());

            if (!limit.TryIncrement(StreamLimitPerMinute, OneMinute))
            {
                var retryAfter = limit.SecondsUntilReset(StreamLimitPerMinute, OneMinute);
                _logger.LogWarning(
                    "[RateLimiter] Stream rate limit exceeded for {Ip} ({Count}/{Limit}), retry after {RetryAfter}s",
                    ipAddress, limit.Count, StreamLimitPerMinute, retryAfter);
                return CreateRateLimitResponse("stream", retryAfter);
            }

            return null;
        }

        private static object CreateRateLimitResponse(string endpoint, int retryAfter) =>
            new
            {
                StatusCode = 429,
                ErrorCode = "rate_limited",
                ErrorMessage = $"Too many {endpoint} requests. Please try again later.",
                RetryAfter = retryAfter
            };

        private bool IsTrusted(string? ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress))
                return false;

            // Check localhost variants
            if (ipAddress == "127.0.0.1" || ipAddress == "::1" || ipAddress == "localhost")
                return true;

            // Check configured trusted IPs
            foreach (var trusted in _trustedIps)
            {
                if (string.Equals(ipAddress, trusted, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the client IP address from the request.
        /// Handles forwarded headers (X-Forwarded-For, X-Real-IP).
        /// </summary>
        public static string? GetClientIp(IRequest request)
        {
            // Try X-Forwarded-For (take first IP)
            var forwarded = request.Headers["X-Forwarded-For"];
            if (!string.IsNullOrEmpty(forwarded))
            {
                var firstIp = forwarded.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(firstIp))
                    return firstIp;
            }

            // Try X-Real-IP
            var realIp = request.Headers["X-Real-IP"];
            if (!string.IsNullOrEmpty(realIp))
                return realIp;

            // Fall back to remote IP
            return request.RemoteIp?.ToString();
        }
    }
}
