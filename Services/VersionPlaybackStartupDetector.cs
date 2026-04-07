using System;
using System.IO;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// IServerEntryPoint that detects server address changes on startup
    /// and triggers URL rewrite sweep for all materialized .strm files.
    ///
    /// When the Emby server's LAN address changes (e.g. DHCP reassignment,
    /// port change, migration to new hardware), every .strm file that
    /// references the old base URL becomes broken. This service normalizes
    /// both addresses, compares them, and rewrites .strm content if they
    /// differ.
    /// </summary>
    public class VersionPlaybackStartupDetector : IServerEntryPoint
    {
        private readonly ILogger<VersionPlaybackStartupDetector> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;

        public VersionPlaybackStartupDetector(
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<VersionPlaybackStartupDetector>(logManager.GetLogger("EmbyStreams"));
        }

        /// <summary>
        /// Runs on server startup to detect address changes and rewrite .strm files.
        /// </summary>
        public void Run()
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DetectAndRewriteAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[VersionPlayback] Error during startup address detection");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VersionPlayback] Failed to start address detection service");
            }
        }

        /// <summary>
        /// No event subscriptions to clean up.
        /// </summary>
        public void Dispose() { }

        // ── Core logic ──────────────────────────────────────────────────────

        private async Task DetectAndRewriteAsync()
        {
            var config = Plugin.Instance?.Configuration;
            var matRepo = Plugin.Instance?.MaterializedVersionRepository;
            if (config == null || matRepo == null)
            {
                _logger.LogWarning("[VersionPlayback] Plugin not fully initialised; skipping address check");
                return;
            }

            var currentAddress = NormalizeAddress(config.EmbyBaseUrl);
            var storedAddress = NormalizeAddress(config.LastKnownServerAddress);

            // Fresh install: no stored address yet
            if (string.IsNullOrEmpty(storedAddress))
            {
                config.LastKnownServerAddress = currentAddress;
                Plugin.Instance?.SaveConfiguration();
                _logger.LogInformation("[VersionPlayback] Stored initial server address: {Address}", currentAddress);
                return;
            }

            // Same address — nothing to do
            if (string.Equals(storedAddress, currentAddress, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[VersionPlayback] Server address unchanged: {Address}", currentAddress);
                return;
            }

            _logger.LogInformation(
                "[VersionPlayback] Server address changed: {Old} → {New}. Starting .strm rewrite sweep",
                storedAddress, currentAddress);

            // Query all materialized versions with .strm paths
            var versions = await matRepo.GetAllWithStrmPathsAsync();

            int rewritten = 0;
            int errors = 0;

            foreach (var mv in versions)
            {
                try
                {
                    if (string.IsNullOrEmpty(mv.StrmPath) || !File.Exists(mv.StrmPath))
                        continue;

                    var content = await File.ReadAllTextAsync(mv.StrmPath);
                    // Replace with scheme-agnostic matching: try both http:// and https:// prefixes
                    var updated = content
                        .Replace("http://" + storedAddress, "http://" + currentAddress, StringComparison.OrdinalIgnoreCase)
                        .Replace("https://" + storedAddress, "https://" + currentAddress, StringComparison.OrdinalIgnoreCase);

                    if (!ReferenceEquals(content, updated))
                    {
                        await File.WriteAllTextAsync(mv.StrmPath, updated);
                        rewritten++;
                    }

                    // Rate limit: 50ms between file rewrites to avoid filesystem pressure
                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogWarning(ex, "[VersionPlayback] Failed to rewrite .strm file: {Path}", mv.StrmPath);
                }
            }

            // Update stored address
            config.LastKnownServerAddress = currentAddress;
            Plugin.Instance?.SaveConfiguration();

            _logger.LogInformation(
                "[VersionPlayback] Address rewrite complete: {Rewritten} rewritten, {Errors} errors, {Total} total",
                rewritten, errors, versions.Count);

            // Trigger library scan on completion so Emby picks up changed files
            if (rewritten > 0)
            {
                try
                {
                    _libraryManager.QueueLibraryScan();
                    _logger.LogInformation("[VersionPlayback] Triggered library scan after .strm rewrite");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[VersionPlayback] Failed to trigger library scan");
                }
            }
        }

        /// <summary>
        /// Normalizes a URL to "host:port" format for comparison.
        /// Strips scheme, trailing slashes, and converts to lowercase.
        /// </summary>
        private static string NormalizeAddress(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return string.Empty;

            // Remove scheme
            var normalized = address.Trim();
            if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["https://".Length..];
            else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["http://".Length..];

            // Remove trailing slashes
            normalized = normalized.TrimEnd('/');

            return normalized.ToLowerInvariant();
        }
    }
}
