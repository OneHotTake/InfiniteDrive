using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Produces a log-safe URL that never includes query strings, fragments, AIOStreams
    /// user/config credentials, or opaque CDN path tokens.
    /// </summary>
    public static class SensitiveUrlRedactor
    {
        private static readonly HashSet<string> ResourceSegments = new(StringComparer.OrdinalIgnoreCase)
        {
            "catalog", "meta", "stream", "subtitles"
        };

        public static string Redact(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "[empty-url]";
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "[invalid-url]";

            var origin = uri.GetLeftPart(UriPartial.Authority);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            var stremioIndex = segments.FindIndex(s => s.Equals("stremio", StringComparison.OrdinalIgnoreCase));
            if (stremioIndex < 0)
                return origin + "/[redacted]";

            var tail = segments.Skip(stremioIndex + 1).ToList();
            if (tail.Count == 0) return origin + "/stremio";

            if (tail[0].Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || ResourceSegments.Contains(tail[0]))
                return origin + "/stremio/" + string.Join('/', tail);

            var resourceIndex = tail.FindIndex(s => s.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                                                   || ResourceSegments.Contains(s));
            var safeTail = resourceIndex >= 0 ? tail.Skip(resourceIndex) : new List<string> { "[redacted]" };
            return origin + "/stremio/[redacted]/" + string.Join('/', safeTail);
        }

        /// <summary>Redacts every absolute HTTP URL embedded in diagnostic text.</summary>
        public static string RedactText(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Regex.Replace(
                value,
                "https?://[^\\s\\\"'<>]+",
                match => Redact(match.Value),
                RegexOptions.IgnoreCase);
        }
    }
}
