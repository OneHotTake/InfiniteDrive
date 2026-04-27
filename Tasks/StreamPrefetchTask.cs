using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using InfiniteDrive.Services.Scoring;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Background task that proactively populates <c>stream_candidates</c> for library items
    /// that have no candidates yet.  Runs automatically after catalog sync and on a daily
    /// schedule.  Once populated, browse latency for those items drops to &lt;100ms.
    /// </summary>
    public class StreamPrefetchTask : IScheduledTask
    {
        private const string TaskName     = "InfiniteDrive Stream Prefetch";
        private const string TaskKey      = "InfiniteDriveStreamPrefetch";
        private const string TaskCategory = "InfiniteDrive";
        private const int MaxCandidatesPerItem = 7;

        private readonly ILogger<StreamPrefetchTask> _logger;

        public StreamPrefetchTask(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<StreamPrefetchTask>(logManager.GetLogger("InfiniteDrive"));
        }

        public string Name        => TaskName;
        public string Key         => TaskKey;
        public string Description => "Pre-resolves stream candidates so the version picker appears instantly on first browse.";
        public string Category    => TaskCategory;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks,
                }
            };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableStreamPrefetch)
            {
                _logger.LogDebug("[StreamPrefetch] Disabled — skipping");
                progress.Report(100);
                return;
            }

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[StreamPrefetch] DatabaseManager not initialised — aborting");
                return;
            }

            if (await db.IsBudgetExhaustedAsync().ConfigureAwait(false))
            {
                _logger.LogWarning("[StreamPrefetch] API daily budget exhausted — skipping");
                progress.Report(100);
                return;
            }

            var providers = ProviderHelper.GetProviders(config);
            if (providers.Count == 0)
            {
                _logger.LogWarning("[StreamPrefetch] No providers configured — aborting");
                return;
            }

            var batchSize  = config.PreCacheBatchSize > 0 ? config.PreCacheBatchSize : 50;
            var delayMs    = config.PrefetchBatchDelayMs > 0 ? config.PrefetchBatchDelayMs : 2000;

            var items = await db.GetItemsWithNoStreamCandidatesAsync(batchSize, cancellationToken)
                .ConfigureAwait(false);

            if (items.Count == 0)
            {
                _logger.LogInformation("[StreamPrefetch] All items already have candidates — nothing to do");
                progress.Report(100);
                return;
            }

            _logger.LogInformation("[StreamPrefetch] Starting — {Count} items to pre-resolve", items.Count);

            var healthTracker = Plugin.Instance?.ResolverHealthTracker;
            int resolved = 0, failed = 0;

            for (int i = 0; i < items.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                progress.Report((double)(i + 1) / items.Count * 100);

                if (await db.IsBudgetExhaustedAsync().ConfigureAwait(false))
                {
                    _logger.LogWarning("[StreamPrefetch] Budget exhausted after {Resolved} items", resolved);
                    break;
                }

                var item = items[i];

                try
                {
                    var candidates = await ResolveItemAsync(item, providers, config, healthTracker, cancellationToken)
                        .ConfigureAwait(false);

                    if (candidates.Count > 0)
                    {
                        await db.UpsertStreamCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false);
                        resolved++;
                        _logger.LogDebug("[StreamPrefetch] Stored {Count} candidates for {Imdb} S{S}E{E}",
                            candidates.Count, item.ImdbId, item.Season, item.Episode);
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (AioStreamsRateLimitException)
                {
                    _logger.LogWarning("[StreamPrefetch] AIO rate limit — backing off 30s for {Imdb}", item.ImdbId);
                    failed++;
                    await Task.Delay(30_000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogDebug(ex, "[StreamPrefetch] Failed {Imdb}", item.ImdbId);
                }

                // Inter-item delay to avoid hammering AIO
                if (i < items.Count - 1)
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("[StreamPrefetch] Complete — {Resolved} resolved, {Failed} failed",
                resolved, failed);
            progress.Report(100);
        }

        private async Task<List<StreamCandidate>> ResolveItemAsync(
            UncachedItem item,
            List<ProviderInfo> providers,
            PluginConfiguration config,
            ResolverHealthTracker? healthTracker,
            CancellationToken ct)
        {
            foreach (var provider in providers)
            {
                if (healthTracker != null && healthTracker.ShouldSkip(provider.DisplayName))
                    continue;

                try
                {
                    using var client = new AioStreamsClient(
                        provider.Url, provider.Uuid, provider.Token, _logger);
                    client.Cooldown = Plugin.Instance?.CooldownGate;

                    AioStreamsStreamResponse? response;

                    if (item.MediaType == "series" && item.Season.HasValue && item.Episode.HasValue)
                        response = await client.GetSeriesStreamsAsync(
                            item.ImdbId, item.Season.Value, item.Episode.Value, ct).ConfigureAwait(false);
                    else if (item.MediaType == "movie")
                        response = await client.GetMovieStreamsAsync(item.ImdbId, ct).ConfigureAwait(false);
                    else
                        continue;

                    if (response?.Streams == null || response.Streams.Count == 0)
                        continue;

                    if (healthTracker != null) healthTracker.RecordSuccess(provider.DisplayName);

                    var ranked = StreamHelpers.RankAndFilterStreams(
                        response, item.ImdbId, item.Season, item.Episode,
                        config.ProviderPriorityOrder ?? "",
                        config.CandidatesPerProvider > 0 ? config.CandidatesPerProvider : 5,
                        config.CacheLifetimeMinutes > 0 ? config.CacheLifetimeMinutes : 360);

                    var scoring = new StreamScoringService(
                        _logger as ILogger<StreamScoringService>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamScoringService>.Instance,
                        config);

                    return scoring.SelectBest(ranked).Take(MaxCandidatesPerItem).ToList();
                }
                catch (AioStreamsRateLimitException) { throw; }
                catch (OperationCanceledException)  { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[StreamPrefetch] Provider {P} failed for {Imdb}", provider.DisplayName, item.ImdbId);
                    if (healthTracker != null) healthTracker.RecordFailure(provider.DisplayName);
                }
            }

            return new List<StreamCandidate>();
        }
    }
}
