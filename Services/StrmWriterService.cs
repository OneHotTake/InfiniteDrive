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
            if (isAnime && !string.IsNullOrWhiteSpace(config.SyncPathAnime))
            {
                var animeFolder = Path.Combine(
                    config.SyncPathAnime,
                    NamingPolicyService.SanitisePath(NamingPolicyService.BuildFolderName(item)));

                if (item.MediaType == "movie")
                {
                    Directory.CreateDirectory(animeFolder);
                    var animeFileName = $"{NamingPolicyService.SanitisePath(item.Title)}{(item.Year.HasValue ? $" ({item.Year})" : string.Empty)}.strm";
                    var animePath = Path.Combine(animeFolder, animeFileName);
                    WriteStrmFile(animePath, BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null));
                    if (config.EnableNfoHints) NfoWriterService.WriteSeedNfo(animePath, item, originSourceType.ToDisplayString());
                    await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                    return animePath;
                }
                else
                {
                    var animeSeasonDir = Path.Combine(animeFolder, "Season 01");
                    Directory.CreateDirectory(animeSeasonDir);
                    var animeStrmPath = Path.Combine(animeSeasonDir, $"{NamingPolicyService.SanitisePath(item.Title)} S01E01.strm");
                    WriteStrmFile(animeStrmPath, BuildSignedStrmUrl(config, item.ImdbId, "series", 1, 1));
                    if (config.EnableNfoHints) NfoWriterService.WriteSeedNfo(animeStrmPath, item, originSourceType.ToDisplayString());
                    await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                    return animeStrmPath;
                }
            }

            if (item.MediaType == "movie")
            {
                if (string.IsNullOrWhiteSpace(config.SyncPathMovies)) return null;
                var folderBareName = NamingPolicyService.SanitisePath(NamingPolicyService.BuildFolderName(item));
                var folder = Path.Combine(config.SyncPathMovies, folderBareName);
                Directory.CreateDirectory(folder);
                // File basename must match folder name for Emby version stacking
                var fileName = $"{folderBareName}.strm";
                var path = Path.Combine(folder, fileName);
                WriteStrmFile(path, BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null));
                if (config.EnableNfoHints) NfoWriterService.WriteSeedNfo(path, item, originSourceType.ToDisplayString());
                await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                return path;
            }

            // Series — seed S01E01
            if (string.IsNullOrWhiteSpace(config.SyncPathShows)) return null;
            var showDir = Path.Combine(config.SyncPathShows,
                NamingPolicyService.SanitisePath(NamingPolicyService.BuildFolderName(item)));
            var seasonDir = Path.Combine(showDir, "Season 01");
            Directory.CreateDirectory(seasonDir);
            var strmPath = Path.Combine(seasonDir, $"{NamingPolicyService.SanitisePath(item.Title)} S01E01.strm");
            WriteStrmFile(strmPath, BuildSignedStrmUrl(config, item.ImdbId, "series", 1, 1));
            if (config.EnableNfoHints) NfoWriterService.WriteSeedNfo(strmPath, item, originSourceType.ToDisplayString());
            await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
            return strmPath;
        }

        /// <summary>
        /// Full episode write: derives path from seriesItem.StrmPath, writes
        /// .strm + NFO. The authoritative method for gap repair and rehydration.
        /// </summary>
        public Task<string?> WriteEpisodeAsync(
            CatalogItem seriesItem, int season, int episode,
            string? episodeTitle, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(seriesItem.StrmPath))
            {
                _logger.LogWarning("[InfiniteDrive] StrmWriterService: cannot write episode — strm_path is null for {ImdbId}", seriesItem.ImdbId);
                return Task.FromResult((string?)null);
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null) return Task.FromResult((string?)null);

            // Derive show root: walk up from .../Show Name/Season XX/file.strm
            var existingDir = Path.GetDirectoryName(seriesItem.StrmPath);
            if (existingDir == null) return Task.FromResult((string?)null);
            var showDir = Path.GetDirectoryName(existingDir);
            if (showDir == null) return Task.FromResult((string?)null);

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

            var fileName = $"{NamingPolicyService.SanitisePath(seriesItem.Title)} S{season:D2}E{episode:D2}.strm";
            var filePath = Path.Combine(seasonDir, fileName);

            if (File.Exists(filePath))
            {
                return Task.FromResult((string?)filePath);
            }

            var url = BuildSignedStrmUrl(config, seriesItem.ImdbId, "series", season, episode);
            WriteStrmFile(filePath, url);

            if (config.EnableNfoHints)
                NfoWriterService.WriteSeedEpisodeNfo(filePath, seriesItem.Title, season, episode, episodeTitle);

            _logger.LogDebug("[InfiniteDrive] StrmWriterService: wrote episode {FilePath}", filePath);
            return Task.FromResult((string?)filePath);
        }

        /// <summary>
        /// Writes a .strm file to a caller-specified path.
        /// For use by SeriesPreExpansionService and EpisodeExpandTask which construct
        /// their own paths. Idempotent — no-op if file already exists.
        /// </summary>
        public Task WriteStrmWithVersionsAsync(
            string filePath, string imdbId, int season, int episode,
            CancellationToken ct)
        {
            if (File.Exists(filePath)) return Task.CompletedTask;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return Task.CompletedTask;

            var url = BuildSignedStrmUrl(config, imdbId, "series", season, episode);
            WriteStrmFile(filePath, url);
            return Task.CompletedTask;
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

            var fileName = $"{NamingPolicyService.SanitisePath(seriesItem.Title)} S{seasonNumber:D2}E{episodeNumber:D2}.strm";
            var filePath = Path.Combine(seasonDir, fileName);

            if (File.Exists(filePath))
                return filePath; // idempotent

            var url = BuildSignedStrmUrl(config, seriesItem.ImdbId, "series", seasonNumber, episodeNumber);
            WriteStrmFile(filePath, url);

            // Write episode NFO if enabled
            if (config.EnableNfoHints)
                NfoWriterService.WriteSeedEpisodeNfo(filePath, seriesItem.Title, seasonNumber, episodeNumber, episodeTitle);

            _logger.LogDebug("[InfiniteDrive] StrmWriterService: wrote episode {FilePath}", filePath);
            return filePath;
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
        /// Generates a signed URL for /InfiniteDrive/resolve endpoint using resolve tokens.
        /// </summary>
        public static string BuildSignedStrmUrl(
            PluginConfiguration config,
            string imdbId,
            string mediaType,
            int? season,
            int? episode,
            string quality = "")
        {
            // Default to config's DefaultSlotKey if not explicitly specified
            if (string.IsNullOrEmpty(quality))
                quality = config.DefaultSlotKey ?? "hd_broad";

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

        /// <summary>
        /// Public wrapper for SanitisePath for external callers.
        /// Delegates to NamingPolicyService.SanitisePath.
        /// </summary>
        public static string SanitisePathPublic(string input) => NamingPolicyService.SanitisePath(input);

        /// <summary>
        /// Writes all episode .strm files from VideosJson (AIOStreams one-pass sync).
        /// Sprint 370: One-pass series episode sync from AIOStreams.
        /// </summary>
        public Task<int> WriteEpisodesFromVideosJsonAsync(
            CatalogItem item,
            PluginConfiguration? config,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(item.VideosJson))
                return Task.FromResult(0);

            config ??= Plugin.Instance?.Configuration;
            if (config == null)
                return Task.FromResult(0);

            var basePath = string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase)
                ? config.SyncPathAnime
                : config.SyncPathShows;

            if (string.IsNullOrEmpty(basePath))
                return Task.FromResult(0);

            // Parse VideosJson to get episode list
            var videos = EpisodeDiffService.ParseVideoKeys(item.VideosJson);
            if (videos.Count == 0)
                return Task.FromResult(0);

            // Create series folder
            var folderName = NamingPolicyService.BuildFolderName(item);
            var seriesPath = Path.Combine(basePath, folderName);
            Directory.CreateDirectory(seriesPath);

            var written = 0;
            var sanitisedName = NamingPolicyService.SanitisePath(item.Title);

            // Group episodes by season
            var seasonGroups = videos.GroupBy(v => v.Season);

            foreach (var seasonGroup in seasonGroups)
            {
                var seasonNum = seasonGroup.Key;
                var seasonPath = Path.Combine(seriesPath, $"Season {seasonNum:D2}");
                Directory.CreateDirectory(seasonPath);

                foreach (var episode in seasonGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fileName = $"{sanitisedName} S{seasonNum:D2}E{episode.Episode:D2}.strm";
                    var filePath = Path.Combine(seasonPath, fileName);

                    if (!File.Exists(filePath))
                    {
                        var strmUrl = BuildSignedStrmUrl(config, item.ImdbId ?? item.Id, "series", seasonNum, episode.Episode);
                        WriteStrmFile(filePath, strmUrl);
                        written++;
                        _logger.LogDebug(
                            "[InfiniteDrive] WriteEpisodesFromVideosJson: Wrote {FilePath}",
                            filePath);
                    }
                }
            }

            // Write tvshow.nfo if enabled
            if (config.EnableNfoHints)
            {
                var nfoPath = Path.Combine(seriesPath, "tvshow.nfo");
                if (!File.Exists(nfoPath))
                    NfoWriterService.WriteSeedNfo(nfoPath, item, "AIOStreams");
            }

            _logger.LogInformation(
                "[InfiniteDrive] WriteEpisodesFromVideosJson: Wrote {Count} episodes for {Title}",
                written, item.Title);

            return Task.FromResult(written);
        }
    }
}
