using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Result of a batch gap repair run.
    /// </summary>
    public record GapRepairResult(
        int SeriesProcessed,
        int EpisodesWritten,
        int EpisodesSkipped,
        int EpisodesFailed,
        TimeSpan Duration);

    /// <summary>
    /// Consumes gap data from <c>seasons_json</c> and writes missing episode .strm files.
    /// Closes the loop that Sprint 220 opened (detection → repair).
    /// </summary>
    public class SeriesGapRepairService
    {
        private readonly DatabaseManager _db;
        private readonly StrmWriterService _strmWriter;
        private readonly ILogger _logger;

        // ── In-memory stats for StatusService ─────────────────────────────────────
        private static readonly object _statsLock = new();
        private static DateTime? _lastRepairAt;
        private static int _episodesRepairedLastRun;
        private static int _episodesRepairedTotal;

        // Sprint 311: upstream verification caps
        private const int MaxUpstreamVerifyPerRun = 50;
        private static readonly TimeSpan UpstreamVerifyDelay = TimeSpan.FromMilliseconds(100);

        public static DateTime? LastRepairAt { get { lock (_statsLock) return _lastRepairAt; } }
        public static int EpisodesRepairedLastRun { get { lock (_statsLock) return _episodesRepairedLastRun; } }
        public static int EpisodesRepairedTotal { get { lock (_statsLock) return _episodesRepairedTotal; } }

        // ── Concurrency guard ────────────────────────────────────────────────────
        private static readonly object _runLock = new();
        private static bool _isRunning;

        public SeriesGapRepairService(DatabaseManager db, StrmWriterService strmWriter, ILogger logger)
        {
            _db = db;
            _strmWriter = strmWriter;
            _logger = logger;
        }

        /// <summary>
        /// Batch repair: processes up to <paramref name="batchLimit"/> series with gaps.
        /// Called by SeriesGapRepairTask.
        /// </summary>
        public async Task<GapRepairResult> RepairSeriesGapsAsync(int batchLimit, CancellationToken ct)
        {
            lock (_runLock)
            {
                if (_isRunning)
                {
                    _logger.LogWarning("[GapRepair] Repair already in progress — skipping");
                    return new GapRepairResult(0, 0, 0, 0, TimeSpan.Zero);
                }
                _isRunning = true;
            }

            var sw = Stopwatch.StartNew();
            int seriesProcessed = 0, totalWritten = 0, totalSkipped = 0, totalFailed = 0;
            const int maxEpisodesPerRun = 500;

            try
            {
                var items = await _db.GetSeriesWithGapsAsync(batchLimit, ct);
                _logger.LogInformation("[GapRepair] Found {Count} series with gaps to repair", items.Count);

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(item.ImdbId))
                    {
                        _logger.LogWarning("[GapRepair] Skipping series with null imdb_id: {Title}", item.Title);
                        continue;
                    }

                    if (totalWritten >= maxEpisodesPerRun)
                    {
                        _logger.LogInformation("[GapRepair] Hit episode cap ({Cap}) — stopping batch", maxEpisodesPerRun);
                        break;
                    }

                    var (written, skipped, failed) = await RepairSingleItemAsync(item, ct);
                    seriesProcessed++;
                    totalWritten += written;
                    totalSkipped += skipped;
                    totalFailed += failed;

                    // Update seasons_json to clear repaired gaps
                    if (written > 0)
                        await ClearRepairedGapsAsync(item, ct);
                }

                // Trigger library scan after batch
                if (totalWritten > 0)
                    await TriggerLibraryScanAsync();

                sw.Stop();

                // Update stats
                lock (_statsLock)
                {
                    _lastRepairAt = DateTime.UtcNow;
                    _episodesRepairedLastRun = totalWritten;
                    _episodesRepairedTotal += totalWritten;
                }

                _logger.LogInformation(
                    "[GapRepair] Processed {Series} series, wrote {Written} episodes, skipped {Skipped}, failed {Failed} in {Ms}ms",
                    seriesProcessed, totalWritten, totalSkipped, totalFailed, sw.ElapsedMilliseconds);

                return new GapRepairResult(seriesProcessed, totalWritten, totalSkipped, totalFailed, sw.Elapsed);
            }
            finally
            {
                lock (_runLock) { _isRunning = false; }
            }
        }

        /// <summary>
        /// Repairs a single series item. Returns number of episodes written.
        /// Does NOT trigger library scan — caller is responsible.
        /// Used by post-expansion hook.
        /// </summary>
        public async Task<int> RepairSingleSeriesAsync(CatalogItem item, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(item.ImdbId))
            {
                _logger.LogWarning("[GapRepair] Cannot repair series with null imdb_id: {Title}", item.Title);
                return 0;
            }

            var (written, _, _) = await RepairSingleItemAsync(item, ct);

            if (written > 0)
                await ClearRepairedGapsAsync(item, ct);

            _logger.LogDebug("[GapRepair] Repaired {N} episodes for {Title}", written, item.Title);
            return written;
        }

        // ── Private ────────────────────────────────────────────────────────────

        private async Task<(int written, int skipped, int failed)> RepairSingleItemAsync(
            CatalogItem item, CancellationToken ct)
        {
            List<SeasonGapEntry> seasons;
            try
            {
                seasons = JsonSerializer.Deserialize<List<SeasonGapEntry>>(item.SeasonsJson!)
                    ?? new List<SeasonGapEntry>();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[GapRepair] Malformed seasons_json for {Title} — skipping", item.Title);
                return (0, 0, 0);
            }

            int written = 0, skipped = 0, failed = 0;
            int upstreamChecked = 0;

            foreach (var season in seasons)
            {
                if (season.MissingEpisodeNumbers == null || season.MissingEpisodeNumbers.Count == 0)
                    continue;

                foreach (var epNum in season.MissingEpisodeNumbers)
                {
                    ct.ThrowIfCancellationRequested();

                    if (written >= MaxUpstreamVerifyPerRun)
                    {
                        _logger.LogInformation("[GapRepair] Hit episode verification cap ({Cap})", MaxUpstreamVerifyPerRun);
                        goto done;
                    }

                    try
                    {
                        // Sprint 311: Verify upstream has streams before writing .strm
                        if (!await VerifyUpstreamHasStreamsAsync(item.ImdbId, season.Season, epNum))
                        {
                            _logger.LogDebug("[GapRepair] Skipping S{S}E{E} — no upstream streams", season.Season, epNum);
                            skipped++;
                            await Task.Delay(UpstreamVerifyDelay, ct);
                            continue;
                        }

                        upstreamChecked++;
                        var path = _strmWriter.WriteEpisodeStrm(item, season.Season, epNum, null);
                        if (path == null)
                            failed++;
                        else if (File.Exists(path) && !WasJustWritten(path))
                            skipped++;
                        else
                            written++;

                        // Rate-limit between writes
                        await Task.Delay(UpstreamVerifyDelay, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[GapRepair] Failed to write S{S}E{E} for {Title}",
                            season.Season, epNum, item.Title);
                        failed++;
                    }
                }
            }

        done:
            return (written, skipped, failed);
        }

        /// <summary>
        /// Sprint 311: Verifies upstream AIOStreams has streams for an episode before writing .strm.
        /// Returns true if streams exist, false if no streams or error.
        /// </summary>
        private async Task<bool> VerifyUpstreamHasStreamsAsync(string imdbId, int season, int episode)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return true; // Can't verify — proceed anyway

                var providers = new List<(string url, string uuid, string token)>();

                if (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
                {
                    var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                    if (!string.IsNullOrWhiteSpace(url))
                        providers.Add((url, uuid ?? "", token ?? ""));
                }

                if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                {
                    var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.SecondaryManifestUrl);
                    if (!string.IsNullOrWhiteSpace(url))
                        providers.Add((url, uuid ?? "", token ?? ""));
                }

                foreach (var (url, uuid, token) in providers)
                {
                    using var client = new AioStreamsClient(url, uuid, token, _logger);
                    using var cts = new CancellationTokenSource(5000);
                    var response = await client.GetSeriesStreamsAsync(imdbId, season, episode, cts.Token);
                    if (response?.Streams != null && response.Streams.Count > 0)
                        return true;
                }

                return false;
            }
            catch
            {
                // Verification failed — give benefit of the doubt
                return true;
            }
        }

        /// <summary>
        /// Checks if a file was written in the last second (our own write).
        /// Used to distinguish "already existed" from "just written by us".
        /// </summary>
        private static bool WasJustWritten(string path)
        {
            try { return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds < 2; }
            catch { return false; }
        }

        /// <summary>
        /// Clears <c>missingEpisodeNumbers</c> for repaired seasons in seasons_json.
        /// </summary>
        private async Task ClearRepairedGapsAsync(CatalogItem item, CancellationToken ct)
        {
            try
            {
                var seasons = JsonSerializer.Deserialize<List<SeasonGapEntry>>(item.SeasonsJson!);
                if (seasons == null) return;

                foreach (var s in seasons)
                    s.MissingEpisodeNumbers = new List<int>();

                var updatedJson = JsonSerializer.Serialize(seasons);
                await _db.UpdateSeasonsJsonAsync(item.ImdbId, item.Source, updatedJson, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GapRepair] Failed to clear gaps in seasons_json for {Title}", item.Title);
            }
        }

        private async Task TriggerLibraryScanAsync()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return;

            try
            {
                var url = $"{config.EmbyBaseUrl.TrimEnd('/')}/Library/Refresh";
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, url);
                req.Headers.Add("X-Emby-Token", config.PluginSecret);
                await http.SendAsync(req);
                _logger.LogInformation("[GapRepair] Triggered library scan");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GapRepair] Failed to trigger library scan");
            }
        }

        // ── JSON deserialization helper ──────────────────────────────────────────

        private class SeasonGapEntry
        {
            public int Season { get; set; }
            public List<int>? Episodes { get; set; }
            public int PresentCount { get; set; }
            public int MissingCount { get; set; }
            public List<int>? MissingEpisodeNumbers { get; set; }
        }
    }
}
