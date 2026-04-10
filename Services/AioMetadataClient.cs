using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Client for fetching enriched metadata from AIOMetadata.
    /// Exclusive enrichment source — no Cinemeta or other providers.
    /// </summary>
    public class AioMetadataClient
    {
        private readonly PluginConfiguration _config;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;

        private const int TimeoutSeconds = 10;
        private Uri? _baseUrl;

        public AioMetadataClient(PluginConfiguration config, ILogger logger)
        {
            _config = config;
            _logger = logger;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
            };

            if (!string.IsNullOrEmpty(config.AioMetadataBaseUrl))
            {
                try
                {
                    _baseUrl = new Uri(config.AioMetadataBaseUrl);
                }
                catch (UriFormatException)
                {
                    _logger.LogWarning("[AioMetadata] Invalid AioMetadataBaseUrl configured");
                }
            }
        }

        /// <summary>
        /// Fetches metadata for the given IMDB ID.
        /// Returns null on failure (caller handles retry).
        /// 10-second hard timeout, rate-limited by caller to 1 call per 2 seconds.
        /// </summary>
        public async Task<EnrichedMetadata?> FetchAsync(string imdbId, int? year, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_config?.AioMetadataBaseUrl))
            {
                _logger.LogWarning("[AioMetadata] AioMetadataBaseUrl not configured");
                return null;
            }

            var baseUrl = _baseUrl ?? new Uri(_config.AioMetadataBaseUrl);

            // Build lookup URL - prefer IMDB ID, fall back to TMDB
            string lookupId;
            string lookupType;

            if (imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                lookupId = imdbId;
                lookupType = "movie"; // AIOMetadata uses 'movie' for both movies and series
            }
            else
            {
                // Fall back to TMDB lookup
                var db = Plugin.Instance?.DatabaseManager;
                if (db != null)
                {
                    var catalogItem = await db.GetCatalogItemByImdbIdAsync(imdbId);
                    lookupId = catalogItem?.TmdbId ?? imdbId;
                }
                else
                {
                    lookupId = imdbId;
                }
                lookupType = "tmdb";
            }

            var url = $"{baseUrl?.Scheme ?? "https"}://{baseUrl?.Host}/meta/{lookupType}/{lookupId}.json";

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);

                var response = await _httpClient.GetAsync(url, linkedCts.Token);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();

                // Parse JSON response
                var meta = ParseAioMetadataResponse(json, imdbId);
                return meta;
            }
            catch (HttpRequestException ex) when (ex.StatusCode != null)
            {
                _logger.LogWarning(ex, "[AioMetadata] HTTP error fetching metadata for {ImdbId}", imdbId);
                return null;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "[AioMetadata] HTTP {Status} error fetching metadata for {ImdbId}",
                    ex.StatusCode, imdbId);
                return null;
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("[AioMetadata] Timeout fetching metadata for {ImdbId}", imdbId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AioMetadata] Error fetching metadata for {ImdbId}", imdbId);
                return null;
            }
        }

        private EnrichedMetadata? ParseAioMetadataResponse(string json, string originalImdbId)
        {
            try
            {
                // Simple JSON parsing - AIOMetadata returns:
                // { "meta": { "id": "tt123", "name": "Title", "year": 2024, "description": "...", "imdb_id": "tt123", "tmdb_id": "123", "genres": ["Action", "Drama"] } }

                // Find meta object start
                var metaStart = json.IndexOf("\"meta\":");
                if (metaStart < 0)
                    return null;

                // Extract values using string operations (avoid JSON dependency)
                var meta = new EnrichedMetadata();

                // Parse name
                var nameStart = json.IndexOf("\"name\":", metaStart);
                if (nameStart > 0)
                {
                    var nameEnd = json.IndexOf("\"", nameStart + 8);
                    if (nameEnd > nameStart)
                    {
                        meta.Name = json.Substring(nameStart + 8, nameEnd - (nameStart + 8));
                        meta.Name = UnescapeJsonString(meta.Name);
                    }
                }

                // Parse year
                var yearStart = json.IndexOf("\"year\":", metaStart);
                if (yearStart > 0)
                {
                    var yearEnd = json.IndexOf(",", yearStart + 7);
                    if (yearEnd > yearStart)
                    {
                        var yearStr = json.Substring(yearStart + 7, yearEnd - (yearStart + 7));
                        if (int.TryParse(yearStr, out var year))
                            meta.Year = year;
                    }
                }

                // Parse description (plot)
                var descStart = json.IndexOf("\"description\":", metaStart);
                if (descStart > 0)
                {
                    var descEnd = json.IndexOf("\"", descStart + 15);
                    if (descEnd > descStart)
                    {
                        meta.Description = json.Substring(descStart + 15, descEnd - (descStart + 15));
                        meta.Description = UnescapeJsonString(meta.Description);
                    }
                }

                // Parse imdb_id
                var imdbStart = json.IndexOf("\"imdb_id\":", metaStart);
                if (imdbStart > 0)
                {
                    var imdbEnd = json.IndexOf("\"", imdbStart + 10);
                    if (imdbEnd > imdbStart)
                    {
                        meta.ImdbId = json.Substring(imdbStart + 10, imdbEnd - (imdbStart + 10));
                    }
                }
                else
                {
                    meta.ImdbId = originalImdbId;
                }

                // Parse tmdb_id
                var tmdbStart = json.IndexOf("\"tmdb_id\":", metaStart);
                if (tmdbStart > 0)
                {
                    var tmdbEnd = json.IndexOf("\"", tmdbStart + 10);
                    if (tmdbEnd > tmdbStart)
                    {
                        meta.TmdbId = json.Substring(tmdbStart + 10, tmdbEnd - (tmdbStart + 10));
                    }
                }

                // Parse genres array
                var genresStart = json.IndexOf("\"genres\":", metaStart);
                if (genresStart > 0)
                {
                    var arrayEnd = json.IndexOf("]", genresStart + 10);
                    if (arrayEnd > genresStart)
                    {
                        var genresJson = json.Substring(genresStart + 10, arrayEnd - (genresStart + 10));
                        meta.Genres = ParseJsonStringArray(genresJson);
                    }
                }

                return meta.Name != string.Empty ? meta : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AioMetadata] Failed to parse JSON response");
                return null;
            }
        }

        private List<string>? ParseJsonStringArray(string json)
        {
            var result = new List<string>();
            var pos = 0;

            while (pos < json.Length)
            {
                // Skip whitespace
                while (pos < json.Length && char.IsWhiteSpace(json[pos]))
                    pos++;

                if (pos >= json.Length)
                    break;

                // Find string start
                if (json[pos] == '"')
                {
                    pos++;
                    var start = pos;

                    // Find string end
                    while (pos < json.Length && json[pos] != '"')
                    {
                        if (json[pos] == '\\' && pos + 1 < json.Length)
                        {
                            pos++; // Skip escaped character
                        }
                        pos++;
                    }

                    if (pos < json.Length)
                    {
                        var value = json.Substring(start, pos - start);
                        result.Add(UnescapeJsonString(value));
                    }

                    pos++; // Skip closing quote
                }
                else if (json[pos] == ']' || json[pos] == '}')
                {
                    break;
                }
                else
                {
                    pos++;
                }
            }

            return result.Count > 0 ? result : null;
        }

        private static string UnescapeJsonString(string value)
        {
            return value
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        public record EnrichedMetadata
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public int? Year { get; set; }
            public string ImdbId { get; set; } = string.Empty;
            public string TmdbId { get; set; } = string.Empty;
            public List<string>? Genres { get; set; }
        }
    }
}
