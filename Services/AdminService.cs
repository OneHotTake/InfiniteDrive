using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace EmbyStreams.Services
{
    // ── Request / Response DTOs ──────────────────────────────────────────────────

    [Route("/EmbyStreams/Admin/BlockedItems", "GET",
        Summary = "Returns all admin-blocked catalog items")]
    public class GetBlockedItemsRequest : IReturn<GetBlockedItemsResponse> { }

    public class BlockedItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string? ImdbId { get; set; }
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string? BlockedAt { get; set; }
        public string? BlockedBy { get; set; }
        public int RetryCount { get; set; }
    }

    public class GetBlockedItemsResponse
    {
        public List<BlockedItemDto> Items { get; set; } = new();
    }

    [Route("/EmbyStreams/Admin/UnblockItems", "POST",
        Summary = "Unblocks selected items and re-queues them for enrichment")]
    public class UnblockItemsRequest : IReturn<UnblockItemsResponse>
    {
        public List<string> ItemIds { get; set; } = new();
    }

    public class UnblockItemsResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Admin-only endpoints for managing blocked catalog items.
    /// All endpoints require admin authentication via AdminGuard.RequireAdmin().
    /// </summary>
    public class AdminService : IService, IRequiresRequest
    {
        private readonly ILogger<AdminService> _logger;
        private readonly DatabaseManager _db;
        private readonly IAuthorizationContext _authCtx;

        public IRequest Request { get; set; } = null!;

        public AdminService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<AdminService>(logManager.GetLogger("EmbyStreams"));
            _db      = Plugin.Instance.DatabaseManager;
            _authCtx = authCtx;
        }

        /// <summary>
        /// Handles <c>GET /EmbyStreams/Admin/BlockedItems</c>.
        /// Returns all items with blocked_at IS NOT NULL.
        /// </summary>
        public async Task<object> Get(GetBlockedItemsRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                var items = await _db.GetBlockedItemsAsync(CancellationToken.None);

                return new GetBlockedItemsResponse
                {
                    Items = items.Select(i => new BlockedItemDto
                    {
                        Id        = i.Id,
                        ImdbId    = i.ImdbId,
                        Title     = i.Title,
                        Year      = i.Year,
                        MediaType = i.MediaType,
                        BlockedAt = i.BlockedAt,
                        BlockedBy = i.BlockedBy,
                        RetryCount = i.RetryCount
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AdminService] Failed to fetch blocked items");
                return new GetBlockedItemsResponse { Items = new() };
            }
        }

        /// <summary>
        /// Handles <c>POST /EmbyStreams/Admin/UnblockItems</c>.
        /// Clears blocked_at/blocked_by and resets nfo_status to NeedsEnrich.
        /// </summary>
        public async Task<object> Post(UnblockItemsRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            if (req.ItemIds == null || req.ItemIds.Count == 0)
                return new UnblockItemsResponse { Success = false, Count = 0 };

            try
            {
                foreach (var itemId in req.ItemIds)
                    await _db.UnblockItemAsync(itemId, CancellationToken.None);

                _logger.LogInformation("[AdminService] Unblocked {Count} items", req.ItemIds.Count);

                return new UnblockItemsResponse { Success = true, Count = req.ItemIds.Count };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AdminService] Failed to unblock items");
                return new UnblockItemsResponse { Success = false, Count = 0 };
            }
        }
    }
}
