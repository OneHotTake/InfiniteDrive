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
            var http = httpClient ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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
                result.ErrorMessage = "Request timed out. Check your network connection.";
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
                    ConfigureUrl = $"{baseUrl}/stremio/configure/{userId}"
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
