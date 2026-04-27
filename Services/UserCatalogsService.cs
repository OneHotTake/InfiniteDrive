using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    // ── Request / Response DTOs ──────────────────────────────────────────────────

    /// <summary>Request for <c>GET /InfiniteDrive/User/Catalogs</c>.</summary>
    [Route("/InfiniteDrive/User/Catalogs", "GET",
        Summary = "Returns the caller's active external lists")]
    public class GetUserCatalogsRequest : IReturn<GetUserCatalogsResponse> { }

    /// <summary>Response from <c>GET /InfiniteDrive/User/Catalogs</c>.</summary>
    public class GetUserCatalogsResponse
    {
        public List<UserCatalogDto> Catalogs { get; set; } = new();
        public int Limit { get; set; } = 5;
    }

    /// <summary>Wire DTO for a single user catalog.</summary>
    public class UserCatalogDto
    {
        public string Id            { get; set; } = string.Empty;
        public string Provider      { get; set; } = string.Empty;
        public string ListUrl       { get; set; } = string.Empty;
        public string DisplayName   { get; set; } = string.Empty;
        public string? LastSyncedAt { get; set; }
        public string? LastSyncStatus { get; set; }
        public int ItemCount        { get; set; }
    }

    /// <summary>Request for <c>POST /InfiniteDrive/User/Catalogs/Add</c>.</summary>
    [Route("/InfiniteDrive/User/Catalogs/Add", "POST",
        Summary = "Adds an external list (MDBList, Trakt, TMDB, AniList)")]
    public class AddUserCatalogRequest : IReturn<AddUserCatalogResponse>
    {
        [ApiMember(Name = "listUrl", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string ListUrl { get; set; } = string.Empty;

        [ApiMember(Name = "displayName", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? DisplayName { get; set; }
    }

    /// <summary>Response from <c>POST /InfiniteDrive/User/Catalogs/Add</c>.</summary>
    public class AddUserCatalogResponse
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

    /// <summary>Request for <c>POST /InfiniteDrive/User/Catalogs/Remove</c>.</summary>
    [Route("/InfiniteDrive/User/Catalogs/Remove", "POST",
        Summary = "Soft-deletes a user catalog")]
    public class RemoveUserCatalogRequest : IReturn<RemoveUserCatalogResponse>
    {
        [ApiMember(Name = "catalogId", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string CatalogId { get; set; } = string.Empty;
    }

    /// <summary>Response from <c>POST /InfiniteDrive/User/Catalogs/Remove</c>.</summary>
    public class RemoveUserCatalogResponse
    {
        public bool   Ok    { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Request for <c>POST /InfiniteDrive/User/Catalogs/Refresh</c>.</summary>
    [Route("/InfiniteDrive/User/Catalogs/Refresh", "POST",
        Summary = "Synchronously refreshes one or all of the caller's external lists")]
    public class RefreshUserCatalogsRequest : IReturn<RefreshUserCatalogsResponse>
    {
        [ApiMember(Name = "catalogId", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? CatalogId { get; set; }
    }

    /// <summary>Response from <c>POST /InfiniteDrive/User/Catalogs/Refresh</c>.</summary>
    public class RefreshUserCatalogsResponse
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

    /// <summary>Request for <c>GET /InfiniteDrive/User/Catalogs/Providers</c>.</summary>
    [Route("/InfiniteDrive/User/Catalogs/Providers", "GET",
        Summary = "Returns which list providers are enabled and user's count/limit")]
    public class GetProvidersRequest : IReturn<ProvidersResponse> { }

    /// <summary>Response with enabled providers info.</summary>
    public class ProvidersResponse
    {
        public List<string> EnabledProviders { get; set; } = new();
        public int CurrentCount { get; set; }
        public int Limit { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// REST API for user-owned external list catalogs.
    /// All endpoints require an authenticated (non-admin) user.
    /// </summary>
    public class UserCatalogsService : IService, IRequiresRequest
    {
        private readonly ILogger<UserCatalogsService> _logger;
        private readonly DatabaseManager _db;
        private readonly UserCatalogSyncService _syncService;
        private readonly IAuthorizationContext _authCtx;

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        public UserCatalogsService(
            ILogManager logManager,
            IAuthorizationContext authCtx)
        {
            _logger      = new EmbyLoggerAdapter<UserCatalogsService>(logManager.GetLogger("InfiniteDrive"));
            _db          = Plugin.Instance.DatabaseManager;
            _authCtx     = authCtx;
            _syncService = new UserCatalogSyncService(
                logManager,
                _db,
                Plugin.Instance.StrmWriterService,
                Plugin.Instance.CooldownGate,
                Plugin.Instance.IdResolverService);
        }

        private int UserLimit => Plugin.Instance.Configuration.UserCatalogLimit;

        // ── GET /InfiniteDrive/User/Catalogs ────────────────────────────────────────

        public async Task<object> Get(GetUserCatalogsRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return new GetUserCatalogsResponse { Limit = UserLimit };

            var catalogs = await _db.GetUserCatalogsByOwnerAsync(userId, activeOnly: true);
            return new GetUserCatalogsResponse
            {
                Limit    = UserLimit,
                Catalogs = catalogs.Select(c => new UserCatalogDto
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

        // ── GET /InfiniteDrive/User/Catalogs/Providers ──────────────────────────────

        public async Task<object> Get(GetProvidersRequest req)
        {
            var userId = GetCurrentUserId();
            var config = Plugin.Instance.Configuration;
            var currentCount = 0;

            if (userId != null)
            {
                var catalogs = await _db.GetUserCatalogsByOwnerAsync(userId, activeOnly: true);
                currentCount = catalogs.Count;
            }

            return new ProvidersResponse
            {
                EnabledProviders = ListFetcher.GetEnabledProviders(config.TraktClientId, config.TmdbApiKey),
                CurrentCount = currentCount,
                Limit = UserLimit,
            };
        }

        // ── POST /InfiniteDrive/User/Catalogs/Add ───────────────────────────────────

        public async Task<object> Post(AddUserCatalogRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Err<AddUserCatalogResponse>("Not authenticated");

            // Validate URL
            if (string.IsNullOrWhiteSpace(req.ListUrl))
                return Err<AddUserCatalogResponse>("List URL is required");

            if (!req.ListUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Err<AddUserCatalogResponse>("List URL must use HTTPS");

            // Detect provider
            var provider = ListFetcher.DetectProvider(req.ListUrl);
            if (provider == null)
                return Err<AddUserCatalogResponse>(
                    "Unsupported list URL. Supported providers: mdblist.com, trakt.tv, themoviedb.org, anilist.co");

            // Check provider is enabled
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
                return Err<AddUserCatalogResponse>(
                    neededKey != null
                        ? $"{provider} is not available. Ask your admin to configure {neededKey}."
                        : $"Provider '{provider}' is not available.");
            }

            // Enforce per-user catalog limit
            var existing = await _db.GetUserCatalogsByOwnerAsync(userId, activeOnly: true);
            if (existing.Count >= UserLimit)
            {
                return new AddUserCatalogResponse
                {
                    Ok    = false,
                    Error = $"List limit reached. You can have at most {UserLimit} lists. Remove one to add another."
                };
            }

            // Fetch list for validation
            var fetchResult = await ListFetcher.FetchAsync(
                req.ListUrl, config.TraktClientId, config.TmdbApiKey, _logger, CancellationToken.None);

            if (!fetchResult.Ok)
                return Err<AddUserCatalogResponse>(fetchResult.Error);

            if (fetchResult.Items.Count == 0)
                return Err<AddUserCatalogResponse>("This list appears to be empty.");

            var displayName = !string.IsNullOrWhiteSpace(req.DisplayName)
                ? req.DisplayName
                : (!string.IsNullOrWhiteSpace(fetchResult.DisplayName) ? fetchResult.DisplayName : req.ListUrl);

            // Insert catalog row
            var catalogId = await _db.CreateUserCatalogAsync(
                userId, provider, req.ListUrl, displayName!);

            // Eager sync
            var result = await _syncService.SyncOneAsync(catalogId, CancellationToken.None);

            return new AddUserCatalogResponse
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

        // ── POST /InfiniteDrive/User/Catalogs/Remove ────────────────────────────────

        public async Task<object> Post(RemoveUserCatalogRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Err<RemoveUserCatalogResponse>("Not authenticated");

            if (string.IsNullOrWhiteSpace(req.CatalogId))
                return Err<RemoveUserCatalogResponse>("catalogId is required");

            var catalog = await _db.GetUserCatalogByIdAsync(req.CatalogId);
            if (catalog == null)
                return Err<RemoveUserCatalogResponse>("List not found");

            if (catalog.OwnerUserId != userId)
                return new RemoveUserCatalogResponse { Ok = false, Error = "Forbidden" };

            await _db.SetUserCatalogActiveAsync(req.CatalogId, active: false);
            return new RemoveUserCatalogResponse { Ok = true };
        }

        // ── POST /InfiniteDrive/User/Catalogs/Refresh ───────────────────────────────

        public async Task<object> Post(RefreshUserCatalogsRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return new RefreshUserCatalogsResponse { Ok = false, Error = "Not authenticated" };

            var ct = CancellationToken.None;

            if (!string.IsNullOrWhiteSpace(req.CatalogId))
            {
                var catalog = await _db.GetUserCatalogByIdAsync(req.CatalogId);
                if (catalog == null)
                    return new RefreshUserCatalogsResponse { Ok = false, Error = "List not found" };

                if (catalog.OwnerUserId != userId)
                    return new RefreshUserCatalogsResponse { Ok = false, Error = "Forbidden" };

                var result = await _syncService.SyncOneAsync(req.CatalogId, ct);
                return new RefreshUserCatalogsResponse
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

            var results = await _syncService.SyncAllForOwnerAsync(userId, ct);
            return new RefreshUserCatalogsResponse
            {
                Ok        = results.All(r => r.Ok),
                Lists     = results.Count,
                Fetched   = results.Sum(r => r.Fetched),
                Added     = results.Sum(r => r.Added),
                Updated   = results.Sum(r => r.Updated),
                Removed   = results.Sum(r => r.Removed),
                ElapsedMs = results.Sum(r => r.ElapsedMs),
                PerList   = results.ToList(),
            };
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private string? GetCurrentUserId()
        {
            try
            {
                var info = _authCtx.GetAuthorizationInfo(Request);
                return info?.User?.Id.ToString("N");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] UserCatalogsService: Failed to get current user ID");
                return null;
            }
        }

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
