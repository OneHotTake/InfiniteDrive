using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Housekeeping operations: orphaned folder cleanup, .strm validity checking,
    /// bulk .strm regeneration.
    /// </summary>
    public class HousekeepingService
    {
        private readonly ILogger<HousekeepingService> _logger;

        public HousekeepingService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<HousekeepingService>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── Orphaned folder cleanup (v0.60.1) ────────────────────────────────────

        /// <summary>
        /// Scans SyncPathMovies and SyncPathShows for old [tmdbid=...] folders
        /// and removes them if empty or containing only orphaned .strm files.
        /// Returns count of folders removed.
        /// </summary>
        public int CleanupOrphanedFolders()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return 0;

            var paths = new[] { config.SyncPathMovies, config.SyncPathShows };
            var removed = 0;
            var tmdbPattern = new Regex(@"\[tmdbid[=-]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (var root in paths)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (!tmdbPattern.IsMatch(name)) continue;

                    // Check if directory has any .strm files
                    var strmFiles = Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories);
                    if (strmFiles.Length == 0)
                    {
                        try
                        {
                            Directory.Delete(dir, recursive: true);
                            _logger.LogInformation(
                                "[InfiniteDrive] Removed empty orphaned folder: {Path}", dir);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "[InfiniteDrive] Could not remove orphaned folder: {Path}", dir);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[InfiniteDrive] Orphaned folder still contains {Count} .strm files — skipping: {Path}",
                            strmFiles.Length, dir);
                    }
                }
            }

            return removed;
        }

        // ── Bulk regeneration API (v0.60.4) ──────────────────────────────────────

        /// <summary>
        /// Counts all .strm files in configured media paths.
        /// Used by the API endpoint to report progress.
        /// </summary>
        public int CountStrmFiles()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return 0;

            var paths = new[] { config.SyncPathMovies, config.SyncPathShows };
            var count = 0;

            foreach (var root in paths)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                count += Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Length;
            }

            return count;
        }

    }

    public class ExpiredStrmResult
    {
        public string Path { get; set; } = string.Empty;
        public string ExpiredAt { get; set; } = string.Empty;
        public string Status { get; set; } = "expired";
    }
}
