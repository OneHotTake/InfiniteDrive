using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Creates and verifies Emby virtual folder libraries for InfiniteDrive.
    /// Idempotent — safe to call on every wizard run.
    /// </summary>
    public class LibraryProvisioningService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<LibraryProvisioningService> _logger;

        public LibraryProvisioningService(
            ILibraryManager libraryManager,
            ILogger<LibraryProvisioningService> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// Creates disk directories and Emby library entries for all configured paths.
        /// Skips any library whose path already exists in Emby. Safe to call repeatedly.
        /// </summary>
        public async Task EnsureLibrariesProvisionedAsync()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[InfiniteDrive] LibraryProvisioningService: config not available");
                return;
            }

            _logger.LogInformation("[InfiniteDrive] Ensuring libraries are provisioned…");

            await ProvisionOneAsync(
                config,
                config.LibraryNameMovies ?? "Streamed Movies",
                "movies",
                config.SyncPathMovies);

            await ProvisionOneAsync(
                config,
                config.LibraryNameSeries ?? "Streamed Series",
                "tvshows",
                config.SyncPathShows);

            if (!string.IsNullOrWhiteSpace(config.SyncPathAnime))
            {
                await ProvisionOneAsync(
                    config,
                    config.LibraryNameAnime ?? "Streamed Anime",
                    "",
                    config.SyncPathAnime);
            }

            _logger.LogInformation("[InfiniteDrive] Library provisioning complete");
        }

        private async Task ProvisionOneAsync(PluginConfiguration config, string name, string contentType, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    _logger.LogInformation("[InfiniteDrive] Created directory: {Path}", path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Failed to create directory: {Path}", path);
                return;
            }

            var norm = path.TrimEnd('/', '\\');
            var existing = _libraryManager.GetVirtualFolders();
            var alreadyRegistered = existing.Any(f =>
                f.Locations != null &&
                f.Locations.Any(loc =>
                    string.Equals(
                        loc.TrimEnd('/', '\\'),
                        norm,
                        StringComparison.OrdinalIgnoreCase)));

            if (alreadyRegistered)
            {
                _logger.LogInformation(
                    "[InfiniteDrive] Library '{Name}' already exists at {Path} — skipping", name, path);
                return;
            }

            try
            {
                var libraryOptions = new LibraryOptions
                {
                    ContentType = contentType,
                    PathInfos = new[]
                    {
                        new MediaPathInfo { Path = path }
                    },
                    // Critical: Disable chapter image extraction to avoid debrid blocking
                    EnableChapterImageExtraction = false,
                    ExtractChapterImagesDuringLibraryScan = false,
                    AutoGenerateChapters = false,

                    // Set metadata preferences from plugin configuration
                    PreferredMetadataLanguage = config.MetadataLanguage ?? "en",
                    PreferredImageLanguage = config.ImageLanguage ?? "en",
                    MetadataCountryCode = config.MetadataCountryCode ?? "US",

                    // Enable embedded titles
                    EnableEmbeddedTitles = true,
                };

                // Set runtime-only properties via reflection for best first-load experience
                var optsType = libraryOptions.GetType();
                try
                {
                    optsType.GetProperty("DownloadImagesInAdvance")?.SetValue(libraryOptions, true);
                }
                catch { /* non-critical */ }

                _libraryManager.AddVirtualFolder(name, libraryOptions, refreshLibrary: false);

                _logger.LogInformation(
                    "[InfiniteDrive] Created Emby library '{Name}' (type='{Type}') at {Path} with metadata language {Lang}",
                    name, string.IsNullOrEmpty(contentType) ? "mixed" : contentType, path,
                    config.MetadataLanguage ?? "en");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[InfiniteDrive] Failed to create library '{Name}'. " +
                    "Create it manually: Emby Dashboard → Libraries → Add Media Library → " +
                    "type '{Type}', path '{Path}'",
                    name, string.IsNullOrEmpty(contentType) ? "mixed" : contentType, path);
            }

            await Task.CompletedTask;
        }
    }
}
