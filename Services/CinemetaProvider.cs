using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Provider for Cinemeta metadata API.
    /// Wraps AIOStreams GetMetadata for Cinemeta URLs.
    /// </summary>
    public class CinemetaProvider
    {
        private static readonly string CinemetaBaseUrl = "https://v3-cinemeta.metahd.io";
        private readonly ILogger<CinemetaProvider> _logger;
        private readonly HttpClient _httpClient;

        public CinemetaProvider(ILogger<CinemetaProvider> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        /// <summary>
        /// Gets metadata for a media item from Cinemeta.
        /// </summary>
        public async Task<CinemetaMetadata?> GetMetadataAsync(
            string id,
            string mediaType,
            CancellationToken ct = default)
        {
            try
            {
                var url = $"{CinemetaBaseUrl}/content/{id}.json";
                _logger.LogDebug("[CinemetaProvider] Fetching metadata from {Url}", url);

                var response = await _httpClient.GetStringAsync(url, ct);
                var json = JsonDocument.Parse(response);

                if (!json.RootElement.TryGetProperty("meta", out var metaElement))
                    return null;

                var title = metaElement.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;

                var year = metaElement.TryGetProperty("year", out var yearElement)
                    ? yearElement.GetInt32()
                    : (int?)null;

                return new CinemetaMetadata
                {
                    Title = title,
                    Year = year
                    };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CinemetaProvider] Failed to fetch metadata for {Id}", id);
                return null;
            }
        }
    }

    /// <summary>
    /// Cinemeta metadata response.
    /// </summary>
    public class CinemetaMetadata
    {
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
    }
}
