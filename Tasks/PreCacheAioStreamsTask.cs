using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    /// Background task that proactively resolves AIOStreams for uncached library items
    /// and stores durable stream metadata (infoHash + fileIdx) in the
    /// <c>cached_streams</c> table.  When users browse pre-cached items, the
    /// version picker appears instantly instead of requiring a 20-40s live resolve.
    ///
    /// Default schedule: interval based on <see cref="PluginConfiguration.PreCacheIntervalHours"/>.
    /// </summary>
    public class PreCacheAioStreamsTask : IScheduledTask
    {
        private const string TaskName     = "InfiniteDrive Stream Pre-Cache";
        private const string TaskKey      = "InfiniteDrivePreCacheStreams";
        private const string TaskCategory = "InfiniteDrive";
        private const int MaxVariantsPerItem = 6;
        private const int BackoffBaseSeconds = 5;
        private const int BackoffMaxSeconds = 60;

        private readonly ILogger<PreCacheAioStreamsTask> _logger;

        public PreCacheAioStreamsTask(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<PreCacheAioStreamsTask>(logManager.GetLogger("InfiniteDrive"));
        }

        public string Name => TaskName;
        public string Key => TaskKey;
        public string Description => "Pre-resolves stream metadata for library items so the version picker appears instantly.";
        public string Category => TaskCategory;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var config = Plugin.Instance?.Configuration;
            var hours = config?.PreCacheIntervalHours ?? 6;

            return new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(hours).Ticks,
                }
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Startup jitter 0-60s
            await Task.Delay(Random.Shared.Next(0, 60_000), cancellationToken).ConfigureAwait(false);

            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnablePreCache)
            {
                _logger.LogDebug("[PreCache] Disabled — skipping");
                progress.Report(100);
                return;
            }

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[PreCache] Plugin not initialised — aborting");
                return;
            }

            if (await db.IsBudgetExhaustedAsync().ConfigureAwait(false))
            {
                _logger.LogWarning("[PreCache] API daily budget exhausted — skipping");
                progress.Report(100);
                return;
            }

            _logger.LogInformation("[PreCache] Starting batch resolution");
            progress.Report(0);

            var cacheService = Plugin.Instance?.StreamCacheService;
            if (cacheService == null)
            {
                _logger.LogWarning("[PreCache] StreamCacheService not initialised — aborting");
                return;
            }

            var batchSize = config.PreCacheBatchSize > 0 ? config.PreCacheBatchSize : 50;
            var ttlDays = config.PreCacheTTLDays > 0 ? config.PreCacheTTLDays : 14;

            var items = await cacheService.GetUncachedAsync(batchSize, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                _logger.LogInformation("[PreCache] No uncached items found");
                progress.Report(100);
                return;
            }

            var providers = ProviderHelper.GetProviders(config);
            if (providers.Count == 0)
            {
                _logger.LogWarning("[PreCache] No providers configured — aborting");
                return;
            }

            var semaphore = new SemaphoreSlim(config.MaxConcurrentResolutions, config.MaxConcurrentResolutions);
            int resolved = 0, failed = 0, throttled = 0, rateLimited = 0;
            int backoffConsecutiveHits = 0;

            for (int i = 0; i < items.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var item = items[i];
                var pct = (double)(i + 1) / items.Count * 100;
                progress.Report(pct);

                // Budget check before each item
                if (await db.IsBudgetExhaustedAsync().ConfigureAwait(false))
                {
                    throttled = items.Count - i;
                    _logger.LogWarning("[PreCache] Budget exhausted after {Resolved} items — {Throttled} remaining",
                        resolved, throttled);
                    break;
                }

                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var entry = await ResolveItemAsync(item, providers, config, cacheService, ttlDays, cancellationToken)
                        .ConfigureAwait(false);

                    if (entry != null)
                    {
                        await cacheService.StoreAsync(entry).ConfigureAwait(false);
                        resolved++;
                        backoffConsecutiveHits = 0; // reset backoff on success
                        _logger.LogDebug("[PreCache] Cached {Imdb} S{S}E{E} ({Count} variants)",
                            item.ImdbId, item.Season, item.Episode,
                            JsonSerializer.Deserialize<List<StreamVariant>>(entry.VariantsJson)?.Count ?? 0);
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (AioStreamsRateLimitException)
                {
                    rateLimited++;
                    backoffConsecutiveHits++;
                    var backoffSeconds = ComputeBackoff(backoffConsecutiveHits);
                    _logger.LogWarning("[PreCache] AIO rate limit hit — backing off {Seconds}s (consecutive: {Hits}) for {Imdb}",
                        backoffSeconds, backoffConsecutiveHits, item.ImdbId);
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken).ConfigureAwait(false);
                    // Don't count as failed — the item can be retried next run
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogDebug(ex, "[PreCache] Failed {Imdb}", item.ImdbId);
                }
                finally
                {
                    semaphore.Release();
                }
            }

            _logger.LogInformation(
                "[PreCache] Complete — {Resolved} resolved, {Failed} failed, {Throttled} throttled, {RateLimited} rate-limited",
                resolved, failed, throttled, rateLimited);

            progress.Report(100);
        }

        internal async Task<CachedStreamEntry?> ResolveItemAsync(
            UncachedItem item,
            List<ProviderInfo> providers,
            PluginConfiguration config,
            IStreamCacheService cacheService,
            int ttlDays,
            CancellationToken ct)
        {
            var healthTracker = Plugin.Instance?.ResolverHealthTracker;

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

                    // Rank and filter using existing scoring
                    var ranked = StreamHelpers.RankAndFilterStreams(
                        response, item.ImdbId, item.Season, item.Episode,
                        config.ProviderPriorityOrder ?? "",
                        config.CandidatesPerProvider > 0 ? config.CandidatesPerProvider : 5,
                        config.CacheLifetimeMinutes > 0 ? config.CacheLifetimeMinutes : 360);

                    // Score and cap to MaxVariantsPerItem
                    var scoringService = new StreamScoringService(
                        _logger as ILogger<StreamScoringService>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamScoringService>.Instance,
                        config);
                    var best = scoringService.SelectBest(ranked);

                    if (best.Count == 0) continue;

                    var variants = best.Take(MaxVariantsPerItem).Select(MapToVariant).ToList();

                    // Build primary key: prefer TMDB, fallback to IMDB
                    var tmdbId = item.TmdbId ?? await cacheService.ResolveTmdbIdAsync(item.ImdbId).ConfigureAwait(false);
                    var primaryKey = cacheService.BuildPrimaryKey(tmdbId, item.ImdbId, item.MediaType, item.Season, item.Episode);

                    return new CachedStreamEntry
                    {
                        TmdbKey = primaryKey,
                        ImdbId = item.ImdbId,
                        MediaType = item.MediaType,
                        Season = item.Season,
                        Episode = item.Episode,
                        VariantsJson = JsonSerializer.Serialize(variants),
                        CachedAt = DateTime.UtcNow.ToString("o"),
                        ExpiresAt = DateTime.UtcNow.AddDays(ttlDays).ToString("o"),
                        Status = "valid",
                    };
                }
                catch (Exception ex)
                {
                    if (healthTracker != null) healthTracker.RecordFailure(provider.DisplayName);
                    _logger.LogDebug(ex, "[PreCache] Provider {Name} failed for {Imdb}", provider.DisplayName, item.ImdbId);
                }
            }

            return null;
        }

        private static StreamVariant MapToVariant(StreamCandidate c)
        {
            // Parse languages into AudioStreamInfo list
            var audioStreams = new List<AudioStreamInfo>();
            if (!string.IsNullOrEmpty(c.Languages))
            {
                foreach (var lang in c.Languages.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    audioStreams.Add(new AudioStreamInfo
                    {
                        Language = lang.Trim(),
                        IsDefault = audioStreams.Count == 0,
                    });
                }
            }

            // Parse subtitles
            List<SubtitleStreamInfo>? subtitles = null;
            if (!string.IsNullOrEmpty(c.SubtitlesJson))
            {
                try
                {
                    var subs = JsonSerializer.Deserialize<List<SubtitleStreamInfo>>(c.SubtitlesJson);
                    subtitles = subs;
                }
                catch { /* non-fatal */ }
            }

            return new StreamVariant
            {
                InfoHash = c.InfoHash,
                FileIdx = c.FileIdx,
                FileName = c.FileName,
                Resolution = c.QualityTier,
                QualityTier = c.QualityTier,
                SizeBytes = c.FileSize,
                Bitrate = c.BitrateKbps,
                VideoCodec = InferCodec(c.FileName),
                AudioStreams = audioStreams.Count > 0 ? audioStreams : null,
                SubtitleStreams = subtitles,
                ProviderName = c.ProviderKey,
                StreamType = c.StreamType,
                SourceName = c.FileName ?? c.StreamKey ?? $"Stream #{c.Rank + 1}",
                BingeGroup = c.BingeGroup,
                StreamKey = c.StreamKey,
                Url = c.Url,
                HeadersJson = c.HeadersJson,
            };
        }

        private static string? InferCodec(string? filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            var lower = filename.ToLowerInvariant();
            if (lower.Contains("x265") || lower.Contains("hevc")) return "hevc";
            if (lower.Contains("x264") || lower.Contains("h264") || lower.Contains("h.264")) return "h264";
            if (lower.Contains("av1")) return "av1";
            if (lower.Contains("xvid")) return "xvid";
            return null;
        }

        /// <summary>
        /// Computes exponential backoff: 5s, 10s, 20s, 40s, 60s (max).
        /// Adds 0-2s random jitter.
        /// </summary>
        private static double ComputeBackoff(int consecutiveHits)
        {
            var baseSeconds = Math.Min(
                BackoffMaxSeconds,
                BackoffBaseSeconds * (1 << (consecutiveHits - 1))); // 5, 10, 20, 40, 60...
            var jitter = Random.Shared.NextDouble() * 2.0; // 0-2s jitter
            return baseSeconds + jitter;
        }
    }
}
