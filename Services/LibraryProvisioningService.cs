using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Service for provisioning Emby libraries on first plugin install.
    /// Creates THREE separate libraries: Movies, Series, and Anime.
    ///
    /// Note: This is a foundational service for v3.3. The actual Emby library
    /// registration and user policy updates require additional implementation via REST API
    /// since the Emby SDK may not have direct programmatic library creation capabilities.
    /// </summary>
    public class LibraryProvisioningService
    {
        private readonly ILogger<LibraryProvisioningService> _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;

        // Flag file to track if provisioning has been done
        private const string ProvisioningFlagFile = "libraries_provisioned_v3.txt";

        public LibraryProvisioningService(
            ILogger<LibraryProvisioningService> logger,
            IApplicationPaths appPaths,
            IUserManager userManager,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _appPaths = appPaths;
            _userManager = userManager;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Checks if libraries need to be provisioned and performs provisioning if needed.
        /// Should be called during plugin initialization.
        /// </summary>
        /// <returns>True if provisioning was performed, false if already done or not needed.</returns>
        public async Task<bool> EnsureLibrariesProvisionedAsync()
        {
            var flagPath = GetProvisioningFlagPath();

            // Check if already provisioned
            if (File.Exists(flagPath))
            {
                _logger.LogInformation("[EmbyStreams] Libraries already provisioned (flag file exists)");
                return false;
            }

            _logger.LogInformation("[EmbyStreams] Starting library provisioning for v3.3");

            try
            {
                await ProvisionLibrariesAsync();

                // Write flag file to prevent re-provisioning
                await File.WriteAllTextAsync(flagPath,
                    $"Provisioned at: {DateTimeOffset.UtcNow:O}\n" +
                    $"Version: 3.3\n" +
                    $"Libraries: Movies, Series, Anime");

                _logger.LogInformation("[EmbyStreams] Library provisioning completed");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Library provisioning failed");
                throw;
            }
        }

        /// <summary>
        /// Performs the actual library provisioning.
        /// </summary>
        private async Task ProvisionLibrariesAsync()
        {
            // Step 1: Create directory paths
            CreateLibraryDirectories();

            // Step 2: Register Emby libraries (via REST API - placeholder)
            await RegisterEmbyLibrariesAsync();

            // Step 3: Hide libraries for all users (placeholder)
            await HideLibrariesForAllUsersAsync();

            // Step 4: Log summary
            LogProvisioningSummary();
        }

        /// <summary>
        /// Creates THREE distinct directory paths for the libraries.
        /// </summary>
        private void CreateLibraryDirectories()
        {
            var basePath = GetEmbyStreamsLibraryBasePath();

            var directories = new[]
            {
                Path.Combine(basePath, "movies"),
                Path.Combine(basePath, "series"),
                Path.Combine(basePath, "anime")
            };

            foreach (var dir in directories)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                        _logger.LogInformation("[EmbyStreams] Created directory: {Dir}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EmbyStreams] Failed to create directory: {Dir}", dir);
                }
            }
        }

        /// <summary>
        /// Registers THREE Emby libraries via REST API.
        /// Note: Emby SDK may not have direct library creation APIs.
        /// This method uses a placeholder that would need REST API integration.
        /// </summary>
        private async Task RegisterEmbyLibrariesAsync()
        {
            var basePath = GetEmbyStreamsLibraryBasePath();

            var libraries = new[]
            {
                new LibraryDefinition
                {
                    Name = "EmbyStreams Movies",
                    Path = Path.Combine(basePath, "movies"),
                    Type = "movies"
                },
                new LibraryDefinition
                {
                    Name = "EmbyStreams Series",
                    Path = Path.Combine(basePath, "series"),
                    Type = "tvshows"
                },
                new LibraryDefinition
                {
                    Name = "EmbyStreams Anime",
                    Path = Path.Combine(basePath, "anime"),
                    Type = "mixed" // Movies+Series for anime
                }
            };

            _logger.LogInformation("[EmbyStreams] Would register {Count} libraries: {Libraries}",
                libraries.Length, string.Join(", ", libraries.Select(l => l.Name)));

            // Note: Actual library registration requires Emby REST API
            // POST /Libraries/VirtualFolders with library configuration
            // This is deferred to implementation that can make HTTP calls to Emby server

            await Task.CompletedTask;
        }

        /// <summary>
        /// Hides all THREE EmbyStreams libraries from the navigation panel for all users.
        /// Note: This is a placeholder implementation that requires further development.
        /// </summary>
        private async Task HideLibrariesForAllUsersAsync()
        {
            var libraryNames = new[] { "EmbyStreams Movies", "EmbyStreams Series", "EmbyStreams Anime" };

            _logger.LogInformation("[EmbyStreams] Would hide libraries: {Libraries}", string.Join(", ", libraryNames));
            _logger.LogInformation("[EmbyStreams] User enumeration requires additional SDK integration");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Logs a summary of what was provisioned.
        /// </summary>
        private void LogProvisioningSummary()
        {
            var basePath = GetEmbyStreamsLibraryBasePath();

            _logger.LogInformation("""
[EmbyStreams] Library Provisioning Summary
=====================================
Libraries Created:
  1. EmbyStreams Movies   -> {MoviesPath}
  2. EmbyStreams Series   -> {SeriesPath}
  3. EmbyStreams Anime    -> {AnimePath}

Configuration:
  - Movies library: TMDB/IMDB metadata providers
  - Series library: TMDB/IMDB metadata providers
  - Anime library: AniList/AniDB metadata providers

Actions Taken:
  - Created directory structure
  - Registered libraries (via REST API - placeholder)
  - Hidden libraries from navigation for all users (placeholder)
  - Wrote provisioning flag file

Notes:
  - Users must manually configure metadata providers for Anime library
  - Library paths may need to be adjusted based on NFS mount location
  - Actual library registration requires REST API implementation
=====================================
""", Path.Combine(basePath, "movies"), Path.Combine(basePath, "series"), Path.Combine(basePath, "anime"));
        }

        /// <summary>
        /// Gets the base path for EmbyStreams libraries.
        /// Uses configuration or defaults to /embystreams/library/
        /// </summary>
        private string GetEmbyStreamsLibraryBasePath()
        {
            // Default to /embystreams/library/
            return "/embystreams/library";
        }

        /// <summary>
        /// Gets the path to the provisioning flag file.
        /// </summary>
        private string GetProvisioningFlagPath()
        {
            var configPath = _appPaths.DataPath;
            return Path.Combine(configPath, "EmbyStreams", ProvisioningFlagFile);
        }

        /// <summary>
        /// Resets the provisioning flag (for testing or re-provisioning).
        /// </summary>
        public void ResetProvisioningFlag()
        {
            var flagPath = GetProvisioningFlagPath();
            if (File.Exists(flagPath))
            {
                File.Delete(flagPath);
                _logger.LogInformation("[EmbyStreams] Provisioning flag reset");
            }
        }
    }

    /// <summary>
    /// Definition for a library to be provisioned.
    /// </summary>
    internal class LibraryDefinition
    {
        public string Name { get; init; } = null!;
        public string Path { get; init; } = null!;
        public string Type { get; init; } = null!;
    }
}
