using System;
using System.Text;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Single source of truth for filesystem naming conventions.
    /// All folder names and path sanitisation must route through this service.
    /// </summary>
    public static class NamingPolicyService
    {
        /// <summary>
        /// Builds a folder name using Emby auto-match convention:
        /// <c>{Title} ({Year}) [imdbid-{imdbId}]</c>
        /// Priority: tt IMDB > TVDB (series/anime) > TMDB > title+year only.
        /// </summary>
        public static string BuildFolderName(
            string title,
            int? year,
            string? imdbId,
            string? tmdbId,
            string? tvdbId,
            string mediaType)
        {
            var sb = new StringBuilder(title);
            if (year.HasValue) sb.Append($" ({year})");

            if (!string.IsNullOrEmpty(imdbId) &&
                imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($" [imdbid-{imdbId}]");
            }
            else if (!string.IsNullOrEmpty(tvdbId) &&
                     (mediaType == "series" || mediaType == "anime"))
            {
                sb.Append($" [tvdbid-{tvdbId}]");
            }
            else if (!string.IsNullOrEmpty(tmdbId))
            {
                sb.Append($" [tmdbid-{tmdbId}]");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Convenience overload for callers with only title/year/imdbId.
        /// </summary>
        public static string BuildFolderName(string title, int? year, string? imdbId)
            => BuildFolderName(title, year, imdbId, null, null, "movie");

        /// <summary>
        /// Convenience overload for CatalogItem.
        /// </summary>
        public static string BuildFolderName(CatalogItem item)
            => BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType);

        /// <summary>Removes filesystem-unsafe characters from a path segment.</summary>
        public static string SanitisePath(string input)
        {
            var invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

            var result = sb.ToString().Trim();
            if (result.Contains(".."))
                throw new InvalidOperationException($"Path traversal detected in input: '{input}'");

            return result;
        }
    }
}
