using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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

            // Apply metadata fetchers via TypeOptions on newly created libraries
            await ApplyMetadataTypeOptionsAsync();

            return new ProvisionLibrariesResponse
            {
                Success = true,
                Message = "Libraries provisioned"
            };
        }

        /// <summary>
        /// Sets TypeOptions (metadata + image fetchers) on all InfiniteDrive libraries
        /// via the Emby REST API. TypeOptions is a runtime-only property not in the
        /// compile-time SDK, so we use the API directly.
        /// </summary>
        private async Task ApplyMetadataTypeOptionsAsync()
        {
            try
            {
                var folders = _libraryManager.GetVirtualFolders();
                var port = 8096;

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var baseUrl = $"http://localhost:{port}";

                foreach (var folder in folders)
                {
                    var name = folder.Name ?? "";
                    if (!name.StartsWith("Streamed ")) continue;

                    var contentType = folder.CollectionType ?? "";
                    var itemId = folder.ItemId;
                    var opts = folder.LibraryOptions;

                    // Build TypeOptions array
                    var typeOptions = new List<Dictionary<string, object>>();

                    if (contentType == "movies")
                    {
                        typeOptions.Add(MakeTypeOption("Movie",
                            new[] { "TheMovieDB", "TheOpenMovieDatabase" },
                            new[] { "TheMovieDB", "FanArt" }));
                    }
                    else if (contentType == "tvshows")
                    {
                        foreach (var t in new[] { "Series", "Season", "Episode" })
                            typeOptions.Add(MakeTypeOption(t,
                                new[] { "TheTVDB", "TheMovieDB" },
                                new[] { "TheTVDB", "TheMovieDB", "FanArt" }));
                    }
                    else
                    {
                        typeOptions.Add(MakeTypeOption("Movie",
                            new[] { "TheMovieDB", "TheOpenMovieDatabase" },
                            new[] { "TheMovieDB", "FanArt" }));
                        typeOptions.Add(MakeTypeOption("Series",
                            new[] { "TheTVDB", "TheMovieDB" },
                            new[] { "TheTVDB", "TheMovieDB", "FanArt" }));
                    }

                    // Set TypeOptions via reflection on the LibraryOptions object
                    var optsType = opts.GetType();
                    var prop = optsType.GetProperty("TypeOptions");
                    if (prop == null) continue;
                    prop.SetValue(opts, typeOptions);

                    // Persist via API
                    var payload = JsonSerializer.Serialize(new { Id = itemId, LibraryOptions = opts });
                    var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    var result = await client.PostAsync($"{baseUrl}/Library/VirtualFolders/LibraryOptions", content);
                    _logger.LogInformation("[InfiniteDrive] Applied metadata TypeOptions to '{Name}': {Status}", name, result.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Could not apply metadata TypeOptions — users may need to configure manually");
            }
        }

        private static Dictionary<string, object> MakeTypeOption(string type, string[] metaFetchers, string[] imgFetchers)
        {
            return new Dictionary<string, object>
            {
                ["Type"] = type,
                ["MetadataFetchers"] = metaFetchers,
                ["MetadataFetcherOrder"] = metaFetchers,
                ["ImageFetchers"] = imgFetchers,
                ["ImageFetcherOrder"] = imgFetchers,
                ["ImageOptions"] = new object[0]
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
