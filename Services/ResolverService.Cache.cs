using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    public partial class ResolverService
    {
        // ── Cache operations ──────────────────────────────────────────────────
        /// Looks up a cached AIOStreams playback URL. If found, returns a 302
        /// redirect to the binary proxy. Returns null on miss.
        /// Cache is forever — URLs are validated at stream time, not resolve time.
        /// </summary>
        private async Task<object?> TryGetCachedUrlAsync(
            Data.DatabaseManager? db, ResolverRequest req)
        {
            // Try stream_candidates first (populated by live resolve)
            var candidate = await TryGetFromStreamCandidatesAsync(db, req);
            if (candidate != null)
                return candidate;

            // Fallback to cached_streams (populated by pre-cache task)
            return await TryGetFromPreCacheAsync(req);
        }

        private async Task<object?> TryGetFromStreamCandidatesAsync(
            Data.DatabaseManager? db, ResolverRequest req)
        {
            if (db == null) return null;

            try
            {
                var candidates = await db.GetStreamCandidatesAsync(
                    req.Id, req.Season, req.Episode);

                var validCandidates = candidates?
                    .Where(c => c.Status == "valid" && !string.IsNullOrEmpty(c.Url))
                    .ToList();

                // Respect REMUX setting — same filter as SelectBest
                if (!Config.UseRemuxForAutoSelection && validCandidates != null)
                    validCandidates = validCandidates
                        .Where(c => !StreamHelpers.IsRemuxFile(c.FileName)).ToList();

                if (validCandidates == null || validCandidates.Count == 0)
                    return null;

                // Filter by quality tier fallback chain
                var matched = FilterByQualityTier(validCandidates, req.Quality);
                if (matched == null || matched.Count == 0)
                    return null;

                var top = PreferLanguageMatch(matched);
                if (top == null)
                    return null;

                var mediaLabel = req.IdType == "series"
                    ? $"series:{req.Id}:S{req.Season}E{req.Episode}"
                    : $"movie:{req.Id}";

                _logger.LogInformation(
                    "[Resolve] Stream-candidates hit for {Media} — age: {Age}",
                    mediaLabel, FormatAge(top.ResolvedAt));

                return RedirectToStream(top.Url);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] stream_candidates lookup failed for {Id}", req.Id);
                return null;
            }
        }

        /// <summary>
        /// Fallback: reads cached_streams (pre-cache table) and returns rank-0
        /// from the scoring service, matching the user's quality preferences.
        /// </summary>
        private async Task<object?> TryGetFromPreCacheAsync(ResolverRequest req)
        {
            try
            {
                var streamCache = Plugin.Instance?.StreamCacheService;
                if (streamCache == null) return null;

                var entry = await streamCache.GetByAioIdAsync(req.Id, req.Season, req.Episode);
                if (entry == null || string.IsNullOrEmpty(entry.VariantsJson)) return null;

                var variants = System.Text.Json.JsonSerializer
                    .Deserialize<List<StreamVariant>>(entry.VariantsJson);
                if (variants == null || variants.Count == 0) return null;

                // Convert variants to StreamCandidates for scoring
                var candidates = variants
                    .Where(v => !string.IsNullOrEmpty(v.Url))
                    .Select((v, i) => new StreamCandidate
                    {
                        AioId = req.Id,
                        Season = req.Season,
                        Episode = req.Episode,
                        Rank = i,
                        Url = v.Url ?? "",
                        FileName = v.FileName,
                        QualityTier = v.QualityTier,
                        Status = "valid",
                        FileSize = v.SizeBytes,
                    })
                    .ToList();

                // REMUX filter
                if (!Config.UseRemuxForAutoSelection)
                    candidates = candidates
                        .Where(c => !StreamHelpers.IsRemuxFile(c.FileName)).ToList();

                if (candidates.Count == 0) return null;

                // Inline ranking: quality tier sort + remux last
                var best = candidates
                    .OrderBy(c => StreamHelpers.IsRemuxFile(c.FileName) ? 1 : 0)
                    .ThenByDescending(c => StreamHelpers.TierScore(c.QualityTier))
                    .ThenByDescending(c => c.BitrateKbps ?? 0)
                    .ThenBy(c => c.Rank)
                    .ToList();
                if (best.Count == 0) return null;

                var top = best[0];
                var mediaLabel = req.IdType == "series"
                    ? $"series:{req.Id}:S{req.Season}E{req.Episode}"
                    : $"movie:{req.Id}";

                _logger.LogInformation(
                    "[Resolve] Pre-cache hit for {Media} — {Name} (age: {Age})",
                    mediaLabel, top.FileName ?? "unknown", FormatAge(entry.CachedAt));

                return RedirectToStream(top.Url);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] Pre-cache lookup failed for {Id}", req.Id);
                return null;
            }
        }

        /// <summary>
        /// Filters cached candidates by matching their QualityTier against the
        /// requested tier's fallback chain. Returns candidates ordered by tier preference.
        /// </summary>
        private List<StreamCandidate>? FilterByQualityTier(List<StreamCandidate> candidates, string? requestedQuality)
        {
            if (string.IsNullOrEmpty(requestedQuality) || !TierFallbacks.TryGetValue(requestedQuality, out var tiers))
                return candidates; // No quality filter — return all

            // Build a resolution set from the fallback chain tiers
            var resolutionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tier in tiers)
            {
                resolutionSet.Add(tier);
                // Also add the resolution label (e.g. "1080p_any" → "1080p")
                var res = tier.Split('_')[0];
                if (!string.IsNullOrEmpty(res))
                    resolutionSet.Add(res);
            }

            var matched = candidates
                .Where(c => !string.IsNullOrEmpty(c.QualityTier) &&
                            resolutionSet.Contains(c.QualityTier))
                .ToList();

            return matched.Count > 0 ? matched : null;
        }

        private StreamCandidate PreferLanguageMatch(List<StreamCandidate> candidates)
        {
            if (candidates.Count == 1)
                return candidates[0];

            string? userLang = null;
            try
            {
                if (_authCtx != null && Request != null)
                {
                    var authInfo = _authCtx.GetAuthorizationInfo(Request);
                    var user = authInfo?.User;
                    userLang = user?.PreferredMetadataLanguage;
                }
            }
            catch { /* non-critical */ }

            if (string.IsNullOrEmpty(userLang))
                userLang = Config.MetadataLanguage;

            if (string.IsNullOrEmpty(userLang))
                return candidates[0];

            // Prefer first candidate whose Languages contains user's preferred language
            var match = candidates.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.Languages) &&
                c.Languages.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Any(l => string.Equals(l.Trim(), userLang, StringComparison.OrdinalIgnoreCase)));

            if (match != null)
            {
                _logger.LogDebug("[Resolve] Preferring language-matched candidate ({Lang}) for {Id}",
                    userLang, match.AioId);
                return match;
            }

            return candidates[0];
        }

        /// <summary>
        /// Writes the resolved stream URL to cache. Fire-and-forget.
        /// Stores a single candidate (rank 0) — the one we resolved via SEL.
        /// </summary>
        private async Task CacheResolvedUrlAsync(
            Data.DatabaseManager? db, ResolverRequest req, ResolvedStream resolved)
        {
            if (db == null) return;

            try
            {
                var now = DateTime.UtcNow;

                var entry = new ResolutionEntry
                {
                    AioId = req.Id,
                    Season = req.Season,
                    Episode = req.Episode,
                    StreamUrl = resolved.PlaybackUrl,
                    QualityTier = req.Quality,
                    FileName = resolved.FileName,
                    Status = "valid",
                    ResolvedAt = now.ToString("o"),
                    ExpiresAt = now.AddYears(1).ToString("o"), // effectively forever
                    ResolutionTier = "sel"
                };

                var candidate = new StreamCandidate
                {
                    AioId = req.Id,
                    Season = req.Season,
                    Episode = req.Episode,
                    Rank = 0,
                    ProviderKey = resolved.ProviderName?.ToLowerInvariant() ?? "unknown",
                    StreamType = "debrid",
                    Url = resolved.PlaybackUrl,
                    QualityTier = req.Quality,
                    FileName = resolved.FileName,
                    Status = "valid",
                    ResolvedAt = now.ToString("o"),
                    ExpiresAt = now.AddYears(1).ToString("o")
                };

                await db.UpsertResolutionResultAsync(entry, new List<StreamCandidate> { candidate });
                _logger.LogDebug("[Resolve] Cached URL for {Id}:{Quality}", req.Id, req.Quality);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resolve] Cache write failed for {Id} (non-fatal)", req.Id);
            }
        }
    }
}
