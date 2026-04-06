using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using EmbyStreams.Services;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Controllers
{
    /// <summary>
    /// API endpoints for Save/Unsave/Block/Unblock actions.
    /// </summary>
    [Route("embystreams/saved")]
    public class SavedController : IService, IRequiresRequest
    {
        private readonly SavedService _service;
        private readonly ILogger<SavedController> _logger;

        public SavedController(SavedService service, ILogger<SavedController> logger)
        {
            _service = service;
            _logger = logger;
        }

        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Saves an item.
        /// POST /embystreams/saved/save
        /// </summary>
        [Route("save")]
        public async Task<ActionResult> Post(SaveRequest request)
        {
            _logger.LogInformation("[SavedController] Save request for {ItemId}", request.ItemId);
            await _service.SaveItemAsync(request.ItemId, CancellationToken.None);
            return new ActionResult { Success = true, Message = "Item saved" };
        }

        /// <summary>
        /// Unsaves an item.
        /// POST /embystreams/saved/unsave
        /// </summary>
        [Route("unsave")]
        public async Task<ActionResult> Post(UnsaveRequest request)
        {
            _logger.LogInformation("[SavedController] Unsave request for {ItemId}", request.ItemId);
            await _service.UnsaveItemAsync(request.ItemId, CancellationToken.None);
            return new ActionResult { Success = true, Message = "Item unsaved" };
        }

        /// <summary>
        /// Blocks an item.
        /// POST /embystreams/saved/block
        /// </summary>
        [Route("block")]
        public async Task<ActionResult> Post(BlockRequest request)
        {
            _logger.LogInformation("[SavedController] Block request for {ItemId}", request.ItemId);
            await _service.BlockItemAsync(request.ItemId, CancellationToken.None);
            return new ActionResult { Success = true, Message = "Item blocked" };
        }

        /// <summary>
        /// Unblocks an item.
        /// POST /embystreams/saved/unblock
        /// </summary>
        [Route("unblock")]
        public async Task<ActionResult> Post(UnblockRequest request)
        {
            _logger.LogInformation("[SavedController] Unblock request for {ItemId}", request.ItemId);
            await _service.UnblockItemAsync(request.ItemId, CancellationToken.None);
            return new ActionResult { Success = true, Message = "Item unblocked" };
        }

        /// <summary>
        /// Lists saved and blocked items.
        /// GET /embystreams/saved/list
        /// </summary>
        [Route("list")]
        public Task<SavedListResponse> Get(SavedListRequest request)
        {
            _logger.LogDebug("[SavedController] List request");
            // Note: Full implementation requires GetAllSavedAsync/GetAllBlockedAsync in SavedService
            return Task.FromResult(new SavedListResponse
            {
                Saved = new List<MediaItem>(),
                Blocked = new List<MediaItem>()
            });
        }
    }

    /// <summary>
    /// Action result DTO.
    /// </summary>
    public class ActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Saved list response DTO.
    /// </summary>
    public class SavedListResponse
    {
        public List<MediaItem> Saved { get; set; } = new();
        public List<MediaItem> Blocked { get; set; } = new();
    }
}

/// <summary>
/// Request DTOs for Saved endpoints.
/// </summary>
public record SaveRequest(string ItemId);
public record UnsaveRequest(string ItemId);
public record BlockRequest(string ItemId);
public record UnblockRequest(string ItemId);
public record SavedListRequest;
