using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Services
{
    public class SearchRequest : IReturn<object>
    {
        /// <summary>Search query (substring match, case-insensitive).</summary>
        public string Q { get; set; } = string.Empty;

        /// <summary>Maximum results to return (default 20, max 100).</summary>
        public int Limit { get; set; } = 20;
    }

    /// <summary>One search result row.</summary>
    public class SearchResultItem
    {
        /// <summary>IMDB ID.</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Release year.</summary>
        public int? Year { get; set; }

        /// <summary>Media type: <c>movie</c> or <c>series</c>.</summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>Catalog source key.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>True if a valid cache entry exists for this item.</summary>
        public bool HasValidCache { get; set; }
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Search</c>.</summary>
    public class SearchResponse
    {
        /// <summary>Search query that was executed.</summary>
        public string Query { get; set; } = string.Empty;

        /// <summary>Matched catalog items.</summary>
        public List<SearchResultItem> Results { get; set; } = new List<SearchResultItem>();

        /// <summary>Error message, or null on success.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Searches the catalog by title and returns matching items with cache status.
    /// Designed to power the Inspect panel's title-lookup helper in the dashboard.
    ///
    /// Example: <c>GET /InfiniteDrive/Search?q=breaking+bad&amp;limit=10</c>
    /// </summary>
    public class SearchService : IService, IRequiresRequest
    {
        private readonly ILogger<SearchService> _logger;
        private readonly IAuthorizationContext  _authCtx;
        public IRequest Request { get; set; } = null!;

        public SearchService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<SearchService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>GET /InfiniteDrive/Search</c>.</summary>
        public async Task<object> Get(SearchRequest request)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
                return new SearchResponse { Error = "Plugin not initialised" };

            var query = (request.Q ?? string.Empty).Trim();
            if (query.Length < 2)
                return new SearchResponse
                {
                    Query = query,
                    Error = "Query must be at least 2 characters",
                };

            var limit = Math.Max(1, Math.Min(100, request.Limit));

            try
            {
                var items = await db.SearchCatalogAsync(query, limit);

                // Load cache stats in one query to show HasValidCache without N+1 calls
                var cacheStats = await db.GetResolutionCacheStatsAsync();

                var results = new List<SearchResultItem>(items.Count);
                foreach (var item in items)
                {
                    var cached = await db.GetCachedStreamAsync(item.ImdbId, null, null);
                    results.Add(new SearchResultItem
                    {
                        ImdbId       = item.ImdbId,
                        Title        = item.Title,
                        Year         = item.Year,
                        MediaType    = item.MediaType,
                        Source       = item.Source,
                        HasValidCache = cached != null && cached.Status == "valid",
                    });
                }

                return new SearchResponse { Query = query, Results = results };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] SearchService: query failed for '{Query}'", query);
                return new SearchResponse { Query = query, Error = ex.Message };
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Admin auth helper
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shared helper for enforcing admin-only access on plugin endpoints.
    /// Injects <see cref="IAuthorizationContext"/> and exposes
    /// <see cref="RequireAdmin"/> which returns a 403 object when the caller
    /// is not an Emby administrator.
    /// </summary>
    internal static class AdminGuard
    {
    // ════════════════════════════════════════════════════════════════════════════════════
    // REFRESH MANIFEST ENDPOINT (Sprint 100A-01)
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Request for <c>POST /InfiniteDrive/RefreshManifest</c>.</summary>
    [Route("/InfiniteDrive/RefreshManifest", "POST",
        Summary = "Force-refreshes the AIOStreams manifest and returns summary")]
    public class RefreshManifestRequest : IReturn<object> { }

    /// <summary>Response from <c>POST /InfiniteDrive/RefreshManifest</c>.</summary>
    public class RefreshManifestResponse
    {
        /// <summary>Status: "ok" or "error".</summary>
        public string Status { get; set; } = "error";

        /// <summary>Manifest status.</summary>
        public ManifestStatusState ManifestStatus { get; set; } = ManifestStatusState.Error;

        /// <summary>ISO8601 timestamp when manifest was last fetched.</summary>
        public string? ManifestLastFetched { get; set; }

        /// <summary>Number of catalogs in manifest.</summary>
        public int CatalogCount { get; set; }

        /// <summary>Resource types present in manifest (catalog, meta, stream).</summary>
        public List<string> ResourceTypes { get; set; } = new List<string>();

        /// <summary>ID prefixes found in manifest (imdb, tmdb, kitsu, etc.).</summary>
        public List<string> IdPrefixes { get; set; } = new List<string>();
    }

    /// <summary>
    /// Service for refreshing the AIOStreams manifest.
    /// </summary>
    public class RefreshManifestService : IService, IRequiresRequest
    {
        private readonly ILogger<RefreshManifestService> _logger;
        private readonly IAuthorizationContext _authCtx;

        public IRequest Request { get; set; } = null!;

        public RefreshManifestService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger = new EmbyLoggerAdapter<RefreshManifestService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>POST /InfiniteDrive/RefreshManifest</c>.</summary>
        public async Task<object> Post(RefreshManifestRequest _)
        {
            // Sprint 100A-09: Admin guard required
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.PrimaryManifestUrl))
            {
                return new RefreshManifestResponse
                {
                    Status = "error",
                    ManifestStatus = ManifestStatusState.Error,
                    ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o"),
                };
            }

            // Sprint 102A-01: Check if manifest is stale before fetching
            Plugin.CheckManifestStale();

            _logger.LogInformation("[InfiniteDrive] RefreshManifest: Force-refreshing manifest from {Url}",
                config.PrimaryManifestUrl);

            try
            {
                var client = new AioStreamsClient(config, _logger);
                client.Cooldown = Plugin.Instance?.CooldownGate;
                var manifest = await client.GetManifestAsync(System.Threading.CancellationToken.None);

                if (manifest == null)
                {
                    _logger.LogWarning("[InfiniteDrive] RefreshManifest: Failed to fetch manifest");
                    // Sprint 102A-01: Set status to error on fetch failure
                    Plugin.SetManifestStatus(ManifestStatusState.Error);
                    return new RefreshManifestResponse
                    {
                        Status = "error",
                        ManifestStatus = Plugin.GetManifestStatus(),
                        ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o"),
                    };
                }

                // Update manifest fetch timestamp and set status to ok
                Plugin.ManifestFetchedAt = DateTimeOffset.UtcNow;
                Plugin.SetManifestStatus(ManifestStatusState.Ok);

                // Extract summary info from manifest
                var resourceTypes = new List<string>();
                if (manifest.Resources != null)
                {
                    foreach (var resource in manifest.Resources)
                    {
                        if (!string.IsNullOrEmpty(resource.Name) && !resourceTypes.Contains(resource.Name))
                            resourceTypes.Add(resource.Name);
                    }
                }

                var idPrefixes = new List<string>();
                if (manifest.IdPrefixes != null)
                {
                    foreach (var prefix in manifest.IdPrefixes)
                    {
                        var trimmed = prefix.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !idPrefixes.Contains(trimmed))
                            idPrefixes.Add(trimmed);
                    }
                }
                if (!idPrefixes.Contains("tt")) idPrefixes.Add("tt");

                var catalogCount = manifest.Catalogs?.Count ?? 0;

                _logger.LogInformation(
                    "[InfiniteDrive] RefreshManifest: Success - {Catalogs} catalogs, " +
                    "{Resources} resource types, {Prefixes} ID prefixes",
                    catalogCount, string.Join(", ", resourceTypes), string.Join(", ", idPrefixes));

                return new RefreshManifestResponse
                {
                    Status = "ok",
                    ManifestStatus = Plugin.GetManifestStatus(),
                    ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o"),
                    CatalogCount = catalogCount,
                    ResourceTypes = resourceTypes,
                    IdPrefixes = idPrefixes,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] RefreshManifest: Exception during manifest refresh");
                // Sprint 102A-01: Set status to error on exception
                Plugin.SetManifestStatus(ManifestStatusState.Error);
                return new RefreshManifestResponse
                {
                    Status = "error",
                    ManifestStatus = Plugin.GetManifestStatus(),
                    ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o"),
                };
            }
        }
    }
        /// <summary>
        /// Returns <c>null</c> if the request is from an admin user, or a
        /// 403 error object that the calling service should return immediately.
        /// </summary>
        public static object? RequireAdmin(IAuthorizationContext authCtx, IRequest request)
        {
            try
            {
                var info = authCtx.GetAuthorizationInfo(request);
                if (info?.User?.Policy?.IsAdministrator == true)
                    return null;
            }
            catch
            {
                // Auth context unavailable — fall through to deny
            }
            request.Response.StatusCode = 403;
            return new { Error = "Forbidden", Message = "This endpoint requires administrator access." };
        }

        /// <summary>
        /// Returns <c>null</c> if the request is from an authenticated user, or a
        /// 403 error object that the calling service should return immediately.
        /// Sprint 204: Added to allow user-facing Discover endpoints
        /// </summary>
        public static object? RequireAuthenticated(IAuthorizationContext authCtx, IRequest request)
        {
            try
            {
                var info = authCtx.GetAuthorizationInfo(request);
                if (info?.User != null)
                    return null;
            }
            catch
            {
                // Auth context unavailable — fall through to deny
            }
            request.Response.StatusCode = 403;
            return new { Error = "Forbidden", Message = "This endpoint requires authentication." };
        }
    }
}
