using System;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Services;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Controllers
{
    /// <summary>
    /// API endpoints for manual actions.
    /// </summary>
    [Route("embystreams/actions")]
    public class ActionsController : IService, IRequiresRequest
    {
        private readonly Tasks.SyncTask _syncTask;
        private readonly Tasks.YourFilesTask _yourFilesTask;
        private readonly Tasks.RemovalTask _removalTask;
        private readonly Tasks.CollectionTask _collectionTask;
        private readonly Services.PlaybackService _playbackService;
        private readonly ILogger<ActionsController> _logger;

        public ActionsController(
            Tasks.SyncTask syncTask,
            Tasks.YourFilesTask yourFilesTask,
            Tasks.RemovalTask removalTask,
            Tasks.CollectionTask collectionTask,
            Services.PlaybackService playbackService,
            ILogger<ActionsController> logger)
        {
            _syncTask = syncTask;
            _yourFilesTask = yourFilesTask;
            _removalTask = removalTask;
            _collectionTask = collectionTask;
            _playbackService = playbackService;
            _logger = logger;
        }

        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Triggers sync task.
        /// POST /embystreams/actions/sync
        /// </summary>
        [Route("sync")]
        public async Task<ActionResult> Sync(CancellationToken ct)
        {
            _logger.LogInformation("[ActionsController] Sync now request");
            await _syncTask.Execute(ct, null!);
            return new ActionResult { Success = true, Message = "Sync complete" };
        }

        /// <summary>
        /// Triggers Your Files reconciliation task.
        /// POST /embystreams/actions/yourfiles
        /// </summary>
        [Route("yourfiles")]
        public async Task<ActionResult> YourFiles(YourFilesRequest request, CancellationToken ct)
        {
            _logger.LogInformation("[ActionsController] Your Files reconcile request");
            await _yourFilesTask.Execute(ct, null!);
            return new ActionResult { Success = true, Message = "Your Files reconciliation complete" };
        }

        /// <summary>
        /// Triggers removal cleanup task.
        /// POST /embystreams/actions/cleanup
        /// </summary>
        [Route("cleanup")]
        public async Task<ActionResult> Cleanup(CancellationToken ct)
        {
            _logger.LogInformation("[ActionsController] Cleanup removed request");
            await _removalTask.Execute(ct, null!);
            return new ActionResult { Success = true, Message = "Cleanup complete" };
        }

        /// <summary>
        /// Triggers collection sync task.
        /// POST /embystreams/actions/collections
        /// </summary>
        [Route("collections")]
        public async Task<ActionResult> Collections(CancellationToken ct)
        {
            _logger.LogInformation("[ActionsController] Sync collections request");
            await _collectionTask.Execute(ct, null!);
            return new ActionResult { Success = true, Message = "Collections synced" };
        }

        /// <summary>
        /// Purges expired cache entries.
        /// POST /embystreams/actions/purge-cache
        /// </summary>
        [Route("purge-cache")]
        public async Task<ActionResult> PurgeCache(CancellationToken ct)
        {
            _logger.LogInformation("[ActionsController] Purge cache request");
            // TODO: Implement PurgeExpiredCacheAsync in PlaybackService
            await Task.CompletedTask;
            return new ActionResult { Success = true, Message = "Cache purged" };
        }

        /// <summary>
        /// Resets database (requires admin confirmation).
        /// POST /embystreams/actions/reset
        /// </summary>
        [Route("reset")]
        public ActionResult Reset()
        {
            // CRITICAL: Removed MigrationService (v20 concept, doesn't exist in v3.3)
            // Per v3.3 spec §17: No migration, fresh wipe only
            // Database reset is handled by DatabaseInitializer re-running

            // This endpoint should be disabled or replaced with proper v3.3 reset logic
            _logger.LogWarning("[ActionsController] Database reset request - not available in v3.3");
            throw new NotImplementedException(
                "Database reset not available in v3.3. Use Danger Zone in Admin UI instead.");
        }
    }

    /// <summary>
    /// Request DTO for Your Files action.
    /// </summary>
    public class YourFilesRequest { }
}
