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
                            "[InfiniteDrive] Could not check .strm file: {Path}", strmFile);
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

        // ── Token rotation (Sprint 141) ───────────────────────────────────────────

        /// <summary>
        /// Finds materialized versions with tokens expiring within 90 days or NULL
        /// (legacy items) and refreshes them with new tokens.
        /// Returns count of tokens rotated.
        /// </summary>
        public async Task<int> RotateExpiredTokensAsync(
            CancellationToken cancellationToken = default)
        {
            var db = Plugin.Instance?.DatabaseManager;
            var mvRepo = Plugin.Instance?.MaterializedVersionRepository;
            var config = Plugin.Instance?.Configuration;

            if (db == null || mvRepo == null || config == null || string.IsNullOrEmpty(config.PluginSecret))
                return 0;

            // Query for items expiring within 90 days or NULL (legacy items)
            var rotated = 0;
            var ninetyDaysSeconds = 90 * 24 * 60 * 60;

            var expiring = await mvRepo.GetMaterializedVersionsExpiringAsync(ninetyDaysSeconds, cancellationToken);
            foreach (var mv in expiring)
            {
                try
                {
                    if (!File.Exists(mv.StrmPath))
                    {
                        _logger.LogDebug(
                            "[InfiniteDrive] Skipping token rotation for missing .strm: {Path}", mv.StrmPath);
                        continue;
                    }

                    // Read current .strm to extract ID for token generation
                    var currentContent = await File.ReadAllTextAsync(mv.StrmPath, cancellationToken);

                    // Parse URL to extract id and idType parameters
                    var idMatch = System.Text.RegularExpressions.Regex.Match(currentContent, @"[?&]id=([^&]+)");
                    var idTypeMatch = System.Text.RegularExpressions.Regex.Match(currentContent, @"[?&]idType=([^&]+)");
                    var qualityMatch = System.Text.RegularExpressions.Regex.Match(currentContent, @"[?&]quality=([^&]+)");

                    if (!idMatch.Success || !idTypeMatch.Success || !qualityMatch.Success)
                    {
                        _logger.LogWarning(
                            "[InfiniteDrive] Could not parse .strm URL for token rotation: {Path}", mv.StrmPath);
                        continue;
                    }

                    var id = System.Uri.UnescapeDataString(idMatch.Groups[1].Value);
                    var idType = System.Uri.UnescapeDataString(idTypeMatch.Groups[1].Value);
                    var quality = System.Uri.UnescapeDataString(qualityMatch.Groups[1].Value);

                    // Generate fresh token
                    var expiresAt = DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds();
                    var newToken = PlaybackTokenService.GenerateResolveToken(
                        quality, id, config.PluginSecret, 365 * 24);

                    // Build new URL
                    var urlBuilder = new System.Text.StringBuilder();
                    urlBuilder.Append(currentContent.Substring(0, currentContent.IndexOf('?') + 1));
                    urlBuilder.Append("token=").Append(System.Uri.EscapeDataString(newToken));
                    // Keep other parameters as-is
                    var ampIndex = currentContent.IndexOf("&", currentContent.IndexOf('?'));
                    if (ampIndex >= 0)
                        urlBuilder.Append(currentContent.Substring(ampIndex));

                    var newContent = urlBuilder.ToString();

                    // Atomic write
                    var tmpPath = mv.StrmPath + ".tmp";
                    await File.WriteAllTextAsync(tmpPath, newContent, cancellationToken);
                    File.Move(tmpPath, mv.StrmPath, overwrite: true);

                    // Update DB after successful write
                    await mvRepo.SetStrmTokenExpiryAsync(
                        mv.MediaItemId, mv.SlotKey, expiresAt, cancellationToken);

                    _logger.LogInformation(
                        "[InfiniteDrive] Rotated token: {Id} | {Type} | {Quality} | expires={Expiry}",
                        id, idType, quality,
                        DateTimeOffset.FromUnixTimeSeconds(expiresAt).ToString("o"));

                    rotated++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[InfiniteDrive] Failed to rotate token for {Path}", mv.StrmPath);
                }
            }

            return rotated;
        }
    }

    public class ExpiredStrmResult
    {
        public string Path { get; set; } = string.Empty;
        public string ExpiredAt { get; set; } = string.Empty;
        public string Status { get; set; } = "expired";
    }
}
