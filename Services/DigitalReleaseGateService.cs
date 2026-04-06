using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Verifies digital release status via TMDB API.
    /// Applies only to SourceType.BuiltIn (Trending Movies, etc.).
    /// </summary>
    public class DigitalReleaseGateService
    {
        private readonly HttpClient _http;
        private readonly ILogger<DigitalReleaseGateService> _logger;

        // Cache results for 24 hours to avoid redundant TMDB calls
        private readonly ConcurrentDictionary<string, (bool Result, DateTimeOffset CachedAt)> _releaseCache
            = new();

        public DigitalReleaseGateService(HttpClient http, ILogger<DigitalReleaseGateService> logger)
        {
            _http = http;
            _logger = logger;
        }

        /// <summary>
        /// Checks if an item has been digitally released.
        /// </summary>
        public async Task<bool> IsDigitallyReleasedAsync(
            MediaId id,
            string mediaType,
            string sourceType,
            CancellationToken ct = default)
        {
            _logger.LogDebug("[DigitalReleaseGate] Checking {MediaId} ({MediaType}, {SourceType})",
                id.ToString(), mediaType, sourceType);

            // Series bypass gate unconditionally
            if (mediaType == "series")
            {
                _logger.LogDebug("[DigitalReleaseGate] Bypassed for series media type");
                return true;
            }

            // User-added sources bypass gate unconditionally
            if (sourceType != nameof(SourceType.BuiltIn))
            {
                _logger.LogDebug("[DigitalReleaseGate] Bypassed for user-added source {SourceType}", sourceType);
                return true;
            }

            // Check cache
            var cacheKey = $"release_gate:{id.ToString()}";
            if (_releaseCache.TryGetValue(cacheKey, out var cached) &&
                (DateTimeOffset.UtcNow - cached.CachedAt).TotalHours < 24)
            {
                _logger.LogDebug("[DigitalReleaseGate] Cache hit for {MediaId}: {Result}",
                    id.ToString(), cached.Result);
                return cached.Result;
            }

            // Query TMDB release date
            var isReleased = await CheckTmdbReleaseAsync(id, ct);

            // Cache result
            _releaseCache[cacheKey] = (isReleased, DateTimeOffset.UtcNow);

            _logger.LogInformation("[DigitalReleaseGate] {MediaId}: {Released}",
                id.ToString(), isReleased ? "Released" : "Not released");

            return isReleased;
        }

        /// <summary>
        /// Queries TMDB for release date and checks if title is available.
        /// </summary>
        private async Task<bool> CheckTmdbReleaseAsync(MediaId id, CancellationToken ct)
        {
            try
            {
                // Only works with TMDB IDs
                if (id.Type != MediaIdType.Tmdb)
                {
                    _logger.LogDebug("[DigitalReleaseGate] Non-TMDB ID {Type}, skipping check", id.Type);
                    return true;
                }

                var url = $"https://api.themoviedb.org/3/movie/{id.Value}?api_key={GetTmdbApiKey()}&append_to_response=releases";
                var response = await _http.GetStringAsync(url, ct);
                var json = JsonDocument.Parse(response);

                // Check release dates
                if (json.RootElement.TryGetProperty("release_dates", out var releaseDates))
                {
                    if (releaseDates.TryGetProperty("us", out var usRelease))
                    {
                        // Check digital release first
                        if (usRelease.TryGetProperty("digital", out var digitalProp) &&
                            DateTime.TryParse(digitalProp.GetString(), out var digitalDate))
                        {
                            return DateTimeOffset.UtcNow >= digitalDate;
                        }

                        // Fallback to theatrical release
                        if (usRelease.TryGetProperty("theatrical", out var theatricalProp) &&
                            DateTime.TryParse(theatricalProp.GetString(), out var theatricalDate))
                        {
                            return DateTimeOffset.UtcNow >= theatricalDate;
                        }
                    }
                }

                // Fallback: check release_date property
                if (json.RootElement.TryGetProperty("release_date", out var releaseDateProp) &&
                    DateTime.TryParse(releaseDateProp.GetString(), out var releaseDate))
                {
                    return DateTimeOffset.UtcNow >= releaseDate;
                }

                _logger.LogWarning("[DigitalReleaseGate] No release date found for {MediaId}", id.ToString());
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DigitalReleaseGate] Failed to query TMDB for {MediaId}", id.ToString());
                return false;
            }
        }

        /// <summary>
        /// Gets TMDB API key from configuration.
        /// </summary>
        private string GetTmdbApiKey()
        {
            // TODO: Get from Plugin.Instance.Configuration.TmdbApiKey
            // For now, return empty string which will cause the check to fail gracefully
            return string.Empty;
        }

        /// <summary>
        /// Clears expired cache entries.
        /// </summary>
        public void ClearExpiredCache()
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            foreach (var key in _releaseCache.Keys.Where(k => _releaseCache[k].CachedAt < cutoff))
            {
                _releaseCache.TryRemove(key, out _);
            }
        }
    }
}
