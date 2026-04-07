using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace EmbyStreams.Services
{
    // ── Request DTO ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Request object for the <c>GET /EmbyStreams/VersionedPlay</c> endpoint.
    /// Resolves a media stream for a given title and quality slot, with candidate
    /// fallback ladder and AIOStreams refresh on complete failure.
    /// </summary>
    [Route("/EmbyStreams/VersionedPlay", "GET", Summary = "Resolve and serve a versioned media stream")]
    [Authenticated]
    public class VersionPlayRequest : IReturn<object>
    {
        /// <summary>IMDB ID or title identifier, e.g. <c>tt1234567</c>.</summary>
        [ApiMember(Name = "titleId", Description = "IMDB ID or title identifier", DataType = "string", ParameterType = "query")]
        public string TitleId { get; set; } = "";

        /// <summary>Version slot key (hd_broad, 4k_hdr, etc.).</summary>
        [ApiMember(Name = "slot", Description = "Version slot key (hd_broad, 4k_hdr, etc.)", DataType = "string", ParameterType = "query")]
        public string? Slot { get; set; }

        /// <summary>Season number for series playback.  Omit for movies.</summary>
        [ApiMember(Name = "season", Description = "Season number", DataType = "int", ParameterType = "query")]
        public int? Season { get; set; }

        /// <summary>Episode number for series playback.  Omit for movies.</summary>
        [ApiMember(Name = "episode", Description = "Episode number", DataType = "int", ParameterType = "query")]
        public int? Episode { get; set; }

        /// <summary>API token for authentication.</summary>
        [ApiMember(Name = "token", Description = "API token for authentication", DataType = "string", ParameterType = "query")]
        public string? Token { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Slot-aware playback resolution. Handles the versioned .strm URL format:
    /// <c>http://host:port/EmbyStreams/VersionedPlay?titleId=tt123&amp;slot=hd_broad</c>
    ///
    /// Resolution flow per design spec:
    /// <list type="number">
    ///   <item>Resolve slot: if null/empty, get default from version_slots.is_default = 1</item>
    ///   <item>Check SnapshotRepository for cached playback URL; if valid, 302 immediately</item>
    ///   <item>If missing/stale, resolve from slot's candidate ladder (candidates table ordered by rank)</item>
    ///   <item>Cache resolved URL briefly (playback_url_expires_at in version_snapshots)</item>
    ///   <item>If primary candidate fails, try next candidate in ladder</item>
    ///   <item>If all candidates fail, refresh AIOStreams, rebuild candidate ladder, retry once</item>
    ///   <item>If still failing, return clean HTTP 503 error (no transcode attempt)</item>
    /// </list>
    /// </summary>
    public class VersionPlaybackService : IService, IRequiresRequest
    {
        private readonly ILogger<VersionPlaybackService> _logger;

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Emby injects dependencies automatically.
        /// </summary>
        public VersionPlaybackService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<VersionPlaybackService>(logManager.GetLogger("EmbyStreams"));
        }

        /// <summary>
        /// Handles <c>GET /EmbyStreams/VersionedPlay?titleId=tt...&amp;slot=hd_broad</c>.
        ///
        /// Resolution flow:
        /// 1. Resolve slot: if null/empty, get default from version_slots.is_default = 1
        /// 2. Check SnapshotRepository for cached playback URL -> if valid, 302 immediately
        /// 3. If missing/stale -> resolve from slot's candidate ladder (candidates table ordered by rank)
        /// 4. Cache resolved URL briefly (playback_url_expires_at in version_snapshots)
        /// 5. If primary candidate fails -> try next candidate in ladder
        /// 6. If all candidates fail -> refresh AIOStreams, rebuild candidate ladder, retry once
        /// 7. If still failing -> return clean HTTP 503 error (no transcode attempt)
        /// </summary>
        public async Task<object> Get(VersionPlayRequest req)
        {
            // ── 1. Validate title ID ──────────────────────────────────────────────

            if (string.IsNullOrWhiteSpace(req.TitleId))
            {
                Request.Response.StatusCode = 400;
                return new { error = "bad_request", message = "titleId parameter is required" };
            }

            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                Request.Response.StatusCode = 500;
                return new { error = "server_error", message = "Plugin not initialized" };
            }

            var db = plugin.DatabaseManager;

            // ── 2. Resolve slot ───────────────────────────────────────────────────

            var slotKey = req.Slot;
            if (string.IsNullOrWhiteSpace(slotKey))
            {
                // Get default slot from version_slots where is_default = 1
                var defaultSlot = await plugin.VersionSlotRepository.GetDefaultSlotAsync(CancellationToken.None);
                if (defaultSlot == null)
                {
                    _logger.LogWarning("[EmbyStreams][VersionPlay] No default slot configured for title {TitleId}", req.TitleId);
                    Request.Response.StatusCode = 503;
                    return new { error = "no_default_slot", message = "No default quality slot configured" };
                }
                slotKey = defaultSlot.SlotKey;
            }

            _logger.LogInformation(
                "[EmbyStreams][VersionPlay] Resolving title={TitleId} slot={Slot} season={Season} episode={Episode}",
                req.TitleId, slotKey, req.Season, req.Episode);

            // ── 3. Look up media item ─────────────────────────────────────────────
            // Use the titleId as the lookup key against catalog items.

            var catalogItem = await db.GetCatalogItemByImdbIdAsync(req.TitleId);
            if (catalogItem == null)
            {
                _logger.LogWarning("[EmbyStreams][VersionPlay] Title not found in catalog: {TitleId}", req.TitleId);
                Request.Response.StatusCode = 404;
                return new { error = "not_found", message = $"Title {req.TitleId} not found in catalog" };
            }

            // ── 4. Check snapshot cache ───────────────────────────────────────────

            var snapshotRepo = plugin.SnapshotRepository;
            var snapshot = await snapshotRepo.GetSnapshotAsync(catalogItem.ImdbId, slotKey, CancellationToken.None);

            if (snapshot != null && snapshot.HasValidPlaybackUrl)
            {
                _logger.LogInformation(
                    "[EmbyStreams][VersionPlay] Cache hit for {TitleId}/{Slot} -> redirecting",
                    req.TitleId, slotKey);

                Request.Response.AddHeader("Cache-Control", "no-store");
                Request.Response.Redirect(snapshot.PlaybackUrl!);
                return null!;
            }

            // ── 5. Resolve from candidate ladder ──────────────────────────────────

            var candidateRepo = plugin.CandidateRepository;
            var candidates = await candidateRepo.GetCandidatesAsync(
                catalogItem.ImdbId, slotKey, CancellationToken.None);

            if (candidates.Count == 0)
            {
                _logger.LogWarning(
                    "[EmbyStreams][VersionPlay] No candidates for {TitleId}/{Slot}, attempting AIOStreams refresh",
                    req.TitleId, slotKey);

                // Attempt one AIOStreams refresh + retry
                var refreshed = await RefreshCandidatesAsync(
                    req.TitleId, req.Season, req.Episode, slotKey,
                    plugin, candidateRepo);

                if (!refreshed)
                {
                    Request.Response.StatusCode = 503;
                    return new { error = "no_candidates", message = "No stream candidates available" };
                }

                candidates = await candidateRepo.GetCandidatesAsync(
                    catalogItem.ImdbId, slotKey, CancellationToken.None);

                if (candidates.Count == 0)
                {
                    Request.Response.StatusCode = 503;
                    return new { error = "no_candidates", message = "No stream candidates available after refresh" };
                }
            }

            // ── 6. Try candidates in rank order ───────────────────────────────────

            string? resolvedUrl = null;
            Candidate? resolvedCandidate = null;

            foreach (var candidate in candidates.OrderBy(c => c.Rank))
            {
                // Candidates store InfoHash + FileIdx, not direct URLs.
                // Resolve through AIOStreams to get a playback URL.
                var url = await ResolveCandidateUrlAsync(
                    candidate, req.TitleId, req.Season, req.Episode, plugin);

                if (!string.IsNullOrEmpty(url))
                {
                    resolvedUrl = url;
                    resolvedCandidate = candidate;
                    break;
                }

                _logger.LogDebug(
                    "[EmbyStreams][VersionPlay] Candidate rank {Rank} failed for {TitleId}/{Slot}",
                    candidate.Rank, req.TitleId, slotKey);
            }

            if (string.IsNullOrEmpty(resolvedUrl))
            {
                _logger.LogWarning(
                    "[EmbyStreams][VersionPlay] All candidates failed for {TitleId}/{Slot}, attempting AIOStreams refresh",
                    req.TitleId, slotKey);

                // One retry after AIOStreams refresh
                var refreshed = await RefreshCandidatesAsync(
                    req.TitleId, req.Season, req.Episode, slotKey,
                    plugin, candidateRepo);

                if (refreshed)
                {
                    candidates = await candidateRepo.GetCandidatesAsync(
                        catalogItem.ImdbId, slotKey, CancellationToken.None);

                    foreach (var candidate in candidates.OrderBy(c => c.Rank))
                    {
                        var url = await ResolveCandidateUrlAsync(
                            candidate, req.TitleId, req.Season, req.Episode, plugin);

                        if (!string.IsNullOrEmpty(url))
                        {
                            resolvedUrl = url;
                            resolvedCandidate = candidate;
                            break;
                        }
                    }
                }
            }

            // ── 7. All resolution failed → 503 ───────────────────────────────────

            if (string.IsNullOrEmpty(resolvedUrl))
            {
                _logger.LogWarning(
                    "[EmbyStreams][VersionPlay] All resolution attempts failed for {TitleId}/{Slot}",
                    req.TitleId, slotKey);

                Request.Response.StatusCode = 503;
                return new { error = "stream_unavailable", message = "Unable to resolve a playable stream" };
            }

            // ── 8. Cache resolved URL and 302 redirect ───────────────────────────

            try
            {
                // Ensure snapshot row exists before caching URL
                var existingSnapshot = await snapshotRepo.GetSnapshotAsync(catalogItem.ImdbId, slotKey, CancellationToken.None);
                if (existingSnapshot == null)
                {
                    await snapshotRepo.UpsertSnapshotAsync(new VersionSnapshot
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        MediaItemId = catalogItem.ImdbId,
                        SlotKey = slotKey,
                        CandidateId = resolvedCandidate?.Id ?? "",
                        SnapshotAt = DateTime.UtcNow.ToString("o"),
                    }, CancellationToken.None);
                }

                await snapshotRepo.CachePlaybackUrlAsync(
                    catalogItem.ImdbId, slotKey, resolvedUrl, 10, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Cache failure is non-fatal — the stream still works
                _logger.LogDebug(ex, "[EmbyStreams][VersionPlay] Failed to cache playback URL (non-fatal)");
            }

            _logger.LogInformation(
                "[EmbyStreams][VersionPlay] Redirecting {TitleId}/{Slot} via candidate rank {Rank}",
                req.TitleId, slotKey, resolvedCandidate?.Rank ?? -1);

            Request.Response.AddHeader("Cache-Control", "no-store");
            Request.Response.Redirect(resolvedUrl);
            return null!;
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a candidate to a playback URL by re-fetching from AIOStreams
        /// using the candidate's identity (service, info hash, etc.).
        /// </summary>
        private async Task<string?> ResolveCandidateUrlAsync(
            Candidate candidate,
            string titleId,
            int? season,
            int? episode,
            Plugin plugin)
        {
            try
            {
                var config = plugin.Configuration;
                if (config == null || string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
                    return null;

                var client = new AioStreamsClient(config, _logger);

                // Fetch fresh streams from AIOStreams and find matching candidate
                var response = season.HasValue && episode.HasValue
                    ? await client.GetSeriesStreamsAsync(titleId, season.Value, episode.Value, CancellationToken.None)
                    : await client.GetMovieStreamsAsync(titleId, CancellationToken.None);

                var streams = response?.Streams;
                if (streams == null || streams.Count == 0)
                    return null;

                // Match by fingerprint or info hash + file index
                foreach (var stream in streams)
                {
                    if (stream.Url == null) continue;

                    // Match by info hash + file index if available
                    if (!string.IsNullOrEmpty(candidate.InfoHash) &&
                        !string.IsNullOrEmpty(stream.InfoHash) &&
                        string.Equals(candidate.InfoHash, stream.InfoHash, StringComparison.OrdinalIgnoreCase) &&
                        candidate.FileIdx.HasValue && stream.FileIdx.HasValue &&
                        candidate.FileIdx.Value == stream.FileIdx.Value)
                    {
                        return stream.Url;
                    }
                }

                // If no hash match, return the first available stream URL as best effort
                var firstStream = streams.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
                return firstStream?.Url;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "[EmbyStreams][VersionPlay] Failed to resolve candidate URL for {TitleId}",
                    titleId);
                return null;
            }
        }

        /// <summary>
        /// Refreshes candidates by fetching fresh streams from AIOStreams,
        /// normalizing them, and matching against slot policies.
        /// </summary>
        private async Task<bool> RefreshCandidatesAsync(
            string titleId,
            int? season,
            int? episode,
            string slotKey,
            Plugin plugin,
            CandidateRepository candidateRepo)
        {
            try
            {
                var config = plugin.Configuration;
                if (config == null || string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
                    return false;

                var client = new AioStreamsClient(config, _logger);
                var normalizer = plugin.CandidateNormalizer;

                // Fetch fresh streams
                var response = season.HasValue && episode.HasValue
                    ? await client.GetSeriesStreamsAsync(titleId, season.Value, episode.Value, CancellationToken.None)
                    : await client.GetMovieStreamsAsync(titleId, CancellationToken.None);

                var streams = response?.Streams;
                if (streams == null || streams.Count == 0)
                    return false;

                // Normalize streams into candidates (slot-agnostic; SlotKey assigned per slot below)
                var normalized = normalizer.NormalizeStreams(titleId, streams);
                if (normalized.Count == 0)
                    return false;

                // Assign the target slot key to all candidates before persisting
                foreach (var c in normalized)
                    c.SlotKey = slotKey;

                // Persist the new candidate ladder (replaces existing candidates for this title+slot)
                await candidateRepo.UpsertCandidatesAsync(normalized, CancellationToken.None);

                _logger.LogInformation(
                    "[EmbyStreams][VersionPlay] Refreshed {Count} candidates for {TitleId}/{Slot}",
                    normalized.Count, titleId, slotKey);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[EmbyStreams][VersionPlay] AIOStreams refresh failed for {TitleId}/{Slot}",
                    titleId, slotKey);
                return false;
            }
        }
    }
}
