using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Pipeline for processing expired grace period items.
    /// Respects Coalition rule with single JOIN query.
    /// </summary>
    public class RemovalPipeline
    {
        private readonly RemovalService _service;
        private readonly DatabaseManager _db;
        private readonly ILogger<RemovalPipeline> _logger;

        // Grace period configuration
        private readonly TimeSpan _gracePeriod = TimeSpan.FromDays(7);

        public RemovalPipeline(
            RemovalService service,
            DatabaseManager db,
            ILogger<RemovalPipeline> logger)
        {
            _service = service;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Processes all items with expired grace periods.
        /// </summary>
        public async Task<RemovalPipelineResult> ProcessExpiredGraceItemsAsync(CancellationToken ct = default)
        {
            // Step 1: Get all items with active grace period
            var graceItems = await _db.GetItemsByGraceStartedAsync(ct);
            _logger.LogInformation("[RemovalPipeline] Found {Count} items in grace period", graceItems.Count);

            var results = new List<RemovalResult>();
            var removedCount = 0;
            var cancelledCount = 0;
            var extendedCount = 0;

            // Step 2: Process each grace period item
            foreach (var item in graceItems)
            {
                var result = await ProcessGraceItemAsync(item, ct);

                if (result.Message.Contains("removed"))
                    removedCount++;
                else if (result.Message.Contains("cancelled"))
                    cancelledCount++;
                else if (result.Message.Contains("active") || result.Message.Contains("until"))
                    extendedCount++;

                results.Add(result);
            }

            return new RemovalPipelineResult(
                graceItems.Count,
                removedCount,
                cancelledCount,
                extendedCount,
                results.Count(r => r.IsSuccess),
                results.Count(r => !r.IsSuccess),
                results
            );
        }

        /// <summary>
        /// Processes a single grace period item.
        /// </summary>
        private async Task<RemovalResult> ProcessGraceItemAsync(MediaItem item, CancellationToken ct)
        {
            // Check grace period expiration
            var graceStarted = item.GraceStartedAt ?? DateTimeOffset.MinValue;
            var graceEnd = graceStarted.Add(_gracePeriod);

            if (DateTimeOffset.UtcNow <= graceEnd)
            {
                // Grace period not expired, keep waiting
                _logger.LogDebug("[RemovalPipeline] Item {ItemId} grace period active until {Ends}", item.Id, graceEnd);
                return RemovalResult.Success($"Grace period active until {graceEnd}");
            }

            // Grace period expired, check coalition rule
            // CRITICAL: This MUST be a single JOIN query
            var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(item.Id, ct);

            // Check saved/blocked boolean columns (NOT status enum)
            if (hasEnabledSource || item.Saved || item.Blocked)
            {
                // Item should not be removed, cancel grace period
                item.GraceStartedAt = null;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.UpsertMediaItemAsync(item, ct);

                var reason = item.Saved ? "Saved" :
                              item.Blocked ? "Blocked" : "has enabled source";

                _logger.LogInformation("[RemovalPipeline] Item {ItemId} removal cancelled ({Reason}), grace cleared", item.Id, reason);
                return RemovalResult.Success($"Removal cancelled ({reason}): {item.Title}");
            }

            // Safe to remove
            return await _service.RemoveItemAsync(item.Id, ct);
        }
    }

    /// <summary>
    /// Summary of removal pipeline processing.
    /// </summary>
    public record RemovalPipelineResult(
        int TotalProcessed,
        int RemovedCount,
        int CancelledCount,
        int ExtendedCount,
        int SuccessCount,
        int FailureCount,
        List<RemovalResult> Results
    );
}
