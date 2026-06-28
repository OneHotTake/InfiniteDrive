using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Manifest URL validation result.
    /// Sprint 100A-02: Returns specific error information for failed validation.
    /// </summary>
    public class ManifestUrlValidationResult
    {
        /// <summary>True if the URL is valid.</summary>
        public bool IsValid { get; set; }

        /// <summary>Error category: "format", "404", "403", "timeout", "invalid_json", "missing_id".</summary>
        public string ErrorType { get; set; } = string.Empty;

        /// <summary>Human-readable error message.</summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>True if the URL uses http:// without localhost/127.0.0.1/::1 (needs warning).</summary>
        public bool UsesInsecureHttp { get; set; }

        // ── Read-only probe (populated when IsValid) ─────────────────────────
        /// <summary>Addon display name from the manifest.</summary>
        public string? AddonName { get; set; }

        /// <summary>Addon version from the manifest.</summary>
        public string? AddonVersion { get; set; }

        /// <summary>True when the manifest advertises the "stream" resource.</summary>
        public bool HasStreamResource { get; set; }

        /// <summary>Total catalogs in the manifest's catalog array (0 = the QuackStart "advertises catalog resource but empty" trap).</summary>
        public int CatalogCount { get; set; }

        /// <summary>Catalogs that can be browsed without a required search term.</summary>
        public int BrowsableCatalogCount { get; set; }

        /// <summary>Catalogs that require a search term (not browsable on their own).</summary>
        public int SearchOnlyCatalogCount { get; set; }
    }

    public static class ManifestUrlParser
    {
        /// <summary>
        /// Validates that a manifest URL is reachable and returns a valid manifest.
        /// Sprint 100A-02: Makes HTTP request to verify URL returns HTTP 200
        /// with valid JSON containing a non-empty id field.
        /// </summary>
        /// <param name="manifestUrl">The manifest URL to validate.</param>
        /// <returns>Validation result with error details if validation failed.</returns>
        public static async Task<ManifestUrlValidationResult> ValidateManifestUrlAsync(
            string manifestUrl,
            System.Net.Http.HttpClient? httpClient = null)
        {
            var result = new ManifestUrlValidationResult();

            // Basic format validation
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                result.ErrorType = "format";
                result.ErrorMessage = "Manifest URL is empty.";
                return result;
            }

            // Sprint 100A-08: HTTPS warning check
            if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri))
            {
                if (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                {
                    var host = uri.Host?.ToLowerInvariant() ?? string.Empty;
                    var isLocalhost = host == "localhost" ||
                                       host == "127.0.0.1" ||
                                       host == "::1";
                    if (!isLocalhost)
                    {
                        result.UsesInsecureHttp = true;
                    }
                }
            }

            // Sprint 100A-02: HTTP validation - check URL is reachable and returns valid manifest
            // Large AIOStreams manifests (100+ catalogs) can legitimately take >10s to
            // return. Use a generous timeout so a slow-but-working instance isn't reported
            // as a failure (catalog sync itself uses a long timeout — keep these aligned).
            var http = httpClient ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            try
            {
                var response = await http.GetAsync(manifestUrl);
                var statusCode = (int)response.StatusCode;

                if (statusCode != 200)
                {
                    switch (statusCode)
                    {
                        case 404:
                            result.ErrorType = "404";
                            result.ErrorMessage = "Manifest URL not found (404).";
                            break;
                        case 403:
                            result.ErrorType = "403";
                            result.ErrorMessage = "Access denied (403). Check your URL and authentication.";
                            break;
                        default:
                            result.ErrorType = $"http_{statusCode}";
                            result.ErrorMessage = $"Server returned HTTP {statusCode}.";
                            break;
                    }
                    return result;
                }

                // Parse JSON and check for id field
                var json = await response.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("id", out var idProp))
                    {
                        result.ErrorType = "missing_id";
                        result.ErrorMessage = "Manifest JSON is missing required 'id' field.";
                        return result;
                    }

                    var idValue = idProp.GetString();
                    if (string.IsNullOrWhiteSpace(idValue))
                    {
                        result.ErrorType = "missing_id";
                        result.ErrorMessage = "Manifest 'id' field is empty.";
                        return result;
                    }

                    // Read-only census: name, version, resources, catalog breakdown.
                    Census(doc.RootElement, result);

                    result.IsValid = true;
                    return result;
                }
                catch (JsonException)
                {
                    result.ErrorType = "invalid_json";
                    result.ErrorMessage = "Manifest URL returned invalid JSON.";
                    return result;
                }
            }
            catch (System.OperationCanceledException)
            {
                result.ErrorType = "timeout";
                result.ErrorMessage = "Timed out waiting for the manifest. Your AIOStreams instance may be slow or overloaded — try again, or check that the URL is reachable.";
                return result;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                result.ErrorType = "connection";
                result.ErrorMessage = $"Connection failed: {ex.Message}";
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorType = "unknown";
                result.ErrorMessage = $"Unexpected error: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Read-only census of a manifest JSON document: addon name/version, whether
        /// the "stream" resource is advertised, and the catalog breakdown. Catches the
        /// QuackStart trap where the "catalog" resource is advertised but the catalog
        /// array is empty (CatalogCount == 0).
        /// </summary>
        private static void Census(JsonElement root, ManifestUrlValidationResult result)
        {
            try
            {
                if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    result.AddonName = nameEl.GetString();
                if (root.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.String)
                    result.AddonVersion = verEl.GetString();

                // resources: array of strings OR objects with a "name" field.
                if (root.TryGetProperty("resources", out var resEl) && resEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in resEl.EnumerateArray())
                    {
                        string? rname = r.ValueKind == JsonValueKind.String
                            ? r.GetString()
                            : (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("name", out var rn) ? rn.GetString() : null);
                        if (string.Equals(rname, "stream", StringComparison.OrdinalIgnoreCase))
                            result.HasStreamResource = true;
                    }
                }

                // catalogs: each has an optional "extra" array of { name, isRequired }.
                if (root.TryGetProperty("catalogs", out var catsEl) && catsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in catsEl.EnumerateArray())
                    {
                        result.CatalogCount++;
                        bool searchRequired = false;
                        if (c.TryGetProperty("extra", out var extraEl) && extraEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var ex in extraEl.EnumerateArray())
                            {
                                if (ex.ValueKind != JsonValueKind.Object) continue;
                                var exName = ex.TryGetProperty("name", out var en) ? en.GetString() : null;
                                var isReq = ex.TryGetProperty("isRequired", out var ir)
                                            && ir.ValueKind == JsonValueKind.True;
                                if (isReq && string.Equals(exName, "search", StringComparison.OrdinalIgnoreCase))
                                    searchRequired = true;
                            }
                        }
                        if (searchRequired) result.SearchOnlyCatalogCount++;
                        else result.BrowsableCatalogCount++;
                    }
                }
            }
            catch { /* census is best-effort; never fail validation over it */ }
        }

        /// <summary>
        /// Result of probing a single stream response for which metadata signal tiers
        /// are actually populated. AIOStreams cosmetic formatters vary wildly but
        /// behaviorHints.filename/videoSize are the reliable backbone.
        /// </summary>
        public class StreamSignalProbe
        {
            public bool Ok { get; set; }
            public int StreamCount { get; set; }
            public bool HasFilename { get; set; }
            public bool HasVideoSize { get; set; }
            public bool HasParsedFile { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        /// <summary>
        /// Read-only: fetches the stream response for one well-known IMDb id from the
        /// same AIOStreams instance and reports which signal tiers are present. Used by
        /// the connect UI to tell the user (and us) what quality detection to expect.
        /// Outbound client call only — no new endpoint.
        /// </summary>
        public static async Task<StreamSignalProbe> ProbeStreamSignalsAsync(
            string manifestUrl, string imdbId = "tt0111161", HttpClient? httpClient = null)
        {
            var probe = new StreamSignalProbe();
            var components = Parse(manifestUrl);
            if (components == null || string.IsNullOrWhiteSpace(components.BaseUrl))
            {
                probe.Message = "Could not derive stream URL from manifest.";
                return probe;
            }

            // Stream base = {BaseUrl}/stremio/{userId}/{configToken}  →  /stream/movie/{id}.json
            var streamBase = $"{components.BaseUrl}/stremio/{components.UserId}/{components.ConfigToken}";
            var streamUrl = $"{streamBase}/stream/movie/{imdbId}.json";
            var http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            try
            {
                var resp = await http.GetAsync(streamUrl);
                if (!resp.IsSuccessStatusCode)
                {
                    probe.Message = $"Stream probe returned HTTP {(int)resp.StatusCode}.";
                    return probe;
                }
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("streams", out var streams)
                    || streams.ValueKind != JsonValueKind.Array)
                {
                    probe.Message = "Stream response had no streams array.";
                    return probe;
                }

                probe.Ok = true;
                probe.StreamCount = streams.GetArrayLength();
                foreach (var s in streams.EnumerateArray())
                {
                    if (s.TryGetProperty("behaviorHints", out var bh) && bh.ValueKind == JsonValueKind.Object)
                    {
                        if (bh.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(fn.GetString()))
                            probe.HasFilename = true;
                        if (bh.TryGetProperty("videoSize", out var vs)
                            && (vs.ValueKind == JsonValueKind.Number))
                            probe.HasVideoSize = true;
                    }
                    if (s.TryGetProperty("parsedFile", out var pf) && pf.ValueKind == JsonValueKind.Object)
                        probe.HasParsedFile = true;
                }

                probe.Message = probe.StreamCount == 0
                    ? "No streams for the probe title (try a more popular title)."
                    : $"{probe.StreamCount} streams · filename {(probe.HasFilename ? "✓" : "✗")} · "
                      + $"size {(probe.HasVideoSize ? "✓" : "✗")} · parsedFile {(probe.HasParsedFile ? "present" : "absent")}";
                return probe;
            }
            catch (Exception ex)
            {
                probe.Message = $"Stream probe failed: {ex.Message}";
                return probe;
            }
        }

        /// <summary>
        /// Parses an AIOStreams manifest URL and returns components.
        /// Returns null if URL doesn't match expected format.
        /// Sprint 100A-02: Now validates URL ends with /manifest.json.
        /// </summary>
        public static ManifestUrlComponents? Parse(string? manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl))
                return null;

            try
            {
                var uri = new Uri(manifestUrl);

                // Sprint 100A-02: Verify URL ends with /manifest.json
                if (!uri.AbsolutePath.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Expected path: /stremio/{userId}/{configToken}/manifest.json
                var segments = uri.AbsolutePath
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                // Minimum: ["stremio", "{userId}", "{configToken}", "manifest.json"]
                if (segments.Length < 4)
                    return null;

                if (!segments[0].Equals("stremio", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (!segments[segments.Length - 1].Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    return null;

                var userId = segments[1];

                // configToken is everything between userId and manifest.json
                var configToken = segments.Length == 4
                    ? segments[2]
                    : string.Join("/", segments, 2, segments.Length - 3);

                var baseUrl = $"{uri.Scheme}://{uri.Host}";
                if (!uri.IsDefaultPort)
                    baseUrl += $":{uri.Port}";

                // Configure URL is simply /stremio/configure (no userId/configToken needed)
                // The user's browser session handles authentication
                return new ManifestUrlComponents
                {
                    Host = uri.Host,
                    Scheme = uri.Scheme,
                    Port = uri.Port,
                    UserId = userId,
                    ConfigToken = configToken,
                    BaseUrl = baseUrl,
                    ConfigureUrl = $"{baseUrl}/stremio/configure"
                };
            }
            catch
            {
                return null;
            }
        }
    }

    public class ManifestUrlComponents
    {
        public string Host { get; set; } = string.Empty;
        public string Scheme { get; set; } = "https";
        public int Port { get; set; } = 443;
        public string UserId { get; set; } = string.Empty;
        public string ConfigToken { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string ConfigureUrl { get; set; } = string.Empty;
    }
}
