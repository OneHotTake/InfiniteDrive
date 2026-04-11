using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Data;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Unified service for writing .strm files to disk.
    /// Replaces the public static WriteStrmFileForItemPublicAsync method
    /// from CatalogSyncTask, ensuring all .strm writes go through one
    /// code path with consistent attribution and NFO generation.
    /// </summary>
    public class StrmWriterService
    {
        private readonly ILogger<StrmWriterService> _logger;
        private readonly ILogManager _logManager;
        private readonly DatabaseManager _db;

        /// <summary>
        /// Constructor takes dependencies needed for filesystem operations.
        /// </summary>
        public StrmWriterService(ILogManager logManager, DatabaseManager db)
        {
            _logManager = logManager;
            _db = db;
            _logger = new EmbyLoggerAdapter<StrmWriterService>(logManager.GetLogger("InfiniteDrive"));
        }

        /// <summary>
        /// Writes a single .strm file for the given catalog item and returns
        /// the path that was written, or <c>null</c> if config paths are missing.
        /// </summary>
        /// <param name="item">The catalog item to write a .strm file for.</param>
        /// <param name="originSourceType">The source type that originated this item.</param>
        /// <param name="ownerUserId">Optional user ID who first added this item.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Path to written .strm file, or null if paths not configured.</returns>
        public async Task<string?> WriteAsync(
            CatalogItem item,
            SourceType originSourceType,
            string? ownerUserId,
            CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[InfiniteDrive] StrmWriterService: Plugin configuration not available");
                return null;
            }

            if (item.MediaType == "movie")
            {
                if (string.IsNullOrWhiteSpace(config.SyncPathMovies)) return null;
                var folder = Path.Combine(
                    config.SyncPathMovies,
                    SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType)));
                Directory.CreateDirectory(folder);
                var fileName = $"{SanitisePath(item.Title)}{(item.Year.HasValue ? $" ({item.Year})" : string.Empty)}.strm";
                var path = Path.Combine(folder, fileName);
                WriteStrmFile(path, BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null));
                WriteNfoFileIfEnabled(config, item, path, originSourceType);
                await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                return path;
            }

            // Series — seed S01E01
            if (string.IsNullOrWhiteSpace(config.SyncPathShows)) return null;
            var showDir = Path.Combine(config.SyncPathShows,
                SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType)));
            var seasonDir = Path.Combine(showDir, "Season 01");
            Directory.CreateDirectory(seasonDir);
            var strmPath = Path.Combine(seasonDir, $"{SanitisePath(item.Title)} S01E01.strm");
            WriteStrmFile(strmPath, BuildSignedStrmUrl(config, item.ImdbId, "series", 1, 1));
            WriteNfoFileIfEnabled(config, item, strmPath, originSourceType);
            await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
            return strmPath;
        }

        // ── Private: .strm file I/O ──────────────────────────────────────────────

        private void WriteStrmFile(string path, string url)
            => File.WriteAllText(path, url, new UTF8Encoding(false));

        private async Task PersistFirstAddedByUserIdIfNotSetAsync(
            CatalogItem item,
            string? ownerUserId,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ownerUserId))
                return; // System-sourced items pass null

            if (!string.IsNullOrEmpty(item.FirstAddedByUserId))
                return; // Already set, first-writer-wins

            await _db.SetFirstAddedByUserIdIfNotSetAsync(item.Id, ownerUserId, ct);
        }

        // ── Private: NFO file generation ───────────────────────────────────────────

        /// <summary>
        /// Writes a minimal Kodi-format .nfo file alongside the .strm if enabled.
        /// Contains only IMDB and TMDB uniqueid tags — no plot, poster, or cast data.
        /// Emby reads these IDs to match the item against its internal scraper.
        /// </summary>
        private void WriteNfoFileIfEnabled(
            PluginConfiguration config,
            CatalogItem item,
            string strmPath,
            SourceType originSourceType)
        {
            if (!config.EnableNfoHints)
                return;

            try
            {
                var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
                using var writer = new StreamWriter(nfoPath, false, new UTF8Encoding(false));
                writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                writer.WriteLine("<movie>");
                writer.WriteLine($"  <uniqueid type=\"imdb\">{item.ImdbId}</uniqueid>");
                if (!string.IsNullOrEmpty(item.TmdbId))
                    writer.WriteLine($"  <uniqueid type=\"tmdb\">{item.TmdbId}</uniqueid>");
                writer.WriteLine($"  <title>{EncodeXml(item.Title)}</title>");
                if (item.Year.HasValue)
                    writer.WriteLine($"  <year>{item.Year.Value}</year>");
                writer.WriteLine($"  <source>{originSourceType.ToDisplayString()}</source>");
                writer.WriteLine("</movie>");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] StrmWriterService: failed to write NFO for {ImdbId}", item.ImdbId);
            }
        }

        /// <summary>
        /// XML-escapes special characters to prevent malformed NFO files.
        /// </summary>
        private static string EncodeXml(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return input.Replace("&", "&amp;")
                       .Replace("<", "&lt;")
                       .Replace(">", "&gt;")
                       .Replace("\"", "&quot;")
                       .Replace("'", "&apos;");
        }

        // ── Private: helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds a folder name using Emby IMDB auto-match convention:
        /// <c>{Title} ({Year}) [imdbid-{imdbId}]</c>
        ///
        /// The <c>[imdbid-ttXXXXXXX]</c> suffix causes Emby's built-in scrapers
        /// (TMDb, OMDb) to automatically fetch poster, backdrop, cast, and ratings
        /// without requiring a separate .nfo file for ID hinting.
        /// </summary>
        private static string BuildFolderName(
            string title,
            int? year,
            string? imdbId,
            string? tmdbId,
            string? tvdbId,
            string mediaType)
        {
            var sb = new StringBuilder(title);
            if (year.HasValue) sb.Append($" ({year})");

            // Priority: tt > tvdb (series) > tmdb > nothing
            // Emby scanner reads the FIRST recognized hint in the folder name.
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
            // No hint if we have nothing — Emby will fuzzy-match by title+year

            return sb.ToString();
        }

        /// <summary>
        /// Generates a signed URL for /InfiniteDrive/resolve endpoint using resolve tokens.
        /// Falls back to legacy /InfiniteDrive/Play URL if PluginSecret is not configured.
        /// </summary>
        public static string BuildSignedStrmUrl(
            PluginConfiguration config,
            string imdbId,
            string mediaType,
            int? season,
            int? episode,
            string quality = "hd_broad")
        {
            // Ensure PluginSecret is initialized before accessing Configuration
            Plugin.Instance?.EnsureInitialization();

            var secret = Plugin.Instance?.Configuration?.PluginSecret;
            if (!string.IsNullOrEmpty(secret))
            {
                var baseUrl = config.EmbyBaseUrl.TrimEnd('/');

                // Generate resolve token (365-day validity for .strm files)
                var token = Services.PlaybackTokenService.GenerateResolveToken(quality, imdbId, secret, 365 * 24);

                var sb = new System.Text.StringBuilder();
                sb.Append(baseUrl);
                sb.Append("/InfiniteDrive/resolve?");
                sb.Append("token=").Append(Uri.EscapeDataString(token));
                sb.Append("&quality=").Append(Uri.EscapeDataString(quality));
                sb.Append("&id=").Append(Uri.EscapeDataString(imdbId));
                sb.Append("&idType=").Append(Uri.EscapeDataString(mediaType));

                if (season.HasValue)
                {
                    sb.Append("&season=").Append(season.Value);
                }
                if (episode.HasValue)
                {
                    sb.Append("&episode=").Append(episode.Value);
                }

                return sb.ToString();
            }

            // Fallback: legacy authenticated endpoint (requires X-Emby-Token)
            var fallbackUrl = config.EmbyBaseUrl.TrimEnd('/');
            if (season.HasValue && episode.HasValue)
                return $"{fallbackUrl}/InfiniteDrive/Play?imdb={imdbId}&season={season}&episode={episode}";
            return $"{fallbackUrl}/InfiniteDrive/Play?imdb={imdbId}";
        }

        /// <summary>Removes filesystem-unsafe characters from a path segment.</summary>
        private static string SanitisePath(string input)
        {
            var invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Public wrapper for SanitisePath for external callers.
        /// Moved from CatalogSyncTask.SanitisePathPublic (Sprint 156).
        /// </summary>
        public static string SanitisePathPublic(string input) => SanitisePath(input);
    }
}
