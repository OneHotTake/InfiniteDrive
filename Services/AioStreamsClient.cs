using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services
{
    // ── Stremio resources polymorphic converter ──────────────────────────────────
    // The Stremio spec allows "resources" to be either a plain string array
    // ["catalog","meta"] or an object array [{"name":"stream","types":["movie"]}].
    // Cinemeta uses the string form; AIOStreams uses the object form.
    // This converter handles both transparently.

    internal sealed class ResourceListConverter : JsonConverter<List<AioStreamsResource>>
    {
        public override List<AioStreamsResource> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var list = new List<AioStreamsResource>();
            if (reader.TokenType != JsonTokenType.StartArray)
                return list;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var name = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add(new AioStreamsResource { Name = name });
                    // else: skip empty/null string entries in resources array
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    // NOTE: inner.Converters.Remove(this) is intentionally a no-op.
                    // 'inner' is a copy of options — Remove() compares by reference
                    // and finds no match. This is safe because System.Text.Json
                    // falls through to the default object deserializer for
                    // JsonTokenType.StartObject, avoiding recursion.
                    // Do NOT attempt to make this removal work — it would cause
                    // infinite recursion on object-form resource entries.
                    var inner = new JsonSerializerOptions(options);
                    // inner.Converters.Remove(this); // no-op by design — see above
                    var res = JsonSerializer.Deserialize<AioStreamsResource>(ref reader, inner)
                              ?? new AioStreamsResource();
                    list.Add(res);
                }
                else
                {
                    reader.Skip();
                }
            }
            return list;
        }

        public override void Write(
            Utf8JsonWriter writer, List<AioStreamsResource> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, options);
    }

    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Centralised HTTP client for all communication with an AIOStreams instance.
    ///
    /// Handles both authenticated and unauthenticated URL formats:
    /// <list type="bullet">
    ///   <item>Unauthenticated: <c>{base}/stremio/{resource}</c></item>
    ///   <item>Authenticated:   <c>{base}/stremio/{uuid}/{token}/{resource}</c></item>
    /// </list>
    ///
    /// All public methods accept a <see cref="CancellationToken"/> and log errors
    /// via the supplied <see cref="ILogger"/> rather than throwing.
    /// </summary>
    public class AioStreamsClient : IManifestProvider
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string UserAgent     = "InfiniteDrive (+https://github.com/OneHotTake/InfiniteDrive)";
        private const int    TimeoutSeconds = 60;  // Increased from 30s to handle slow AIOStreams responses (10+ seconds)

        // ── Fields ──────────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOptions =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // File-scoped static — one socket pool for all AioStreamsClient
        // instances. HttpClient is thread-safe and designed for reuse.
        private static readonly HttpClient _sharedHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
        };

        static AioStreamsClient()
        {
            _sharedHttp.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", UserAgent);
        }

        private readonly ILogger  _logger;
        private readonly string   _stremioBase;
        private readonly string?  _rawToken;     // stored for log sanitization only

        /// <summary>
        /// Optional cooldown gate for pre-call throttling and 429 backoff.
        /// Set by tasks/services that want automatic rate-limit handling.
        /// When null, no throttling or 429 detection is applied.
        /// </summary>
        public CooldownGate? Cooldown { get; set; }

        /// <summary>
        /// The cooldown kind to use for WaitAsync calls. Default: StreamResolve.
        /// Callers can set this to SeriesMeta for different delay profiles.
        /// </summary>
        public CooldownKind ActiveCooldownKind { get; set; } = CooldownKind.Default;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the client from a <see cref="PluginConfiguration"/>.
        /// Parses <see cref="PluginConfiguration.PrimaryManifestUrl"/> first,
        /// falling back to <see cref="PluginConfiguration.SecondaryManifestUrl"/> if needed.
        /// </summary>
        public AioStreamsClient(PluginConfiguration config, ILogger logger)
        {
            _logger = logger;

            // Attempt to parse the primary manifest URL.
            var (baseUrl, uuid, token) = TryParseManifestUrl(config.PrimaryManifestUrl);

            // Fall back to secondary manifest URL if configured (URL presence is the toggle).
            if (string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                (baseUrl, uuid, token) = TryParseManifestUrl(config.SecondaryManifestUrl);

            // Build the base Stremio path segment.
            _stremioBase = BuildStremioBase(baseUrl, uuid, token);
            _rawToken    = string.IsNullOrWhiteSpace(token) ? null : token;
        }

        /// <summary>
        /// Direct constructor for when base URL, UUID, and token are already known.
        /// Used by tests and the dashboard health check endpoint.
        /// </summary>
        public AioStreamsClient(string baseUrl, string? uuid, string? token, ILogger logger)
        {
            _logger      = logger;
            _stremioBase = BuildStremioBase(baseUrl.TrimEnd('/'), uuid, token);
            _rawToken    = string.IsNullOrWhiteSpace(token) ? null : token;
        }

        /// <summary>
        /// Direct constructor for standard Stremio addons whose manifest lives at
        /// <c>{stremioBase}/manifest.json</c> with NO additional <c>/stremio/</c>
        /// path segment (e.g. Cinemeta: <c>https://v3-cinemeta.strem.io</c>).
        ///
        /// Use this instead of <see cref="AioStreamsClient(string,string?,string?,ILogger)"/>
        /// when the caller already knows the exact Stremio base path.
        /// </summary>
        public static AioStreamsClient CreateForStremioBase(string stremioBase, ILogger logger)
        {
            return new AioStreamsClient(stremioBase, logger);
        }

        // Private constructor used by CreateForStremioBase — sets _stremioBase directly.
        private AioStreamsClient(string directStremioBase, ILogger logger)
        {
            _logger      = logger;
            _stremioBase = directStremioBase.TrimEnd('/');
            _rawToken    = null;
        }

        // ── Public URL properties ───────────────────────────────────────────────

        /// <summary>
        /// The fully-qualified manifest URL for this AIOStreams instance.
        /// Useful for connection testing and display in the health dashboard.
        /// </summary>
        public string ManifestUrl => $"{_stremioBase}/manifest.json";

        /// <summary>
        /// Returns true when the client has a non-empty base URL.
        /// </summary>
        public bool IsConfigured => !string.IsNullOrEmpty(_stremioBase);

        // ── Manifest ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches and parses the AIOStreams manifest.
        /// Returns null on any error (connectivity, auth, parse failure).
        /// </summary>
        public async Task<AioStreamsManifest?> GetManifestAsync(
            CancellationToken cancellationToken = default)
        {
            return await GetJsonAsync<AioStreamsManifest>(ManifestUrl, cancellationToken);
        }

        // ── Catalogs ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches a catalog page from AIOStreams.
        /// </summary>
        /// <param name="type">Media type: <c>movie</c>, <c>series</c>, or <c>anime</c>.</param>
        /// <param name="catalogId">Catalog identifier from the manifest.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<AioStreamsCatalogResponse?> GetCatalogAsync(
            string type,
            string catalogId,
            CancellationToken cancellationToken = default)
        {
            var url = $"{_stremioBase}/catalog/{type}/{Uri.EscapeDataString(catalogId)}.json";
            return await GetJsonAsync<AioStreamsCatalogResponse>(url, cancellationToken);
        }

        /// <summary>
        /// Fetches a catalog page with optional extra parameters (genre, search query, skip).
        /// </summary>
        public async Task<AioStreamsCatalogResponse?> GetCatalogAsync(
            string type,
            string catalogId,
            string? searchQuery,
            string? genre,
            int? skip,
            CancellationToken cancellationToken = default)
        {
            // Build extra segment following the Stremio extra-params convention:
            //   /catalog/{type}/{id}/{extra1=val1}&{extra2=val2}.json
            var extras = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(searchQuery))
                Append(extras, $"search={Uri.EscapeDataString(searchQuery)}");
            if (!string.IsNullOrEmpty(genre))
                Append(extras, $"genre={Uri.EscapeDataString(genre)}");
            if (skip.HasValue && skip.Value > 0)
                Append(extras, $"skip={skip.Value}");

            var url = extras.Length > 0
                ? $"{_stremioBase}/catalog/{type}/{Uri.EscapeDataString(catalogId)}/{extras}.json"
                : $"{_stremioBase}/catalog/{type}/{Uri.EscapeDataString(catalogId)}.json";

            return await GetJsonAsync<AioStreamsCatalogResponse>(url, cancellationToken);
        }

        // ── Live Search ──────────────────────────────────────────────────────

        /// <summary>
        /// Performs a live search against AIOStreams using the "top" catalog with search extra.
        /// Falls back to Cinemeta search if AIOStreams returns no results.
        /// </summary>
        public async Task<AioStreamsCatalogResponse?> SearchLiveAsync(
            string query, string type, int skip, int limit,
            CancellationToken cancellationToken = default)
        {
            // Search AIOStreams top catalog
            var result = await GetCatalogAsync(type, "top", query, null, skip, cancellationToken);
            if (result?.Metas?.Count > 0)
                return new AioStreamsCatalogResponse { Metas = result.Metas.Take(limit).ToList() };

            // Fallback: try Cinemeta search
            try
            {
                var cinemetaUrl = $"https://v3-cinemeta.strem.io/catalog/{type}/top/search={Uri.EscapeDataString(query)}.json";
                var cinemetaResult = await GetJsonAsync<AioStreamsCatalogResponse>(cinemetaUrl, cancellationToken);
                if (cinemetaResult?.Metas?.Count > 0)
                    return new AioStreamsCatalogResponse { Metas = cinemetaResult.Metas.Skip(skip).Take(limit).ToList() };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Cinemeta search fallback failed for '{Query}'", query);
            }

            return result;
        }

        /// <summary>
        /// Fetches Cinemeta Top 10 items for a given type.
        /// Returns up to <paramref name="count"/> items.
        /// </summary>
        public async Task<List<AioStreamsMeta>> GetCinemetaTopAsync(
            string type, int count, CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"https://v3-cinemeta.strem.io/catalog/{type}/top.json";
                var result = await GetJsonAsync<AioStreamsCatalogResponse>(url, cancellationToken);
                return result?.Metas?.Take(count).ToList() ?? new List<AioStreamsMeta>();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Cinemeta top fetch failed for type '{Type}'", type);
                return new List<AioStreamsMeta>();
            }
        }

        // ── Streams ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches streams for a movie by IMDB ID.
        /// AIOStreams returns streams sorted by user preference; always use index 0.
        /// </summary>
        public Task<AioStreamsStreamResponse?> GetMovieStreamsAsync(
            string imdbId,
            CancellationToken cancellationToken = default,
            string? sel = null)
            => GetStreamsCoreAsync("movie", imdbId, "GetMovieStreamsAsync", sel, cancellationToken);

        /// <summary>
        /// Fetches streams for a TV episode.
        /// AIOStreams returns streams sorted by user preference; always use index 0.
        /// </summary>
        public Task<AioStreamsStreamResponse?> GetSeriesStreamsAsync(
            string imdbId,
            int season,
            int episode,
            CancellationToken cancellationToken = default,
            string? sel = null)
            => GetStreamsCoreAsync("series", $"{imdbId}:{season}:{episode}", "GetSeriesStreamsAsync", sel, cancellationToken);

        private async Task<AioStreamsStreamResponse?> GetStreamsCoreAsync(
            string type, string id, string label, string? sel, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            _logger.LogInformation("[AioStreamsClient] START: {Label} for {Id}", label, id);
            try
            {
                var path = $"/stream/{type}/{Uri.EscapeDataString(id)}.json";
                if (!string.IsNullOrEmpty(sel))
                    path += $"?sel={Uri.EscapeDataString(sel)}";
                var result = await GetJsonWithFallbackAsync<AioStreamsStreamResponse>(path, ct);
                sw.Stop();
                _logger.LogInformation("[AioStreamsClient] COMPLETE: {Label} for {Id} took {ElapsedMs}ms",
                    label, id, sw.ElapsedMilliseconds);
                return CheckForErrorStub(result, id);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "[AioStreamsClient] FAILED: {Label} for {Id} after {ElapsedMs}ms",
                    label, id, sw.ElapsedMilliseconds);
                throw;
            }
        }

        // ── Multi-provider fetch loop ─────────────────────────────────────

        /// <summary>
        /// Iterates providers in order, skipping unhealthy ones, and returns the first
        /// non-empty <see cref="AioStreamsStreamResponse"/>.  Records health tracking.
        /// Returns null when no provider yields streams.
        /// </summary>
        internal static async Task<AioStreamsStreamResponse?> FetchAioStreamsAsync(
            IReadOnlyList<ProviderInfo> providers,
            string imdbId, string mediaType, int? season, int? episode,
            ILogger logger,
            ResolverHealthTracker? healthTracker,
            CooldownGate? cooldown,
            CancellationToken ct)
        {
            foreach (var provider in providers)
            {
                if (healthTracker != null && healthTracker.ShouldSkip(provider.DisplayName))
                {
                    logger.LogDebug("[InfiniteDrive] Skipping {Name} — circuit open", provider.DisplayName);
                    continue;
                }

                try
                {
                    using var client = new AioStreamsClient(provider.Url, provider.Uuid, provider.Token, logger);
                    if (cooldown != null) client.Cooldown = cooldown;

                    AioStreamsStreamResponse? response;
                    if (mediaType == "series" && season.HasValue && episode.HasValue)
                        response = await client.GetSeriesStreamsAsync(imdbId, season.Value, episode.Value, ct).ConfigureAwait(false);
                    else
                        response = await client.GetMovieStreamsAsync(imdbId, ct).ConfigureAwait(false);

                    if (response?.Streams == null || response.Streams.Count == 0)
                    {
                        logger.LogDebug("[InfiniteDrive] No streams from {Name} for {Id}", provider.DisplayName, imdbId);
                        continue;
                    }

                    if (healthTracker != null) healthTracker.RecordSuccess(provider.DisplayName);
                    logger.LogInformation("[InfiniteDrive] Got {Count} streams for {Id} from {Provider}",
                        response.Streams.Count, imdbId, provider.DisplayName);
                    return response;
                }
                catch (Exception ex)
                {
                    if (healthTracker != null) healthTracker.RecordFailure(provider.DisplayName);
                    logger.LogWarning(ex, "[InfiniteDrive] Provider {Name} failed for {Id}", provider.DisplayName, imdbId);
                }
            }

            return null;
        }

        // ── Error stub detection (Sprint 100A-05) ────────────────────────

        /// <summary>
        /// Detects AIOStreams error stub responses and returns empty list if found.
        /// Error stubs have title containing "error" or name containing "[AIOStreams]".
        /// </summary>
        private AioStreamsStreamResponse? CheckForErrorStub(
            AioStreamsStreamResponse? response,
            string itemId)
        {
            if (response?.Streams == null || response.Streams.Count == 0)
                return response;

            var firstStream = response.Streams[0];
            var title = firstStream.Title ?? string.Empty;
            var name = firstStream.Name ?? string.Empty;

            if (title.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("[AIOStreams]", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[InfiniteDrive] Error stub detected for item {Item}: Title='{Title}', Name='{Name}'. " +
                    "Treating as resolution failure.",
                    itemId, title, name);
                return new AioStreamsStreamResponse { Streams = new List<AioStreamsStream>() };
            }

            return response;
        }

        // ── Metadata ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches metadata for a single item (poster, genres, description, etc.).
        /// Only available on AIOStreams instances that have the meta resource enabled.
        /// </summary>
        public async Task<JsonElement?> GetMetaAsync(
            string type,
            string id,
            CancellationToken cancellationToken = default)
        {
            var url = $"{_stremioBase}/meta/{type}/{Uri.EscapeDataString(id)}.json";
            return await GetJsonElementAsync(url, cancellationToken);
        }

        /// <summary>
        /// Fetches strongly-typed metadata for a single item.
        /// Sprint 101A-02: AIOMetadata deserialization.
        /// Returns null if deserialization fails or response is invalid.
        /// </summary>
        public async Task<AioMetaResponse?> GetMetaAsyncTyped(
            string type,
            string id,
            CancellationToken cancellationToken = default)
        {
            var url = $"{_stremioBase}/meta/{type}/{Uri.EscapeDataString(id)}.json";
            var json = await GetJsonAsync(url, cancellationToken);

            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<AioMetaResponse>(json, _jsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(
                    ex,
                    "[AioStreamsClient] Failed to deserialize metadata for {Type} {Id}",
                    type, id);
                return null;
            }
        }

        // ── Connection health ────────────────────────────────────────────────────

        /// <summary>
        /// Maps exception types and HTTP status codes to user-friendly error messages.
        /// Used by TestConnectionAsync to provide clear UI feedback.
        /// </summary>
        private static string MapErrorToFriendlyMessage(Exception ex)
        {
            return ex switch
            {
                TaskCanceledException
                    => "Connection timed out. Is your provider reachable?",
                HttpRequestException
                    => "Could not reach the server. Check your network connection.",
                _ => ex.Message
            };
        }


        /// <summary>
        /// Tests connectivity by fetching the manifest.
        /// Returns <c>(true, null)</c> on success or <c>(false, errorMessage)</c>.
        /// </summary>
        public async Task<(bool Ok, string? Error)> TestConnectionAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var manifest = await GetManifestAsync(cancellationToken);
                if (manifest?.Id != null)
                    return (true, null);

                return (false, "Manifest fetched but contained no ID field — check UUID/token");
            }
            catch (OperationCanceledException)
            {
                return (false, "Connection timed out. Is your provider reachable?");
            }
            catch (HttpRequestException ex)
            {
                return (false, MapErrorToFriendlyMessage(ex));
            }
            catch (Exception ex)
            {
                // Log the full exception for debugging, but return a clean message to UI
                return (false, $"Unexpected error: {ex.Message}");
            }
        }

        // ── Utility: URL builders (public for external use) ──────────────────────

        /// <summary>
        /// Returns the stream URL for a movie without making an HTTP call.
        /// Useful for building .strm file content and debug logging.
        /// </summary>
        public string GetMovieStreamUrl(string imdbId)
            => $"{_stremioBase}/stream/movie/{Uri.EscapeDataString(imdbId)}.json";

        /// <summary>
        /// Returns the stream URL for a TV episode without making an HTTP call.
        /// </summary>
        public string GetSeriesStreamUrl(string imdbId, int season, int episode)
            => $"{_stremioBase}/stream/series/{Uri.EscapeDataString($"{imdbId}:{season}:{episode}")}.json";

        /// <summary>
        /// ── FIX-100B-05: Kitsu/AniList absolute episode numbering ────
        /// Returns the stream URL for an anime episode using absolute episode numbering.
        /// Stream ID format: {provider}:{seriesId}:{absoluteEpisode}
        /// Supported providers: kitsu, anilist
        /// </summary>
        /// <param name="provider">Provider prefix: "kitsu" or "anilist".</param>
        /// <param name="seriesId">Series ID from the provider.</param>
        /// <param name="absoluteEpisode">Absolute episode number across all seasons.</param>
        public string GetAnimeStreamUrl(string provider, string seriesId, int absoluteEpisode)
            => $"{_stremioBase}/stream/series/{Uri.EscapeDataString($"{provider}:{seriesId}:{absoluteEpisode}")}.json";

        /// <summary>
        /// ── FIX-100B-05: Absolute episode calculation ────────────────────
        /// Calculates the absolute episode number for anime series.
        /// Absolute episode = sum of episodes in previous seasons + current episode.
        /// Uses 12 as the default episode count estimate for unknown season lengths.
        /// </summary>
        /// <param name="season">Current season (1-based).</param>
        /// <param name="episode">Current episode within the season (1-based).</param>
        /// <param name="previousSeasonCounts">Array of episode counts for seasons before current (index 0 = season 1).</param>
        /// <returns>Absolute episode number.</returns>
        public static int CalculateAbsoluteEpisode(
            int season,
            int episode,
            int[]? previousSeasonCounts = null)
        {
            if (season < 1) season = 1;
            if (episode < 1) episode = 1;

            int absolute = 0;

            // Add up episodes from all previous seasons
            if (previousSeasonCounts != null)
            {
                for (int s = 0; s < Math.Min(season - 1, previousSeasonCounts.Length); s++)
                {
                    absolute += previousSeasonCounts[s];
                }
            }
            else
            {
                // Default estimate: 12 episodes per season for unknown seasons
                absolute += (season - 1) * 12;
            }

            // Add current episode
            absolute += episode;

            return absolute;
        }

        /// <summary>
        /// Returns the catalog URL for a given type and catalog ID.
        /// </summary>
        public string GetCatalogUrl(string type, string catalogId)
            => $"{_stremioBase}/catalog/{type}/{Uri.EscapeDataString(catalogId)}.json";

        // ── Static helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Parses base URL, UUID, and token out of a manifest URL.
        /// Supports the patterns:
        /// <c>{base}/stremio/{uuid}/{token}/manifest.json</c>
        /// <c>{base}/stremio/manifest.json</c>
        /// </summary>
        public static (string BaseUrl, string Uuid, string Token) TryParseManifestUrl(
            string? manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl))
                return (string.Empty, string.Empty, string.Empty);

            try
            {
                var uri     = new Uri(manifestUrl);
                var segs    = uri.AbsolutePath.TrimStart('/').Split('/');
                // Expected: stremio / {uuid} / {token} / manifest.json   (len ≥ 4)
                //       or: stremio / manifest.json                       (len = 2)
                if (segs.Length >= 4
                    && string.Equals(segs[0], "stremio", StringComparison.OrdinalIgnoreCase))
                {
                    var baseUrl = $"{uri.Scheme}://{uri.Authority}";
                    return (baseUrl, segs[1], segs[2]);
                }

                if (segs.Length >= 2
                    && string.Equals(segs[0], "stremio", StringComparison.OrdinalIgnoreCase))
                {
                    var baseUrl = $"{uri.Scheme}://{uri.Authority}";
                    return (baseUrl, string.Empty, string.Empty);
                }

                // Plain manifest URL: {base}/manifest.json or {base}/some/path/manifest.json
                // e.g. https://v3-cinemeta.strem.io/manifest.json
                // Use "DIRECT" sentinel so BuildStremioBase returns the base without appending /stremio
                if (manifestUrl.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)
                    || manifestUrl.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    var stremioBase = manifestUrl.Substring(0, manifestUrl.LastIndexOf("/manifest.json", StringComparison.OrdinalIgnoreCase));
                    return (stremioBase, "DIRECT", string.Empty);
                }
            }
            catch
            {
                // Malformed URL — caller will fall back to individual fields
            }

            return (string.Empty, string.Empty, string.Empty);
        }

        // ── IDisposable ──────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            // _sharedHttp is static — not disposed per instance.
        }

        // ── Private: HTTP helpers ────────────────────────────────────────────────

        /// <summary>
        /// Fetches JSON from the AIOStreams instance for the given relative path.
        ///
        /// On connection failure or timeout, throws <see cref="AioStreamsUnreachableException"/>
        /// so that PlaybackService can try the next provider in the list.
        ///
        /// On HTTP 4xx/5xx response from a reachable server, returns null (application error,
        /// not a network error, so don't retry on another provider).
        /// </summary>
        private async Task<T?> GetJsonWithFallbackAsync<T>(
            string relativePath, CancellationToken cancellationToken) where T : class
        {
            var fullUrl = _stremioBase + relativePath;
            var result = await GetJsonAsync<T>(fullUrl, cancellationToken, throwOnUnreachable: true);
            return result;
        }

        /// <summary>
        /// Fetches a URL and deserialises the response.
        /// Always throws <see cref="AioStreamsRateLimitException"/> on HTTP 429.
        /// When <paramref name="throwOnUnreachable"/> is <c>true</c>, throws
        /// <see cref="AioStreamsUnreachableException"/> on connection failure or
        /// timeout so <see cref="GetJsonWithFallbackAsync{T}"/> can retry on a
        /// fallback instance.  When <c>false</c> (default, used by manifest/catalog
        /// callers), returns null on any network failure — preserving the original
        /// return-null-on-error contract.
        /// </summary>
        private async Task<T?> GetJsonAsync<T>(
            string url,
            CancellationToken cancellationToken,
            bool throwOnUnreachable = false) where T : class
        {
            var raw = await GetRawStringAsync(url, cancellationToken, throwOnUnreachable);
            if (raw == null) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(raw, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Error deserializing {Url}", SanitizeUrl(url));
                return null;
            }
        }

        /// <summary>
        /// Core HTTP-with-retry primitive used by all three JSON fetch variants.
        /// Applies 3-attempt exponential backoff (1s/4s/16s).
        /// When <paramref name="throwOnUnreachable"/> is true, throws
        /// <see cref="AioStreamsUnreachableException"/> instead of returning null
        /// after connection failure or timeout exhausts all retries.
        /// </summary>
        private async Task<string?> GetRawStringAsync(string url, CancellationToken cancellationToken,
            bool throwOnUnreachable = false)
        {
            var safeUrl = SanitizeUrl(url);
            const int maxAttempts = 3;
            var totalSw = Stopwatch.StartNew();

            _logger.LogInformation("[GetRawStringAsync] START: {Url}", safeUrl);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _logger.LogDebug("[InfiniteDrive] GET {Url} (attempt {Attempt}/{Max})",
                        safeUrl, attempt, maxAttempts);

                    if (Cooldown != null)
                        await Cooldown.WaitAsync(ActiveCooldownKind, cancellationToken);

                    var httpSw = Stopwatch.StartNew();
                    var response = await _sharedHttp.GetAsync(url, cancellationToken);
                    httpSw.Stop();
                    _logger.LogDebug("[GetRawStringAsync] HTTP GET completed in {ElapsedMs}ms for {Url}",
                        httpSw.ElapsedMilliseconds, safeUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        var code = (int)response.StatusCode;
                        if (code == 401 || code == 403 || code == 404)
                        {
                            _logger.LogDebug("[InfiniteDrive] {Code} from AIOStreams: {Url} — not retrying",
                                code, safeUrl);
                            return null;
                        }
                        if (code == 429)
                        {
                            Cooldown?.Tripped(CooldownGate.ParseRetryAfter(
                                response.Headers.Contains("Retry-After")
                                    ? response.Headers.RetryAfter?.ToString()
                                    : null));
                            throw new AioStreamsRateLimitException(safeUrl);
                        }
                        _logger.LogWarning("[InfiniteDrive] AIOStreams returned {Status} for {Url}", code, safeUrl);
                        return null;
                    }

                    var contentSw = Stopwatch.StartNew();
                    var content = await response.Content.ReadAsStringAsync();
                    contentSw.Stop();
                    totalSw.Stop();

                    _logger.LogInformation("[GetRawStringAsync] COMPLETE: {Url} - HTTP: {HttpMs}ms, ContentRead: {ContentMs}ms, Total: {TotalMs}ms",
                        safeUrl, httpSw.ElapsedMilliseconds, contentSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);

                    return content;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    totalSw.Stop();
                    _logger.LogWarning("[GetRawStringAsync] CANCELLED after {TotalMs}ms for {Url}",
                        totalSw.ElapsedMilliseconds, safeUrl);
                    throw;
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning("[InfiniteDrive] Timeout fetching {Url}: {Msg}", safeUrl, ex.Message);
                    if (attempt < maxAttempts)
                    {
                        int delayMs = attempt == 1 ? 1000 : (attempt == 2 ? 4000 : 16000);
                        _logger.LogWarning("[InfiniteDrive] Retrying {Url} in {DelayMs}ms (attempt {Attempt}/{Max})",
                            safeUrl, delayMs, attempt, maxAttempts);
                        await Task.Delay(delayMs, cancellationToken);
                        continue;
                    }
                    totalSw.Stop();
                    _logger.LogWarning("[GetRawStringAsync] TIMEOUT after {TotalMs}ms for {Url}",
                        totalSw.ElapsedMilliseconds, safeUrl);
                    if (throwOnUnreachable) throw new AioStreamsUnreachableException(safeUrl, null);
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("[InfiniteDrive] Connection failed for {Url}: {Msg}", safeUrl, ex.Message);
                    if (attempt < maxAttempts)
                    {
                        int delayMs = attempt == 1 ? 1000 : (attempt == 2 ? 4000 : 16000);
                        _logger.LogWarning("[InfiniteDrive] Retrying {Url} in {DelayMs}ms (attempt {Attempt}/{Max})",
                            safeUrl, delayMs, attempt, maxAttempts);
                        await Task.Delay(delayMs, cancellationToken);
                        continue;
                    }
                    totalSw.Stop();
                    _logger.LogError(ex, "[GetRawStringAsync] CONNECTION FAILED after {TotalMs}ms for {Url}",
                        totalSw.ElapsedMilliseconds, safeUrl);
                    if (throwOnUnreachable) throw new AioStreamsUnreachableException(safeUrl, ex);
                    return null;
                }
                catch (AioStreamsRateLimitException) { throw; }
                catch (AioStreamsUnreachableException) { throw; }
                catch (Exception ex)
                {
                    totalSw.Stop();
                    _logger.LogError(ex, "[GetRawStringAsync] EXCEPTION after {TotalMs}ms for {Url}",
                        totalSw.ElapsedMilliseconds, safeUrl);
                    return null;
                }
            }

            totalSw.Stop();
            _logger.LogWarning("[GetRawStringAsync] ALL ATTEMPTS FAILED after {TotalMs}ms for {Url}",
                totalSw.ElapsedMilliseconds, safeUrl);
            return null;
        }

        private async Task<JsonElement?> GetJsonElementAsync(
            string url, CancellationToken cancellationToken)
        {
            var raw = await GetRawStringAsync(url, cancellationToken);
            if (raw == null) return null;
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// Fetches raw JSON string from a URL with full retry logic.
        /// Returns null on error.
        /// </summary>
        private async Task<string?> GetJsonAsync(
            string url,
            CancellationToken cancellationToken)
            => await GetRawStringAsync(url, cancellationToken);

        private static string BuildStremioBase(string baseUrl, string? uuid, string? token)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return string.Empty;

            // "DIRECT" sentinel: baseUrl is already the full stremio base (no /stremio suffix needed)
            if (string.Equals(uuid, "DIRECT", StringComparison.Ordinal))
                return baseUrl.TrimEnd('/');

            var hasAuth = !string.IsNullOrWhiteSpace(uuid)
                       && !string.IsNullOrWhiteSpace(token);

            return hasAuth
                ? $"{baseUrl}/stremio/{uuid}/{token}"
                : $"{baseUrl}/stremio";
        }

        /// <summary>
        /// Returns a sanitized version of a URL safe for log output.
        /// Replaces the token segment in <c>/stremio/{uuid}/{token}/…</c>
        /// paths with <c>[token]</c> so credentials never appear in log files.
        /// </summary>
        private string SanitizeUrl(string url)
        {
            if (_rawToken == null || string.IsNullOrEmpty(url))
                return url;

            return url.Replace(_rawToken, "[token]");
        }

        private static void Append(System.Text.StringBuilder sb, string part)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(part);
        }
    }
}
