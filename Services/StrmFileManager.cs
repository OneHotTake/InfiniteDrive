using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Manages all filesystem I/O for multi-version .strm files.
    /// Handles atomic writes, defensive folder creation, cleanup of stale versions,
    /// and empty directory removal. Never creates a folder unless writing at least one file.
    /// </summary>
    public class StrmFileManager
    {
        private readonly ILogger<StrmFileManager> _logger;
        private readonly ILibraryMonitor? _libraryMonitor;

        public StrmFileManager(ILogManager logManager, ILibraryMonitor? libraryMonitor = null)
        {
            _logger = new EmbyLoggerAdapter<StrmFileManager>(logManager.GetLogger("InfiniteDrive"));
            _libraryMonitor = libraryMonitor;
        }

        /// <summary>
        /// Writes or replaces multi-version .strm files in <paramref name="mediaFolder"/>.
        /// Follows Emby's multi-version naming: "{FolderName} - {VersionLabel}.strm"
        /// plus a default "{FolderName}.strm" for the best version.
        ///
        /// Rules:
        ///   - Only creates the folder if versions.Count > 0.
        ///   - Deletes stale .strm files not in the new version set.
        ///   - Uses atomic write (.tmp + Move) for corruption safety.
        ///   - Never leaves behind empty folders.
        /// </summary>
        /// <param name="mediaFolder">Target folder (e.g. /movies/Movie Name (2024))</param>
        /// <param name="folderBareName">Folder name without path (used for Emby version naming)</param>
        /// <param name="versions">Selected versions to write</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Number of .strm files written (including the default)</returns>
        public async Task<int> WriteOrReplaceStrmFilesAsync(
            string mediaFolder,
            string folderBareName,
            List<SelectedVersion> versions,
            CancellationToken ct)
        {
            if (versions.Count == 0)
            {
                // No valid versions — clean up existing files and folder
                await CleanupFolderAsync(mediaFolder, ct);
                return 0;
            }

            ct.ThrowIfCancellationRequested();

            // Ensure folder exists (only now that we know we have versions)
            Directory.CreateDirectory(mediaFolder);

            // Build the target filename set
            var baseName = Path.GetFileNameWithoutExtension(folderBareName);
            var desiredFiles = new Dictionary<string, SelectedVersion>(StringComparer.OrdinalIgnoreCase);

            // First version gets the default name (Emby auto-selects this)
            desiredFiles[$"{baseName}.strm"] = versions[0];

            // Subsequent versions get Emby multi-version naming
            for (int i = 1; i < versions.Count; i++)
            {
                var v = versions[i];
                var label = string.IsNullOrEmpty(v.VersionLabel) ? $"v{i + 1}" : v.VersionLabel;
                // Emby convention: "basename - suffix.strm"
                desiredFiles[$"{baseName} - {SanitiseFileName(label)}.strm"] = v;
            }

            // Delete stale .strm files that are no longer desired
            var staleDeleted = 0;
            if (Directory.Exists(mediaFolder))
            {
                var existingStrms = Directory.GetFiles(mediaFolder, "*.strm");
                var desiredNames = new HashSet<string>(desiredFiles.Keys, StringComparer.OrdinalIgnoreCase);

                foreach (var existing in existingStrms)
                {
                    var existingName = Path.GetFileName(existing);
                    if (!desiredNames.Contains(existingName))
                    {
                        SafeDelete(existing);
                        staleDeleted++;
                        _logger.LogDebug("[StrmFileManager] Removed stale version: {File}", existingName);
                    }
                }
            }

            // Write new/updated .strm files
            var written = 0;
            foreach (var (fileName, version) in desiredFiles)
            {
                ct.ThrowIfCancellationRequested();
                var fullPath = Path.Combine(mediaFolder, fileName);
                var content = version.Stream.Url;

                if (AtomicWrite(fullPath, content))
                {
                    written++;
                    _logger.LogDebug(
                        "[StrmFileManager] Wrote version: {File} ({Resolution} - {Audio} - {Score})",
                        fileName, version.Stream.Resolution, version.Stream.AudioPretty, version.SelectedScore);
                }
            }

            if (staleDeleted > 0 || written > 0)
                _logger.LogInformation(
                    "[StrmFileManager] {Folder}: wrote {Written}, removed {Stale} stale versions",
                    folderBareName, written, staleDeleted);

            return written;
        }

        /// <summary>
        /// Removes all .strm files from a folder and deletes the folder if empty.
        /// Safe to call on non-existent paths.
        /// </summary>
        public Task CleanupFolderAsync(string mediaFolder, CancellationToken ct)
        {
            if (!Directory.Exists(mediaFolder))
                return Task.CompletedTask;

            try
            {
                var removed = 0;
                foreach (var f in Directory.GetFiles(mediaFolder, "*.strm"))
                {
                    SafeDelete(f);
                    removed++;
                }

                CleanEmptyDir(mediaFolder);

                if (removed > 0)
                    _logger.LogInformation(
                        "[StrmFileManager] Cleaned up {Count} .strm files from {Folder}",
                        removed, mediaFolder);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmFileManager] Non-fatal cleanup error for {Folder}", mediaFolder);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Serialises a list of selected versions to JSON for database storage.
        /// </summary>
        public static string SerializeVersions(List<SelectedVersion> versions)
        {
            var dto = versions.Select(v => new StoredVersion
            {
                Url = v.Stream.Url,
                Resolution = v.Stream.Resolution,
                AudioPretty = v.Stream.AudioPretty,
                SourceTag = v.Stream.SourceTag,
                SizeGiB = v.Stream.SizeGiB,
                RankScore = v.SelectedScore,
                VersionLabel = v.VersionLabel,
                StreamKey = v.Stream.StreamKey,
            }).ToList();

            return JsonSerializer.Serialize(dto);
        }

        /// <summary>
        /// Deserialises stored versions from database JSON.
        /// </summary>
        public static List<StoredVersion> DeserializeVersions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return JsonSerializer.Deserialize<List<StoredVersion>>(json) ?? new(); }
            catch { return new(); }
        }

        // ── Atomic write ──────────────────────────────────────────────────────

        /// <summary>
        /// Writes content to a file atomically via .tmp + Move.
        /// Returns true if a new file was written or content changed.
        /// </summary>
        private bool AtomicWrite(string path, string content)
        {
            try
            {
                // Skip if content is identical (avoid unnecessary I/O + library scan)
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path);
                    if (string.Equals(existing, content, StringComparison.Ordinal))
                        return false; // No change needed
                }

                var tmpPath = path + ".tmp";
                File.WriteAllText(tmpPath, content, new UTF8Encoding(false));
                File.Move(tmpPath, path, overwrite: true);

                NotifyLibraryMonitor(path);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StrmFileManager] Failed to write {Path}", path);
                // Clean up temp file if left behind
                try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
                return false;
            }
        }

        // ── Filesystem helpers ─────────────────────────────────────────────────

        private void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    NotifyLibraryMonitor(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmFileManager] Non-fatal delete failure: {Path}", path);
            }
        }

        private static void CleanEmptyDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { /* Non-fatal: another process may have added a file */ }
        }

        private void NotifyLibraryMonitor(string path)
        {
            try { _libraryMonitor?.ReportFileSystemChanged(path); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmFileManager] ReportFileSystemChanged failed (non-fatal) for {Path}", path);
            }
        }

        /// <summary>
        /// Sanitises a string for use as a filename component (replaces invalid chars with spaces).
        /// </summary>
        private static string SanitiseFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result = name;
            foreach (var c in invalid)
                result = result.Replace(c, ' ');
            return result.Trim();
        }
    }
}
