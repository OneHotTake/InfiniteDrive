using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Input DTO for enrichment. Callers (MarvinTask, RefreshTask) map their items to this.
    /// </summary>
    public class EnrichmentRequest
    {
        public string Id { get; init; } = string.Empty;
        public string? ImdbId { get; init; }
        public string Title { get; init; } = string.Empty;
        public int? Year { get; init; }
        public int RetryCount { get; set; }
        public long? NextRetryAt { get; set; }
        /// <summary>
        /// Full CatalogItem for NFO writing. If null, service will look up by ImdbId.
        /// </summary>
        public CatalogItem? CatalogItem { get; init; }
    }

    /// <summary>
    /// Result of a batch enrichment run.
    /// </summary>
    public record EnrichmentResult(int EnrichedCount, int BlockedCount, int SkippedCount);

    /// <summary>
    /// Shared enrichment logic extracted from MarvinTask and RefreshTask.
    /// Retry schedule: 4h → 24h → block at 3 retries. 2s rate limit between API calls.
    /// </summary>
    public static class MetadataEnrichmentService
    {
        // Sentinel unix timestamp: year 2100 — effectively "never retry".
        public static readonly long NeverRetryUnixSeconds =
            new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        /// <summary>
        /// Enrich a batch of items using the provided fetch function.
        /// </summary>
        /// <param name="items">Items to enrich.</param>
        /// <param name="fetchFunc">
        /// Delegate that fetches metadata. Receives (request, ct), returns EnrichedMetadata or null.
        /// </param>
        /// <param name="db">Database manager for state updates.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task<EnrichmentResult> EnrichBatchAsync(
            List<EnrichmentRequest> items,
            Func<EnrichmentRequest, CancellationToken, Task<AioMetadataClient.EnrichedMetadata?>> fetchFunc,
            DatabaseManager db,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var enrichedCount = 0;
            var blockedCount = 0;
            var skippedCount = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ── Retry / backoff gate ──────────────────────────────────
                if (item.RetryCount >= 1 && item.NextRetryAt.HasValue)
                {
                    var retryTime = DateTimeOffset.FromUnixTimeSeconds(item.NextRetryAt.Value);
                    if (retryTime > DateTimeOffset.UtcNow)
                    {
                        skippedCount++;
                        continue;
                    }
                }

                if (item.RetryCount >= 3)
                {
                    blockedCount++;
                    continue;
                }

                try
                {
                    var meta = await fetchFunc(item, cancellationToken);

                    if (meta != null)
                    {
                        // ── Success ────────────────────────────────────
                        // NFO writing removed — metadata now served via IRemoteMetadataProvider
                        await db.SetNfoStatusAsync(item.Id, "Enriched", cancellationToken);
                        await db.UpdateItemRetryInfoAsync(item.Id, 0, null, cancellationToken);

                        enrichedCount++;
                        logger.LogDebug("[InfiniteDrive] Enriched metadata for {Imdb}", item.ImdbId ?? item.Title);
                    }
                    else
                    {
                        // ── Failure: retry / block ─────────────────────
                        item.RetryCount++;
                        var nextRetrySeconds = item.RetryCount switch
                        {
                            1 => DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds(),
                            2 => DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds(),
                            _ => NeverRetryUnixSeconds
                        };
                        item.NextRetryAt = nextRetrySeconds;

                        if (item.RetryCount >= 3)
                        {
                            await db.SetNfoStatusAsync(item.Id, "Blocked", cancellationToken);
                            blockedCount++;
                            logger.LogWarning("[InfiniteDrive] Enrichment blocked for {Id} after 3 retries", item.ImdbId ?? item.Title);
                        }
                        else
                        {
                            await db.UpdateItemRetryInfoAsync(item.Id, item.RetryCount, nextRetrySeconds, cancellationToken);
                        }

                        logger.LogDebug("[InfiniteDrive] Enrichment failed for {Id}, retry {Count}", item.ImdbId ?? item.Title, item.RetryCount);
                    }

                    // 2-second rate limit between API calls
                    await Task.Delay(2000, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex) when (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests"))
                {
                    logger.LogWarning("[InfiniteDrive] Enrich rate-limited (429). Stopping for this cycle.");
                    break;
                }
                catch (IOException ex)
                {
                    logger.LogError(ex, "[InfiniteDrive] Enrich I/O failure. Stopping enrichment.");
                    break;
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "[InfiniteDrive] Enrich bad metadata for {Id}, skipping.", item.ImdbId ?? item.Title);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[InfiniteDrive] Enrich failed for {Id}", item.ImdbId ?? item.Title);
                }
            }

            return new EnrichmentResult(enrichedCount, blockedCount, skippedCount);
        }
    }
}
