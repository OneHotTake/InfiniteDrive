using System;
using System.Collections.Generic;
using System.Linq;
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
    // ── Request / Response DTOs ──────────────────────────────────────────────────

    /// <summary>Request for <c>GET /InfiniteDrive/Admin/Lists</c>.</summary>
    [Route("/InfiniteDrive/Admin/Lists", "GET",
        Summary = "Returns all server-wide external lists (admin only)")]
    public class GetAdminListsRequest : IReturn<GetAdminListsResponse> { }

    /// <summary>Response from <c>GET /InfiniteDrive/Admin/Lists</c>.</summary>
    public class GetAdminListsResponse
    {
        public List<AdminListDto> Lists { get; set; } = new();
    }

    /// <summary>Wire DTO for a single admin list.</summary>
    public class AdminListDto
    {
        public string Id            { get; set; } = string.Empty;
        public string Provider      { get; set; } = string.Empty;
        public string ListUrl       { get; set; } = string.Empty;
        public string DisplayName   { get; set; } = string.Empty;
        public string? LastSyncedAt { get; set; }
        public string? LastSyncStatus { get; set; }
        public int ItemCount        { get; set; }
    }

    /// <summary>Request for <c>POST /InfiniteDrive/Admin/Lists/Add</c>.</summary>
    [Route("/InfiniteDrive/Admin/Lists/Add", "POST",
        Summary = "Adds a server-wide external list (admin only)")]
    public class AddAdminListRequest : IReturn<AddAdminListResponse>
    {
        [ApiMember(Name = "listUrl", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string ListUrl { get; set; } = string.Empty;

        [ApiMember(Name = "displayName", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? DisplayName { get; set; }
    }

    /// <summary>Response from <c>POST /InfiniteDrive/Admin/Lists/Add</c>.</summary>
    public class AddAdminListResponse
    {
        public bool   Ok          { get; set; }
        public string? CatalogId  { get; set; }
        public string? Provider   { get; set; }
        public int    Fetched     { get; set; }
        public int    Added       { get; set; }
        public int    Updated     { get; set; }
        public long   ElapsedMs   { get; set; }
        public string? Error      { get; set; }
    }

    /// <summary>Request for <c>POST /InfiniteDrive/Admin/Lists/Remove</c>.</summary>
    [Route("/InfiniteDrive/Admin/Lists/Remove", "POST",
        Summary = "Soft-deletes a server-wide external list (admin only)")]
    public class RemoveAdminListRequest : IReturn<RemoveAdminListResponse>
    {
        [ApiMember(Name = "catalogId", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string CatalogId { get; set; } = string.Empty;
    }

    /// <summary>Response from <c>POST /InfiniteDrive/Admin/Lists/Remove</c>.</summary>
    public class RemoveAdminListResponse
    {
        public bool   Ok    { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Request for <c>POST /InfiniteDrive/Admin/Lists/Refresh</c>.</summary>
    [Route("/InfiniteDrive/Admin/Lists/Refresh", "POST",
        Summary = "Refreshes one or all server-wide external lists (admin only)")]
    public class RefreshAdminListsRequest : IReturn<RefreshAdminListsResponse>
    {
        [ApiMember(Name = "catalogId", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? CatalogId { get; set; }
    }

    /// <summary>Response from <c>POST /InfiniteDrive/Admin/Lists/Refresh</c>.</summary>
    public class RefreshAdminListsResponse
    {
        public bool   Ok       { get; set; }
        public int    Lists    { get; set; }
        public int    Fetched  { get; set; }
        public int    Added    { get; set; }
        public int    Updated  { get; set; }
        public int    Removed  { get; set; }
        public long   ElapsedMs { get; set; }
        public List<UserCatalogSyncResult> PerList { get; set; } = new();
        public string? Error   { get; set; }
    }

    /// <summary>Request for <c>GET /InfiniteDrive/Admin/Lists/Providers</c>.</summary>
    [Route("/InfiniteDrive/Admin/Lists/Providers", "GET",
        Summary = "Returns which list providers are enabled (admin only)")]
    public class GetAdminProvidersRequest : IReturn<AdminProvidersResponse> { }

    /// <summary>Response with enabled providers info.</summary>
    public class AdminProvidersResponse
    {
        public List<string> EnabledProviders { get; set; } = new();
    }

    /// <summary>Request for <c>GET /InfiniteDrive/Admin/Lists/UserCount</c>.</summary>
    [Route("/InfiniteDrive/Admin/Lists/UserCount", "GET",
        Summary = "Returns total active user catalog count (admin only)")]
    public class GetUserCatalogCountRequest : IReturn<UserCatalogCountResponse> { }

    /// <summary>Response with total user catalog count.</summary>
    public class UserCatalogCountResponse
    {
        public int TotalUserLists { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// REST API for admin-managed server-wide external lists.
    /// All endpoints require admin authentication.
    /// </summary>
    public class AdminListService : IService, IRequiresRequest
    {
        private readonly ILogger<AdminListService> _logger;
        private readonly DatabaseManager _db;
        private readonly UserCatalogSyncService _syncService;
        private readonly IAuthorizationContext _authCtx;

        private const string ServerOwner = "SERVER";

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        public AdminListService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger      = new EmbyLoggerAdapter<AdminListService>(logManager.GetLogger("InfiniteDrive"));
            _db          = Plugin.Instance.DatabaseManager;
            _authCtx     = authCtx;
            _syncService = new UserCatalogSyncService(
                logManager,
                _db,
                Plugin.Instance.StrmWriterService,
                Plugin.Instance.CooldownGate,
                Plugin.Instance.IdResolverService);
        }

        // ── GET /InfiniteDrive/Admin/Lists ────────────────────────────────────────

        public async Task<object> Get(GetAdminListsRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var catalogs = await _db.GetUserCatalogsByOwnerAsync(ServerOwner, activeOnly: true);
            return new GetAdminListsResponse
            {
                Lists = catalogs.Select(c => new AdminListDto
                {
                    Id             = c.Id,
                    Provider       = c.Service,
                    ListUrl        = c.ListUrl,
                    DisplayName    = c.DisplayName,
                    LastSyncedAt   = c.LastSyncedAt,
                    LastSyncStatus = c.LastSyncStatus,
                }).ToList(),
            };
        }

        // ── GET /InfiniteDrive/Admin/Lists/Providers ──────────────────────────────

        public object Get(GetAdminProvidersRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var config = Plugin.Instance.Configuration;
            return new AdminProvidersResponse
            {
                EnabledProviders = ListFetcher.GetEnabledProviders(config.TraktClientId, config.TmdbApiKey),
            };
        }

        // ── GET /InfiniteDrive/Admin/Lists/UserCount ─────────────────────────────

        public async Task<object> Get(GetUserCatalogCountRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var count = await _db.GetActiveUserCatalogCountAsync();
            return new UserCatalogCountResponse { TotalUserLists = count };
        }

        // ── POST /InfiniteDrive/Admin/Lists/Add ───────────────────────────────────

        public async Task<object> Post(AddAdminListRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            if (string.IsNullOrWhiteSpace(req.ListUrl))
                return Err<AddAdminListResponse>("List URL is required");

            if (!req.ListUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Err<AddAdminListResponse>("List URL must use HTTPS");

            var provider = ListFetcher.DetectProvider(req.ListUrl);
            if (provider == null)
                return Err<AddAdminListResponse>(
                    "Unsupported list URL. Supported providers: mdblist.com, trakt.tv, themoviedb.org, anilist.co");

            var config = Plugin.Instance.Configuration;
            var enabled = ListFetcher.GetEnabledProviders(config.TraktClientId, config.TmdbApiKey);
            if (!enabled.Contains(provider))
            {
                var neededKey = provider switch
                {
                    "trakt" => "a Trakt Client ID",
                    "tmdb" => "a TMDB API Key",
                    _ => null
                };
                return Err<AddAdminListResponse>(
                    neededKey != null
                        ? $"{provider} is not available. Configure {neededKey} in plugin settings first."
                        : $"Provider '{provider}' is not available.");
            }

            // Fetch list for validation
            var fetchResult = await ListFetcher.FetchAsync(
                req.ListUrl, config.TraktClientId, config.TmdbApiKey, _logger, CancellationToken.None);

            if (!fetchResult.Ok)
                return Err<AddAdminListResponse>(fetchResult.Error);

            if (fetchResult.Items.Count == 0)
                return Err<AddAdminListResponse>("This list appears to be empty.");

            var displayName = !string.IsNullOrWhiteSpace(req.DisplayName)
                ? req.DisplayName
                : (!string.IsNullOrWhiteSpace(fetchResult.DisplayName) ? fetchResult.DisplayName : req.ListUrl);

            var catalogId = await _db.CreateUserCatalogAsync(
                ServerOwner, provider, req.ListUrl, displayName!);

            // Eager sync
            var result = await _syncService.SyncOneAsync(catalogId, CancellationToken.None);

            return new AddAdminListResponse
            {
                Ok        = result.Ok,
                CatalogId = catalogId,
                Provider  = provider,
                Fetched   = fetchResult.Items.Count,
                Added     = result.Added,
                Updated   = result.Updated,
                ElapsedMs = result.ElapsedMs,
                Error     = result.Error,
            };
        }

        // ── POST /InfiniteDrive/Admin/Lists/Remove ────────────────────────────────

        public async Task<object> Post(RemoveAdminListRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            if (string.IsNullOrWhiteSpace(req.CatalogId))
                return Err<RemoveAdminListResponse>("catalogId is required");

            var catalog = await _db.GetUserCatalogByIdAsync(req.CatalogId);
            if (catalog == null)
                return Err<RemoveAdminListResponse>("List not found");

            if (catalog.OwnerUserId != ServerOwner)
                return Err<RemoveAdminListResponse>("Not a server list");

            await _db.SetUserCatalogActiveAsync(req.CatalogId, active: false);
            return new RemoveAdminListResponse { Ok = true };
        }

        // ── POST /InfiniteDrive/Admin/Lists/Refresh ───────────────────────────────

        public async Task<object> Post(RefreshAdminListsRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var ct = CancellationToken.None;

            if (!string.IsNullOrWhiteSpace(req.CatalogId))
            {
                var catalog = await _db.GetUserCatalogByIdAsync(req.CatalogId);
                if (catalog == null)
                    return new RefreshAdminListsResponse { Ok = false, Error = "List not found" };

                if (catalog.OwnerUserId != ServerOwner)
                    return new RefreshAdminListsResponse { Ok = false, Error = "Not a server list" };

                var result = await _syncService.SyncOneAsync(req.CatalogId, ct);
                return new RefreshAdminListsResponse
                {
                    Ok        = result.Ok,
                    Lists     = 1,
                    Fetched   = result.Fetched,
                    Added     = result.Added,
                    Updated   = result.Updated,
                    ElapsedMs = result.ElapsedMs,
                    PerList   = new List<UserCatalogSyncResult> { result },
                    Error     = result.Error,
                };
            }

            var catalogs = await _db.GetUserCatalogsByOwnerAsync(ServerOwner, activeOnly: true, ct);
            var results = new List<UserCatalogSyncResult>(catalogs.Count);

            foreach (var catalog in catalogs)
            {
                if (ct.IsCancellationRequested) break;
                results.Add(await _syncService.SyncOneAsync(catalog.Id, ct));
            }

            return new RefreshAdminListsResponse
            {
                Ok        = results.All(r => r.Ok),
                Lists     = results.Count,
                Fetched   = results.Sum(r => r.Fetched),
                Added     = results.Sum(r => r.Added),
                Updated   = results.Sum(r => r.Updated),
                Removed   = results.Sum(r => r.Removed),
                ElapsedMs = results.Sum(r => r.ElapsedMs),
                PerList   = results,
            };
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static T Err<T>(string message) where T : class, new()
        {
            var resp = new T();
            var okProp = typeof(T).GetProperty("Ok");
            var errProp = typeof(T).GetProperty("Error");
            okProp?.SetValue(resp, false);
            errProp?.SetValue(resp, message);
            return resp;
        }
    }
}
