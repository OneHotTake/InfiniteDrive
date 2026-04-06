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
    /// API endpoints for collection management.
    /// </summary>
    [Route("embystreams/collections")]
    public class CollectionsController : IService, IRequiresRequest
    {
        private readonly CollectionSyncService _service;
        private readonly ILogger<CollectionsController> _logger;

        public CollectionsController(CollectionSyncService service, ILogger<CollectionsController> logger)
        {
            _service = service;
            _logger = logger;
        }

        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Lists all collections.
        /// GET /embystreams/collections
        /// </summary>
        [Route("")]
        public async Task<List<Collection>> Get(CancellationToken ct)
        {
            _logger.LogDebug("[CollectionsController] List collections request");
            return await Task.Run(async () =>
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return new List<Collection>();
                return await db.GetAllCollectionsListAsync(ct);
            }, ct);
        }

        /// <summary>
        /// Syncs a collection for a source.
        /// POST /embystreams/collections/{sourceId}/sync
        /// </summary>
        [Route("{sourceId}/sync")]
        public async Task<CollectionSyncResult> Post(string sourceId, CancellationToken ct)
        {
            _logger.LogInformation("[CollectionsController] Sync collection request for {SourceId}", sourceId);
            return await _service.SyncCollectionsAsync(ct);
        }
    }
}
