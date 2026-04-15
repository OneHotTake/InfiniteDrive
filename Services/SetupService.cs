using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Request to create library directories during initial setup.
    /// </summary>
    [Route("/InfiniteDrive/Setup/CreateDirectories", "POST",
        Summary = "Create library directories for movies and shows")]
    public class CreateDirectoriesRequest : IReturn<CreateDirectoriesResponse>
    {
        /// <summary>Full path to movies directory (e.g. /media/infinitedrive/movies)</summary>
        public string MoviesPath { get; set; } = string.Empty;

        /// <summary>Full path to shows directory (e.g. /media/infinitedrive/shows)</summary>
        public string ShowsPath { get; set; } = string.Empty;

        /// <summary>Full path to anime directory (e.g. /media/infinitedrive/anime)</summary>
        public string AnimePath { get; set; } = string.Empty;
    }

    /// <summary>Response from directory creation.</summary>
    public class CreateDirectoriesResponse
    {
        /// <summary>True if both directories were created successfully.</summary>
        public bool Success { get; set; }

        /// <summary>Human-readable status message.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Absolute path to movies directory.</summary>
        public string MoviesPath { get; set; } = string.Empty;

        /// <summary>Absolute path to shows directory.</summary>
        public string ShowsPath { get; set; } = string.Empty;

        /// <summary>Absolute path to anime directory.</summary>
        public string AnimePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request to rotate the playback API key and rewrite all .strm files.
    /// </summary>
    [Route("/InfiniteDrive/Setup/RotateApiKey", "POST",
        Summary = "Rotate playback API key and rewrite all .strm files")]
    public class RotateApiKeyRequest : IReturn<RotateApiKeyResponse> { }

    /// <summary>Response from API key rotation.</summary>
    public class RotateApiKeyResponse
    {
        /// <summary>True if rotation completed successfully.</summary>
        public bool Success { get; set; }

        /// <summary>Human-readable status message.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Number of .strm files rewritten.</summary>
        public int FilesRewritten { get; set; }

        /// <summary>Any errors encountered during rewrite.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Request to check rotation status during active key rotation.
    /// </summary>
    [Route("/InfiniteDrive/Setup/RotationStatus", "GET",
        Summary = "Get current rotation status")]
    public class RotationStatusRequest : IReturn<RotationStatusResponse> { }

    /// <summary>Response from rotation status check.</summary>
    public class RotationStatusResponse
    {
        /// <summary>True if rotation is currently in progress.</summary>
        public bool IsRotating { get; set; }

        /// <summary>Total number of .strm files to rewrite.</summary>
        public int FilesTotal { get; set; }

        /// <summary>Number of files rewritten so far.</summary>
        public int FilesWritten { get; set; }

        /// <summary>Unix timestamp of last rotation. 0 = never.</summary>
        public long LastRotatedAt { get; set; }
    }

    /// <summary>
    /// Request to create Emby virtual folder libraries.
    /// </summary>
    [Route("/InfiniteDrive/Setup/ProvisionLibraries", "POST",
        Summary = "Create Emby library entries for InfiniteDrive paths if they do not exist")]
    public class ProvisionLibrariesRequest : IReturn<ProvisionLibrariesResponse> { }

    /// <summary>Response from library provisioning.</summary>
    public class ProvisionLibrariesResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Service for setup operations (creating directories, rotating API keys, etc.).
    /// Called by the wizard during initial configuration and user maintenance.
    /// </summary>
    public class SetupService : IService, IRequiresRequest
    {
        private readonly ILogger<SetupService> _logger;
        private readonly ILogManager _logManager;
        private readonly IAuthorizationContext _authCtx;
        private readonly ILibraryManager _libraryManager;

        // Rotation state (in-memory only, used by RotationStatus endpoint)
        private static bool _rotationInProgress = false;
        private static int _rotationTotal = 0;
        private static int _rotationWritten = 0;

        public IRequest Request { get; set; } = null!;

        public SetupService(ILogManager logManager, IAuthorizationContext authCtx, ILibraryManager libraryManager)
        {
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<SetupService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Creates Emby library entries for all configured InfiniteDrive paths.
        /// Idempotent — safe to call on every wizard run.
        /// </summary>
        public async Task<object> Post(ProvisionLibrariesRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var service = new LibraryProvisioningService(
                _libraryManager,
                new EmbyLoggerAdapter<LibraryProvisioningService>(_logManager.GetLogger("InfiniteDrive")));

            await service.EnsureLibrariesProvisionedAsync();

            return new ProvisionLibrariesResponse
            {
                Success = true,
                Message = "Libraries provisioned"
            };
        }

        public object Post(CreateDirectoriesRequest request)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                // Validate paths against traversal attacks
                if (!IsPathSafe(request.MoviesPath) || !IsPathSafe(request.ShowsPath) || !IsPathSafe(request.AnimePath))
                {
                    _logger.LogError("[Setup] Invalid path detected — possible path traversal attempt");
                    return new CreateDirectoriesResponse
                    {
                        Success = false,
                        Message = "Invalid directory path"
                    };
                }

                // Create movies directory
                if (!string.IsNullOrWhiteSpace(request.MoviesPath))
                {
                    Directory.CreateDirectory(request.MoviesPath);
                }

                // Create shows directory
                if (!string.IsNullOrWhiteSpace(request.ShowsPath))
                {
                    Directory.CreateDirectory(request.ShowsPath);
                }

                // Create anime directory
                if (!string.IsNullOrWhiteSpace(request.AnimePath))
                {
                    Directory.CreateDirectory(request.AnimePath);
                }

                return new CreateDirectoriesResponse
                {
                    Success = true,
                    Message = "Directories created successfully",
                    MoviesPath = request.MoviesPath,
                    ShowsPath = request.ShowsPath,
                    AnimePath = request.AnimePath
                };
            }
            catch (Exception ex)
            {
                return new CreateDirectoriesResponse
                {
                    Success = false,
                    Message = $"Failed to create directories: {ex.Message}",
                    MoviesPath = request.MoviesPath,
                    ShowsPath = request.ShowsPath,
                    AnimePath = request.AnimePath
                };
            }
        }

        /// <summary>
        /// Rotates the playback signing secret (PluginSecret) and rewrites all .strm files.
        /// Two-phase flow: write all files first, then save config only if all succeed.
        /// </summary>
        public async Task<object> Post(RotateApiKeyRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return new RotateApiKeyResponse
                {
                    Success = false,
                    Message = "Plugin configuration not available"
                };
            }

            if (string.IsNullOrEmpty(config.PluginSecret))
            {
                return new RotateApiKeyResponse
                {
                    Success = false,
                    Message = "PluginSecret is not set. Generate a secret in Settings first."
                };
            }

            // Prevent concurrent rotations
            if (_rotationInProgress)
            {
                return new RotateApiKeyResponse
                {
                    Success = false,
                    Message = "Rotation already in progress"
                };
            }

            try
            {
                _logger.LogInformation("[InfiniteDrive] Starting PluginSecret rotation");

                // Phase 1: Generate new secret (do NOT save yet)
                var newSecret = PlaybackTokenService.GenerateSecret();
                var oldSecret = config.PluginSecret;

                // Count total files for progress tracking
                int totalFiles = 0;
                totalFiles += CountStrmFiles(config.SyncPathMovies);
                totalFiles += CountStrmFiles(config.SyncPathShows);

                _logger.LogInformation("[InfiniteDrive] Found {TotalFiles} .strm files to rewrite", totalFiles);

                // Set rotation state for UI polling
                _rotationInProgress = true;
                _rotationTotal = totalFiles;
                _rotationWritten = 0;

                var validityDays = config.SignatureValidityDays > 0 ? config.SignatureValidityDays : 365;

                // Phase 2: Rewrite all .strm files with new secret (do not save config yet)
                int filesRewritten = 0;

                // Movies
                if (!string.IsNullOrWhiteSpace(config.SyncPathMovies) && Directory.Exists(config.SyncPathMovies))
                {
                    filesRewritten += await RewriteStrmFilesInDirectory(config.SyncPathMovies, config.EmbyBaseUrl, newSecret, validityDays);
                }

                // Shows
                if (!string.IsNullOrWhiteSpace(config.SyncPathShows) && Directory.Exists(config.SyncPathShows))
                {
                    filesRewritten += await RewriteStrmFilesInDirectory(config.SyncPathShows, config.EmbyBaseUrl, newSecret, validityDays);
                }

                // Phase 3: Only save config after all files written successfully
                config.PluginSecret = newSecret;
                config.PluginSecretRotatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Plugin.Instance?.SaveConfiguration();

                _logger.LogInformation(
                    "[InfiniteDrive] PluginSecret rotation complete: {FilesRewritten} .strm files rewritten",
                    filesRewritten);

                return new RotateApiKeyResponse
                {
                    Success = true,
                    Message = $"Signing secret rotated successfully. Rewrote {filesRewritten} .strm files.",
                    FilesRewritten = filesRewritten
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] PluginSecret rotation failed");
                // Config NOT saved on failure - old secret remains active
                return new RotateApiKeyResponse
                {
                    Success = false,
                    Message = "Secret rotation failed",
                    Error = ex.Message
                };
            }
            finally
            {
                _rotationInProgress = false;
                _rotationTotal = 0;
                _rotationWritten = 0;
            }
        }

        private static int CountStrmFiles(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return 0;
            return Directory.GetFiles(path, "*.strm", SearchOption.AllDirectories).Length;
        }

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Setup/RotationStatus</c>.
        /// Returns the current rotation state for UI polling.
        /// </summary>
        public object Get(RotationStatusRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var cfg = Plugin.Instance?.Configuration;
            return new RotationStatusResponse
            {
                IsRotating    = _rotationInProgress,
                FilesTotal    = _rotationTotal,
                FilesWritten  = _rotationWritten,
                LastRotatedAt = cfg?.PluginSecretRotatedAt ?? 0
            };
        }

        /// <summary>
        /// Recursively rewrite all .strm files in a directory with the new API key.
        /// </summary>
        private async Task<int> RewriteStrmFilesInDirectory(string directoryPath, string embyBaseUrl, string secret, int validityDays)
        {
            int count = 0;
            var baseUrl = embyBaseUrl?.TrimEnd('/') ?? "http://localhost:8096";

            try
            {
                var strmFiles = Directory.GetFiles(directoryPath, "*.strm", SearchOption.AllDirectories);

                foreach (var strmFile in strmFiles)
                {
                    try
                    {
                        // Read the .strm file to extract the IMDB ID
                        var content = await File.ReadAllTextAsync(strmFile);

                        // Extract IMDB ID from URL (e.g., "http://...?imdb=tt123" or "/InfiniteDrive/Stream?id=tt123")
                        var imdbMatch = System.Text.RegularExpressions.Regex.Match(content, @"[?&]imdb=([^&\s]+)|id=([^&\s]+)");
                        if (!imdbMatch.Success)
                            continue;

                        var imdbId = imdbMatch.Groups[1].Success ? imdbMatch.Groups[1].Value : imdbMatch.Groups[2].Value;

                        // Extract season/episode if present
                        var seasonMatch = System.Text.RegularExpressions.Regex.Match(content, @"[?&]season=(\d+)");
                        var episodeMatch = System.Text.RegularExpressions.Regex.Match(content, @"[?&]episode=(\d+)");

                        // Determine media type from path or content
                        var mediaType = strmFile.Contains("/shows/") || strmFile.Contains("/anime/")
                            ? "series" : "movie";

                        // Re-sign with new validity period using PlaybackTokenService
                        var newUrl = PlaybackTokenService.GenerateSignedUrl(
                            baseUrl,
                            imdbId,
                            mediaType,
                            seasonMatch.Success ? int.Parse(seasonMatch.Groups[1].Value) : (int?)null,
                            episodeMatch.Success ? int.Parse(episodeMatch.Groups[1].Value) : (int?)null,
                            secret,
                            TimeSpan.FromDays(validityDays));

                        // Write new content
                        await File.WriteAllTextAsync(strmFile, newUrl);
                        count++;
                        Interlocked.Increment(ref _rotationWritten);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[InfiniteDrive] Failed to rewrite .strm file: {Path}", strmFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Error rewriting .strm files in {Directory}", directoryPath);
            }

            return count;
        }

        private static int? ParsePort(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            try
            {
                var uri = new Uri(url);
                return uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPathSafe(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true; // empty paths are skipped, not unsafe

            if (path.Contains(".."))
                return false;

            try
            {
                var normalized = Path.GetFullPath(path);
                // Reject if GetFullPath changes the path significantly (indicates traversal)
                if (!normalized.StartsWith(path.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
                    && !path.TrimEnd('/', '\\').StartsWith(normalized.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }
    }
}
