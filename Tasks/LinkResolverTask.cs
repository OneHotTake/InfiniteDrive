using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Pre-resolves stream URLs into the <c>resolution_cache</c> table so that
    /// playback can be served in under 100ms from cache.
    ///
    /// Priority queue — processed in strict tier order, stops when budget is
    /// exhausted:
    /// <list type="number">
    ///   <item><b>Tier 0</b> — items flagged by PlaybackService on cache miss</item>
    ///   <item><b>Tier 1</b> — stale entries played in the last 24h (next-episode pre-warm)</item>
    ///   <item><b>Tier 2</b> — all items in the catalog added in the last 48h or from Trakt watchlist</item>
    ///   <item><b>Tier 3</b> — never resolved proactively (costs too much API quota)</item>
    /// </list>
    ///
    /// Rate limiting: max <see cref="PluginConfiguration.MaxConcurrentResolutions"/>
    /// concurrent calls, <see cref="CooldownGate"/> between calls,
    /// exponential back-off on HTTP 429.
    ///
    /// Default schedule: every 15 minutes.
    /// </summary>
    public class LinkResolverTask : IScheduledTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string TaskName     = "InfiniteDrive Link Resolver";
        private const string TaskKey      = "InfiniteDriveLinkResolver";
        private const string TaskCategory = "InfiniteDrive";

        // Max items to process per tier per run (safety cap per iteration)
        private const int MaxItemsPerTier = 200;

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<LinkResolverTask> _logger;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="loggerFactory"/> automatically.
        /// </summary>
        public LinkResolverTask(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<LinkResolverTask>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Pre-resolves stream URLs for upcoming media into the SQLite cache so playback starts instantly.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromMinutes(15).Ticks,
                }
            };
        }

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Sprint 100A-12: Startup jitter to prevent thundering herd on Emby restart
            await Task.Delay(Random.Shared.Next(0, 120_000), cancellationToken);

            _logger.LogInformation("[InfiniteDrive] LinkResolverTask started");
            progress.Report(0);

            var config = Plugin.Instance?.Configuration;
            var db     = Plugin.Instance?.DatabaseManager;
            if (config == null || db == null)
            {
                _logger.LogWarning("[InfiniteDrive] Plugin not initialised — aborting link resolution");
                return;
            }

            if (await db.IsBudgetExhaustedAsync())
            {
                _logger.LogWarning("[InfiniteDrive] API daily budget exhausted — skipping link resolution");
                progress.Report(100);
                return;
            }

            using var client    = new AioStreamsClient(config, _logger);
            client.Cooldown    = Plugin.Instance?.CooldownGate;
            var       semaphore = new SemaphoreSlim(config.MaxConcurrentResolutions, config.MaxConcurrentResolutions);
            var       stats     = new ResolverStats();

            // ── Process tiers in priority order ──────────────────────────────────

            await ProcessTierAsync(db, config, client, semaphore, "tier0",
                "Tier 0 (play-miss)", stats, cancellationToken, progress, 5, 35);

            if (!cancellationToken.IsCancellationRequested && !await db.IsBudgetExhaustedAsync())
            {
                await ProcessTierAsync(db, config, client, semaphore, "tier1",
                    "Tier 1 (next-episode)", stats, cancellationToken, progress, 35, 65);
            }

            if (!cancellationToken.IsCancellationRequested && !await db.IsBudgetExhaustedAsync())
            {
                await ProcessTierAsync(db, config, client, semaphore, "tier2",
                    "Tier 2 (watchlist)", stats, cancellationToken, progress, 65, 95);
            }

            // Tier 3 is NEVER resolved proactively (would exhaust API quota)

            _logger.LogInformation(
                "[InfiniteDrive] LinkResolverTask complete — {Resolved} resolved, {Skipped} skipped, {Failed} failed",
                stats.Resolved, stats.Skipped, stats.Failed);

            progress.Report(100);
        }

        // ── Private: tier processor ─────────────────────────────────────────────

        private async Task ProcessTierAsync(
            Data.DatabaseManager  db,
            PluginConfiguration   config,
            AioStreamsClient       client,
            SemaphoreSlim         semaphore,
            string                tier,
            string                tierLabel,
            ResolverStats         stats,
            CancellationToken     cancellationToken,
            IProgress<double>     progress,
            double                progressStart,
            double                progressEnd)
        {
            var items = await db.GetPendingResolutionsByTierAsync(tier, MaxItemsPerTier);
            if (items.Count == 0)
            {
                _logger.LogDebug("[InfiniteDrive] {Tier}: nothing to resolve", tierLabel);
                return;
            }

            _logger.LogInformation("[InfiniteDrive] {Tier}: resolving {Count} items", tierLabel, items.Count);

            // Also pull catalog items that have no cache entry yet for tier2
            List<CatalogItem> catalogItems = new List<CatalogItem>();
            if (tier == "tier2")
                catalogItems = await GetTier2CatalogItemsAsync(db, cancellationToken);

            var allWork = BuildWorkItems(items, catalogItems, tier);
            var tasks   = new List<Task>();

            for (int i = 0; i < allWork.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await db.IsBudgetExhaustedAsync())
                {
                    _logger.LogWarning("[InfiniteDrive] Budget exhausted during {Tier} — stopping", tierLabel);
                    break;
                }

                var work    = allWork[i];
                var pct     = progressStart + (progressEnd - progressStart) * i / allWork.Count;
                progress.Report(pct);

                await semaphore.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ResolveOneAsync(db, config, client, work, tier, stats, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }

        // ── Private: single item resolution ─────────────────────────────────────

        private async Task ResolveOneAsync(
            Data.DatabaseManager db,
            PluginConfiguration  config,
            AioStreamsClient      client,
            WorkItem             work,
            string               tier,
            ResolverStats        stats,
            CancellationToken    cancellationToken)
        {
            AioStreamsStreamResponse? response = null;
            var attempt    = 0;
            var wasRateLimited = false; // P4: track 429 exhaustion separately

            while (attempt < 3)
            {
                try
                {
                    if (work.Season.HasValue && work.Episode.HasValue)
                        response = await client.GetSeriesStreamsAsync(
                            work.ImdbId, work.Season.Value, work.Episode.Value, cancellationToken);
                    else
                        response = await client.GetMovieStreamsAsync(work.ImdbId, cancellationToken);

                    await db.IncrementApiCallCountAsync();
                    wasRateLimited = false; // succeeded — clear flag
                    break;
                }
                catch (AioStreamsRateLimitException)
                {
                    wasRateLimited = true;
                    var backoff = StreamHelpers.ExponentialBackoffMs(attempt);
                    _logger.LogWarning(
                        "[InfiniteDrive] 429 for {Imdb} — backing off {Ms}ms (attempt {N})",
                        work.ImdbId, backoff, attempt + 1);

                    await db.RecordRateLimitHitAsync(
                        DateTime.UtcNow.AddMilliseconds(backoff).ToString("o"));

                    await Task.Delay(backoff, cancellationToken);
                    attempt++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[InfiniteDrive] Failed to resolve {Imdb}", work.ImdbId);
                    break;
                }
            }

            // T2/P2 — distinguish network error (null response) from confirmed empty streams.
            // Network errors get full failed-with-backoff treatment.
            // Empty-streams results (item exists but has no available links) get a shorter
            // 1-hour cache entry so they are retried sooner without hammering the API.
            // P4 — rate-limited responses get an even shorter 5-minute cache TTL so the item
            // is retried promptly once the 429 window clears, rather than sitting out a full
            // 6-hour failure backoff.
            if (response == null)
            {
                if (wasRateLimited)
                {
                    _logger.LogWarning(
                        "[InfiniteDrive] Rate-limited (429) resolving {Imdb} — caching failed with 5min backoff",
                        work.ImdbId);
                    await db.UpsertResolutionCacheAsync(new ResolutionEntry
                    {
                        ImdbId         = work.ImdbId,
                        Season         = work.Season,
                        Episode        = work.Episode,
                        StreamUrl      = string.Empty,
                        Status         = "failed",
                        ResolutionTier = tier,
                        ExpiresAt      = DateTime.UtcNow.AddMinutes(5).ToString("o"),
                    });
                    stats.Failed++;
                    return;
                }
                _logger.LogWarning("[InfiniteDrive] Network/timeout error resolving {Imdb} — marking failed with 6h backoff", work.ImdbId);
                await MarkFailedWithBackoffAsync(db, work);
                stats.Failed++;
                return;
            }

            if (response.Streams == null || response.Streams.Count == 0)
            {
                // T2/P2: AIOStreams explicitly returned an empty streams array — the item
                // exists but has no available links right now.  Cache the empty result
                // with a 1-hour TTL (much shorter than the normal cache lifetime) so
                // we retry sooner without hammering the API every 15 minutes.
                // We use status='failed' with an empty stream_url as the sentinel;
                // PlaybackService distinguishes this from a hard failure at serve time.
                _logger.LogDebug("[InfiniteDrive] AIOStreams returned empty streams for {Imdb} — caching no-streams result for 1h", work.ImdbId);
                await db.UpsertResolutionCacheAsync(new ResolutionEntry
                {
                    ImdbId         = work.ImdbId,
                    Season         = work.Season,
                    Episode        = work.Episode,
                    StreamUrl      = string.Empty,
                    Status         = "failed",
                    ResolutionTier = tier,
                    ExpiresAt      = DateTime.UtcNow.AddHours(1).ToString("o"),
                });
                stats.Failed++;
                return;
            }

            // Build ranked candidates and primary ResolutionEntry
            var fakeReq = new PlayRequest { Imdb = work.ImdbId, Season = work.Season, Episode = work.Episode };
            var candidates = StreamHelpers.RankAndFilterStreams(
                response, work.ImdbId, work.Season, work.Episode,
                config.ProviderPriorityOrder,
                config.CandidatesPerProvider,
                config.CacheLifetimeMinutes);

            ResolutionEntry? entry = null;
            if (candidates.Count > 0)
            {
                entry = StreamResolutionHelper.BuildEntryFromCandidates(candidates, fakeReq, config, tier);
                // Preserve torrent hash from the original rank-0 stream for season-pack detection
                var primary = response.Streams?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
                entry.TorrentHash = primary?.InfoHash;
            }

            if (entry == null)
            {
                stats.Skipped++;
                return;
            }

            // Season pack detection: if AIOStreams signals a season pack via filename,
            // record the torrent hash for future bulk invalidation and queue all
            // other episodes of the season for Tier 1 background resolution.
            var filename = response.Streams?.FirstOrDefault()?.BehaviorHints?.Filename;
            if (StreamHelpers.IsSeasonPack(filename))
            {
                entry!.TorrentHash = response.Streams?.FirstOrDefault()?.InfoHash;
                _logger.LogDebug("[InfiniteDrive] Season pack detected for {Imdb} S{S} — hash={Hash}",
                    work.ImdbId, work.Season, entry.TorrentHash ?? "(none)");

                if (work.Season.HasValue)
                    await BulkQueueSeasonEpisodesAsync(db, work.ImdbId, work.Season.Value, work.Episode);
            }

            // Write ranked candidates (Sprint 16+) then the primary entry
            if (candidates.Count > 0)
                await db.UpsertStreamCandidatesAsync(candidates);

            await db.UpsertResolutionCacheAsync(entry);
            stats.Resolved++;

            _logger.LogDebug(
                "[InfiniteDrive] Resolved {Imdb} S{S}E{E} → {Quality} via {Tier}",
                work.ImdbId, work.Season, work.Episode, entry.QualityTier, tier);
        }

        // ── Private: build work list ────────────────────────────────────────────

        private static List<WorkItem> BuildWorkItems(
            List<ResolutionEntry> staleEntries,
            List<CatalogItem>     catalogItems,
            string                tier)
        {
            var items = new List<WorkItem>();

            // Existing stale cache entries
            foreach (var e in staleEntries)
                items.Add(new WorkItem(e.ImdbId, e.Season, e.Episode));

            // Catalog items with no cache row at all (tier2 only)
            if (tier == "tier2")
            {
                var seen = new HashSet<string>(
                    staleEntries.Select(e => WorkItem.Key(e.ImdbId, e.Season, e.Episode)),
                    StringComparer.Ordinal);

                foreach (var c in catalogItems)
                {
                    // For movies and series without episode data: queue the item itself.
                    // For series seeds (S01E01): queue only that episode.
                    var wk = new WorkItem(c.ImdbId, c.MediaType == "series" ? (int?)1 : null,
                                                    c.MediaType == "series" ? (int?)1 : null);
                    if (seen.Add(WorkItem.Key(wk.ImdbId, wk.Season, wk.Episode)))
                        items.Add(wk);
                }
            }

            return items;
        }

        /// <summary>
        /// Returns catalog items eligible for Tier 2 resolution:
        /// active items added within the last 48 hours or from Trakt source.
        /// </summary>
        private static async Task<List<CatalogItem>> GetTier2CatalogItemsAsync(
            Data.DatabaseManager db, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var all = await db.GetActiveCatalogItemsAsync();

            var cutoff = DateTime.UtcNow.AddHours(-48).ToString("o");
            return all.Where(c =>
                string.Compare(c.AddedAt, cutoff, StringComparison.Ordinal) >= 0
                || c.Source == "trakt"
            ).ToList();
        }

        // ── Private: season pack bulk-queue ─────────────────────────────────────

        /// <summary>
        /// When a season pack is detected, queues episodes 1..MaxEpisodesPerSeason of
        /// the same season for Tier 1 background resolution (excluding the episode just
        /// resolved).  Each will be picked up by the next <see cref="ProcessTierAsync"/>
        /// run with its own AIOStreams call, throttled normally.
        /// </summary>
        private static async Task BulkQueueSeasonEpisodesAsync(
            Data.DatabaseManager db,
            string               imdbId,
            int                  season,
            int?                 alreadyResolvedEpisode)
        {
            const int MaxEpisodesPerSeason = 30;

            for (int ep = 1; ep <= MaxEpisodesPerSeason; ep++)
            {
                if (ep == alreadyResolvedEpisode) continue; // just resolved — skip
                try
                {
                    await db.QueueForResolutionAsync(imdbId, season, ep, "tier1");
                }
                catch
                {
                    // Non-fatal — best-effort queue
                }
            }
        }

        // ── Private: failure with backoff ───────────────────────────────────────

        private static async Task MarkFailedWithBackoffAsync(
            Data.DatabaseManager db, WorkItem work)
        {
            await db.MarkStreamFailedAsync(work.ImdbId, work.Season, work.Episode);
            // The GetPendingResolutionsByTierAsync query already excludes items
            // marked failed within the last 6 hours, providing natural backoff.
        }

        // ── Private helper types ────────────────────────────────────────────────

        private class WorkItem
        {
            public string ImdbId  { get; }
            public int?   Season  { get; }
            public int?   Episode { get; }

            public WorkItem(string imdbId, int? season, int? episode)
            {
                ImdbId  = imdbId;
                Season  = season;
                Episode = episode;
            }

            public static string Key(string imdbId, int? season, int? episode)
                => $"{imdbId}|{season}|{episode}";
        }

        private class ResolverStats
        {
            public int Resolved { get; set; }
            public int Skipped  { get; set; }
            public int Failed   { get; set; }
        }
    }
}
