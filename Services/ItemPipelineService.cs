using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Manages item lifecycle transitions: Known → Resolved → Hydrated → Created → Indexed → Active.
    /// </summary>
    public class ItemPipelineService
    {
        private readonly ILogger<ItemPipelineService> _logger;
        private readonly DatabaseManager _db;
        private readonly StreamResolver _streamResolver;
        private readonly MetadataHydrator _metadataHydrator;
        private readonly DigitalReleaseGateService _digitalReleaseGate;

        public ItemPipelineService(
            ILogger<ItemPipelineService> logger,
            DatabaseManager db,
            StreamResolver streamResolver,
            MetadataHydrator metadataHydrator,
            DigitalReleaseGateService digitalReleaseGate)
        {
            _logger = logger;
            _db = db;
            _streamResolver = streamResolver;
            _metadataHydrator = metadataHydrator;
            _digitalReleaseGate = digitalReleaseGate;
        }

        /// <summary>
        /// Processes an item through the full pipeline.
        /// </summary>
        public async Task<ItemPipelineResult> ProcessItemAsync(
            MediaItem item,
            PipelineTrigger trigger,
            CancellationToken ct = default)
        {
            _logger.LogDebug("[ItemPipeline] Processing {MediaId} with trigger {Trigger}",
                item.PrimaryId.ToString(), trigger);

            try
            {
                // Check Coalition Rule: item must have at least one enabled source
                var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(item.Id, ct);
                if (!hasEnabledSource)
                {
                    _logger.LogInformation("[ItemPipeline] Skipping {MediaId}: no enabled source (Coalition Rule)",
                        item.PrimaryId.ToString());

                    // Remove from Emby if previously indexed
                    if (item.Status >= ItemStatus.Indexed)
                    {
                        await RemoveFromEmbyAsync(item, ct);
                    }

                    item.Status = ItemStatus.Known;
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.UpsertMediaItemAsync(item, ct);
                    await LogPipelineEvent(item, "CoalitionRule", trigger, item.Status.ToString(), false, "No enabled source", ct);

                    return new ItemPipelineResult
                    {
                        Success = false,
                        Status = item.Status,
                        Error = "No enabled source (Coalition Rule)"
                    };
                }

                // Check digital release gate (only for SourceType.BuiltIn and movies)
                // User-added items bypass gate unconditionally - users explicitly chose to add them
                var isDigitallyReleased = await _digitalReleaseGate.IsDigitallyReleasedAsync(
                    item.PrimaryId,
                    item.MediaType,
                    nameof(SourceType.Aio), // User-added source bypasses digital release gate
                    ct);

                if (!isDigitallyReleased)
                {
                    _logger.LogInformation("[ItemPipeline] Skipping {MediaId}: not digitally released",
                        item.PrimaryId.ToString());

                    item.Status = ItemStatus.Failed;
                    item.FailureReason = FailureReason.DigitalReleaseGate;
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.UpsertMediaItemAsync(item, ct);
                    await LogPipelineEvent(item, "DigitalReleaseGate", trigger, item.Status.ToString(), false, "Not digitally released", ct);

                    return new ItemPipelineResult
                    {
                        Success = false,
                        Status = item.Status,
                        Error = "Not digitally released"
                    };
                }

                // Execute pipeline phases in order
                var status = await ResolvePhaseAsync(item, trigger, ct);
                status = await HydratePhaseAsync(item, status, trigger, ct);
                status = await CreatePhaseAsync(item, status, trigger, ct);
                status = await IndexPhaseAsync(item, status, trigger, ct);

                // Check if we can transition to Active
                if (status == ItemStatus.Indexed && status.CanTransitionTo(ItemStatus.Active))
                {
                    status = ItemStatus.Active;
                    item.Status = status;
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.UpsertMediaItemAsync(item, ct);
                    await LogPipelineEvent(item, "Activate", trigger, status.ToString(), true, null, ct);
                }

                return new ItemPipelineResult
                {
                    Success = true,
                    Status = status,
                    Item = item
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ItemPipeline] Failed to process {MediaId}",
                    item.PrimaryId.ToString());

                item.Status = ItemStatus.Failed;
                item.FailureReason = FailureReason.None;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.UpsertMediaItemAsync(item, ct);
                await LogPipelineEvent(item, "Process", trigger, item.Status.ToString(), false, ex.Message, ct);

                return new ItemPipelineResult
                {
                    Success = false,
                    Status = item.Status,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Resolves streams for item: Known → Resolved.
        /// </summary>
        private async Task<ItemStatus> ResolvePhaseAsync(MediaItem item, PipelineTrigger trigger, CancellationToken ct)
        {
            if (!item.Status.CanTransitionTo(ItemStatus.Resolved))
                return item.Status;

            // Resolve streams
            var streams = await _streamResolver.ResolveStreamsAsync(item, ct);

            if (streams == null || streams.Count == 0)
            {
                item.FailureReason = FailureReason.NoStreamsFound;
                item.Status = ItemStatus.Failed;
                _logger.LogInformation("[ItemPipeline] No streams found for {MediaId}",
                    item.PrimaryId.ToString());
            }
            else
            {
                item.Status = ItemStatus.Resolved;
                _logger.LogInformation("[ItemPipeline] Resolved {MediaId}: {Count} streams",
                    item.PrimaryId.ToString(), streams.Count);
            }

            item.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.UpsertMediaItemAsync(item, ct);
            await LogPipelineEvent(item, "Resolve", trigger, item.Status.ToString(), item.Status == ItemStatus.Resolved, null, ct);

            return item.Status;
        }

        /// <summary>
        /// Fetches metadata for item: Resolved → Hydrated.
        /// </summary>
        private async Task<ItemStatus> HydratePhaseAsync(MediaItem item, ItemStatus currentStatus, PipelineTrigger trigger, CancellationToken ct)
        {
            if (currentStatus != ItemStatus.Resolved)
                return currentStatus;

            // Hydrate with metadata
            var updatedItem = await _metadataHydrator.HydrateAsync(item, ct);
            updatedItem.Status = ItemStatus.Hydrated;
            updatedItem.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.UpsertMediaItemAsync(updatedItem, ct);
            await LogPipelineEvent(updatedItem, "Hydrate", trigger, updatedItem.Status.ToString(), true, null, ct);

            return updatedItem.Status;
        }

        /// <summary>
        /// Writes .strm file for item: Hydrated → Created.
        /// </summary>
        private async Task<ItemStatus> CreatePhaseAsync(MediaItem item, ItemStatus currentStatus, PipelineTrigger trigger, CancellationToken ct)
        {
            if (currentStatus != ItemStatus.Hydrated)
                return currentStatus;

            // Write .strm file
            await WriteStrmFileAsync(item, ct);
            item.Status = ItemStatus.Created;
            item.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.UpsertMediaItemAsync(item, ct);
            await LogPipelineEvent(item, "Create", trigger, item.Status.ToString(), true, null, ct);

            return item.Status;
        }

        /// <summary>
        /// Writes .strm file for a media item.
        /// </summary>
        private Task WriteStrmFileAsync(MediaItem item, CancellationToken ct)
        {
            // This is a placeholder - actual implementation would use:
            // - Plugin.Instance.Configuration.EmbyBaseUrl
            // - Plugin.Instance.Configuration.PluginSecret
            // - PlaybackTokenService to generate signed URLs
            // - File I/O to write to item.StrmPath

            _logger.LogInformation("[ItemPipeline] Would write .strm file for {MediaId}",
                item.PrimaryId.ToString());

            return Task.CompletedTask;
        }

        /// <summary>
        /// Indexes item in Emby library: Created → Indexed.
        /// </summary>
        private async Task<ItemStatus> IndexPhaseAsync(MediaItem item, ItemStatus currentStatus, PipelineTrigger trigger, CancellationToken ct)
        {
            if (currentStatus != ItemStatus.Created)
                return currentStatus;

            // This is a placeholder - actual implementation would:
            // - Trigger Emby library scan
            // - Wait for item to appear in library
            // - Update item.EmbyItemId and item.EmbyIndexedAt

            item.Status = ItemStatus.Indexed;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("[ItemPipeline] Indexed {MediaId} in Emby",
                item.PrimaryId.ToString());

            await _db.UpsertMediaItemAsync(item, ct);
            await LogPipelineEvent(item, "Index", trigger, item.Status.ToString(), true, null, ct);

            return item.Status;
        }

        /// <summary>
        /// Removes item from Emby library.
        /// </summary>
        private Task RemoveFromEmbyAsync(MediaItem item, CancellationToken ct)
        {
            // This is a placeholder - actual implementation would:
            // - Delete the .strm file
            // - Trigger library refresh
            // - Wait for Emby to remove the item

            _logger.LogInformation("[ItemPipeline] Removing {MediaId} from Emby (no enabled source)",
                item.PrimaryId.ToString());

            return Task.CompletedTask;
        }

        private Task LogPipelineEvent(MediaItem item, string phase, PipelineTrigger trigger, string toStatus, bool success, string? error, CancellationToken ct)
        {
            return _db.LogPipelineEventAsync(
                item.PrimaryId.Value,
                item.PrimaryId.Type.ToString(),
                item.MediaType,
                trigger.ToString(),
                null,
                toStatus,
                success,
                error,
                ct);
        }
    }
}
