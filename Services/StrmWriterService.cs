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
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Unified service for writing .strm files to disk.
    /// Replaces the public static WriteStrmFileForItemPublicAsync method
    /// from CatalogSyncTask, ensuring all .strm writes go through one
    /// code path with consistent attribution.
    /// </summary>
    public class StrmWriterService
    {
        private readonly ILogger<StrmWriterService> _logger;
        private readonly ILogManager _logManager;
        private readonly DatabaseManager _db;
        private readonly ILibraryMonitor? _libraryMonitor;

        /// <summary>
        /// Constructor takes dependencies needed for filesystem operations.
        /// </summary>
        public StrmWriterService(ILogManager logManager, DatabaseManager db, ILibraryMonitor? libraryMonitor = null)
        {
            _logManager = logManager;
            _db = db;
            _libraryMonitor = libraryMonitor;
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

            var isAnime = string.Equals(item.CatalogType, "anime", StringComparison.OrdinalIgnoreCase)
                         || IsAnimeId(item.AioId);
            if (isAnime && !string.IsNullOrWhiteSpace(config.SyncPathAnime))
            {
                var animeFolder = Path.Combine(
                    config.SyncPathAnime,
                    NamingPolicyService.SanitisePath(NamingPolicyService.BuildFolderName(item)));

                if (item.MediaType == "movie")
                {
                    Directory.CreateDirectory(animeFolder);
                    var animePath = Path.Combine(animeFolder, NamingPolicyService.BuildStrmFileName(item));
                    WriteStrmFile(animePath, string.Empty);
                    await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                    return animePath;
                }
                else
                {
                    var animeSeasonDir = Path.Combine(animeFolder, "Season 01");
                    Directory.CreateDirectory(animeSeasonDir);
                    var animeStrmPath = Path.Combine(animeSeasonDir, NamingPolicyService.BuildStrmFileName(item, 1, 1));
                    WriteStrmFile(animeStrmPath, string.Empty);
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
                var fileName = NamingPolicyService.BuildStrmFileName(item);
                var path = Path.Combine(folder, fileName);
                WriteStrmFile(path, string.Empty);
                await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                return path;
            }

            // Series — seed S01E01
            if (string.IsNullOrWhiteSpace(config.SyncPathShows)) return null;
            var showDir = Path.Combine(config.SyncPathShows,
                NamingPolicyService.SanitisePath(NamingPolicyService.BuildFolderName(item)));
            var seasonDir = Path.Combine(showDir, "Season 01");
            Directory.CreateDirectory(seasonDir);
            var strmPath = Path.Combine(seasonDir, NamingPolicyService.BuildStrmFileName(item, 1, 1));
            WriteStrmFile(strmPath, string.Empty);
            await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
            return strmPath;
        }

        /// <summary>
        /// Full episode write: derives path from seriesItem.StrmPath, writes
        /// .strm. The authoritative method for gap repair and rehydration.
        /// </summary>
        public Task<string?> WriteEpisodeAsync(
            CatalogItem seriesItem, int season, int episode,
            string? episodeTitle, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(seriesItem.StrmPath))
            {
                _logger.LogWarning("[InfiniteDrive] StrmWriterService: cannot write episode — strm_path is null for {AioId}", seriesItem.AioId);
                return Task.FromResult((string?)null);
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null) return Task.FromResult((string?)null);

            var filePath = BuildEpisodePath(seriesItem.StrmPath, seriesItem, season, episode);
            if (filePath == null) return Task.FromResult((string?)null);

            if (File.Exists(filePath))
                return Task.FromResult((string?)filePath);

            WriteStrmFile(filePath, string.Empty);

            _logger.LogDebug("[InfiniteDrive] StrmWriterService: wrote episode {FilePath}", filePath);
            return Task.FromResult((string?)filePath);
        }

        /// <summary>
        /// Writes a .strm file to a caller-specified path.
        /// Idempotent — no-op if file already exists.
        /// </summary>
        public Task WriteStrmWithVersionsAsync(
            string filePath, string aioId, int season, int episode,
            CancellationToken ct)
        {
            if (File.Exists(filePath)) return Task.CompletedTask;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return Task.CompletedTask;

            WriteStrmFile(filePath, string.Empty);
            return Task.CompletedTask;
        }

        // ── Private: .strm file I/O ──────────────────────────────────────────────

        /// <summary>
        /// Derives the full file path for an episode .strm from an existing StrmPath.
        /// Walks up from .../Show Name/Season XX/file.strm to find the show root,
        /// then builds the target season/episode path.
        /// </summary>
        private static string? BuildEpisodePath(
            string existingStrmPath, CatalogItem item, int season, int episode)
        {
            var existingDir = Path.GetDirectoryName(existingStrmPath);
            if (existingDir == null) return null;
            var showDir = Path.GetDirectoryName(existingDir);
            if (showDir == null) return null;

            var seasonName = Path.GetFileName(existingDir);
            if (!seasonName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
                showDir = existingDir;

            var seasonDir = Path.Combine(showDir, $"Season {season:D2}");
            Directory.CreateDirectory(seasonDir);

            var fileName = NamingPolicyService.BuildStrmFileName(item, season, episode);
            return Path.Combine(seasonDir, fileName);
        }

        private void WriteStrmFile(string path, string url)
        {
            File.WriteAllText(path, url, new UTF8Encoding(false));
            try { _libraryMonitor?.ReportFileSystemChanged(path); }
            catch (Exception ex) { _logger.LogDebug(ex, "[StrmWriterService] ReportFileSystemChanged failed (non-fatal) for {Path}", path); }
        }

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
        /// Deletes a .strm file, all version slot variants, and cleans
        /// empty parent directories. Null-safe — no-op if path is null or missing.
        /// </summary>
        public static void DeleteWithVersions(string? strmPath)
        {
            if (string.IsNullOrEmpty(strmPath)) return;

            try
            {
                var dir = Path.GetDirectoryName(strmPath);
                var baseName = Path.GetFileNameWithoutExtension(strmPath);

                SafeDelete(strmPath);

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                // Versioned variants: "basename - suffix.strm"
                foreach (var f in Directory.GetFiles(dir, $"{baseName} - *.strm"))
                    SafeDelete(f);

                // Clean empty directories up two levels (season → show → library)
                CleanEmptyDir(dir);
                if (!string.IsNullOrEmpty(dir))
                    CleanEmptyDir(Path.GetDirectoryName(dir));
            }
            catch (Exception ex) { Plugin.Instance?.Logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "DeleteWithVersions cleanup"); }
        }

        /// <summary>
        /// Deletes episode .strm files (base + version variants) for the
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
            try { File.Delete(path); } catch (Exception ex) { Plugin.Instance?.Logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", $"SafeDelete {path}"); }
        }

        private static void CleanEmptyDir(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) { Plugin.Instance?.Logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", $"CleanEmptyDir {dir}"); }
        }

        // ── Private: helpers ─────────────────────────────────────────────────────

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

            // Group episodes by season
            var seasonGroups = videos.GroupBy(v => v.Season);

            foreach (var seasonGroup in seasonGroups)
            {
                var seasonNum = seasonGroup.Key;
                var seasonPath = Path.Combine(seriesPath, $"Season {seasonNum:D2}");
                Directory.CreateDirectory(seasonPath);

                foreach (var ep in seasonGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fileName = NamingPolicyService.BuildStrmFileName(item, seasonNum, ep.Episode);
                    var filePath = Path.Combine(seasonPath, fileName);

                    if (!File.Exists(filePath))
                    {
                        WriteStrmFile(filePath, string.Empty);
                        written++;
                        _logger.LogDebug(
                            "[InfiniteDrive] WriteEpisodesFromVideosJson: Wrote {FilePath}",
                            filePath);
                    }
                }
            }

            _logger.LogInformation(
                "[InfiniteDrive] WriteEpisodesFromVideosJson: Wrote {Count} episodes for {Title}",
                written, item.Title);

            return Task.FromResult(written);
        }

        /// <summary>
        /// Detects anime-specific ID prefixes that indicate an item should be
        /// routed to the anime library regardless of CatalogType.
        /// Covers all prefixes from AIOStreams IdParser (id-parser.ts).
        /// </summary>
        private static bool IsAnimeId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var s = id!;
            // Fast path: check first character to avoid scanning non-anime IDs
            var c = char.ToLowerInvariant(s[0]);
            return c switch
            {
                'k' => s.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase),
                'a' => s.StartsWith("anilist:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("anidb:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("anidb_id:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("anidbid:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("animeplanet:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("ap:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("acd:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("anisearch:", StringComparison.OrdinalIgnoreCase),
                'm' => s.StartsWith("mal:", StringComparison.OrdinalIgnoreCase),
                'n' => s.StartsWith("notifymoe:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("nm:", StringComparison.OrdinalIgnoreCase),
                's' => s.StartsWith("simkl:", StringComparison.OrdinalIgnoreCase),
                'l' => s.StartsWith("livechart:", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
    }
}
