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

            var sanitisedTitle = NamingPolicyService.SanitisePath(series.Title);
            var episodes = removedEpisodes.Select(e => (e.Season, e.Episode)).ToList();

            // Count files before delete for reporting
            int countBefore = 0;
            foreach (var ep in episodes)
            {
                var seasonDir = Path.Combine(showRoot, $"Season {ep.Season:D2}");
                if (!Directory.Exists(seasonDir)) continue;
                var pattern = $"{sanitisedTitle} S{ep.Season:D2}E{ep.Episode:D2}*";
                countBefore += Directory.GetFiles(seasonDir, $"{pattern}.strm").Length;
                countBefore += Directory.GetFiles(seasonDir, $"{pattern}.nfo").Length;
            }

            StrmWriterService.DeleteEpisodesWithVersions(showRoot, sanitisedTitle, episodes);

            _logger.LogInformation(
                "[EpisodeRemoval] {Title} — removed {Count} episodes: {List}",
                series.Title, removedEpisodes.Count, string.Join(", ", removedEpisodes.Select(e => $"S{e.Season:D2}E{e.Episode:D2}")));

            return Task.FromResult(countBefore);
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
