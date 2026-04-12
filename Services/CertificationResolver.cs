using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Fetches US MPAA/TV certifications from TMDB for discover catalog items.
    /// Sprint 209: Parental filtering for Discover browse/search.
    /// </summary>
    public class CertificationResolver
    {
        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, (string? Cert, DateTimeOffset CachedAt)> _cache
            = new();

        private const string TmdbBaseUrl = "https://api.themoviedb.org/3";
        private const int CacheTtlHours = 24;
        private const int RateLimitDelayMs = 25;

        /// <summary>
        /// Creates a new CertificationResolver.
        /// </summary>
        public CertificationResolver(HttpClient http, ILogger logger)
        {
            _http = http;
            _logger = logger;
        }

        /// <summary>
        /// Fetches US certification (MPAA rating) for a movie from TMDB.
        /// Returns null if no certification found or TMDB key not configured.
        /// </summary>
        public async Task<string?> FetchCertificationAsync(
            string tmdbId,
            CancellationToken ct = default)
        {
            var apiKey = Plugin.Instance?.Configuration?.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                return null;

            // Check cache
            if (_cache.TryGetValue(tmdbId, out var cached))
            {
                if (DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromHours(CacheTtlHours))
                    return cached.Cert;
            }

            // Not in cache or expired, fetch from TMDB
            var cert = await FetchMovieCertificationInternalAsync(tmdbId, apiKey, ct);
            _cache[tmdbId] = (cert, DateTimeOffset.UtcNow);
            return cert;
        }

        /// <summary>
        /// Batch-fetches certifications for multiple items.
        /// Respects TMDB rate limits (25ms delay between requests).
        /// Max 50 items per call to stay within free tier.
        /// Returns dictionary mapping IMDB ID to certification.
        /// </summary>
        public async Task<Dictionary<string, string>> FetchCertificationsBatchAsync(
            List<(string ImdbId, string? TmdbId)> items,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, string>();
            var apiKey = Plugin.Instance?.Configuration?.TmdbApiKey;

            if (string.IsNullOrWhiteSpace(apiKey) || items.Count == 0)
                return result;

            // Limit batch size
            var batch = items.Take(50).ToList();

            foreach (var (imdbId, tmdbId) in batch)
            {
                if (ct.IsCancellationRequested)
                    break;

                // Skip if we already have TMDB ID
                if (string.IsNullOrWhiteSpace(tmdbId))
                    continue;

                // Check cache first
                if (_cache.TryGetValue(tmdbId, out var cached))
                {
                    if (DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromHours(CacheTtlHours))
                    {
                        if (cached.Cert != null)
                            result[imdbId] = cached.Cert;
                        continue;
                    }
                }

                // Fetch from TMDB
                try
                {
                    var cert = await FetchMovieCertificationInternalAsync(tmdbId, apiKey, ct);
                    _cache[tmdbId] = (cert, DateTimeOffset.UtcNow);

                    if (cert != null)
                        result[imdbId] = cert;

                    // Rate limit delay
                    await Task.Delay(RateLimitDelayMs, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CertificationResolver] Failed to fetch certification for TMDB ID {TmdbId}", tmdbId);
                }
            }

            return result;
        }

        private async Task<string?> FetchMovieCertificationInternalAsync(string tmdbId, string apiKey, CancellationToken ct)
        {
            try
            {
                var url = $"{TmdbBaseUrl}/movie/{tmdbId}/release_dates?api_key={apiKey}";
                var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("results", out var results))
                    return null;

                // Find US certification
                string? certification = null;
                int priority = -1;

                foreach (var item in results.EnumerateArray())
                {
                    if (item.TryGetProperty("iso_3166_1", out var iso) && iso.GetString() == "US")
                    {
                        // Priority: theatrical (3) > digital (4) > premiere (1) > any
                        int currentPriority = item.TryGetProperty("type", out var typeProp)
                            ? typeProp.GetInt32()
                            : 0;

                        if (currentPriority > priority)
                        {
                            priority = currentPriority;
                            certification = item.TryGetProperty("release_dates", out var dates)
                                ? dates.EnumerateArray().FirstOrDefault().TryGetProperty("certification", out var cert)
                                    ? cert.GetString()
                                    : null
                                : null;
                        }
                    }
                }

                return string.IsNullOrWhiteSpace(certification) ? null : certification;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CertificationResolver] TMDB API error for movie {TmdbId}", tmdbId);
                return null;
            }
        }

        /// <summary>
        /// Clears the certification cache.
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();
        }
    }
}
