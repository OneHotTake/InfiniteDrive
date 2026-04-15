using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Users;

namespace InfiniteDrive.Services
{
    // ── Request/Response DTOs ────────────────────────────────────────────────────

    /// <summary>
    /// Request object for testing on-demand stream resolution.
    /// </summary>
    [Route("/InfiniteDrive/Discover/TestStreamResolution", "GET",
        Summary = "Test on-demand stream resolution for an item")]
    public class DiscoverTestStreamResolutionRequest : IReturn<DiscoverTestStreamResolutionResponse>
    {
        /// <summary>IMDb ID of the item to resolve (required).</summary>
        [ApiMember(Name = "imdb", Description = "IMDb ID (e.g., tt0133093)", DataType = "string", ParameterType = "query")]
        public string ImdbId { get; set; } = "";

        /// <summary>Season number (for TV series).</summary>
        [ApiMember(Name = "season", Description = "Season number", DataType = "int", ParameterType = "query")]
        public int? Season { get; set; }

        /// <summary>Episode number (for TV series).</summary>
        [ApiMember(Name = "episode", Description = "Episode number", DataType = "int", ParameterType = "query")]
        public int? Episode { get; set; }
    }


    /// <summary>
    /// Response from <c>GET /InfiniteDrive/Discover/TestStreamResolution</c>.
    /// </summary>
    public class DiscoverTestStreamResolutionResponse
    {
        /// <summary>Whether stream resolution was successful.</summary>
        public bool Success { get; set; }

        /// <summary>Error message if resolution failed.</summary>
        public string? Error { get; set; }

        /// <summary>Proxy session token for streaming.</summary>
        public string? ProxyToken { get; set; }

        /// <summary>Direct stream URL using the proxy token.</summary>
        public string? StreamUrl { get; set; }

        /// <summary>Emby web URL for the item (for reference).</summary>
        public string? EmbyUrl { get; set; }
    }

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Discover/Browse</c>.
    /// Returns paginated discover catalog entries.
    /// </summary>
    [Route("/InfiniteDrive/Discover/Browse", "GET",
        Summary = "Browse available items in the Discover catalog")]
    public class DiscoverBrowseRequest : IReturn<DiscoverBrowseResponse>
    {
        /// <summary>Maximum number of items to return (default 50, max 200).</summary>
        [ApiMember(Name = "limit", Description = "Page size", DataType = "int", ParameterType = "query")]
        public int Limit { get; set; } = 50;

        /// <summary>Number of items to skip (for pagination).</summary>
        [ApiMember(Name = "offset", Description = "Skip N items", DataType = "int", ParameterType = "query")]
        public int Offset { get; set; } = 0;
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Discover/Browse</c>.</summary>
    public class DiscoverBrowseResponse
    {
        /// <summary>Items in this page.</summary>
        public List<DiscoverItem> Items { get; set; } = new();

        /// <summary>Total number of items available (for pagination).</summary>
        public int Total { get; set; }

        /// <summary>Current page offset.</summary>
        public int Offset { get; set; }
    }

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Discover/Search</c>.
    /// Searches local catalog and optionally AIOStreams live.
    /// </summary>
    [Route("/InfiniteDrive/Discover/Search", "GET",
        Summary = "Search available items in Discover catalog")]
    public class DiscoverSearchRequest : IReturn<DiscoverSearchResponse>
    {
        /// <summary>Search query (use ?q= or ?Query= parameter).</summary>
        [ApiMember(Name = "q", Description = "Search query", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Query { get; set; } = string.Empty;

        /// <summary>Alias for Query to support ?q= parameter binding.</summary>
        public string Q { get => Query; set => Query = value; }

        /// <summary>Filter by media type: <c>movie</c>, <c>series</c>, or omit for all.</summary>
        [ApiMember(Name = "type", Description = "Media type filter", DataType = "string", ParameterType = "query")]
        public string? Type { get; set; }

        /// <summary>Force live AIOStreams search even if local results are good (default false).</summary>
        [ApiMember(Name = "live", Description = "Force live search", DataType = "bool", ParameterType = "query")]
        public bool Live { get; set; } = false;
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Discover/Search</c>.</summary>
    public class DiscoverSearchResponse
    {
        /// <summary>Merged and deduplicated search results.</summary>
        public List<DiscoverItem> Items { get; set; } = new();
    }

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Discover/Detail</c>.
    /// Fetches detailed metadata for a specific item.
    /// </summary>
    [Route("/InfiniteDrive/Discover/Detail", "GET",
        Summary = "Get detailed metadata for a Discover item")]
    public class DiscoverDetailRequest : IReturn<DiscoverDetailResponse>
    {
        /// <summary>IMDB ID, e.g. <c>tt1160419</c>.</summary>
        [ApiMember(Name = "imdbId", Description = "IMDB ID", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string ImdbId { get; set; } = string.Empty;
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Discover/Detail</c>.</summary>
    public class DiscoverDetailResponse
    {
        /// <summary>The detailed item.</summary>
        public DiscoverItem? Item { get; set; }
    }

    /// <summary>
    /// Request object for <c>POST /InfiniteDrive/Discover/AddToLibrary</c>.
    /// Creates a .strm file and adds item to the library.
    /// </summary>
    [Route("/InfiniteDrive/Discover/AddToLibrary", "POST",
        Summary = "Add a Discover item to the user's library")]
    public class DiscoverAddToLibraryRequest : IReturn<DiscoverAddToLibraryResponse>
    {
        /// <summary>IMDB ID of the item to add.</summary>
        [ApiMember(Name = "imdbId", Description = "IMDB ID", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Media type: <c>movie</c> or <c>series</c>.</summary>
        [ApiMember(Name = "type", Description = "Media type", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Type { get; set; } = string.Empty;

        /// <summary>Display title (used for .strm filename).</summary>
        [ApiMember(Name = "title", Description = "Display title", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Title { get; set; } = string.Empty;

        /// <summary>Release year (optional, used for .strm filename).</summary>
        [ApiMember(Name = "year", Description = "Release year", DataType = "int", ParameterType = "query")]
        public int? Year { get; set; }
    }

    /// <summary>Response from <c>POST /InfiniteDrive/Discover/AddToLibrary</c>.</summary>
    public class DiscoverAddToLibraryResponse
    {
        /// <summary><c>true</c> if the item was added successfully.</summary>
        public bool Ok { get; set; }

        /// <summary>Path to the created .strm file.</summary>
        public string? StrmPath { get; set; }

        /// <summary>Error message if <c>Ok</c> is <c>false</c>.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Request object for <c>POST /InfiniteDrive/Discover/RemoveFromLibrary</c>.
    /// Removes item from current user's saved library.
    /// </summary>
    [Route("/InfiniteDrive/Discover/RemoveFromLibrary", "POST",
        Summary = "Remove item from current user's saved library")]
    public class DiscoverRemoveFromLibraryRequest : IReturn<DiscoverRemoveFromLibraryResponse>
    {
        /// <summary>IMDB ID of the item to remove.</summary>
        [ApiMember(Name = "imdbId", Description = "IMDB ID", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string ImdbId { get; set; } = string.Empty;
    }

    /// <summary>Response from <c>POST /InfiniteDrive/Discover/RemoveFromLibrary</c>.</summary>
    public class DiscoverRemoveFromLibraryResponse
    {
        /// <summary><c>true</c> if the item was removed successfully.</summary>
        public bool Ok { get; set; }

        /// <summary>Error message if <c>Ok</c> is <c>false</c>.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// A single item in the Discover catalog (used in browse/search responses).
    /// Combines metadata from discover_catalog and library status.
    /// </summary>
    public class DiscoverItem
    {
        /// <summary>IMDB ID.</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Display title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Release year or first-air year.</summary>
        public int? Year { get; set; }

        /// <summary><c>movie</c> or <c>series</c>.</summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>Poster image URL.</summary>
        public string? PosterUrl { get; set; }

        /// <summary>Backdrop image URL.</summary>
        public string? BackdropUrl { get; set; }

        /// <summary>Overview/synopsis text.</summary>
        public string? Overview { get; set; }

        /// <summary>Comma-separated genre names.</summary>
        public string? Genres { get; set; }

        /// <summary>IMDb rating (0-10).</summary>
        public double? ImdbRating { get; set; }

        /// <summary>MPAA/TV rating (e.g., "PG-13", "R", "TV-MA").</summary>
        public string? Certification { get; set; }

        /// <summary><c>true</c> if already in user's Emby library.</summary>
        public bool InLibrary { get; set; }

        /// <summary>Emby internal item ID (for navigation to detail page).</summary>
        public string? EmbyItemId { get; set; }

        /// <summary>Source catalog (for tracking).</summary>
        public string CatalogSource { get; set; } = string.Empty;
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles Discover browsing and search requests.
    /// Provides REST API for the Discover channel UI.
    /// </summary>
    public class DiscoverService : IService, IRequiresRequest
    {
        private readonly ILogger<DiscoverService> _logger;
        private readonly DatabaseManager _db;
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authCtx;
        private readonly StrmWriterService _strmWriter;
        private readonly IUserManager _userManager;

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Emby injects dependencies automatically.
        /// </summary>
        public DiscoverService(ILogManager logManager, ILibraryManager libraryManager, IAuthorizationContext authCtx, IUserManager userManager)
        {
            _logger = new EmbyLoggerAdapter<DiscoverService>(logManager.GetLogger("InfiniteDrive"));
            _db = Plugin.Instance.DatabaseManager;
            _libraryManager = libraryManager;
            _authCtx = authCtx;
            _strmWriter = Plugin.Instance.StrmWriterService;
            _userManager = userManager;
        }

        // ── Handlers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Discover/Browse</c>.
        /// Returns paginated catalog entries from the local discover_catalog table.
        /// </summary>
        public async Task<object> Get(DiscoverBrowseRequest req)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                var limit = Math.Min(Math.Max(req.Limit, 1), 200);
                var offset = Math.Max(req.Offset, 0);

                var entries = await _db.GetDiscoverCatalogAsync(limit, offset);
                var maxRating = GetUserMaxParentalRating();
                var filtered = ApplyParentalFilter(entries, maxRating);
                var items = filtered.Select(e => MapToDiscoverItem(e, null)).ToList();
                var total = await _db.GetDiscoverCatalogCountAsync();

                return new DiscoverBrowseResponse
                {
                    Items = items,
                    Total = total,
                    Offset = offset
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error browsing Discover catalog");
                throw;
            }
        }

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Discover/Search</c>.
        /// Searches local FTS5 index first; if results are thin or live=true, fans out to AIOStreams.
        /// </summary>
        public async Task<object> Get(DiscoverSearchRequest req)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                if (string.IsNullOrWhiteSpace(req.Query))
                {
                    return new DiscoverSearchResponse { Items = new() };
                }

                // Step 1: Search local FTS5 index
                var localEntries = await _db.SearchDiscoverCatalogAsync(req.Query, req.Type);
                var maxRating = GetUserMaxParentalRating();
                var filtered = ApplyParentalFilter(localEntries, maxRating);
                var localItems = filtered.Select(e => MapToDiscoverItem(e, null)).ToList();

                // Step 2: Determine if we should do a live AIOStreams search
                var shouldLiveSearch = req.Live || localItems.Count < 5;
                var config = Plugin.Instance?.Configuration;
                var liveItems = new List<DiscoverItem>();

                if (shouldLiveSearch && config != null &&
                    (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl) || !string.IsNullOrWhiteSpace(config.SecondaryManifestUrl)))
                {
                    try
                    {
                        liveItems = await GetLiveSearchResultsAsync(req.Query, req.Type);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Live search failed, returning local results only");
                    }
                }

                // Step 3: Merge and deduplicate (local results take priority)
                var mergedByImdbId = new Dictionary<string, DiscoverItem>(StringComparer.OrdinalIgnoreCase);

                // Add live results first
                foreach (var item in liveItems)
                {
                    if (!mergedByImdbId.ContainsKey(item.ImdbId))
                    {
                        mergedByImdbId[item.ImdbId] = item;
                    }
                }

                // Override with local results (they're more up-to-date)
                foreach (var item in localItems)
                    mergedByImdbId[item.ImdbId] = item;

                var merged = mergedByImdbId.Values.OrderBy(x => x.InLibrary ? 0 : 1).ToList();

                return new DiscoverSearchResponse { Items = merged.Take(50).ToList() };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching Discover catalog");
                throw;
            }
        }

        /// <summary>
        /// Queries AIOStreams live for search results across all search-capable catalogs.
        /// Caches results back to the database and returns as DiscoverItems.
        /// </summary>
        private async Task<List<DiscoverItem>> GetLiveSearchResultsAsync(string query, string? mediaType)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return new();

            using (var client = new AioStreamsClient(config, _logger))
            {
                client.Cooldown = Plugin.Instance?.CooldownGate;
                try
                {
                    // Fetch manifest to identify search-capable catalogs
                    var manifest = await client.GetManifestAsync(CancellationToken.None);
                    if (manifest?.Catalogs == null || manifest.Catalogs.Count == 0)
                        return new();

                    // Filter to search-capable catalogs
                    var searchableCatalogs = manifest.Catalogs
                        .Where(c => c.Extra != null && c.Extra.Any(e => e.Name == "search"))
                        .ToList();

                    if (searchableCatalogs.Count == 0)
                        return new();

                    // Filter to applicable catalogs (by media type if specified)
                    var applicableCatalogs = searchableCatalogs
                        .Where(c => mediaType == null || c.Type!.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Fan out to all applicable catalogs in parallel (10s per-catalog timeout)
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var catalogTasks = applicableCatalogs.Select(async catalogDef =>
                    {
                        try
                        {
                            var response = await client.GetCatalogAsync(
                                catalogDef.Type!,
                                catalogDef.Id!,
                                searchQuery: query,
                                genre: null,
                                skip: null,
                                cancellationToken: cts.Token);
                            return (catalogDef, response);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Live search timeout/fail for catalog {Catalog}", catalogDef.Id);
                            return (catalogDef, null);
                        }
                    }).ToList();

                    var catalogResults = await Task.WhenAll(catalogTasks);

                    var liveResults = new Dictionary<string, (DiscoverItem Item, string CatalogSource)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (catalogDef, response) in catalogResults)
                    {
                        if (response?.Metas == null) continue;
                        foreach (var meta in response.Metas)
                        {
                            if (string.IsNullOrWhiteSpace(meta.Id) && string.IsNullOrWhiteSpace(meta.ImdbId))
                                continue;

                            var imdbId = meta.ImdbId ?? meta.Id ?? "";
                            if (string.IsNullOrWhiteSpace(imdbId))
                                continue;

                            if (!liveResults.ContainsKey(imdbId))
                            {
                                var item = new DiscoverItem
                                {
                                    ImdbId = imdbId,
                                    Title = meta.Name ?? "",
                                    Year = ParseYear(meta.ReleaseInfo),
                                    MediaType = meta.Type ?? catalogDef.Type ?? "movie",
                                    PosterUrl = meta.Poster,
                                    BackdropUrl = meta.Background,
                                    Overview = meta.Description,
                                    Genres = meta.Genres != null && meta.Genres.Count > 0 ? string.Join(", ", meta.Genres) : null,
                                    ImdbRating = string.IsNullOrEmpty(meta.ImdbRating) || !double.TryParse(meta.ImdbRating, out var r) ? null : r,
                                    InLibrary = false,
                                    CatalogSource = $"search:{query}"
                                };
                                liveResults[imdbId] = (item, catalogDef.Id!);
                                _ = CacheLiveSearchResultAsync(imdbId, meta, catalogDef);
                            }
                        }
                    }

                    return liveResults.Values.Select(x => x.Item).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch live search results");
                    return new();
                }
            }
        }

        /// <summary>
        /// Caches a live search result to the database for future queries.
        /// </summary>
        private async Task CacheLiveSearchResultAsync(string imdbId, AioStreamsMeta meta, AioStreamsCatalogDef catalogDef)
        {
            try
            {
                var entry = new DiscoverCatalogEntry
                {
                    Id = $"aio:{catalogDef.Type}:{imdbId}",
                    ImdbId = imdbId,
                    Title = meta.Name ?? "",
                    Year = ParseYear(meta.ReleaseInfo),
                    MediaType = meta.Type ?? catalogDef.Type ?? "movie",
                    PosterUrl = meta.Poster,
                    BackdropUrl = meta.Background,
                    Overview = meta.Description,
                    Genres = meta.Genres != null && meta.Genres.Count > 0 ? string.Join(", ", meta.Genres) : null,
                    ImdbRating = string.IsNullOrEmpty(meta.ImdbRating) || !double.TryParse(meta.ImdbRating, out var r) ? null : r,
                    CatalogSource = catalogDef.Id!,
                    AddedAt = DateTime.UtcNow.ToString("o"),
                    IsInUserLibrary = false
                };
                await _db.UpsertDiscoverCatalogEntryAsync(entry);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cache live search result for {ImdbId}", imdbId);
            }
        }

        /// <summary>
        /// Parses year from ReleaseInfo string (format: "2022" or "2022–").
        /// </summary>
        private static int? ParseYear(string? releaseInfo)
        {
            if (string.IsNullOrEmpty(releaseInfo))
                return null;
            var yearStr = releaseInfo.Split('–', '–')[0].Trim();
            return int.TryParse(yearStr, out var y) ? y : null;
        }

        /// <summary>
        /// Debug endpoint to test catalog queries
        /// </summary>
        [Route("/InfiniteDrive/Debug/TestCatalog", "GET", Summary = "Debug catalog query")]
        public object TestCatalog()
        {
            try
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null)
                    return new { error = "Database not available" };

                var movies = db.GetDiscoverCatalogMovies(0, 3);
                var tvShows = db.GetDiscoverCatalogTvShows(0, 3);

                var sampleMovies = movies.Take(3).ToList();
                _logger.LogInformation("[Debug] Found {Count} movies", sampleMovies.Count);

                foreach (var movie in sampleMovies)
                {
                    var name = movie.GetType().GetProperty("Name")?.GetValue(movie);
                    var id = movie.GetType().GetProperty("Id")?.GetValue(movie);
                    var type = movie.GetType().GetProperty("Type")?.GetValue(movie);
                    _logger.LogInformation("[Debug] Movie: {Name} ({Id}) Type: {Type}", name, id, type);
                }

                return new
                {
                    movieCount = movies.Count,
                    tvShowCount = tvShows.Count,
                    sampleMovies = sampleMovies.Select(m => new
                    {
                        name = m.GetType().GetProperty("Name")?.GetValue(m),
                        id = m.GetType().GetProperty("Id")?.GetValue(m),
                        type = m.GetType().GetProperty("Type")?.GetValue(m),
                        imageUrl = m.GetType().GetProperty("ImageUrl")?.GetValue(m)
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TestCatalog");
                return new { error = "An internal error occurred. Check server logs." };
            }
        }

        /// <summary>
        /// Debug endpoint to test what the channel returns
        /// </summary>
        [Route("/InfiniteDrive/Debug/TestChannel", "GET", Summary = "Test channel item retrieval")]
        public object TestChannel()
        {
            try
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null)
                    return new { error = "Database not available" };

                // Test GetDiscoverCatalogMovies
                var movies = db.GetDiscoverCatalogMovies(0, 3);
                _logger.LogInformation("[Debug] Found {Count} movies from database", movies.Count);

                var result = new List<object>();
                foreach (var movie in movies)
                {
                    var name = movie.GetType().GetProperty("Name")?.GetValue(movie);
                    var id = movie.GetType().GetProperty("Id")?.GetValue(movie);
                    var type = movie.GetType().GetProperty("Type")?.GetValue(movie);

                    result.Add(new {
                        name = name ?? "No name",
                        id = id ?? "No id",
                        type = type ?? "No type"
                    });
                }

                return new
                {
                    success = true,
                    movieCount = movies.Count,
                    sampleMovies = result
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TestChannel");
                return new { error = "An internal error occurred. Check server logs." };
            }
        }

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Discover/Detail</c>.
        /// Returns detailed metadata for a single item.
        /// </summary>
        public async Task<object> Get(DiscoverDetailRequest req)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                if (string.IsNullOrWhiteSpace(req.ImdbId))
                {
                    return new DiscoverDetailResponse { Item = null };
                }

                var entry = await _db.GetDiscoverCatalogEntryByImdbIdAsync(req.ImdbId);
                if (entry == null)
                {
                    return new DiscoverDetailResponse { Item = null };
                }

                // Apply parental filter
                var maxRating = GetUserMaxParentalRating();
                var filtered = ApplyParentalFilter(new List<DiscoverCatalogEntry> { entry }, maxRating);
                var item = filtered.Count > 0 ? MapToDiscoverItem(filtered[0], null) : null;

                return new DiscoverDetailResponse { Item = item };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Discover detail");
                throw;
            }
        }

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/Discover/AddToLibrary</c>.
        /// Creates a .strm file in the appropriate library folder and creates a catalog_item entry.
        /// </summary>
        public async Task<object> Post(DiscoverAddToLibraryRequest req, CancellationToken ct)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            // Ensure PluginSecret is initialized before accessing Configuration
            Plugin.Instance?.EnsureInitialization();

            try
            {
                // Validate inputs
                if (string.IsNullOrWhiteSpace(req.ImdbId) ||
                    string.IsNullOrWhiteSpace(req.Type) ||
                    string.IsNullOrWhiteSpace(req.Title))
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = false,
                        Error = "ImdbId, Type, and Title are required"
                    };
                }

                // Check if already in library
                var existing = await _db.GetCatalogItemByImdbIdAsync(req.ImdbId);
                if (existing != null)
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = true,
                        StrmPath = existing.StrmPath,
                        Error = "Item is already in library"
                    };
                }

                // Determine target directory based on media type
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = false,
                        Error = "Plugin configuration not available"
                    };
                }

                var targetDir = req.Type.ToLowerInvariant() switch
                {
                    "movie" => config.SyncPathMovies,
                    "series" => config.SyncPathShows,
                    "anime" => config.EnableAnimeLibrary ? config.SyncPathAnime : null,
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = false,
                        Error = $"Library path not configured or does not exist: {targetDir}"
                    };
                }

                // Get current user ID for attribution
                var callerUserId = TryGetCurrentUserId();

                // Create catalog_item entry with PINNED state
                var now = DateTime.UtcNow.ToString("o");
                var catalogItem = new CatalogItem
                {
                    Id = Guid.NewGuid().ToString(),
                    ImdbId = req.ImdbId,
                    Title = req.Title,
                    Year = req.Year,
                    MediaType = req.Type.ToLowerInvariant(),
                    Source = "discover",
                    ItemState = ItemState.Pinned,
                    PinSource = $"user:discover:{now}",
                    PinnedAt = now
                };

                // Write .strm and .nfo files via StrmWriterService (Sprint 156)
                var strmPath = await _strmWriter.WriteAsync(
                    catalogItem,
                    SourceType.Aio,
                    callerUserId,
                    ct);

                if (strmPath == null)
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = false,
                        Error = "Failed to write .strm file - check sync paths in configuration"
                    };
                }

                // Update catalog_item with strmPath and local source
                catalogItem.StrmPath = strmPath;
                catalogItem.LocalPath = strmPath;
                catalogItem.LocalSource = "strm";
                await _db.UpsertCatalogItemAsync(catalogItem);

                // Update discover_catalog to mark as in library
                await _db.UpdateDiscoverCatalogLibraryStatusAsync(req.ImdbId, true);

                // Per-user save: resolve IMDB ID → media_item, then save for calling user
                if (!string.IsNullOrEmpty(callerUserId))
                {
                    var mediaItem = await _db.FindMediaItemByProviderIdAsync("imdb", req.ImdbId, ct);
                    if (mediaItem != null)
                    {
                        await _db.UpsertUserSaveAsync(callerUserId, mediaItem.Id, "explicit", null, ct);
                        await _db.SyncGlobalSavedFlagAsync(mediaItem.Id, ct);
                        _logger.LogDebug("Per-user save: user {UserId} saved media item {ItemId}", callerUserId, mediaItem.Id);
                    }
                    else
                    {
                        _logger.LogDebug("Media item not yet indexed for IMDB {ImdbId}, per-user save deferred to SyncTask", req.ImdbId);
                    }
                }

                _logger.LogInformation("Added {ImdbId} to library", req.ImdbId);

                // Auto-trigger library refresh in background (fire-and-forget)
                // This ensures that new .strm file is indexed automatically without user action
                TriggerLibraryRefreshAsync(targetDir);

                return new DiscoverAddToLibraryResponse
                {
                    Ok = true,
                    StrmPath = strmPath
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding item to library: {ImdbId}", req.ImdbId);
                return new DiscoverAddToLibraryResponse
                {
                    Ok = false,
                    Error = "An internal error occurred. Check server logs."
                };
            }
        }

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/Discover/RemoveFromLibrary</c>.
        /// Removes item from current user's saved library.
        /// </summary>
        public async Task<object> Post(DiscoverRemoveFromLibraryRequest req, CancellationToken ct)
        {
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                if (string.IsNullOrWhiteSpace(req.ImdbId))
                {
                    return new DiscoverRemoveFromLibraryResponse
                    {
                        Ok = false,
                        Error = "ImdbId is required"
                    };
                }

                var callerUserId = TryGetCurrentUserId();
                if (string.IsNullOrEmpty(callerUserId))
                {
                    return new DiscoverRemoveFromLibraryResponse
                    {
                        Ok = false,
                        Error = "Unable to identify user"
                    };
                }

                var mediaItem = await _db.FindMediaItemByProviderIdAsync("imdb", req.ImdbId, ct);
                if (mediaItem == null)
                {
                    return new DiscoverRemoveFromLibraryResponse
                    {
                        Ok = false,
                        Error = "Item not found in library"
                    };
                }

                await _db.DeleteUserSaveAsync(callerUserId, mediaItem.Id, ct);
                await _db.SyncGlobalSavedFlagAsync(mediaItem.Id, ct);

                _logger.LogInformation("Removed {ImdbId} from library for user {UserId}", req.ImdbId, callerUserId);

                return new DiscoverRemoveFromLibraryResponse { Ok = true };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing item from library: {ImdbId}", req.ImdbId);
                return new DiscoverRemoveFromLibraryResponse
                {
                    Ok = false,
                    Error = "An internal error occurred. Check server logs."
                };
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Triggers a library refresh for the folder where .strm files are created.
        /// This runs in the background (fire-and-forget) to ensure items are indexed automatically.
        /// </summary>
        private void TriggerLibraryRefreshAsync(string folderPath)
        {
            try
            {
                // Fire-and-forget library refresh
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Give Emby a brief moment to process the file write
                        await Task.Delay(100);

                        _logger.LogInformation("Triggering library refresh for {Path}", folderPath);

                        // Request Emby to rescan the library
                        // This will detect the new .strm file and index it automatically
                        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None);

                        _logger.LogInformation("Library refresh triggered");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error triggering library refresh");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queuing library refresh");
            }
        }

        /// <summary>
        /// Returns the Emby user ID from the current request context.
        /// </summary>
        private string? TryGetCurrentUserId()
        {
            try
            {
                var user = _authCtx.GetAuthorizationInfo(Request).User;
                return user?.Id.ToString("N");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Maps a DiscoverCatalogEntry to a DiscoverItem (response DTO).
        /// Looks up the Emby internal item ID for library items.
        /// userPinnedImdbIds: when non-null, overrides InLibrary with per-user pin status.
        /// </summary>
        private DiscoverItem MapToDiscoverItem(DiscoverCatalogEntry entry, HashSet<string>? userPinnedImdbIds = null)
        {
            var inLibrary = userPinnedImdbIds != null
                ? (!string.IsNullOrEmpty(entry.ImdbId) && userPinnedImdbIds.Contains(entry.ImdbId))
                : entry.IsInUserLibrary;

            string? embyItemId = null;
            if (inLibrary && !string.IsNullOrWhiteSpace(entry.ImdbId))
            {
                try
                {
                    var candidates = _libraryManager.GetItemList(
                        new MediaBrowser.Controller.Entities.InternalItemsQuery
                        {
                            IncludeItemTypes = entry.MediaType == "series"
                                ? new[] { "Series" }
                                : new[] { "Movie" },
                            Recursive = true
                        });
                    var match = candidates.FirstOrDefault(i =>
                        i.ProviderIds != null &&
                        i.ProviderIds.TryGetValue("Imdb", out var id) &&
                        string.Equals(id, entry.ImdbId, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        embyItemId = match.Id.ToString("N");
                }
                catch (Exception ex)
                {
                    // Non-critical failure to look up Emby item - log and continue
                    _logger.LogDebug(ex, "[InfiniteDrive] DiscoverService: Failed to look up Emby item for {ImdbId}", entry.ImdbId);
                }
            }

            return new DiscoverItem
            {
                ImdbId = entry.ImdbId,
                Title = entry.Title,
                Year = entry.Year,
                MediaType = entry.MediaType,
                PosterUrl = entry.PosterUrl,
                BackdropUrl = entry.BackdropUrl,
                Overview = entry.Overview,
                Genres = entry.Genres,
                ImdbRating = entry.ImdbRating,
                Certification = entry.Certification,
                InLibrary = inLibrary,
                EmbyItemId = embyItemId,
                CatalogSource = entry.CatalogSource
            };
        }

        /// <summary>
        /// Sanitizes a string for use as a filename.
        /// Removes or replaces characters invalid in filenames.
        /// </summary>
        private static string SanitizeFilename(string filename)
        {
            var invalidChars = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            var result = filename;
            foreach (var c in invalidChars)
            {
                result = result.Replace(c, '_');
            }
            return result.Trim().TrimEnd('.');
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

        /// <summary>
        /// Test endpoint for on-demand stream resolution.
        /// Demonstrates StreamResolver integration for on-demand playback.
        /// </summary>
        [Route("/InfiniteDrive/Discover/TestStreamResolution", "GET")]
        public async Task<object> Get(DiscoverTestStreamResolutionRequest req)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                if (string.IsNullOrWhiteSpace(req.ImdbId))
                {
                    return new DiscoverTestStreamResolutionResponse
                    {
                        Success = false,
                        Error = "ImdbId is required",
                        ProxyToken = null,
                        StreamUrl = null
                    };
                }

                _logger.LogInformation("[Discover] Testing stream resolution for {ImdbId}", req.ImdbId);

                var config = Plugin.Instance?.Configuration;
                var db = Plugin.Instance?.DatabaseManager;
                if (config == null || db == null)
                {
                    return new DiscoverTestStreamResolutionResponse
                    {
                        Success = false,
                        Error = "Plugin not initialized",
                        ProxyToken = null,
                        StreamUrl = null
                    };
                }

                // Check cache first
                var cached = await db.GetCachedStreamAsync(req.ImdbId, req.Season, req.Episode);
                var candidates = cached != null
                    ? await db.GetStreamCandidatesAsync(req.ImdbId, req.Season, req.Episode)
                    : new List<StreamCandidate>();

                // If valid cache hit, use it
                if (cached?.Status != "valid" || string.IsNullOrEmpty(cached.ExpiresAt) ||
                    DateTime.TryParse(cached.ExpiresAt, out var expiry) && DateTime.UtcNow > expiry)
                {
                    // Cache miss or stale - sync resolve
                    var playReq = new PlayRequest
                    {
                        Imdb = req.ImdbId,
                        Season = req.Season,
                        Episode = req.Episode
                    };
                    var resolved = await StreamResolutionHelper.SyncResolveViaProvidersAsync(
                        playReq, config, db, _logger, Plugin.Instance?.ResolverHealthTracker, CancellationToken.None);
                    if (resolved != null)
                    {
                        candidates = await db.GetStreamCandidatesAsync(req.ImdbId, req.Season, req.Episode);
                        cached = resolved;
                    }
                }

                if (cached == null || string.IsNullOrEmpty(cached.StreamUrl))
                {
                    return new DiscoverTestStreamResolutionResponse
                    {
                        Success = false,
                        Error = "No stream available",
                        ProxyToken = null,
                        StreamUrl = null
                    };
                }

                // Use direct stream URL (proxy tokens removed in Sprint 137)
                var streamUrl = cached.StreamUrl;
                var port = ParsePort(config.EmbyBaseUrl) ?? 8096;

                _logger.LogInformation("[Discover] Stream resolution successful for {ImdbId}", req.ImdbId);

                return new DiscoverTestStreamResolutionResponse
                {
                    Success = true,
                    ProxyToken = null,
                    StreamUrl = streamUrl,
                    EmbyUrl = $"http://127.0.0.1:{port}/web/#/details/{req.ImdbId}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Error testing stream resolution for {ImdbId}", req.ImdbId ?? "null");
                return new DiscoverTestStreamResolutionResponse
                {
                    Success = false,
                    Error = "An internal error occurred. Check server logs.",
                    ProxyToken = null,
                    StreamUrl = null
                };
            }
        }

        // ── Parental Filter Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Applies parental rating filter to a list of discover catalog entries.
        /// Filters out items that exceed the user's maximum allowed parental rating.
        /// </summary>
        private List<DiscoverCatalogEntry> ApplyParentalFilter(List<DiscoverCatalogEntry> items, int maxRating)
        {
            // Unrestricted user: never block anything
            if (maxRating >= 999)
                return items;

            var config = Plugin.Instance?.Configuration;
            var blockUnrated = config?.BlockUnratedForRestricted ?? false;

            return items.Where(item =>
            {
                var itemRating = ParseRating(item.Certification);

                // Rated item: block if exceeds user's ceiling
                if (itemRating < 999)
                {
                    return itemRating <= maxRating;
                }

                // Unrated item for restricted user: check server toggle
                if (itemRating >= 999)
                {
                    return !blockUnrated; // Show only if NOT blocking unrated
                }

                return true;
            }).ToList();
        }

        /// <summary>
        /// Gets the maximum allowed parental rating for the current request user.
        /// Returns 999 for unrestricted access (no ceiling).
        /// </summary>
        private int GetUserMaxParentalRating()
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return 999; // Unauthenticated = unrestricted for now
                }

                // Load user from Emby user manager
                var user = _userManager.GetUserById(userId);
                if (user?.Policy == null)
                {
                    return 999; // User or policy not found = unrestricted
                }

                // Return user's MaxParentalRating (null means unrestricted)
                return user.Policy.MaxParentalRating ?? 999;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ParentalFilter] Failed to get user max parental rating, defaulting to unrestricted");
                return 999;
            }
        }

        /// <summary>
        /// Maps rating labels to numeric values for comparison.
        /// Higher values = more restrictive content.
        /// Unknown/null ratings return 999 (fail-closed = restricted).
        /// </summary>
        private int ParseRating(string? ratingLabel)
        {
            if (string.IsNullOrEmpty(ratingLabel))
            {
                return 999; // Unknown = restricted
            }

            // Map rating labels to numeric values for comparison
            return ratingLabel switch
            {
                "G" => 100,
                "TV-Y" => 100,
                "TV-G" => 200,
                "PG" => 300,
                "TV-PG" => 300,
                "PG-13" => 400,
                "TV-14" => 500,
                "R" => 600,
                "TV-MA" => 700,
                "NC-17" => 800,
                _ => 999
            };
        }

        private string? GetUserId() => TryGetCurrentUserId();
    }
}
