using System;
using System.Collections.Generic;
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
    /// Scans indexed series via Emby's TV REST endpoints to detect episode gaps.
    /// Compares Emby's canonical season/episode map against our .strm coverage.
    /// </summary>
    public class SeriesGapDetector
    {
        private readonly EmbyTvApiClient _tvClient;
        private readonly DatabaseManager _db;
        private readonly ILogger _logger;

        // ── In-memory snapshot for StatusService ─────────────────────────────────
        private static readonly object _snapshotLock = new();
        private static GapScanSnapshot _lastSnapshot = new();

        /// <summary>Last scan result, read by StatusService.</summary>
        public static GapScanSnapshot LastSnapshot
        {
            get { lock (_snapshotLock) return _lastSnapshot; }
        }

        // ── Concurrency guard ────────────────────────────────────────────────────
        private static readonly object _runLock = new();
        private static bool _isRunning;

        public SeriesGapDetector(EmbyTvApiClient tvClient, DatabaseManager db, ILogger logger)
        {
            _tvClient = tvClient;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Scans all indexed series for gaps. Called by SeriesGapScanTask.
        /// </summary>
        public async Task ScanAllAsync(IProgress<double>? progress, CancellationToken ct)
        {
            lock (_runLock)
            {
                if (_isRunning) { _logger.LogWarning("[GapDetector] Scan already in progress — skipping"); return; }
                _isRunning = true;
            }

            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return;

                var baseUrl = config.EmbyBaseUrl;
                var token = config.PluginSecret;

                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("[GapDetector] PluginSecret not set — cannot call Emby TV API");
                    return;
                }

                var series = await _db.GetIndexedSeriesAsync(ct);
                _logger.LogInformation("[GapDetector] Starting gap scan for {Count} indexed series", series.Count);

                int scanned = 0, complete = 0, withGaps = 0, totalMissing = 0;

                for (int i = 0; i < series.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var item = series[i];
                    if (string.IsNullOrEmpty(item.EmbyItemId))
                    {
                        _logger.LogDebug("[GapDetector] Skipping {Title} — no EmbyItemId", item.Title);
                        continue;
                    }

                    var report = await ScanSingleSeriesAsync(item, baseUrl, token, ct);
                    scanned++;

                    if (report != null)
                    {
                        if (report.IsComplete) complete++;
                        else { withGaps++; totalMissing += report.Seasons.Sum(s => s.MissingCount); }

                        // Enrich seasons_json on catalog_items if Emby data is richer
                        await TryUpdateSeasonsJsonAsync(item, report, ct);
                    }

                    // Rate-limit: 200ms between series
                    if (i < series.Count - 1) await Task.Delay(200, ct);

                    progress?.Report((double)(i + 1) / series.Count);
                }

                // Update in-memory snapshot for StatusService
                lock (_snapshotLock)
                {
                    _lastSnapshot = new GapScanSnapshot
                    {
                        TotalSeriesScanned = scanned,
                        CompleteSeriesCount = complete,
                        SeriesWithGaps = withGaps,
                        TotalMissingEpisodes = totalMissing,
                        LastScanAt = DateTime.UtcNow
                    };
                }

                _logger.LogInformation(
                    "[GapDetector] Scan complete: {Scanned} scanned, {Complete} complete, {Gaps} with gaps ({Missing} missing episodes)",
                    scanned, complete, withGaps, totalMissing);
            }
            finally
            {
                lock (_runLock) { _isRunning = false; }
            }
        }

        /// <summary>
        /// Scans a single series. Called from ScanAllAsync or as a deferred post-expansion hook.
        /// When <paramref name="autoRepair"/> is true, triggers immediate repair for detected gaps.
        /// </summary>
        public async Task<SeriesGapReport?> ScanSingleSeriesAsync(
            MediaItem item, string baseUrl, string token, CancellationToken ct,
            bool autoRepair = false)
        {
            try
            {
                var seasons = await _tvClient.GetSeasonsAsync(item.EmbyItemId!, baseUrl, token, ct);
                var missing = await _tvClient.GetMissingEpisodesAsync(item.EmbyItemId!, baseUrl, token, ct);

                if (seasons.Count == 0)
                {
                    _logger.LogDebug("[GapDetector] No seasons returned for {Title}", item.Title);
                    return null;
                }

                var coverages = new List<SeasonCoverage>();

                foreach (var season in seasons)
                {
                    var episodes = await _tvClient.GetEpisodesAsync(
                        item.EmbyItemId!, season.Id, baseUrl, token, ct);

                    var present = episodes.Where(e => e.IndexNumber.HasValue).Select(e => e.IndexNumber!.Value).ToList();
                    var missingInSeason = missing
                        .Where(m => m.ParentIndexNumber == season.IndexNumber && m.IndexNumber.HasValue)
                        .Select(m => m.IndexNumber!.Value)
                        .ToList();

                    coverages.Add(new SeasonCoverage(
                        season.IndexNumber,
                        present.Count,
                        missingInSeason.Count,
                        missingInSeason));
                }

                var report = new SeriesGapReport(
                    item.EmbyItemId!,
                    item.Title,
                    coverages,
                    coverages.All(c => c.MissingCount == 0));

                // Log summary
                foreach (var sc in coverages)
                {
                    if (sc.MissingCount > 0)
                    {
                        _logger.LogInformation(
                            "[GapDetector] {Title}: S{Season} — {Present} present, gaps: {Gaps}",
                            item.Title, sc.SeasonNumber, sc.PresentCount,
                            string.Join(",", sc.MissingEpisodeNumbers));
                    }
                }

                // Auto-repair: if gaps found and caller requested immediate repair
                if (autoRepair && !report.IsComplete)
                {
                    await AutoRepairAsync(item, ct);
                }

                return report;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GapDetector] Failed to scan {Title}", item.Title);
                return null;
            }
        }

        /// <summary>
        /// Triggers immediate gap repair for a single series after detection.
        /// Looks up the corresponding CatalogItem via imdb_id.
        /// </summary>
        private async Task AutoRepairAsync(MediaItem item, CancellationToken ct)
        {
            try
            {
                if (!string.Equals(item.PrimaryIdType, "imdb", StringComparison.OrdinalIgnoreCase))
                    return;

                var catalogItem = await _db.GetCatalogItemByImdbIdAsync(item.PrimaryIdValue);
                if (catalogItem == null) return;

                var strmWriter = Plugin.Instance?.StrmWriterService;
                if (strmWriter == null) return;

                var repairService = new SeriesGapRepairService(_db, strmWriter, _logger);
                var repaired = await repairService.RepairSingleSeriesAsync(catalogItem, ct);

                if (repaired > 0)
                    _logger.LogInformation("[GapDetector] Auto-repaired {N} episodes for {Title}", repaired, item.Title);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GapDetector] Auto-repair failed for {Title}", item.Title);
            }
        }

        /// <summary>
        /// Persists the Emby-derived season map into catalog_items.seasons_json
        /// only if the Emby data has more episodes than what's currently stored.
        /// </summary>
        private async Task TryUpdateSeasonsJsonAsync(
            MediaItem item, SeriesGapReport report, CancellationToken ct)
        {
            // Look up catalog_item by primary_id (if imdb)
            if (!string.Equals(item.PrimaryIdType, "imdb", StringComparison.OrdinalIgnoreCase))
                return; // Only enrich via imdb-matched catalog items

            var imdbId = item.PrimaryIdValue;
            var catalogItem = await _db.GetCatalogItemByImdbIdAsync(imdbId);
            if (catalogItem == null) return;

            // Build new seasons_json from Emby data — includes gap info for repair service
            var newSeasons = report.Seasons.Select(s => new
            {
                season = s.SeasonNumber,
                episodes = Enumerable.Range(1, s.PresentCount + s.MissingCount).ToList(),
                presentCount = s.PresentCount,
                missingCount = s.MissingCount,
                missingEpisodeNumbers = s.MissingEpisodeNumbers
            }).ToList();

            var newJson = JsonSerializer.Serialize(newSeasons);

            // Compare: only overwrite if Emby data is richer
            int currentCount = 0;
            if (!string.IsNullOrEmpty(catalogItem.SeasonsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(catalogItem.SeasonsJson);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.TryGetProperty("episodes", out var eps))
                            currentCount += eps.GetArrayLength();
                    }
                }
                catch { /* malformed json — overwrite is fine */ }
            }

            int newCount = newSeasons.Sum(s => s.episodes.Count);
            if (newCount <= currentCount) return;

            await _db.UpdateSeasonsJsonAsync(imdbId, catalogItem.Source, newJson, ct);
            _logger.LogDebug(
                "[GapDetector] Updated seasons_json for {Title}: {Old} → {New} episodes",
                item.Title, currentCount, newCount);
        }
    }

    /// <summary>In-memory snapshot for the Status endpoint.</summary>
    public class GapScanSnapshot
    {
        public int TotalSeriesScanned { get; set; }
        public int CompleteSeriesCount { get; set; }
        public int SeriesWithGaps { get; set; }
        public int TotalMissingEpisodes { get; set; }
        public DateTime? LastScanAt { get; set; }
    }
}
