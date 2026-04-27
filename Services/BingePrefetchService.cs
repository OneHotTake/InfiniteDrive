using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Shared helper for binge-prefetching the next episode.
    /// Called from both ResolverService (default play) and AioMediaSourceProvider (version picker).
    /// Fire-and-forget — never blocks playback.
    /// </summary>
    public static class BingePrefetchService
    {
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(90);

        public static async Task PrefetchNextEpisodeAsync(
            string imdbId,
            int season,
            int episode,
            Microsoft.Extensions.Logging.ILogger logger)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                var db = Plugin.Instance?.DatabaseManager;
                if (config == null || db == null) return;

                var providers = ProviderHelper.GetProviders(config);
                if (providers.Count == 0) return;

                var healthTracker = Plugin.Instance?.ResolverHealthTracker;

                // Try each provider until one returns streams
                foreach (var provider in providers)
                {
                    if (healthTracker != null && healthTracker.ShouldSkip(provider.DisplayName))
                        continue;

                    try
                    {
                        using var client = new AioStreamsClient(
                            provider.Url, provider.Uuid, provider.Token, logger);

                        var response = await client.GetSeriesStreamsAsync(imdbId, season, episode + 1);

                        var streams = response?.Streams;
                        if (streams == null || streams.Count == 0) continue;

                        var stream = streams[0];
                        if (string.IsNullOrEmpty(stream.Url)) continue;

                        if (healthTracker != null)
                            healthTracker.RecordSuccess(provider.DisplayName);

                        // Cache the resolved URL
                        var now = DateTime.UtcNow;
                        var ttl = stream.Duration.HasValue && stream.Duration.Value > 0
                            ? TimeSpan.FromSeconds(stream.Duration.Value) + TimeSpan.FromMinutes(15)
                            : DefaultTtl;

                        var entry = new ResolutionEntry
                        {
                            ImdbId = imdbId,
                            Season = season,
                            Episode = episode + 1,
                            StreamUrl = stream.Url,
                            QualityTier = "any",
                            FileName = stream.BehaviorHints?.Filename,
                            Status = "valid",
                            ResolvedAt = now.ToString("o"),
                            ExpiresAt = now.Add(ttl).ToString("o"),
                            ResolutionTier = "binge_prefetch"
                        };

                        var candidate = new StreamCandidate
                        {
                            ImdbId = imdbId,
                            Season = season,
                            Episode = episode + 1,
                            Rank = 0,
                            ProviderKey = provider.DisplayName.ToLowerInvariant(),
                            StreamType = "debrid",
                            Url = stream.Url,
                            QualityTier = "any",
                            FileName = stream.BehaviorHints?.Filename,
                            Status = "valid",
                            ResolvedAt = now.ToString("o"),
                            ExpiresAt = now.Add(ttl).ToString("o")
                        };

                        await db.UpsertResolutionResultAsync(entry, new List<StreamCandidate> { candidate });

                        logger.LogInformation(
                            "[Binge] Prefetched S{Season}E{Episode} for {ImdbId} (TTL: {Ttl}min)",
                            season, episode + 1, imdbId, (int)ttl.TotalMinutes);

                        return; // Success — stop trying providers
                    }
                    catch (Exception ex)
                    {
                        if (healthTracker != null)
                            healthTracker.RecordFailure(provider.DisplayName);
                        logger.LogDebug(ex,
                            "[Binge] Provider {Name} failed prefetch for {ImdbId} S{S}E{E}",
                            provider.DisplayName, imdbId, season, episode + 1);
                    }
                }

                logger.LogDebug("[Binge] No streams found for {ImdbId} S{S}E{E} — prefetch skipped",
                    imdbId, season, episode + 1);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[Binge] Prefetch failed for {ImdbId} (non-fatal)", imdbId);
            }
        }
    }
}
