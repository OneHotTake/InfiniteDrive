using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// A single item fetched from an external list provider.
    /// </summary>
    public sealed record ListItem(
        string Title,
        string? ImdbId,
        int? Year,
        string? MediaType);

    /// <summary>
    /// Result of fetching an external list.
    /// </summary>
    public sealed class ListFetchResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? DisplayName { get; set; }
        public List<ListItem> Items { get; set; } = new();
        public int ItemCount => Items.Count;
    }

    /// <summary>
    /// URL-sniffing dispatcher that fetches items from external list providers
    /// (MDBList, Trakt, TMDB, AniList). Given a list URL and plugin config,
    /// auto-detects the provider and fetches normalized results.
    /// </summary>
    public static class ListFetcher
    {
        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches items from the given list URL using the appropriate provider.
        /// Returns a <see cref="ListFetchResult"/> with items or an error.
        /// </summary>
        public static async Task<ListFetchResult> FetchAsync(
            string listUrl,
            string traktClientId,
            string tmdbApiKey,
            ILogger? logger,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(listUrl))
                return Fail("List URL is required.");

            if (!Uri.TryCreate(listUrl.Trim(), UriKind.Absolute, out var uri))
                return Fail($"Invalid URL: {listUrl}");

            var host = uri.Host.ToLowerInvariant();

            if (host.Contains("mdblist.com"))
                return await FetchMdblistAsync(uri, logger, ct);

            if (host.Contains("trakt.tv") || host.Contains("trakt.it"))
                return await FetchTraktAsync(uri, traktClientId, logger, ct);

            if (host.Contains("themoviedb.org") || host.Contains("tmdb.org"))
                return await FetchTmdbAsync(uri, tmdbApiKey, logger, ct);

            if (host.Contains("anilist.co"))
                return await FetchAnilistAsync(uri, logger, ct);

            return Fail(
                $"Unsupported list provider '{uri.Host}'. " +
                "Supported: mdblist.com, trakt.tv, themoviedb.org, anilist.co");
        }

        /// <summary>
        /// Returns which providers are enabled based on admin-configured keys.
        /// </summary>
        public static List<string> GetEnabledProviders(string traktClientId, string tmdbApiKey)
        {
            var providers = new List<string> { "mdblist", "anilist" };
            if (!string.IsNullOrWhiteSpace(traktClientId)) providers.Add("trakt");
            if (!string.IsNullOrWhiteSpace(tmdbApiKey)) providers.Add("tmdb");
            return providers;
        }

        /// <summary>
        /// Detects the provider name from a URL, or null if unrecognized.
        /// </summary>
        public static string? DetectProvider(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
            var host = uri.Host.ToLowerInvariant();
            if (host.Contains("mdblist.com")) return "mdblist";
            if (host.Contains("trakt.tv") || host.Contains("trakt.it")) return "trakt";
            if (host.Contains("themoviedb.org") || host.Contains("tmdb.org")) return "tmdb";
            if (host.Contains("anilist.co")) return "anilist";
            return null;
        }

        // ── MDBList ──────────────────────────────────────────────────────────────

        private static async Task<ListFetchResult> FetchMdblistAsync(
            Uri uri, ILogger? logger, CancellationToken ct)
        {
            // Build the /json endpoint URL
            var cleanUrl = uri.ToString().TrimEnd('/');
            if (!cleanUrl.EndsWith("/json")) cleanUrl += "/json";

            var all = new List<ListItem>();
            const int pageSize = 1000;
            var offset = 0;

            while (true)
            {
                var apiUrl = $"{cleanUrl}?limit={pageSize}&offset={offset}&_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                try
                {
                    using var resp = await _http.GetAsync(apiUrl, ct);
                    if (!resp.IsSuccessStatusCode)
                        return Fail($"MDBList returned HTTP {(int)resp.StatusCode}.");

                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    // API format: { movies: [...], shows: [...] }
                    // Legacy format: [ ... ]
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var combined = new List<JsonElement>();
                        if (doc.RootElement.TryGetProperty("movies", out var movies))
                            foreach (var item in movies.EnumerateArray()) combined.Add(item);
                        if (doc.RootElement.TryGetProperty("shows", out var shows))
                            foreach (var item in shows.EnumerateArray()) combined.Add(item);
                        if (combined.Count == 0) break;

                        foreach (var item in combined)
                        {
                            var li = ParseMdblistItem(item);
                            if (li != null) all.Add(li);
                        }

                        if (combined.Count < pageSize) break;
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var arr = doc.RootElement.EnumerateArray().ToList();
                        if (arr.Count == 0) break;
                        foreach (var item in arr)
                        {
                            var li = ParseMdblistItem(item);
                            if (li != null) all.Add(li);
                        }
                        if (arr.Count < pageSize) break;
                    }
                    else break;

                    if (all.Count >= 5000) break; // safety cap
                    offset += pageSize;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (all.Count > 0) break; // return what we have
                    return Fail($"Could not fetch MDBList: {ex.Message}");
                }
            }

            if (all.Count == 0)
                return Fail("This MDBList is empty or private.");

            return new ListFetchResult { Ok = true, Items = all };
        }

        private static ListItem? ParseMdblistItem(JsonElement item)
        {
            string? imdbId = null;
            if (item.TryGetProperty("imdb_id", out var imdbEl) && imdbEl.ValueKind == JsonValueKind.String)
                imdbId = imdbEl.GetString();
            if (string.IsNullOrWhiteSpace(imdbId)) return null;

            var title = item.TryGetProperty("title", out var titleEl)
                ? titleEl.GetString() ?? "Unknown"
                : "Unknown";

            return new ListItem(title, imdbId.ToLowerInvariant(), null, null);
        }

        // ── Trakt ────────────────────────────────────────────────────────────────

        private static async Task<ListFetchResult> FetchTraktAsync(
            Uri uri, string clientId, ILogger? logger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return Fail("Trakt isn't configured. Ask your admin to set up a Trakt Client ID.");

            // Normalize URL to API path
            var path = uri.AbsolutePath.TrimEnd('/');

            // Append /items if it looks like a list URL without it
            if (path.Contains("/users/") && path.Contains("/lists/") && !path.EndsWith("/items"))
                path += "/items";

            // Handle watchlist / collection
            if (path.Contains("/users/") && !path.Contains("/lists/") &&
                !path.Contains("/watchlist") && !path.Contains("/collection") &&
                !path.Contains("/history") && !path.Contains("/ratings"))
            {
                // Could be a user page; default to watchlist
                path = path.TrimEnd('/') + "/watchlist/items";
            }

            var apiBase = "https://api.trakt.tv";
            var url = $"{apiBase}{path}?limit=1000";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("trakt-api-version", "2");
                req.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    logger?.LogWarning("[ListFetcher] Trakt API error {Status}: {Body}",
                        (int)resp.StatusCode, body);

                    return (int)resp.StatusCode == 404
                        ? Fail("This Trakt list couldn't be found. Check the URL and try again.")
                        : Fail($"Trakt returned HTTP {(int)resp.StatusCode}.");
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                return ParseTraktResponse(json);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Fail($"Could not fetch Trakt list: {ex.Message}");
            }
        }

        private static ListFetchResult ParseTraktResponse(string json)
        {
            var items = new List<ListItem>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return Fail("Unexpected Trakt response format.");

                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    // Wrapped format: { "movie": {...} } or { "show": {...} }
                    JsonElement? mediaObj = null;
                    if (entry.TryGetProperty("movie", out var movieEl) && movieEl.ValueKind == JsonValueKind.Object)
                        mediaObj = movieEl;
                    else if (entry.TryGetProperty("show", out var showEl) && showEl.ValueKind == JsonValueKind.Object)
                        mediaObj = showEl;
                    else if (entry.TryGetProperty("ids", out _))
                        mediaObj = entry; // Flat format

                    if (mediaObj == null) continue;

                    var m = mediaObj.Value;
                    var title = m.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    string? imdbId = null;
                    if (m.TryGetProperty("ids", out var ids))
                    {
                        if (ids.TryGetProperty("imdb", out var imdbEl))
                            imdbId = imdbEl.GetString();
                    }

                    if (string.IsNullOrWhiteSpace(imdbId)) continue;

                    int? year = null;
                    if (m.TryGetProperty("year", out var yearEl) && yearEl.ValueKind == JsonValueKind.Number)
                        year = yearEl.GetInt32();

                    items.Add(new ListItem(title, imdbId.ToLowerInvariant(), year, null));
                }
            }
            catch
            {
                return Fail("Could not parse Trakt response.");
            }

            if (items.Count == 0)
                return Fail("This Trakt list is empty or contains no items with IMDb IDs.");

            return new ListFetchResult { Ok = true, Items = items };
        }

        // ── TMDB ─────────────────────────────────────────────────────────────────

        private static async Task<ListFetchResult> FetchTmdbAsync(
            Uri uri, string apiKey, ILogger? logger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return Fail("TMDB isn't configured. Ask your admin to set up a TMDB API Key.");

            var path = uri.AbsolutePath.Trim('/').ToLowerInvariant();

            // Extract list ID from /list/{id} patterns
            var segs = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string? listId = null;

            for (int i = 0; i < segs.Length; i++)
            {
                if (string.Equals(segs[i], "list", StringComparison.OrdinalIgnoreCase) && i + 1 < segs.Length)
                {
                    var numPart = segs[i + 1];
                    var dashIdx = numPart.IndexOf('-');
                    listId = dashIdx > 0 ? numPart.Substring(0, dashIdx) : numPart;
                    break;
                }
            }

            if (listId == null)
                return Fail("Could not find a TMDB list ID in this URL. Use a URL like themoviedb.org/list/12345");

            // Fetch list from TMDB API
            var apiBase = "https://api.themoviedb.org/3";
            var listUrl = $"{apiBase}/list/{listId}?api_key={apiKey}&language=en-US";

            try
            {
                using var resp = await _http.GetAsync(listUrl, ct);
                if (!resp.IsSuccessStatusCode)
                    return Fail($"TMDB returned HTTP {(int)resp.StatusCode}.");

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("items", out var itemsEl))
                    return Fail("This TMDB list appears empty.");

                var items = new List<ListItem>();

                foreach (var entry in itemsEl.EnumerateArray())
                {
                    var title = entry.TryGetProperty("title", out var t) ? t.GetString()
                        : entry.TryGetProperty("name", out var n) ? n.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    var mediaType = entry.TryGetProperty("media_type", out var mt)
                        ? mt.GetString() ?? "movie" : "movie";

                    if (!entry.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                        continue;

                    var rawId = $"tmdb_{idEl.GetInt32()}";

                    int? year = null;
                    if (entry.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String)
                    {
                        var dateStr = rd.GetString();
                        if (dateStr?.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var y))
                            year = y;
                    }
                    else if (entry.TryGetProperty("first_air_date", out var fad) && fad.ValueKind == JsonValueKind.String)
                    {
                        var dateStr = fad.GetString();
                        if (dateStr?.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var y))
                            year = y;
                    }

                    items.Add(new ListItem(title, rawId, year, mediaType));
                }

                if (items.Count == 0)
                    return Fail("This TMDB list is empty.");

                return new ListFetchResult { Ok = true, Items = items };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Fail($"Could not fetch TMDB list: {ex.Message}");
            }
        }

        // ── AniList ──────────────────────────────────────────────────────────────

        private static readonly Regex AnilistUrlRegex = new(
            @"anilist\.co/user/([^/]+)/animelist(?:/([^/]+))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static async Task<ListFetchResult> FetchAnilistAsync(
            Uri uri, ILogger? logger, CancellationToken ct)
        {

            // Parse username from URL
            var match = AnilistUrlRegex.Match(uri.ToString());
            if (!match.Success)
                return Fail("Invalid AniList URL. Use: anilist.co/user/{username}/animelist");

            var username = match.Groups[1].Value;
            var statusFilter = match.Groups[2].Success ? match.Groups[2].Value : null;

            // GraphQL query
            var query = @"
query ($userName: String!, $type: MediaType!) {
  MediaListCollection(userName: $userName, type: $type) {
    lists {
      name
      status
      entries {
        status
        media {
          id
          idMal
          title { romaji english native }
          startDate { year }
          type
        }
      }
    }
  }
}";

            var payload = JsonSerializer.Serialize(new
            {
                query,
                variables = new { userName = username, type = "ANIME" }
            });

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
                {
                    Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
                };
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return (int)resp.StatusCode == 404
                        ? Fail($"AniList user '{username}' not found.")
                        : Fail($"AniList returned HTTP {(int)resp.StatusCode}.");
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                // Check for GraphQL errors
                if (doc.RootElement.TryGetProperty("errors", out var errors))
                {
                    var msg = errors.EnumerateArray()
                        .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error")
                        .FirstOrDefault();
                    return Fail($"AniList error: {msg}");
                }

                var lists = doc.RootElement
                    .GetProperty("data")
                    .GetProperty("MediaListCollection")
                    .GetProperty("lists");

                var items = new List<(string title, string? titleEnglish, string? titleRomaji, int? year, int anilistId)>();

                foreach (var list in lists.EnumerateArray())
                {
                    var entries = list.GetProperty("entries");
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var media = entry.GetProperty("media");
                        var titleObj = media.GetProperty("title");
                        var titleEnglish = titleObj.TryGetProperty("english", out var te) ? te.GetString() : null;
                        var titleRomaji = titleObj.TryGetProperty("romaji", out var tr) ? tr.GetString() : null;
                        var title = titleEnglish ?? titleRomaji ?? "Unknown Anime";

                        int? year = null;
                        if (media.TryGetProperty("startDate", out var sd) &&
                            sd.TryGetProperty("year", out var yr) && yr.ValueKind == JsonValueKind.Number)
                            year = yr.GetInt32();

                        var anilistId = media.TryGetProperty("id", out var idEl)
                            && idEl.ValueKind == JsonValueKind.Number
                            ? idEl.GetInt32() : 0;

                        items.Add((title, titleEnglish, titleRomaji, year, anilistId));
                    }
                }

                if (items.Count == 0)
                    return Fail($"AniList user '{username}' has no anime in their list.");

                // Return items with native AniList IDs (anilist:XXX) — resolution
                // happens downstream via IdResolverService in UserCatalogSyncService.
                // The official Emby AniList plugin uses these numeric IDs.
                var result = items
                    .Where(i => i.anilistId > 0)
                    .Select(i => new ListItem(i.title, $"anilist:{i.anilistId}", i.year, "tv"))
                    .ToList();

                if (result.Count == 0)
                    return Fail($"AniList user '{username}' has no anime with valid IDs.");

                return new ListFetchResult { Ok = true, Items = result };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Fail($"Could not fetch AniList: {ex.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static ListFetchResult Fail(string error) => new()
        {
            Ok = false,
            Error = error
        };
    }
}
