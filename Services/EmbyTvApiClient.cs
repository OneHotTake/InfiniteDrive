using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    // ── Lightweight record types for Emby TV API responses ─────────────────────

    public record EmbySeasonInfo(string Id, int IndexNumber, string Name);

    public record EmbyEpisodeInfo(
        string Id,
        int? IndexNumber,
        int? ParentIndexNumber,
        string Name,
        Dictionary<string, string>? ProviderIds,
        string? LocationType);

    public record SeriesGapReport(
        string EmbyItemId,
        string Title,
        List<SeasonCoverage> Seasons,
        bool IsComplete);

    public record SeasonCoverage(
        int SeasonNumber,
        int PresentCount,
        int MissingCount,
        List<int> MissingEpisodeNumbers);

    // ── Client ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Thin HTTP client wrapping Emby's TV REST endpoints for season/episode
    /// gap detection. Uses loopback to the local Emby server.
    /// </summary>
    public class EmbyTvApiClient
    {
        private readonly HttpClient _http;
        private readonly ILogger _logger;

        public EmbyTvApiClient(ILogger logger)
        {
            _logger = logger;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        /// <summary>
        /// GET /Shows/{embySeriesId}/Seasons?userId=...&amp;Fields=ProviderIds
        /// </summary>
        public async Task<List<EmbySeasonInfo>> GetSeasonsAsync(
            string embySeriesId, string baseUrl, string token, CancellationToken ct)
        {
            var url = $"{baseUrl.TrimEnd('/')}/Shows/{Uri.EscapeDataString(embySeriesId)}/Seasons"
                      + "?Fields=ProviderIds";
            return await GetListAsync<EmbySeasonInfo>(url, token, ct, "GetSeasons");
        }

        /// <summary>
        /// GET /Shows/{embySeriesId}/Episodes?SeasonId={seasonId}&amp;Fields=ProviderIds,Path
        /// </summary>
        public async Task<List<EmbyEpisodeInfo>> GetEpisodesAsync(
            string embySeriesId, string seasonId, string baseUrl, string token, CancellationToken ct)
        {
            var url = $"{baseUrl.TrimEnd('/')}/Shows/{Uri.EscapeDataString(embySeriesId)}/Episodes"
                      + $"?SeasonId={Uri.EscapeDataString(seasonId)}"
                      + "&Fields=ProviderIds,Path&IsMissing=false";
            return await GetListAsync<EmbyEpisodeInfo>(url, token, ct, "GetEpisodes");
        }

        /// <summary>
        /// GET /Shows/Missing?ParentId={embySeriesId}&amp;Fields=ProviderIds&amp;IsUnaired=false
        /// </summary>
        public async Task<List<EmbyEpisodeInfo>> GetMissingEpisodesAsync(
            string embySeriesId, string baseUrl, string token, CancellationToken ct)
        {
            var url = $"{baseUrl.TrimEnd('/')}/Shows/Missing"
                      + $"?ParentId={Uri.EscapeDataString(embySeriesId)}"
                      + "&Fields=ProviderIds&IsUnaired=false";
            return await GetListAsync<EmbyEpisodeInfo>(url, token, ct, "GetMissingEpisodes");
        }

        // ── Private ────────────────────────────────────────────────────────────

        private async Task<List<T>> GetListAsync<T>(
            string url, string token, CancellationToken ct, string operation)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Emby-Token", token);

                _logger.LogDebug("[EmbyTvApiClient] {Op}: {Url}", operation, url);

                using var resp = await _http.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[EmbyTvApiClient] {Op} returned {Status} for {Url}",
                        operation, (int)resp.StatusCode, url);
                    return new List<T>();
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("Items", out var items))
                    return new List<T>();

                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                };

                return items.EnumerateArray()
                    .Select(el => SafeDeserialize<T>(el, opts))
                    .Where(x => x != null)
                    .Cast<T>()
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyTvApiClient] {Op} failed for {Url}", operation, url);
                return new List<T>();
            }
        }

        private static T? SafeDeserialize<T>(JsonElement el, JsonSerializerOptions opts)
        {
            try { return el.Deserialize<T>(opts); }
            catch { return default; }
        }
    }
}
