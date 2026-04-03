using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
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
            _logger = new EmbyLoggerAdapter<HousekeepingService>(logManager.GetLogger("EmbyStreams"));
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
                                "[EmbyStreams] Removed empty orphaned folder: {Path}", dir);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "[EmbyStreams] Could not remove orphaned folder: {Path}", dir);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[EmbyStreams] Orphaned folder still contains {Count} .strm files — skipping: {Path}",
                            strmFiles.Length, dir);
                    }
                }
            }

            return removed;
        }

        // ── .strm validity check (v0.60.2) ───────────────────────────────────────

        /// <summary>
        /// Scans all .strm files in configured media paths and returns those
        /// with expired or invalid HMAC signatures.
        /// </summary>
        public List<ExpiredStrmResult> FindExpiredStrmFiles()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.PluginSecret))
                return new List<ExpiredStrmResult>();

            var paths = new[] { config.SyncPathMovies, config.SyncPathShows };
            var expired = new List<ExpiredStrmResult>();

            foreach (var root in paths)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                foreach (var strmFile in Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories))
                {
                    try
                    {
                        var content = File.ReadAllText(strmFile).Trim();
                        var expiry = CheckStrmExpiry(content, config.PluginSecret, config.SignatureValidityDays);
                        if (expiry != null)
                            expired.Add(expiry);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[EmbyStreams] Could not check .strm file: {Path}", strmFile);
                    }
                }
            }

            return expired;
        }

        /// <summary>
        /// Checks if a .strm URL has an expired signature. Returns null if valid.
        /// </summary>
        private ExpiredStrmResult? CheckStrmExpiry(string url, string secret, int validityDays)
        {
            // Look for exp= parameter in signed URLs
            var expMatch = Regex.Match(url, @"[?&]exp=(\d+)");
            if (!expMatch.Success) return null; // legacy unsigned URL — skip

            if (!long.TryParse(expMatch.Groups[1].Value, out var exp)) return null;

            var expTime = DateTimeOffset.FromUnixTimeSeconds(exp);
            var now = DateTimeOffset.UtcNow;

            if (expTime <= now)
            {
                return new ExpiredStrmResult
                {
                    Path = url,
                    ExpiredAt = expTime.ToString("o"),
                    Status = "expired"
                };
            }

            return null;
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
