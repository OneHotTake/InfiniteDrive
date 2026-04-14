using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Deletes episode .strm + .nfo files for removed episodes.
    /// Used by catalog-first episode sync (Sprint 222).
    /// </summary>
    public class EpisodeRemovalService
    {
        private readonly ILogger _logger;

        public EpisodeRemovalService(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Deletes .strm + .nfo files for the given removed episodes.
        /// Derives file paths from the series StrmPath pattern.
        /// Removes empty season folders after deletion.
        /// Returns count of files deleted.
        /// </summary>
        public Task<int> RemoveEpisodesAsync(
            CatalogItem series,
            List<EpisodeKey> removedEpisodes,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(series.StrmPath))
            {
                _logger.LogWarning("[EpisodeRemoval] No StrmPath for {Title} — skipping", series.Title);
                return Task.FromResult(0);
            }

            if (removedEpisodes == null || removedEpisodes.Count == 0)
                return Task.FromResult(0);

            // Derive show root from StrmPath (may point to series folder or a season subfolder)
            var showRoot = DeriveShowRoot(series.StrmPath);
            if (string.IsNullOrEmpty(showRoot) || !Directory.Exists(showRoot))
            {
                _logger.LogWarning("[EpisodeRemoval] Show folder not found: {Path}", showRoot);
                return Task.FromResult(0);
            }

            // Build a set of filename patterns to match
            var sanitisedTitle = StrmWriterService.SanitisePathPublic(series.Title);
            int deleted = 0;
            var emptySeasonDirs = new HashSet<string>();

            foreach (var ep in removedEpisodes)
            {
                ct.ThrowIfCancellationRequested();

                var seasonDir = Path.Combine(showRoot, $"Season {ep.Season:D2}");
                if (!Directory.Exists(seasonDir)) continue;

                var pattern = $"{sanitisedTitle} S{ep.Season:D2}E{ep.Episode:D2}.*";
                var files = Directory.GetFiles(seasonDir, pattern);

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file);
                    if (ext != ".strm" && ext != ".nfo") continue;

                    try
                    {
                        File.Delete(file);
                        deleted++;
                        _logger.LogDebug("[EpisodeRemoval] Deleted {File}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[EpisodeRemoval] Failed to delete {File}", file);
                    }
                }

                emptySeasonDirs.Add(seasonDir);
            }

            // Clean up empty season folders
            foreach (var dir in emptySeasonDirs)
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                        _logger.LogDebug("[EpisodeRemoval] Removed empty season folder: {Dir}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EpisodeRemoval] Failed to remove season folder {Dir}", dir);
                }
            }

            _logger.LogInformation(
                "[EpisodeRemoval] {Title} — removed {Count} episodes: {List}",
                series.Title, deleted, string.Join(", ", removedEpisodes.Select(e => $"S{e.Season:D2}E{e.Episode:D2}")));

            return Task.FromResult(deleted);
        }

        /// <summary>
        /// Derives the show root folder from StrmPath.
        /// If StrmPath points to a Season XX subfolder, go up one level.
        /// If StrmPath is the series folder itself, use as-is.
        /// </summary>
        private static string DeriveShowRoot(string strmPath)
        {
            if (string.IsNullOrEmpty(strmPath)) return strmPath;

            var dirName = Path.GetFileName(strmPath);
            if (dirName != null && dirName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(strmPath) ?? strmPath;

            return strmPath;
        }
    }
}
