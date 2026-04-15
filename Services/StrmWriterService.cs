using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            var isAnime = string.Equals(item.CatalogType, "anime", StringComparison.OrdinalIgnoreCase);
            if (isAnime && config.EnableAnimeLibrary && !string.IsNullOrWhiteSpace(config.SyncPathAnime))
            {
                var animeFolder = Path.Combine(
                    config.SyncPathAnime,
                    SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType)));

                if (item.MediaType == "movie")
                {
                    Directory.CreateDirectory(animeFolder);
                    var animeFileName = $"{SanitisePath(item.Title)}{(item.Year.HasValue ? $" ({item.Year})" : string.Empty)}.strm";
                    var animePath = Path.Combine(animeFolder, animeFileName);
                    WriteStrmFile(animePath, BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null));
                    WriteNfoFileIfEnabled(config, item, animePath, originSourceType);
                    await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                    await WriteVersionSlotsAsync(animePath, config, item.ImdbId, "movie", null, null, ct);
                    return animePath;
                }
                else
                {
                    var animeSeasonDir = Path.Combine(animeFolder, "Season 01");
                    Directory.CreateDirectory(animeSeasonDir);
                    var animeStrmPath = Path.Combine(animeSeasonDir, $"{SanitisePath(item.Title)} S01E01.strm");
                    WriteStrmFile(animeStrmPath, BuildSignedStrmUrl(config, item.ImdbId, "series", 1, 1));
                    WriteNfoFileIfEnabled(config, item, animeStrmPath, originSourceType);
                    await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                    await WriteVersionSlotsAsync(animeStrmPath, config, item.ImdbId, "series", 1, 1, ct);
                    return animeStrmPath;
                }
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
                await WriteVersionSlotsAsync(path, config, item.ImdbId, "movie", null, null, ct);
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
            await WriteVersionSlotsAsync(strmPath, config, item.ImdbId, "series", 1, 1, ct);
            return strmPath;
        }

        /// <summary>
        /// Writes additional versioned .strm files for each enabled non-default quality slot.
        /// Emby groups files with " - {suffix}" naming as versions of the same item,
        /// enabling the native quality picker in the UI.
        /// No-op when only the default slot is enabled (preserves existing behavior).
        /// </summary>
        private async Task WriteVersionSlotsAsync(
            string baseStrmPath,
            PluginConfiguration config,
            string imdbId,
            string mediaType,
            int? season,
            int? episode,
            CancellationToken ct)
        {
            var slotRepo = Plugin.Instance?.VersionSlotRepository;
            if (slotRepo == null) return;

            var enabledSlots = await slotRepo.GetEnabledSlotsAsync(ct);
            var defaultSlot = enabledSlots.FirstOrDefault(s => s.IsDefault) ?? enabledSlots.FirstOrDefault();
            if (defaultSlot == null) return;

            var additionalSlots = enabledSlots.Where(s => s.SlotKey != defaultSlot.SlotKey).ToList();
            if (additionalSlots.Count == 0) return;

            var folder = Path.GetDirectoryName(baseStrmPath)!;
            var baseName = Path.GetFileNameWithoutExtension(baseStrmPath);

            foreach (var slot in additionalSlots)
            {
                var suffix = slot.FileSuffix;
                if (string.IsNullOrWhiteSpace(suffix)) continue;

                var versionedPath = Path.Combine(folder, $"{baseName} - {suffix}.strm");
                var url = BuildSignedStrmUrl(config, imdbId, mediaType, season, episode, quality: slot.SlotKey);
                WriteStrmFile(versionedPath, url);
            }
        }

        /// <summary>
        /// Full episode write: derives path from seriesItem.StrmPath, writes
        /// .strm + NFO + all enabled version slots. The authoritative method
        /// for gap repair and rehydration.
        /// </summary>
        public async Task<string?> WriteEpisodeAsync(
            CatalogItem seriesItem, int season, int episode,
            string? episodeTitle, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(seriesItem.StrmPath))
            {
                _logger.LogWarning("[InfiniteDrive] StrmWriterService: cannot write episode — strm_path is null for {ImdbId}", seriesItem.ImdbId);
                return null;
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            // Derive show root: walk up from .../Show Name/Season XX/file.strm
            var existingDir = Path.GetDirectoryName(seriesItem.StrmPath);
            if (existingDir == null) return null;
            var showDir = Path.GetDirectoryName(existingDir);
            if (showDir == null) return null;

            var seasonName = Path.GetFileName(existingDir);
            string seasonDir;
            if (!seasonName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
            {
                showDir = existingDir;
                seasonDir = Path.Combine(showDir, $"Season {season:D2}");
            }
            else
            {
                seasonDir = Path.Combine(showDir, $"Season {season:D2}");
            }

            Directory.CreateDirectory(seasonDir);

            var fileName = $"{SanitisePath(seriesItem.Title)} S{season:D2}E{episode:D2}.strm";
            var filePath = Path.Combine(seasonDir, fileName);

            if (File.Exists(filePath))
            {
                // Idempotent — but still ensure version slots exist
                await WriteVersionSlotsAsync(filePath, config, seriesItem.ImdbId, "series", season, episode, ct);
                return filePath;
            }

            var url = BuildSignedStrmUrl(config, seriesItem.ImdbId, "series", season, episode);
            WriteStrmFile(filePath, url);

            if (config.EnableNfoHints)
                WriteEpisodeNfo(seriesItem, season, episode, episodeTitle, filePath);

            await WriteVersionSlotsAsync(filePath, config, seriesItem.ImdbId, "series", season, episode, ct);

            _logger.LogDebug("[InfiniteDrive] StrmWriterService: wrote episode + versions {FilePath}", filePath);
            return filePath;
        }

        /// <summary>
        /// Writes a .strm file + all enabled version slots to a caller-specified path.
        /// For use by SeriesPreExpansionService and EpisodeExpandTask which construct
        /// their own paths. Idempotent — no-op if file already exists.
        /// </summary>
        public async Task WriteStrmWithVersionsAsync(
            string filePath, string imdbId, int season, int episode,
            CancellationToken ct)
        {
            if (File.Exists(filePath)) return;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return;

            var url = BuildSignedStrmUrl(config, imdbId, "series", season, episode);
            WriteStrmFile(filePath, url);
            await WriteVersionSlotsAsync(filePath, config, imdbId, "series", season, episode, ct);
        }

        /// <summary>
        /// Writes a single episode .strm + .nfo for a series repair (sync).
        /// Prefer <see cref="WriteEpisodeAsync"/> which also writes version slots.
        /// Kept for backward compatibility.
        /// </summary>
        public string? WriteEpisodeStrm(
            CatalogItem seriesItem,
            int seasonNumber,
            int episodeNumber,
            string? episodeTitle)
        {
            if (string.IsNullOrEmpty(seriesItem.StrmPath))
            {
                _logger.LogWarning("[InfiniteDrive] StrmWriterService: cannot repair episode — strm_path is null for {ImdbId}", seriesItem.ImdbId);
                return null;
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            // Derive show root: walk up from .../Show Name/Season XX/file.strm
            var seasonDir = Path.GetDirectoryName(seriesItem.StrmPath);
            if (seasonDir == null) return null;
            var showDir = Path.GetDirectoryName(seasonDir);
            if (showDir == null) return null;

            // If the existing path isn't in a Season XX folder (edge case), use its parent
            var seasonName = Path.GetFileName(seasonDir);
            if (!seasonName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
            {
                // StrmPath is directly in show dir — use showDir as base
                showDir = seasonDir;
                seasonDir = Path.Combine(showDir, $"Season {seasonNumber:D2}");
            }
            else
            {
                // Ensure we target the correct season folder
                seasonDir = Path.Combine(showDir, $"Season {seasonNumber:D2}");
            }

            Directory.CreateDirectory(seasonDir);

            var fileName = $"{SanitisePath(seriesItem.Title)} S{seasonNumber:D2}E{episodeNumber:D2}.strm";
            var filePath = Path.Combine(seasonDir, fileName);

            if (File.Exists(filePath))
                return filePath; // idempotent

            var url = BuildSignedStrmUrl(config, seriesItem.ImdbId, "series", seasonNumber, episodeNumber);
            WriteStrmFile(filePath, url);

            // Write episode NFO if enabled
            if (config.EnableNfoHints)
                WriteEpisodeNfo(seriesItem, seasonNumber, episodeNumber, episodeTitle, filePath);

            _logger.LogDebug("[InfiniteDrive] StrmWriterService: wrote episode {FilePath}", filePath);
            return filePath;
        }

        /// <summary>
        /// Writes a minimal episodedetails NFO for a repaired episode.
        /// </summary>
        private void WriteEpisodeNfo(
            CatalogItem seriesItem,
            int seasonNumber,
            int episodeNumber,
            string? episodeTitle,
            string strmPath)
        {
            try
            {
                var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
                using var writer = new StreamWriter(nfoPath, false, new UTF8Encoding(false));
                writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                writer.WriteLine("<episodedetails>");
                writer.WriteLine($"  <title>{EncodeXml(episodeTitle ?? $"Episode {episodeNumber}")}</title>");
                writer.WriteLine($"  <season>{seasonNumber}</season>");
                writer.WriteLine($"  <episode>{episodeNumber}</episode>");
                writer.WriteLine($"  <showtitle>{EncodeXml(seriesItem.Title)}</showtitle>");
                if (!string.IsNullOrEmpty(seriesItem.ImdbId))
                    writer.WriteLine($"  <uniqueid type=\"imdb\">{seriesItem.ImdbId}</uniqueid>");
                if (!string.IsNullOrEmpty(seriesItem.TmdbId))
                    writer.WriteLine($"  <uniqueid type=\"tmdb\">{seriesItem.TmdbId}</uniqueid>");
                if (!string.IsNullOrEmpty(seriesItem.TvdbId))
                    writer.WriteLine($"  <uniqueid type=\"tvdb\">{seriesItem.TvdbId}</uniqueid>");
                writer.WriteLine("</episodedetails>");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] StrmWriterService: failed to write episode NFO for {ImdbId} S{S}E{E}",
                    seriesItem.ImdbId, seasonNumber, episodeNumber);
            }
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

        // ── Public: centralized delete helpers (FIX-353-01) ────────────────────

        /// <summary>
        /// Deletes a .strm file, its .nfo, all version slot variants, and cleans
        /// empty parent directories. Null-safe — no-op if path is null or missing.
        /// </summary>
        public static void DeleteWithVersions(string? strmPath)
        {
            if (string.IsNullOrEmpty(strmPath)) return;

            try
            {
                var dir = Path.GetDirectoryName(strmPath);
                var baseName = Path.GetFileNameWithoutExtension(strmPath);

                // Base .strm + .nfo
                SafeDelete(strmPath);
                SafeDelete(Path.ChangeExtension(strmPath, ".nfo"));

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                // Versioned variants: "basename - suffix.strm" / "basename - suffix.nfo"
                foreach (var f in Directory.GetFiles(dir, $"{baseName} - *.strm"))
                    SafeDelete(f);
                foreach (var f in Directory.GetFiles(dir, $"{baseName} - *.nfo"))
                    SafeDelete(f);

                // Clean empty directories up two levels (season → show → library)
                CleanEmptyDir(dir);
                if (!string.IsNullOrEmpty(dir))
                    CleanEmptyDir(Path.GetDirectoryName(dir));
            }
            catch { /* best-effort cleanup */ }
        }

        /// <summary>
        /// Deletes episode .strm + .nfo files (base + version variants) for the
        /// given episodes, then cleans empty season and show directories.
        /// </summary>
        public static void DeleteEpisodesWithVersions(
            string showDir,
            string sanitisedTitle,
            IEnumerable<(int season, int episode)> episodes)
        {
            if (string.IsNullOrEmpty(showDir) || !Directory.Exists(showDir)) return;

            var emptySeasonDirs = new HashSet<string>();

            foreach (var (season, episode) in episodes)
            {
                var seasonDir = Path.Combine(showDir, $"Season {season:D2}");
                if (!Directory.Exists(seasonDir)) continue;

                // "Title S01E01*" catches base + "Title S01E01 - 4K" etc.
                var epPattern = $"{sanitisedTitle} S{season:D2}E{episode:D2}*";

                foreach (var f in Directory.GetFiles(seasonDir, $"{epPattern}.strm"))
                    SafeDelete(f);
                foreach (var f in Directory.GetFiles(seasonDir, $"{epPattern}.nfo"))
                    SafeDelete(f);

                emptySeasonDirs.Add(seasonDir);
            }

            foreach (var d in emptySeasonDirs)
                CleanEmptyDir(d);
            CleanEmptyDir(showDir);
        }

        // ── Private: delete helpers ─────────────────────────────────────────────

        private static void SafeDelete(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { File.Delete(path); } catch { }
        }

        private static void CleanEmptyDir(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
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
                // Token is opaque - IMDB ID and quality are passed as query parameters
                var token = Services.PlaybackTokenService.GenerateResolveToken(secret, 365 * 24);

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

            // PluginSecret is required — fail closed rather than generating dead-end URLs
            throw new InvalidOperationException("PluginSecret not configured — cannot sign .strm URL");
        }

        /// <summary>Removes filesystem-unsafe characters from a path segment.</summary>
        private static string SanitisePath(string input)
        {
            var invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

            // Sprint 303-04: Block path traversal
            var result = sb.ToString().Trim();
            if (result.Contains(".."))
                throw new InvalidOperationException($"Path traversal detected in input: '{input}'");

            return result;
        }

        /// <summary>
        /// Public wrapper for SanitisePath for external callers.
        /// Moved from CatalogSyncTask.SanitisePathPublic (Sprint 156).
        /// </summary>
        public static string SanitisePathPublic(string input) => SanitisePath(input);
    }
}
