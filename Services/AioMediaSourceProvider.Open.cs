using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    public partial class AioMediaSourceProvider
    {
        // ── OpenMediaSource flow ────────────────────────────────────────────────

        public async Task<ILiveStream> OpenMediaSource(
            string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            // ── Try OpenTokenData first (has Candidates array) ───────────────────
            OpenTokenData? tokenData = null;
            try
            {
                tokenData = JsonSerializer.Deserialize<OpenTokenData>(openToken);
                _logger.LogInformation("[AioMediaSourceProvider] OpenTokenData parsed: AioId={AioId}, MediaType={MediaType}, CandidateCount={Count}",
                    tokenData?.AioId, tokenData?.MediaType, tokenData?.Candidates?.Count ?? 0);
                if (tokenData?.Candidates != null && tokenData.Candidates.Count > 0)
                    _logger.LogInformation("[AioMediaSourceProvider] First 3 candidates: Ranks=[{Ranks}], Providers=[{Providers}]",
                        string.Join(", ", tokenData.Candidates.Take(3).Select(c => c.Rank)),
                        string.Join(", ", tokenData.Candidates.Take(3).Select(c => c.ProviderKey)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AioMediaSourceProvider] Failed to parse OpenTokenData, trying cached format");
                /* not an OpenTokenData — try cached format */
            }

            // No Candidates → must be a CachedStreamOpenToken
            if (tokenData?.Candidates == null || tokenData.Candidates.Count == 0)
            {
                CachedStreamOpenToken? cachedToken = null;
                try
                {
                    cachedToken = JsonSerializer.Deserialize<CachedStreamOpenToken>(openToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AioMediaSourceProvider] Invalid open token");
                    throw new InvalidOperationException("Invalid open token");
                }

                return await OpenFromCachedTokenAsync(cachedToken!, cancellationToken).ConfigureAwait(false);
            }

            // Try candidates in rank order — Open() is the health check, no HEAD pre-filter
            // (CDN returns 405 for HEAD, so HEAD is useless as a pre-check)
            _logger.LogInformation("[AioMediaSourceProvider] Starting candidate loop for {AioId}, {Count} candidates",
                tokenData.AioId, tokenData.Candidates.Count);
            for (var i = 0; i < tokenData.Candidates.Count; i++)
            {
                var cand = tokenData.Candidates[i];
                _logger.LogDebug("[AioMediaSourceProvider] Trying rank {Rank} (index {Index}), Provider={Provider}, HasUrl={HasUrl}",
                    cand.Rank, i, cand.ProviderKey, !string.IsNullOrEmpty(cand.Url));
                if (string.IsNullOrEmpty(cand.Url)) continue;

                try
                {
                    var source = await BuildSourceFromCandidateAsync(cand, tokenData.AioId, cancellationToken).ConfigureAwait(false);
                    var liveStream = new InfiniteDriveLiveStream(source, _logger);
                    await liveStream.Open(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "[AioMediaSourceProvider] Opened rank {Rank} for {AioId} via {Provider}",
                        cand.Rank, tokenData.AioId, cand.ProviderKey);
                    return liveStream;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AioMediaSourceProvider] Rank {Rank} failed", cand.Rank);

                    // Attempt refresh via infoHash+fileIdx
                    if (!string.IsNullOrEmpty(cand.StreamKey) && !string.IsNullOrEmpty(cand.InfoHash))
                    {
                        try
                        {
                            var freshUrl = await TryRefreshCandidateUrlAsync(
                                cand, tokenData.AioId, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(freshUrl))
                            {
                                cand.Url = freshUrl;
                                var refreshedSource = await BuildSourceFromCandidateAsync(
                                    cand, tokenData.AioId, cancellationToken).ConfigureAwait(false);
                                var refreshedStream = new InfiniteDriveLiveStream(refreshedSource, _logger);
                                await refreshedStream.Open(cancellationToken).ConfigureAwait(false);
                                _logger.LogInformation("[AioMediaSourceProvider] Refreshed rank {Rank}", cand.Rank);
                                return refreshedStream;
                            }
                        }
                        catch (Exception refreshEx)
                        {
                            _logger.LogDebug(refreshEx, "[AioMediaSourceProvider] Refresh failed for rank {Rank}", cand.Rank);
                        }
                    }

                    // Mark failed in DB
                    var failedDb = Plugin.Instance?.DatabaseManager;
                    if (failedDb != null)
                        _ = failedDb.UpdateCandidateStatusAsync(tokenData.AioId, null, null, cand.Rank, "failed");

                    continue;
                }
            }

            // All candidates exhausted — attempt fresh resolve before giving up
            _logger.LogWarning("[AioMediaSourceProvider] All {Count} candidates failed for {AioId}, attempting fresh resolve",
                tokenData.Candidates.Count, tokenData.AioId);

            var freshStreamResult = await TryFreshResolveAsync(
                tokenData.AioId, tokenData.MediaType ?? "movie", null, null, cancellationToken).ConfigureAwait(false);
            if (freshStreamResult != null) return freshStreamResult;

            throw new InvalidOperationException("All candidates failed and fresh resolve returned no results");
        }
        private async Task<ILiveStream> OpenFromCachedTokenAsync(
            CachedStreamOpenToken token, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) throw new InvalidOperationException("Plugin not configured");

            // If we have a direct URL, try it first (fast path — no HEAD, CDN returns 405)
            if (!string.IsNullOrEmpty(token.Url))
            {
                try
                {
                    var source = new MediaSourceInfo
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = token.ProviderName ?? "Stream",
                        Path = token.Url,
                        Protocol = MediaProtocol.Http,
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,
                        RequiresOpening = false,
                    };
                    if (!string.IsNullOrEmpty(token.HeadersJson))
                    {
                        try
                        {
                            source.RequiredHttpHeaders =
                                JsonSerializer.Deserialize<Dictionary<string, string>>(token.HeadersJson);
                        }
                        catch (Exception ex) { _logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "deserialize cached token headers"); }
                    }

                    // Container from filename
                    if (!string.IsNullOrEmpty(token.FileName))
                    {
                        var ext = System.IO.Path.GetExtension(token.FileName)?.TrimStart('.');
                        if (!string.IsNullOrEmpty(ext))
                            source.Container = ext;
                    }

                    // ffprobe the selected stream for real metadata
                    if (!string.IsNullOrEmpty(token.Url))
                    {
                        try
                        {
                            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            probeCts.CancelAfter(TimeSpan.FromSeconds(8));
                            var probed = await CdnProber.ProbeAsync(token.Url, _logger, probeCts.Token).ConfigureAwait(false);
                            if (probed != null && probed.Count > 1)
                            {
                                source.MediaStreams = FilterKnownStreams(probed);
                                // Cache probe result
                                var sk = !string.IsNullOrEmpty(token.InfoHash)
                                    ? $"{token.InfoHash}:{token.FileIdx}" : null;
                                if (!string.IsNullOrEmpty(sk))
                                {
                                    var json = SerializeProbeStreams(probed);
                                    var db = Plugin.Instance?.DatabaseManager;
                                    if (db != null) _ = db.SaveProbeJsonAsync(sk, json);
                                    InvalidateCache(token.AioId, token.Season, token.Episode);
                                }
                            }
                        }
                        catch (Exception ex) { _logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "ffprobe cached URL"); }
                    }
                    if (source.MediaStreams == null || source.MediaStreams.Count == 0)
                        source.MediaStreams = BuildStreamsFromFilename(token.FileName);

                    var cachedStreamKey = !string.IsNullOrEmpty(token.InfoHash)
                        ? $"{token.InfoHash}:{token.FileIdx}"
                        : null;

                    var liveStream = new InfiniteDriveLiveStream(source, _logger);
                    await liveStream.Open(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("[AioMediaSourceProvider] Opened cached URL for {AioId}", token.AioId);
                    return liveStream;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "cached URL expired, falling through to fresh resolve"); }
            }

            // Fresh resolve via AIOStreams using infoHash+fileIdx or filename match
            var providers = ProviderHelper.GetProviders(config);
            var response = await AioStreamsClient.FetchAioStreamsAsync(
                providers, token.AioId, token.MediaType ?? "movie",
                token.Season, token.Episode,
                _logger, Plugin.Instance?.ResolverHealthTracker,
                cooldown: null, cancellationToken).ConfigureAwait(false);

            if (response?.Streams == null)
                throw new InvalidOperationException($"Could not resolve stream for {token.AioId}");

            // Find the stream matching our infoHash+fileIdx, or by filename
            var match = !string.IsNullOrEmpty(token.InfoHash)
                ? response.Streams.FirstOrDefault(s =>
                    string.Equals(s.InfoHash, token.InfoHash, StringComparison.OrdinalIgnoreCase)
                    && s.FileIdx == token.FileIdx)
                : null;

            // Fallback: match by filename
            if (match == null || string.IsNullOrEmpty(match.Url))
                match = response.Streams.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.Url) &&
                    !string.IsNullOrEmpty(s.BehaviorHints?.Filename) &&
                    string.Equals(s.BehaviorHints.Filename, token.FileName, StringComparison.OrdinalIgnoreCase));

            // Fallback: first stream with a direct URL
            if (match == null || string.IsNullOrEmpty(match.Url))
                match = response.Streams.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));

            if (match == null || string.IsNullOrEmpty(match.Url))
                throw new InvalidOperationException($"No usable stream URL for {token.AioId}");

            var freshSource = new MediaSourceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = token.ProviderName ?? "Stream",
                Path = match.Url,
                Protocol = MediaProtocol.Http,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                RequiresOpening = false,
            };

            if (match.Headers != null)
                freshSource.RequiredHttpHeaders = match.Headers;

            // Container from filename
            var freshFileName = match.BehaviorHints?.Filename ?? token.FileName;
            if (!string.IsNullOrEmpty(freshFileName))
            {
                var ext = System.IO.Path.GetExtension(freshFileName)?.TrimStart('.');
                if (!string.IsNullOrEmpty(ext))
                    freshSource.Container = ext;
            }

            // ffprobe the selected stream for real metadata
            if (!string.IsNullOrEmpty(match.Url))
            {
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(8));
                    var probed = await CdnProber.ProbeAsync(match.Url, _logger, probeCts.Token).ConfigureAwait(false);
                    if (probed != null && probed.Count > 1)
                    {
                        freshSource.MediaStreams = FilterKnownStreams(probed);
                        // Cache probe result
                        var sk = !string.IsNullOrEmpty(token.InfoHash)
                            ? $"{token.InfoHash}:{token.FileIdx}" : null;
                        if (!string.IsNullOrEmpty(sk))
                        {
                            var json = SerializeProbeStreams(probed);
                            var db = Plugin.Instance?.DatabaseManager;
                            if (db != null) _ = db.SaveProbeJsonAsync(sk, json);
                            InvalidateCache(token.AioId, token.Season, token.Episode);
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "ffprobe fresh URL"); }
            }
            if (freshSource.MediaStreams == null || freshSource.MediaStreams.Count == 0)
                freshSource.MediaStreams = BuildStreamsFromFilename(freshFileName);

            var freshStreamKey = !string.IsNullOrEmpty(token.InfoHash)
                ? $"{token.InfoHash}:{token.FileIdx}"
                : null;

            var freshLiveStream = new InfiniteDriveLiveStream(freshSource, _logger);
            await freshLiveStream.Open(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("[AioMediaSourceProvider] Opened fresh CDN for {AioId} via infoHash", token.AioId);
            return freshLiveStream;
        }
        private async Task<string?> TryRefreshCandidateUrlAsync(
            OpenTokenCandidate cand, string aioId, CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            var providers = ProviderHelper.GetProviders(config);
            var db = Plugin.Instance?.DatabaseManager;

            foreach (var provider in providers)
            {
                try
                {
                    using var client = AioStreamsClientFactory.CreateForProvider(provider, _logger);

                    AioStreamsStreamResponse? response;
                    response = await client.GetMovieStreamsAsync(aioId, ct).ConfigureAwait(false);

                    if (response?.Streams == null || response.Streams.Count == 0) continue;

                    // Find matching stream by infoHash+fileIdx, then filename, then first URL
                    var match = !string.IsNullOrEmpty(cand.InfoHash)
                        ? response.Streams.FirstOrDefault(s =>
                            string.Equals(s.InfoHash, cand.InfoHash, StringComparison.OrdinalIgnoreCase)
                            && (cand.FileIdx == null || s.FileIdx == cand.FileIdx))
                        : null;

                    if (match == null || string.IsNullOrEmpty(match.Url))
                        match = response.Streams.FirstOrDefault(s =>
                            !string.IsNullOrEmpty(s.Url) &&
                            !string.IsNullOrEmpty(s.BehaviorHints?.Filename) &&
                            string.Equals(s.BehaviorHints.Filename, cand.FileName, StringComparison.OrdinalIgnoreCase));

                    if (match == null || string.IsNullOrEmpty(match.Url))
                        match = response.Streams.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));

                    if (match == null || string.IsNullOrEmpty(match.Url))
                    {
                        // infoHash absent from response — mark failed
                        if (db != null)
                            _ = db.UpdateCandidateStatusAsync(aioId, null, null, cand.Rank, "failed");
                        continue;
                    }

                    // Fresh URL found — update stream_candidates
                    if (db != null)
                    {
                        var now = DateTime.UtcNow;
                        _ = db.UpdateCandidateUrlAsync(
                            cand.StreamKey!, match.Url,
                            now.ToString("o"), now.AddHours(24).ToString("o"));
                    }

                    return match.Url;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "[AioMediaSourceProvider] CDN refresh provider {Name} failed", provider.DisplayName);
                }
            }

            return null;
        }

        /// <summary>
        /// Fresh-resolve fallback when all cached candidates fail.
        /// Reuses the live scoring pipeline (AIOStreams → RankAndFilter → SelectBest).
        /// </summary>
        private async Task<ILiveStream?> TryFreshResolveAsync(
            string aioId, string mediaType, int? season, int? episode, CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            try
            {
                var candidates = await ResolveFromAioStreams(aioId, mediaType, season, episode, config).ConfigureAwait(false);
                var best = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.Url));
                if (best == null) return null;

                var source = new MediaSourceInfo
                {
                    Id = best.Id ?? Guid.NewGuid().ToString("N"),
                    Name = FormatCandidateName(best),
                    Path = best.Url!,
                    Protocol = MediaProtocol.Http,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                };
                if (!string.IsNullOrEmpty(best.HeadersJson))
                {
                    try { source.RequiredHttpHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(best.HeadersJson); }
                    catch (Exception ex) { _logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "deserialize fresh-resolve headers"); }
                }

                if (!string.IsNullOrEmpty(best.FileName))
                {
                    var ext = System.IO.Path.GetExtension(best.FileName)?.TrimStart('.');
                    if (!string.IsNullOrEmpty(ext))
                        source.Container = ext;
                }

                // ffprobe the selected stream for real metadata
                if (!string.IsNullOrEmpty(best.Url))
                {
                    try
                    {
                        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        probeCts.CancelAfter(TimeSpan.FromSeconds(8));
                        var probed = await CdnProber.ProbeAsync(best.Url, _logger, probeCts.Token).ConfigureAwait(false);
                        if (probed != null && probed.Count > 1)
                        {
                            source.MediaStreams = FilterKnownStreams(probed);
                            if (!string.IsNullOrEmpty(best.StreamKey))
                            {
                                var json = SerializeProbeStreams(probed);
                                var db = Plugin.Instance?.DatabaseManager;
                                if (db != null) _ = db.SaveProbeJsonAsync(best.StreamKey, json);
                                InvalidateCache(aioId, season, episode);
                            }
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "ffprobe fresh-resolve URL"); }
                }
                if (source.MediaStreams == null || source.MediaStreams.Count == 0)
                    source.MediaStreams = BuildStreamsFromFilename(best.FileName);

                var liveStream = new InfiniteDriveLiveStream(source, _logger);
                await liveStream.Open(ct).ConfigureAwait(false);
                _logger.LogInformation("[AioMediaSourceProvider] Fresh resolve succeeded for {AioId}", aioId);
                return liveStream;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[InfiniteDrive] Non-fatal: {Context}", "TryFreshResolveAsync failed"); return null; }
        }
    }
}
