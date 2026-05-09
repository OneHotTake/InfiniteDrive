using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Shared stream resolution helper used by PlaybackService, SignedStreamService, and DiscoverService.
    /// Extracts the SyncResolveAsync logic so it can be called from multiple services.
    /// </summary>
    public static class StreamResolutionHelper
    {
        private const int FallbackSyncResolveTimeoutMs = 30_000;

        /// <summary>
        /// Synchronous stream resolution with provider fallback.
        /// This is extracted from PlaybackService.SyncResolveAsync for shared use.
        /// Writes both ranked candidates and the primary ResolutionEntry to the database.
        /// </summary>
        public static async Task<ResolutionResult> SyncResolveViaProvidersAsync(
            PlayRequest req,
            PluginConfiguration config,
            DatabaseManager db,
            ILogger logger,
            ResolverHealthTracker? healthTracker = null,
            CancellationToken cancellationToken = default)
        {
            // Use the larger of the user-configured floor and the timeout discovered
            // from the AIOStreams manifest (behaviorHints.requestTimeout).
            int configuredSecs = config.SyncResolveTimeoutSeconds > 0
                ? config.SyncResolveTimeoutSeconds
                : FallbackSyncResolveTimeoutMs / 1000;
            int discoveredSecs = config.AioStreamsDiscoveredTimeoutSeconds > 0
                ? config.AioStreamsDiscoveredTimeoutSeconds
                : 0;
            // Cap at 60 s to prevent indefinite holds
            int timeoutMs = Math.Min(Math.Max(configuredSecs, discoveredSecs) * 1000, 60_000);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            // Build list of providers to try
            var providers = ProviderHelper.GetProviders(config);

            AioStreamsUnreachableException? lastUnreachable = null;

            foreach (var provider in providers)
            {
                var providerKey = provider.DisplayName ?? "unknown";

                // Sprint 310: Circuit breaker — skip providers with open circuits
                if (healthTracker?.ShouldSkip(providerKey) == true)
                {
                    logger.LogDebug("[InfiniteDrive] Skipping provider {Name} — circuit open", providerKey);
                    continue;
                }

                try
                {
                    logger.LogDebug("[InfiniteDrive] Trying provider {Name} for {AioId}",
                        providerKey, req.AioId);

                    using var client = AioStreamsClientFactory.CreateForProvider(provider, logger);
                    AioStreamsStreamResponse? response;

                    if (req.Season.HasValue && req.Episode.HasValue)
                        response = await client.GetSeriesStreamsAsync(
                            req.AioId, req.Season.Value, req.Episode.Value, cts.Token);
                    else
                        response = await client.GetMovieStreamsAsync(req.AioId, cts.Token);

                    if (response?.Streams == null || response.Streams.Count == 0)
                        continue;

                    // Build ranked candidates and write to stream_candidates
                    var candidates = StreamHelpers.RankAndFilterStreams(
                        response, req.AioId, req.Season, req.Episode,
                        config.ProviderPriorityOrder,
                        0, // unlimited — let SelectBest's bucket algorithm curate per tier
                        config.CacheLifetimeMinutes);

                    if (candidates.Count == 0) continue;

                    // Write resolution_cache + stream_candidates atomically
                    var entry = BuildEntryFromCandidates(candidates, req, config, "tier0");
                    try
                    {
                        await db.UpsertResolutionResultAsync(entry, candidates);
                        await db.IncrementApiCallCountAsync();
                    }
                    catch (Exception dbEx)
                    {
                        logger.LogError(dbEx, "[InfiniteDrive] Failed to cache resolution for {AioId} — resolution succeeded but not cached", req.AioId);
                    }

                    healthTracker?.RecordSuccess(providerKey);

                    // Sprint 311: Update active provider on failover
                    if (providerKey == "Secondary")
                    {
                        var state = Plugin.Instance?.ActiveProviderState;
                        if (state?.Current != Models.ActiveProvider.Secondary)
                        {
                            state!.Current = Models.ActiveProvider.Secondary;
                            logger.LogWarning("[Failover] Primary unavailable, switched to Secondary");

                            // Persist failover state for restart recovery
                            _ = System.Threading.Tasks.Task.Run(async () => {
                                try { await Plugin.Instance!.DatabaseManager.SetActiveProviderAsync("Secondary"); }
                                catch { /* best effort */ }
                            });
                        }
                    }

                    return new ResolutionResult { Status = ResolutionStatus.Success, StreamUrl = entry.StreamUrl, Entry = entry };
                }
                catch (AioStreamsUnreachableException ex)
                {
                    lastUnreachable = ex;
                    healthTracker?.RecordFailure(providerKey);
                    logger.LogDebug("[InfiniteDrive] Provider {Name} unreachable, trying next",
                        providerKey);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("[InfiniteDrive] Sync resolve timed out ({Timeout}s) for {AioId}",
                        timeoutMs / 1000, req.AioId);
                    return new ResolutionResult { Status = ResolutionStatus.ProviderDown };
                }
                catch (Exception ex)
                {
                    healthTracker?.RecordFailure(providerKey);
                    logger.LogError(ex, "[InfiniteDrive] Sync resolve failed for {AioId} with provider {Name}",
                        req.AioId, providerKey);
                }
            }

            // If we got here, all providers were either unreachable or returned no streams
            if (lastUnreachable != null)
                return new ResolutionResult { Status = ResolutionStatus.ProviderDown };

            return new ResolutionResult { Status = ResolutionStatus.ContentMissing };
        }

        /// <summary>
        /// Builds a ResolutionEntry from the primary (rank=0) candidate.
        /// Public for use by LinkResolverTask.
        /// </summary>
        public static ResolutionEntry BuildEntryFromCandidates(
            List<StreamCandidate> candidates,
            PlayRequest req,
            PluginConfiguration config,
            string tier)
        {
            var primary = candidates[0];
            var fb1 = candidates.Count > 1 ? candidates[1] : null;
            var fb2 = candidates.Count > 2 ? candidates[2] : null;

            return new ResolutionEntry
            {
                AioId = req.AioId,
                Season = req.Season,
                Episode = req.Episode,
                StreamUrl = primary.Url,
                QualityTier = primary.QualityTier,
                FileName = primary.FileName,
                FileSize = primary.FileSize,
                FileBitrateKbps = primary.BitrateKbps,
                Fallback1 = fb1?.Url,
                Fallback1Quality = fb1?.QualityTier,
                Fallback2 = fb2?.Url,
                Fallback2Quality = fb2?.QualityTier,
                TorrentHash = null,
                RdCached = primary.IsCached ? 1 : 0,
                ResolutionTier = tier,
                Status = "valid",
                ExpiresAt = primary.ExpiresAt,
            };
        }

        /// <summary>
        /// Check if a cached entry has expired.
        /// </summary>
        private static bool IsExpired(string? expiresAt)
        {
            if (string.IsNullOrEmpty(expiresAt))
                return true;
            if (!DateTime.TryParse(expiresAt, out var expiry))
                return true;
            return DateTime.UtcNow > expiry;
        }
    }
}

    /// <summary>
    /// Stream resolution request used by DiscoverService.
    /// </summary>
    public class PlayRequest
    {
        public string AioId { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }

