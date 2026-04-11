using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
        Summary = "Returns the caller's active RSS catalogs")]
    public class GetUserCatalogsRequest : IReturn<GetUserCatalogsResponse> { }

    /// <summary>Response from <c>GET /InfiniteDrive/User/Catalogs</c>.</summary>
    public class GetUserCatalogsResponse
    {
        public List<UserCatalogDto> Catalogs { get; set; } = new();
        /// <summary>Per-user catalog limit enforced by the server.</summary>
        public int Limit { get; set; } = 5;
    }

    /// <summary>Wire DTO for a single user catalog.</summary>
    public class UserCatalogDto
    {
        public string Id            { get; set; } = string.Empty;
        public string Service       { get; set; } = string.Empty;
        public string RssUrl        { get; set; } = string.Empty;
        public string DisplayName   { get; set; } = string.Empty;
        public string? LastSyncedAt { get; set; }
        public string? LastSyncStatus { get; set; }
    }

    /// <summary>Request for <c>POST /InfiniteDrive/User/Catalogs/Add</c>.</summary>
    [Route("/InfiniteDrive/User/Catalogs/Add", "POST",
        Summary = "Adds a public Trakt or MDBList RSS feed as a user catalog")]
    public class AddUserCatalogRequest : IReturn<AddUserCatalogResponse>
    {
        [ApiMember(Name = "rssUrl", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string RssUrl { get; set; } = string.Empty;

        [ApiMember(Name = "displayName", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? DisplayName { get; set; }
    }

    /// <summary>Response from <c>POST /InfiniteDrive/User/Catalogs/Add</c>.</summary>
    public class AddUserCatalogResponse
    {
        public bool   Ok          { get; set; }
        public string? CatalogId  { get; set; }
        public int    Fetched     { get; set; }
        public int    Added       { get; set; }
        public int    Updated     { get; set; }
        public long   ElapsedMs   { get; set; }
        public string? Error      { get; set; }
    }

    /// <summary>Request for <c>POST /InfiniteDrive/User/Catalogs/Remove</c>.</summary>
    [Route("/InfiniteDrive/User/Catalogs/Remove", "POST",
        Summary = "Soft-deletes a user catalog (sets active=0)")]
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
        Summary = "Synchronously refreshes one or all of the caller's RSS catalogs")]
    public class RefreshUserCatalogsRequest : IReturn<RefreshUserCatalogsResponse>
    {
        /// <summary>Optional catalog ID. When absent, all active catalogs are refreshed.</summary>
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

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// REST API for user-owned public RSS catalogs (Trakt / MDBList).
    /// All endpoints require an authenticated (non-admin) user.
    /// Sprint 158.
    /// </summary>
    public class UserCatalogsService : IService, IRequiresRequest
    {
        private const int UserCatalogLimit = 5;

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
                Plugin.Instance.CooldownGate);
        }

        // ── GET /InfiniteDrive/User/Catalogs ────────────────────────────────────────

        public async Task<object> Get(GetUserCatalogsRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return new GetUserCatalogsResponse();

            var catalogs = await _db.GetUserCatalogsByOwnerAsync(userId, activeOnly: true);
            return new GetUserCatalogsResponse
            {
                Limit    = UserCatalogLimit,
                Catalogs = catalogs.Select(c => new UserCatalogDto
                {
                    Id             = c.Id,
                    Service        = c.Service,
                    RssUrl         = c.RssUrl,
                    DisplayName    = c.DisplayName,
                    LastSyncedAt   = c.LastSyncedAt,
                    LastSyncStatus = c.LastSyncStatus,
                }).ToList(),
            };
        }

        // ── POST /InfiniteDrive/User/Catalogs/Add ───────────────────────────────────

        public async Task<object> Post(AddUserCatalogRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Err<AddUserCatalogResponse>("Not authenticated");

            // Validate URL
            if (string.IsNullOrWhiteSpace(req.RssUrl))
                return Err<AddUserCatalogResponse>("rssUrl is required");

            if (!req.RssUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Err<AddUserCatalogResponse>("RSS URL must use HTTPS");

            string service;
            try { service = RssFeedParser.DetectService(req.RssUrl); }
            catch (ArgumentException ex)
            {
                return Err<AddUserCatalogResponse>(ex.Message);
            }

            // Enforce per-user catalog limit
            var existing = await _db.GetUserCatalogsByOwnerAsync(userId, activeOnly: true);
            if (existing.Count >= UserCatalogLimit)
            {
                return new AddUserCatalogResponse
                {
                    Ok    = false,
                    Error = $"Catalog limit reached. You can have at most {UserCatalogLimit} lists. Remove one to add another."
                };
            }

            // Fetch feed once to validate it has items and to get display name
            string xml;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var resp = await http.GetAsync(req.RssUrl);
                resp.EnsureSuccessStatusCode();
                xml = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return Err<AddUserCatalogResponse>($"Could not fetch feed: {ex.Message}");
            }

            var items = RssFeedParser.Parse(xml, _logger, out var feedTitle, out _);
            if (items.Count == 0)
                return Err<AddUserCatalogResponse>("Feed appears to be empty or contains no recognisable IMDb IDs");

            var displayName = !string.IsNullOrWhiteSpace(req.DisplayName)
                ? req.DisplayName
                : (!string.IsNullOrWhiteSpace(feedTitle) ? feedTitle : req.RssUrl);

            // Insert catalog row
            var catalogId = await _db.CreateUserCatalogAsync(
                userId, service, req.RssUrl, displayName!);

            // Eager sync
            var ct = CancellationToken.None;
            var result = await _syncService.SyncOneAsync(catalogId, ct);

            return new AddUserCatalogResponse
            {
                Ok        = result.Ok,
                CatalogId = catalogId,
                Fetched   = result.Fetched,
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
                return Err<RemoveUserCatalogResponse>("Catalog not found");

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
                // Single-catalog refresh
                var catalog = await _db.GetUserCatalogByIdAsync(req.CatalogId);
                if (catalog == null)
                    return new RefreshUserCatalogsResponse { Ok = false, Error = "Catalog not found" };

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

            // All-catalogs refresh
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
            catch { return null; }
        }

        private static T Err<T>(string message) where T : class, new()
        {
            // Set Ok=false and Error on any response DTO that has these properties
            var resp = new T();
            var okProp = typeof(T).GetProperty("Ok");
            var errProp = typeof(T).GetProperty("Error");
            okProp?.SetValue(resp, false);
            errProp?.SetValue(resp, message);
            return resp;
        }
    }
}
