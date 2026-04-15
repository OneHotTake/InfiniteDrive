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
            var providers = GetProvidersToTry(config);

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
                    logger.LogDebug("[InfiniteDrive] Trying provider {Name} for {Imdb}",
                        providerKey, req.Imdb);

                    using var client = new AioStreamsClient(
                        provider.Url, provider.Uuid, provider.Token, logger);
                    AioStreamsStreamResponse? response;

                    if (req.Season.HasValue && req.Episode.HasValue)
                        response = await client.GetSeriesStreamsAsync(
                            req.Imdb, req.Season.Value, req.Episode.Value, cts.Token);
                    else
                        response = await client.GetMovieStreamsAsync(req.Imdb, cts.Token);

                    if (response?.Streams == null || response.Streams.Count == 0)
                        continue;

                    // Build ranked candidates and write to stream_candidates
                    var candidates = StreamHelpers.RankAndFilterStreams(
                        response, req.Imdb, req.Season, req.Episode,
                        config.ProviderPriorityOrder,
                        config.CandidatesPerProvider,
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
                        logger.LogError(dbEx, "[InfiniteDrive] Failed to cache resolution for {Imdb} — resolution succeeded but not cached", req.Imdb);
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
                    logger.LogWarning("[InfiniteDrive] Sync resolve timed out ({Timeout}s) for {Imdb}",
                        timeoutMs / 1000, req.Imdb);
                    return new ResolutionResult { Status = ResolutionStatus.ProviderDown };
                }
                catch (Exception ex)
                {
                    healthTracker?.RecordFailure(providerKey);
                    logger.LogError(ex, "[InfiniteDrive] Sync resolve failed for {Imdb} with provider {Name}",
                        req.Imdb, providerKey);
                }
            }

            // If we got here, all providers were either unreachable or returned no streams
            if (lastUnreachable != null)
                return new ResolutionResult { Status = ResolutionStatus.ProviderDown };

            return new ResolutionResult { Status = ResolutionStatus.ContentMissing };
        }

        /// <summary>
        /// Cache-first stream URL resolution.
        /// Returns the stream URL from cache or resolves via providers if cache miss/stale.
        /// </summary>
        public static async Task<string?> GetStreamUrlAsync(
            string imdbId,
            int? season,
            int? episode,
            PluginConfiguration config,
            DatabaseManager db,
            ILogger logger,
            ResolverHealthTracker? healthTracker = null,
            CancellationToken cancellationToken = default)
        {
            // Step 1: Check cache first
            var cached = await db.GetCachedStreamAsync(imdbId, season, episode);
            var candidates = cached != null
                ? await db.GetStreamCandidatesAsync(imdbId, season, episode)
                : new List<StreamCandidate>();

            // Step 2: If valid cache hit, use rank 0 candidate
            string? streamUrl = null;
            if (cached?.Status == "valid" && !IsExpired(cached.ExpiresAt))
            {
                streamUrl = candidates.Count > 0 ? candidates[0].Url : cached.StreamUrl;
                logger.LogDebug("[StreamResolutionHelper] Cache HIT for {Imdb} S{Season}E{Episode}",
                    imdbId, season ?? 0, episode ?? 0);
                return streamUrl;
            }

            // Step 3: Cache miss or stale — sync resolve
            logger.LogDebug("[StreamResolutionHelper] Cache MISS for {Imdb} - resolving via providers", imdbId);

            var playReq = new PlayRequest
            {
                Imdb = imdbId,
                Season = season,
                Episode = episode
            };

            var resolved = await SyncResolveViaProvidersAsync(playReq, config, db, logger, healthTracker, cancellationToken);
            if (resolved.Status == ResolutionStatus.Success && resolved.Entry != null)
            {
                var resolvedCandidates = await db.GetStreamCandidatesAsync(imdbId, season, episode);
                streamUrl = resolvedCandidates.Count > 0 ? resolvedCandidates[0].Url : resolved.StreamUrl;
            }

            return streamUrl;
        }

        /// <summary>
        /// Builds the ordered list of AIOStreams providers to try for stream resolution.
        /// </summary>
        private static List<AioProvider> GetProvidersToTry(PluginConfiguration config)
        {
            var providers = new List<AioProvider>();

            if (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    providers.Add(new AioProvider
                    {
                        DisplayName = "Primary",
                        Url = url,
                        Uuid = uuid ?? string.Empty,
                        Token = token ?? string.Empty
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.SecondaryManifestUrl);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    providers.Add(new AioProvider
                    {
                        DisplayName = "Secondary",
                        Url = url,
                        Uuid = uuid ?? string.Empty,
                        Token = token ?? string.Empty
                    });
                }
            }

            return providers;
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
                ImdbId = req.Imdb,
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

        /// <summary>
        /// Simple provider holder for stream resolution attempts.
        /// </summary>
        private class AioProvider
        {
            public string DisplayName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Uuid { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
        }
    }
}

    /// <summary>
    /// Stream resolution request used by DiscoverService.
    /// </summary>
    public class PlayRequest
    {
        public string Imdb { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }

