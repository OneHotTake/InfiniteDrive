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
        /// <c>{Title} ({Year}) [imdbid-{aioId}]</c>
        /// Priority: tt IMDB > anime provider (kitsu/anilist) > TVDB (series/anime) > TMDB > title+year only.
        /// </summary>
        public static string BuildFolderName(
            string title,
            int? year,
            string? aioId,
            string? tmdbId,
            string? tvdbId,
            string mediaType)
        {
            var sb = new StringBuilder(title);
            if (year.HasValue) sb.Append($" ({year})");

            if (!string.IsNullOrEmpty(aioId) &&
                aioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($" [imdbid-{aioId}]");
            }
            else if (!string.IsNullOrEmpty(aioId) && aioId.Contains(':'))
            {
                // Anime/obscure: emit the native provider tag (e.g. [kitsu=46474], [anilist=12345])
                var tag = ParseAioIdTag(aioId);
                if (tag != null) sb.Append($" [{tag}]");
                else if (!string.IsNullOrEmpty(tvdbId) && (mediaType == "series" || mediaType == "anime"))
                    sb.Append($" [tvdbid-{tvdbId}]");
                else if (!string.IsNullOrEmpty(tmdbId))
                    sb.Append($" [tmdbid-{tmdbId}]");
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
        /// Convenience overload for callers with only title/year/aioId.
        /// </summary>
        public static string BuildFolderName(string title, int? year, string? aioId)
            => BuildFolderName(title, year, aioId, null, null, "movie");

        /// <summary>
        /// Convenience overload for CatalogItem.
        /// </summary>
        public static string BuildFolderName(CatalogItem item)
            => BuildFolderName(item.Title, item.Year, item.AioId, item.TmdbId, item.TvdbId, item.MediaType);

        /// <summary>
        /// Builds ID tags for .strm filenames: <c>[tmdbid=X][imdbid=Y]</c>.
        /// TMDB first (preferred by Emby scraper), then IMDB/anime provider. Only non-null IDs included.
        /// </summary>
        public static string BuildIdTags(string? tmdbId, string? aioId, string? tvdbId = null)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(tmdbId))
                sb.Append($"[tmdbid={tmdbId}]");
            if (!string.IsNullOrEmpty(aioId))
            {
                if (aioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"[imdbid={aioId}]");
                else if (aioId.Contains(':'))
                {
                    var tag = ParseAioIdTag(aioId);
                    if (tag != null) sb.Append($"[{tag}]");
                }
            }
            if (!string.IsNullOrEmpty(tvdbId))
                sb.Append($"[tvdbid={tvdbId}]");
            return sb.ToString();
        }

        /// <summary>
        /// Parses an AIO ID with format "provider:id" into a tag string like "kitsu=46474".
        /// Returns null if the format doesn't match.
        /// </summary>
        private static string? ParseAioIdTag(string? aioId)
        {
            if (string.IsNullOrEmpty(aioId) || !aioId.Contains(':'))
                return null;

            var sep = aioId.IndexOf(':');
            var provider = aioId[..sep].ToLowerInvariant();
            var id = aioId[(sep + 1)..];
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(id))
                return null;

            return $"{provider}={id}";
        }

        /// <summary>
        /// Builds a .strm filename with embedded ID tags for Emby scraper matching.
        /// <para>Movies: <c>Title (Year) [tmdbid=X][imdbid=Y].strm</c></para>
        /// <para>Episodes: <c>Title S01E01 [tmdbid=X][imdbid=Y].strm</c></para>
        /// </summary>
        public static string BuildStrmFileName(CatalogItem item, int? season = null, int? episode = null)
        {
            var sanitised = SanitisePath(item.Title);
            var idTags = BuildIdTags(item.TmdbId, item.AioId, item.TvdbId);

            if (season.HasValue && episode.HasValue)
            {
                // Episode: Title S01E01 [tmdbid=X][imdbid=Y].strm
                var sb = new StringBuilder();
                sb.Append(sanitised);
                sb.Append($" S{season.Value:D2}E{episode.Value:D2}");
                if (idTags.Length > 0)
                {
                    sb.Append(' ');
                    sb.Append(idTags);
                }
                sb.Append(".strm");
                return sb.ToString();
            }

            // Movie: Title (Year) [tmdbid=X][imdbid=Y].strm
            {
                var sb = new StringBuilder();
                sb.Append(sanitised);
                if (item.Year.HasValue)
                    sb.Append($" ({item.Year})");
                if (idTags.Length > 0)
                {
                    sb.Append(' ');
                    sb.Append(idTags);
                }
                sb.Append(".strm");
                return sb.ToString();
            }
        }

        /// <summary>Removes filesystem-unsafe characters from a path segment.</summary>
        public static string SanitisePath(string input)
        {
            var invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

            return sb.ToString().Trim();
        }
    }
}
