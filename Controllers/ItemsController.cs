using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Controllers
{
    /// <summary>
    /// API endpoints for item queries.
    /// </summary>
    [Route("embystreams/items")]
    public class ItemsController : IService, IRequiresRequest
    {
        private readonly DatabaseManager _db;
        private readonly ILogger<ItemsController> _logger;

        public ItemsController(DatabaseManager db, ILogger<ItemsController> logger)
        {
            _db = db;
            _logger = logger;
        }

        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Lists items with pagination.
        /// GET /embystreams/items
        /// </summary>
        [Route("")]
        public async Task<ItemListResponse> Get(ItemListRequest request, CancellationToken ct)
        {
            _logger.LogDebug("[ItemsController] List items request: Status={Status}, Limit={Limit}, Offset={Offset}",
                request.Status, request.Limit, request.Offset);

            var items = await _db.GetItemsAsync(
                request.Status,
                request.OrderBy,
                request.OrderDirection,
                request.Limit,
                request.Offset,
                ct);

            var total = await _db.GetItemCountAsync(request.Status, ct);

            return new ItemListResponse
            {
                Items = items,
                Total = total,
                Limit = request.Limit,
                Offset = request.Offset
            };
        }

        /// <summary>
        /// Gets a single item by ID.
        /// GET /embystreams/items/{id}
        /// </summary>
        [Route("{id}")]
        public async Task<MediaItem?> Get(string id, CancellationToken ct)
        {
            _logger.LogDebug("[ItemsController] Get item request for {ItemId}", id);
            // CRITICAL: MediaItem.Id is string TEXT UUID, not int
            return await _db.GetMediaItemAsync(id, ct);
        }

        /// <summary>
        /// Searches items by query string.
        /// POST /embystreams/items/search
        /// </summary>
        [Route("search")]
        public async Task<List<MediaItem>> Post(SearchRequest request, CancellationToken ct)
        {
            _logger.LogDebug("[ItemsController] Search items request: Query={Query}", request.Query);
            return await _db.SearchItemsAsync(request.Query, ct);
        }
    }

    /// <summary>
    /// Request DTO for listing items.
    /// </summary>
    public class ItemListRequest
    {
        public ItemStatus? Status { get; set; }
        public string OrderBy { get; set; } = "title";
        public string OrderDirection { get; set; } = "asc";
        public int Limit { get; set; } = 50;
        public int Offset { get; set; } = 0;
    }

    /// <summary>
    /// Response DTO for listing items.
    /// </summary>
    public class ItemListResponse
    {
        public List<MediaItem> Items { get; set; } = new();
        public int Total { get; set; }
        public int Limit { get; set; }
        public int Offset { get; set; }
    }

    /// <summary>
    /// Request DTO for searching items.
    /// </summary>
    public class SearchRequest
    {
        public string Query { get; set; } = string.Empty;
    }
}
