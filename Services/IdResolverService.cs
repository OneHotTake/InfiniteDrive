using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Structured provider IDs resolved from a raw manifest item ID.
    /// CanonicalId is always non-null: tt > tmdb_ > tvdb_ > native (fallback).
    /// </summary>
    public sealed record ResolvedIds(
        string CanonicalId,
        string? ImdbId,
        string? TmdbId,
        string? TvdbId,
        string? AniDbId,
        string? RawMetaJson);

    /// <summary>
    /// Attempts to extract structured provider IDs from a raw manifest item ID
    /// and enrich them by calling the source addon's /meta endpoint.
    ///
    /// tt-style IMDb IDs are preferred as the canonical key. However, tmdb and
    /// tvdb IDs also give Emby enough to auto-identify items.
    ///
    /// Items are NEVER dropped because ID resolution fails — all fallback paths
    /// return the native ID as the canonical key.
    /// </summary>
    public sealed class IdResolverService
    {
        private readonly ILogger<IdResolverService> _logger;

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(1.5)
        };

        public IdResolverService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<IdResolverService>(logManager.GetLogger("InfiniteDrive"));
            // AioMetadataClient is configured lazily from Plugin.Instance — we
            // create it on demand so IdResolverService can be a singleton.
        }

        /// <summary>
        /// Resolves provider IDs from a raw manifest item ID.
        /// Never throws. Never returns null.
        /// </summary>
        /// <param name="manifestId">Raw ID from catalog: "tmdb_260192", "tt0075543", "kitsu:1234", etc.</param>
        /// <param name="addonBaseUrl">Source addon base URL (manifest URL with /manifest.json stripped).</param>
        /// <param name="mediaType">"movie", "series", or "anime".</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<ResolvedIds> ResolveAsync(
            string manifestId,
            string addonBaseUrl,
            string mediaType,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(manifestId))
                return new ResolvedIds(manifestId ?? "", null, null, null, null, null);

            // ── Step 1: Parse manifest ID (synchronous) ───────────────────────────
            string? imdbId  = null;
            string? tmdbId  = null;
            string? tvdbId  = null;
            string? aniDbId = null;

            var lower = manifestId.ToLowerInvariant();

            if (lower.StartsWith("tt", StringComparison.Ordinal))
            {
                imdbId = manifestId;
                _logger.LogDebug("[IdResolver] Fast path: {Id} is already a tt ID", manifestId);
                return new ResolvedIds(manifestId, imdbId, tmdbId, tvdbId, aniDbId, null);
            }
            else if (lower.StartsWith("tmdb_") || lower.StartsWith("tmdb:"))
            {
                tmdbId = manifestId.Substring(5); // strip "tmdb_" or "tmdb:"
            }
            else if (lower.StartsWith("tvdb_") || lower.StartsWith("tvdb:"))
            {
                tvdbId = manifestId.Substring(5);
            }
            else if (lower.StartsWith("kitsu:") || lower.StartsWith("kitsu_"))
            {
                aniDbId = manifestId.Substring(6);
            }
            else if (lower.StartsWith("mal:") || lower.StartsWith("mal_"))
            {
                aniDbId = manifestId.Substring(4); // MAL → use AniDb slot for AIOMetadata lookup
            }
            else if (lower.StartsWith("imdb:"))
            {
                imdbId = manifestId.Substring(5);
                return new ResolvedIds(imdbId, imdbId, null, null, null, null);
            }
            // else: opaque ID — fall through to network resolution

            // ── Step 2: Call source addon's /meta endpoint ────────────────────────
            string? rawMetaJson = null;
            if (!string.IsNullOrWhiteSpace(addonBaseUrl))
            {
                var metaUrl = $"{addonBaseUrl.TrimEnd('/')}/meta/{mediaType}/{Uri.EscapeDataString(manifestId)}.json";
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(1.5));

                    _logger.LogDebug("[IdResolver] Calling source addon meta: {Url}", metaUrl);
                    using var resp = await _http.GetAsync(metaUrl, cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        rawMetaJson = await resp.Content.ReadAsStringAsync();
                        var parsed = ParseMetaResponse(rawMetaJson);
                        if (parsed.ImdbId != null) imdbId  = parsed.ImdbId;
                        if (parsed.TmdbId != null) tmdbId  = parsed.TmdbId;
                        if (parsed.TvdbId != null) tvdbId  = parsed.TvdbId;

                        if (imdbId != null)
                        {
                            _logger.LogInformation(
                                "[IdResolver] Resolved {ManifestId} → {ImdbId} via source addon meta",
                                manifestId, imdbId);
                            return BuildResult(manifestId, imdbId, tmdbId, tvdbId, aniDbId, rawMetaJson);
                        }
                    }
                    else
                    {
                        _logger.LogDebug(
                            "[IdResolver] Source addon meta returned {Status} for {ManifestId}",
                            (int)resp.StatusCode, manifestId);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("[IdResolver] Source addon meta timed out for {ManifestId}", manifestId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[IdResolver] Source addon meta failed for {ManifestId}", manifestId);
                }
            }

            // ── Step 3: AIOMetadata fallback for tmdb/kitsu/mal IDs ───────────────
            if (imdbId == null && (tmdbId != null || aniDbId != null))
            {
                try
                {
                    var config = Plugin.Instance?.Configuration;
                    if (config != null && !string.IsNullOrEmpty(config.AioMetadataBaseUrl))
                    {
                        var client = new AioMetadataClient(config, _logger);
                        // Use tmdbId as the lookup key (AioMetadataClient accepts non-tt IDs)
                        var lookupId = tmdbId != null ? $"tmdb_{tmdbId}" : manifestId;
                        var meta = await client.FetchAsync(lookupId, null, ct);
                        if (meta != null && meta.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                        {
                            imdbId = meta.ImdbId;
                            if (!string.IsNullOrEmpty(meta.TmdbId)) tmdbId = meta.TmdbId;
                            _logger.LogInformation(
                                "[IdResolver] Resolved {ManifestId} → {ImdbId} via AIOMetadata",
                                manifestId, imdbId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[IdResolver] AIOMetadata lookup failed for {ManifestId}", manifestId);
                }
            }

            // ── Step 4: Build canonical ID from best available ────────────────────
            if (imdbId == null)
            {
                _logger.LogWarning(
                    "[IdResolver] Could not resolve tt ID for {ManifestId} — storing native ID",
                    manifestId);
            }

            return BuildResult(manifestId, imdbId, tmdbId, tvdbId, aniDbId, rawMetaJson);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static ResolvedIds BuildResult(
            string manifestId,
            string? imdbId,
            string? tmdbId,
            string? tvdbId,
            string? aniDbId,
            string? rawMetaJson)
        {
            // Canonical: tt > tmdb_ > tvdb_ > native
            var canonical = imdbId
                ?? (tmdbId != null ? "tmdb_" + tmdbId : null)
                ?? (tvdbId != null ? "tvdb_" + tvdbId : null)
                ?? manifestId;

            return new ResolvedIds(canonical, imdbId, tmdbId, tvdbId, aniDbId, rawMetaJson);
        }

        private readonly record struct ParsedMeta(string? ImdbId, string? TmdbId, string? TvdbId);

        private static ParsedMeta ParseMetaResponse(string json)
        {
            // Minimal parse — look for imdb_id, moviedb_id/tmdb_id, tvdb_id
            // without full JSON deserialization to keep allocations low.
            try
            {
                using var doc = JsonDocument.Parse(json);

                // Response can be { "meta": {...} } or the object directly
                var root = doc.RootElement;
                JsonElement meta = root;
                if (root.TryGetProperty("meta", out var metaEl))
                    meta = metaEl;

                string? imdbId  = null;
                string? tmdbId  = null;
                string? tvdbId  = null;

                if (meta.TryGetProperty("imdb_id", out var imdbEl))
                {
                    var val = imdbEl.GetString();
                    if (!string.IsNullOrEmpty(val) && val.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                        imdbId = val;
                }

                // tmdb: both "moviedb_id" and "tmdb_id" appear in the wild
                if (meta.TryGetProperty("moviedb_id", out var movieDbEl) ||
                    meta.TryGetProperty("tmdb_id", out movieDbEl))
                {
                    var val = movieDbEl.ValueKind == JsonValueKind.Number
                        ? movieDbEl.GetRawText()
                        : movieDbEl.GetString();
                    if (!string.IsNullOrEmpty(val)) tmdbId = val;
                }

                if (meta.TryGetProperty("tvdb_id", out var tvdbEl))
                {
                    var val = tvdbEl.ValueKind == JsonValueKind.Number
                        ? tvdbEl.GetRawText()
                        : tvdbEl.GetString();
                    if (!string.IsNullOrEmpty(val)) tvdbId = val;
                }

                return new ParsedMeta(imdbId, tmdbId, tvdbId);
            }
            catch
            {
                return new ParsedMeta(null, null, null);
            }
        }
    }
}
