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
    /// API endpoints for removal operations.
    /// </summary>
    [Route("embystreams/removal")]
    public class RemovalController : IService, IRequiresRequest
    {
        private readonly RemovalService _service;
        private readonly RemovalPipeline _pipeline;
        private readonly DatabaseManager _db;
        private readonly ILogger<RemovalController> _logger;

        public RemovalController(
            RemovalService service,
            RemovalPipeline pipeline,
            DatabaseManager db,
            ILogger<RemovalController> logger)
        {
            _service = service;
            _pipeline = pipeline;
            _db = db;
            _logger = logger;
        }

        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Marks an item for removal (starts grace period).
        /// POST /embystreams/removal/mark
        /// </summary>
        [Route("mark")]
        public async Task<RemovalResult> Post(MarkForRemovalRequest request)
        {
            _logger.LogInformation("[RemovalController] Mark for removal request for {ItemId}", request.ItemId);
            return await _service.MarkForRemovalAsync(request.ItemId, CancellationToken.None);
        }

        /// <summary>
        /// Removes an item (if grace period has expired).
        /// POST /embystreams/removal/remove
        /// </summary>
        [Route("remove")]
        public async Task<RemovalResult> Post(RemoveRequest request)
        {
            _logger.LogInformation("[RemovalController] Remove request for {ItemId}", request.ItemId);
            return await _service.RemoveItemAsync(request.ItemId, CancellationToken.None);
        }

        /// <summary>
        /// Processes all expired grace period items.
        /// POST /embystreams/removal/process
        /// </summary>
        [Route("process")]
        public async Task<RemovalPipelineResult> Post(ProcessRemovalRequest request)
        {
            _logger.LogInformation("[RemovalController] Process removal pipeline request");
            return await _pipeline.ProcessExpiredGraceItemsAsync(CancellationToken.None);
        }

        /// <summary>
        /// Lists all items with active grace periods.
        /// GET /embystreams/removal/list
        /// </summary>
        [Route("list")]
        public async Task<RemovalListResponse> Get(RemovalListRequest request)
        {
            _logger.LogDebug("[RemovalController] List grace period items request");
            var graceItems = await _db.GetItemsByGraceStartedAsync(CancellationToken.None);
            return new RemovalListResponse { Items = graceItems };
        }
    }
}

/// <summary>
/// Request DTOs for Removal endpoints.
/// </summary>
public record MarkForRemovalRequest(string ItemId);
public record RemoveRequest(string ItemId);
public record ProcessRemovalRequest;
public record RemovalListRequest;

/// <summary>
/// Response DTO for listing grace period items.
/// </summary>
public class RemovalListResponse
{
    public List<MediaItem> Items { get; set; } = new();
}
