using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
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
        /// <summary>Full path to movies directory (e.g. /media/embystreams/movies)</summary>
        public string MoviesPath { get; set; } = string.Empty;

        /// <summary>Full path to shows directory (e.g. /media/embystreams/shows)</summary>
        public string ShowsPath { get; set; } = string.Empty;

        /// <summary>Full path to anime directory (e.g. /media/embystreams/anime)</summary>
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
    /// Service for setup operations (creating directories, rotating API keys, etc.).
    /// Called by the wizard during initial configuration and user maintenance.
    /// </summary>
    public class SetupService : IService, IRequiresRequest
    {
        private readonly ILogger<SetupService> _logger;
        private readonly IAuthorizationContext _authCtx;

        public IRequest Request { get; set; } = null!;

        public SetupService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger = new EmbyLoggerAdapter<SetupService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        public object Post(CreateDirectoriesRequest request)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
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
        /// Rotates the playback API key and rewrites all .strm files with the new key.
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

            try
            {
                _logger.LogInformation("[InfiniteDrive] Starting API key rotation");

                // Generate new API key (simple GUID-based key)
                var newApiKey = Guid.NewGuid().ToString("N").Substring(0, 32);
                var oldApiKey = config.EmbyApiKey;

                // Update configuration
                config.EmbyApiKey = newApiKey;
                Plugin.Instance?.SaveConfiguration();

                _logger.LogInformation("[InfiniteDrive] New API key generated, rewriting .strm files");

                var secret = config.PluginSecret;
                var validityDays = config.SignatureValidityDays > 0 ? config.SignatureValidityDays : 365;

                // Rewrite all .strm files with signed URLs
                int filesRewritten = 0;

                // Movies
                if (!string.IsNullOrWhiteSpace(config.SyncPathMovies) && Directory.Exists(config.SyncPathMovies))
                {
                    filesRewritten += await RewriteStrmFilesInDirectory(config.SyncPathMovies, config.EmbyBaseUrl, secret, validityDays);
                }

                // Shows
                if (!string.IsNullOrWhiteSpace(config.SyncPathShows) && Directory.Exists(config.SyncPathShows))
                {
                    filesRewritten += await RewriteStrmFilesInDirectory(config.SyncPathShows, config.EmbyBaseUrl, secret, validityDays);
                }

                _logger.LogInformation(
                    "[InfiniteDrive] API key rotation complete: {FilesRewritten} .strm files rewritten",
                    filesRewritten);

                return new RotateApiKeyResponse
                {
                    Success = true,
                    Message = $"API key rotated successfully. Rewrote {filesRewritten} .strm files.",
                    FilesRewritten = filesRewritten
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] API key rotation failed");
                return new RotateApiKeyResponse
                {
                    Success = false,
                    Message = "API key rotation failed",
                    Error = ex.Message
                };
            }
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
    }
}
