using System;

namespace EmbyStreams.Services
{
    public static class ManifestUrlParser
    {
        /// <summary>
        /// Parses an AIOStreams manifest URL and returns components.
        /// Returns null if URL doesn't match expected format.
        /// </summary>
        public static ManifestUrlComponents? Parse(string? manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl))
                return null;

            try
            {
                var uri = new Uri(manifestUrl);

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

                return new ManifestUrlComponents
                {
                    Host = uri.Host,
                    Scheme = uri.Scheme,
                    Port = uri.Port,
                    UserId = userId,
                    ConfigToken = configToken,
                    BaseUrl = baseUrl,
                    ConfigureUrl = $"{baseUrl}/stremio/configure/{userId}/{configToken}"
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
