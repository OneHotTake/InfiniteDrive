using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Services
{
    // ── Request / Response DTOs ──────────────────────────────────────────────────

    [Route("/InfiniteDrive/Admin/SearchItems", "GET",
        Summary = "Search InfiniteDrive catalog by title for blocking")]
    public class SearchItemsRequest : IReturn<SearchItemsResponse>
    {
        [ApiMember(Name = "q")] public string Query { get; set; } = "";
        [ApiMember(Name = "limit")] public int Limit { get; set; } = 5;
    }

    public class SearchItemDto
    {
        public string Id { get; set; } = string.Empty;        // internal UUID from media_items
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
        public string MediaType { get; set; } = string.Empty; // "movie" | "series" | "anime"
        public string? DisplayExternalId { get; set; }      // best available external ID for display
        public string? DisplayExternalIdType { get; set; } // type of the displayed external ID
    }

    public class SearchItemsResponse
    {
        public List<SearchItemDto> Items { get; set; } = new();
    }

    [Route("/InfiniteDrive/Admin/BlockedItems", "GET",
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

    [Route("/InfiniteDrive/Admin/UnblockItems", "POST",
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

    [Route("/InfiniteDrive/Admin/BlockItems", "POST",
        Summary = "Blocks items by internal ID or IMDB ID: deletes .strm/.nfo, clears user saves, triggers scan")]
    public class BlockItemsRequest : IReturn<BlockItemsResponse>
    {
        public List<string>? ItemIds { get; set; }   // internal UUIDs (preferred)
        public List<string>? ImdbIds { get; set; }   // legacy, still supported
    }

    public class BlockItemsResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    [Route("/InfiniteDrive/Admin/ClearSentinel", "POST",
        Summary = "Clear no_streams sentinel for an item, allowing fresh resolution")]
    public class ClearSentinelRequest : IReturn<ClearSentinelResponse>
    {
        [ApiMember(Name = "imdbId", Description = "IMDb ID to clear", DataType = "string", ParameterType = "query")]
        public string ImdbId { get; set; } = "";
    }

    public class ClearSentinelResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
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
        private readonly ILibraryManager _libraryManager;

        public IRequest Request { get; set; } = null!;

        public AdminService(ILogManager logManager, IAuthorizationContext authCtx, ILibraryManager libraryManager)
        {
            _logger  = new EmbyLoggerAdapter<AdminService>(logManager.GetLogger("InfiniteDrive"));
            _db      = Plugin.Instance.DatabaseManager;
            _authCtx = authCtx;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Admin/BlockedItems</c>.
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
        /// Handles <c>GET /InfiniteDrive/Admin/SearchItems</c>.
        /// Searches local media_items catalog by title for blocking UI.
        /// Only returns non-blocked InfiniteDrive-managed items.
        /// </summary>
        public async Task<object> Get(SearchItemsRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                var items = await _db.SearchMediaItemsByTitleAsync(req.Query, req.Limit, CancellationToken.None);

                return new SearchItemsResponse
                {
                    Items = items.Select(i => new SearchItemDto
                    {
                        Id                  = i.Id,
                        Title               = i.Title,
                        Year                = i.Year,
                        MediaType           = i.MediaType,
                        DisplayExternalId    = i.PrimaryId.Value,
                        DisplayExternalIdType = i.PrimaryId.Type.ToString().ToLowerInvariant()
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AdminService] Failed to search items");
                return new SearchItemsResponse { Items = new() };
            }
        }

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/Admin/UnblockItems</c>.
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

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/Admin/BlockItems</c>.
        /// Blocks items by internal ID or IMDB ID: sets blocked flag, deletes .strm/.nfo, clears user saves, triggers scan.
        /// Supports both ItemIds (internal UUIDs, preferred) and ImdbIds (legacy).
        /// </summary>
        public async Task<object> Post(BlockItemsRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var errors = new List<string>();
            var count = 0;
            var ct = CancellationToken.None;

            try
            {
                // Prefer ItemIds (internal UUIDs), fall back to ImdbIds (legacy)
                List<string> itemIdsToBlock = new();
                if (req.ItemIds != null && req.ItemIds.Count > 0)
                {
                    itemIdsToBlock = req.ItemIds;
                }
                else if (req.ImdbIds != null && req.ImdbIds.Count > 0)
                {
                    // Legacy path: resolve ImdbIds to internal IDs first
                    foreach (var imdbId in req.ImdbIds)
                    {
                        var mediaItem = await _db.GetMediaItemByPrimaryIdAsync(imdbId, ct);
                        if (mediaItem != null)
                            itemIdsToBlock.Add(mediaItem.Id);
                        else
                            errors.Add($"{imdbId}: Item not found in catalog");
                    }
                }
                else
                {
                    return new BlockItemsResponse { Success = false, Count = 0, Errors = errors };
                }

                foreach (var itemId in itemIdsToBlock)
                {
                    try
                    {
                        // 1. Get media item by internal UUID
                        var mediaItem = await _db.GetMediaItemByIdAsync(itemId, ct);
                        if (mediaItem == null)
                        {
                            errors.Add($"{itemId}: Media item not found");
                            continue;
                        }

                        // 2. Block in catalog_items using primary ID (IMDB value from PrimaryId)
                        await _db.BlockCatalogItemByImdbIdAsync(mediaItem.PrimaryId.Value, "admin", ct);

                        // 3. Delete .strm/.nfo from catalog_item
                        var catalogItem = await _db.GetCatalogItemByImdbIdAsync(mediaItem.PrimaryId.Value);
                        if (catalogItem != null)
                        {
                            DeleteFileIfExists(catalogItem.StrmPath, ".strm");
                            DeleteFileIfExists(
                                !string.IsNullOrEmpty(catalogItem.StrmPath)
                                    ? Path.ChangeExtension(catalogItem.StrmPath, ".nfo")
                                    : null, ".nfo");
                        }

                        // 4. Block in media_items + clear user saves
                        mediaItem.Blocked = true;
                        mediaItem.BlockedAt = DateTimeOffset.UtcNow;
                        mediaItem.UpdatedAt = DateTimeOffset.UtcNow;
                        await _db.UpsertMediaItemAsync(mediaItem, ct);

                        await _db.DeleteAllUserSavesForItemAsync(mediaItem.Id, ct);
                        await _db.SyncGlobalSavedFlagAsync(mediaItem.Id, ct);

                        count++;
                        _logger.LogInformation("[AdminService] Blocked item {ItemId}", itemId);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{itemId}: {ex.Message}");
                        _logger.LogError(ex, "[AdminService] Failed to block item {ItemId}", itemId);
                    }
                }

                // 5. Trigger library scan (fire-and-forget)
                _ = TriggerLibraryScanAsync();

                return new BlockItemsResponse
                {
                    Success = errors.Count == 0,
                    Count = count,
                    Errors = errors
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AdminService] Failed to block items");
                return new BlockItemsResponse { Success = false, Count = count, Errors = errors };
            }
        }

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/Admin/ClearSentinel</c>.
        /// Deletes failed resolution cache entries for the given IMDb ID,
        /// allowing fresh resolution on next playback attempt.
        /// Sprint 311: Clear no_streams sentinel.
        /// </summary>
        public async Task<object> Post(ClearSentinelRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            if (string.IsNullOrWhiteSpace(req.ImdbId))
                return new ClearSentinelResponse { Success = false, Message = "imdbId is required" };

            try
            {
                var deleted = await _db.ClearFailedSentinelAsync(req.ImdbId);

                if (deleted == 0)
                    return new ClearSentinelResponse { Success = false, Message = "No failed sentinel found for " + req.ImdbId };

                _logger.LogInformation("[AdminService] Cleared failed sentinel for {ImdbId}", req.ImdbId);
                return new ClearSentinelResponse { Success = true, Message = $"Cleared {deleted} failed sentinel(s)" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AdminService] Failed to clear sentinel for {ImdbId}", req.ImdbId);
                return new ClearSentinelResponse { Success = false, Message = ex.Message };
            }
        }

        private void DeleteFileIfExists(string? path, string label)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            File.Delete(path);
            _logger.LogInformation("[AdminService] Deleted {Label}: {Path}", label, path);
        }

        private async Task TriggerLibraryScanAsync()
        {
            try
            {
                _logger.LogInformation("[AdminService] Triggering Emby library scan");
                await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AdminService] Failed to trigger library scan");
            }
        }
    }
}
