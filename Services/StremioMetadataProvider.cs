using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Fetches full series metadata from AIOStreams or Cinemeta using the Stremio protocol.
    /// Returns the complete Videos[] array with all episodes.
    ///
    /// TODO: This provider does NOT use the MetadataService priority chain.
    /// It directly fetches from a single base URL instead of trying AIOStreams /meta
    /// first, then falling back to the configured metadata provider.
    /// Consider refactoring to use MetadataService.ResolveMetadataAsync().
    /// </summary>
    public class StremioMetadataProvider : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _baseUrl;

        // File-scoped static — one socket pool for all StremioMetadataProvider
        // instances. HttpClient is thread-safe and designed for reuse.
        private static readonly HttpClient _sharedHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public StremioMetadataProvider(string baseUrl, ILogger logger)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _logger = logger;
        }

        public void Dispose()
        {
            // _sharedHttp is static — not disposed per instance.
        }

        public async Task<StremioMeta?> GetFullSeriesMetaAsync(string id, CancellationToken ct = default)
        {
            try
            {
                _logger.LogDebug("[EmbyStreams] Fetching series meta for {Id}", id);
                var url = $"{_baseUrl}/meta/series/{id}.json";

                using var resp = await _sharedHttp.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[EmbyStreams] Series meta fetch failed for {Id}: {Code} {Reason}",
                        id, (int)resp.StatusCode, resp.ReasonPhrase);
                    return null;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var wrapper = await JsonSerializer.DeserializeAsync<StremioMetaResponse>(stream, JsonOpts, ct);
                return wrapper?.Meta;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Error fetching series meta for {Id}", id);
                return null;
            }
        }
    }

    // ── Response / Meta models ──────────────────────────────────────────────────────────────────

    public class StremioMetaResponse
    {
        [JsonPropertyName("meta")]
        public required StremioMeta Meta { get; set; }
    }

    public class StremioMeta
    {
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("poster")]
        public string? Poster { get; set; }

        [JsonPropertyName("releaseInfo")]
        public string? ReleaseInfo { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("videos")]
        public List<StremioVideo>? Videos { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        /// <summary>TMDB numeric ID. Written to NFO as &lt;tmdbid&gt;.</summary>
        [JsonPropertyName("tmdb_id")]
        public string? TmdbId { get; set; }

        /// <summary>Alternative TMDB field name used by some Stremio addons.</summary>
        [JsonPropertyName("tmdb")]
        public string? TmdbAlt { get; set; }

        /// <summary>AniList numeric ID. Written to NFO as &lt;anilistid&gt;.</summary>
        [JsonPropertyName("anilist_id")]
        public string? AniListId { get; set; }

        /// <summary>Kitsu numeric ID. Written to NFO as &lt;kitsuid&gt;.</summary>
        [JsonPropertyName("kitsu_id")]
        public string? KitsuId { get; set; }

        /// <summary>MyAnimeList numeric ID. Written to NFO as &lt;malid&gt;.</summary>
        [JsonPropertyName("mal_id")]
        public string? MalId { get; set; }

        /// <summary>
        /// Returns TmdbId ?? TmdbAlt — handles both JSON field name
        /// variants used by different Stremio addons.
        /// </summary>
        public string? GetTmdbId() => TmdbId ?? TmdbAlt;

        [JsonPropertyName("released")]
        public DateTime? Released { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonConverter(typeof(NullableIntLenientConverter))]
        public int? Year { get; set; }

        [JsonPropertyName("first_aired")]
        public DateTime? FirstAired { get; set; }

        [JsonPropertyName("genres")]
        public List<string>? Genres { get; set; }

        public string GetName() => Title ?? Name ?? "";

        public bool IsValid() =>
            !Id.Contains("error", StringComparison.OrdinalIgnoreCase);

        public bool IsReleased(int bufferDays = 0)
        {
            var now = DateTime.UtcNow;
            if (Released.HasValue)
                return Released.Value.AddDays(bufferDays) <= now;
            if (FirstAired.HasValue)
                return FirstAired.Value.AddDays(bufferDays) <= now;
            if (Status is "Ended" or "Continuing")
                return true;
            var year = GetYear();
            if (year.HasValue)
                return new DateTime(year.Value, 6, 1).AddDays(bufferDays) <= now;
            return false;
        }

        public int? GetYear()
        {
            if (Year is not null) return Year;
            if (Released is { } dt) return dt.Year;

            if (!string.IsNullOrWhiteSpace(ReleaseInfo))
            {
                var s = ReleaseInfo.Trim();
                if (s.Length >= 4 && int.TryParse(s.AsSpan(0, 4), out var y) && y is > 1800 and < 2200)
                    return y;
                var dash = s.IndexOf('-');
                if (dash > 0 && int.TryParse(s[..dash], out var y2) && y2 is > 1800 and < 2200)
                    return y2;
                if (int.TryParse(s, out var plain) && plain is > 1800 and < 2200)
                    return plain;
            }

            return null;
        }
    }

    public class StremioVideo
    {
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("released")]
        public DateTime? Released { get; set; }

        [JsonPropertyName("first_aired")]
        public DateTime? FirstAired { get; set; }

        [JsonPropertyName("thumbnail")]
        public string? Thumbnail { get; set; }

        [JsonPropertyName("episode")]
        public int? Episode { get; set; }

        [JsonPropertyName("season")]
        public int? Season { get; set; }

        [JsonPropertyName("number")]
        public int? Number { get; set; }

        [JsonPropertyName("absoluteEpisodeNumber")]
        public int? AbsoluteEpisodeNumber { get; set; }

        public string GetName() => Name ?? "";

        public bool IsReleased(int bufferDays = 0)
        {
            var now = DateTime.UtcNow;
            if (Released.HasValue)
                return Released.Value.AddDays(bufferDays) <= now;
            if (FirstAired.HasValue)
                return FirstAired.Value.AddDays(bufferDays) <= now;
            return true; // assume released if we have metadata for it
        }
    }

    public class NullableIntLenientConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader r, Type typeToConvert, JsonSerializerOptions options)
        {
            return r.TokenType switch
            {
                JsonTokenType.Number => r.TryGetInt32(out var i) ? i : null,
                JsonTokenType.String => int.TryParse(r.GetString(), out var v) ? v : null,
                _ => null
            };
        }

        public override void Write(Utf8JsonWriter w, int? v, JsonSerializerOptions _)
        {
            if (v.HasValue) w.WriteNumberValue(v.Value);
            else w.WriteNullValue();
        }
    }
}