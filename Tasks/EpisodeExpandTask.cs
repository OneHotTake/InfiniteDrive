using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EmbyStreams.Tasks
{
    /// <summary>
    /// Expands series catalog items from the initial S01E01 seed to full
    /// per-episode .strm files, using Emby's own metadata as the episode
    /// source of truth.
    ///
    /// <para><b>DEPRECATED (Sprint 66):</b> This task has been consolidated into
    /// <see cref="DoctorTask"/>. Use the Doctor task instead for all catalog
    /// reconciliation operations. Episode expansion is now handled by
    /// <see cref="SeriesPreExpansionService"/> during catalog sync.</para>
    ///
    /// After <see cref="CatalogSyncTask"/> writes a single S01E01.strm for a
    /// new series, Emby fetches the series metadata from TMDB/TVDB and
    /// creates Episode items for every season and episode.  This task reads
    /// those Episode items and writes the missing .strm files, then records
    /// the full season list in <c>catalog_items.seasons_json</c> so the
    /// series won't be revisited on subsequent runs.
    ///
    /// Items that Emby hasn't indexed yet are skipped silently and retried
    /// on the next run.
    ///
    /// Default schedule: every 4 hours.
    /// </summary>
    [Obsolete("Use DoctorTask instead (Sprint 66)")]
    public class EpisodeExpandTask : IScheduledTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string TaskName     = "EmbyStreams Episode Expander";
        private const string TaskKey      = "EmbyStreamsEpisodeExpander";
        private const string TaskCategory = "EmbyStreams";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<EpisodeExpandTask> _logger;
        private readonly ILibraryManager             _libraryManager;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>Emby injects dependencies automatically.</summary>
        public EpisodeExpandTask(
            ILibraryManager  libraryManager,
            ILogManager      logManager)
        {
            _libraryManager = libraryManager;
            _logger         = new EmbyLoggerAdapter<EpisodeExpandTask>(logManager.GetLogger("EmbyStreams"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Writes per-episode .strm files for all seasons of series catalog items " +
            "using Emby's indexed episode metadata.";

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
                    IntervalTicks = TimeSpan.FromHours(4).Ticks,
                }
            };
        }

        // ── Execute ──────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Sprint 100A-12: Startup jitter to prevent thundering herd on Emby restart
            await Task.Delay(Random.Shared.Next(0, 120_000), cancellationToken);

            _logger.LogInformation("[EmbyStreams] EpisodeExpandTask started");
            progress.Report(0);

            var config = Plugin.Instance?.Configuration;
            var db     = Plugin.Instance?.DatabaseManager;

            if (config == null || db == null)
            {
                _logger.LogWarning("[EmbyStreams] EpisodeExpandTask: plugin not initialised — aborting");
                return;
            }

            if (string.IsNullOrWhiteSpace(config.SyncPathShows))
            {
                _logger.LogInformation("[EmbyStreams] EpisodeExpandTask: SyncPathShows not set — nothing to do");
                progress.Report(100);
                return;
            }

            // 1. Series catalog items that still need expansion
            var pending = await db.GetSeriesWithoutSeasonsJsonAsync();
            if (pending.Count == 0)
            {
                _logger.LogInformation("[EmbyStreams] EpisodeExpandTask: all series already expanded");
                progress.Report(100);
                return;
            }

            _logger.LogInformation(
                "[EmbyStreams] EpisodeExpandTask: {Count} series pending expansion", pending.Count);

            // 2. Build lookup: IMDB ID → Emby Series item
            var embySeriesMap = BuildEmbySeriesMap();

            // 3. Process each pending series
            int expanded = 0, skipped = 0;

            for (int i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = pending[i];
                try
                {
                    bool ok = await ExpandSeriesAsync(item, embySeriesMap, db, config, cancellationToken);
                    if (ok) expanded++;
                    else    skipped++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[EmbyStreams] EpisodeExpandTask: error expanding {ImdbId} ({Title})",
                        item.ImdbId, item.Title);
                    skipped++;
                }

                progress.Report(10.0 + 90.0 * (i + 1) / pending.Count);
            }

            if (expanded > 0)
            {
                try { _libraryManager.QueueLibraryScan(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[EmbyStreams] EpisodeExpandTask: library scan queue failed");
                }
            }

            _logger.LogInformation(
                "[EmbyStreams] EpisodeExpandTask complete — {Expanded} expanded, {Skipped} skipped",
                expanded, skipped);
            progress.Report(100);
        }

        // ── Private: Emby series map ─────────────────────────────────────────────

        private Dictionary<string, BaseItem> BuildEmbySeriesMap()
        {
            var map = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Series" },
                    Recursive        = true,
                });

                foreach (var item in items)
                {
                    string? imdbId = null;
                    item.ProviderIds?.TryGetValue("Imdb", out imdbId);
                    if (!string.IsNullOrEmpty(imdbId) && !map.ContainsKey(imdbId))
                        map[imdbId] = item;
                }

                _logger.LogDebug(
                    "[EmbyStreams] EpisodeExpandTask: {Count} series found in Emby library", map.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] EpisodeExpandTask: failed to build Emby series map");
            }

            return map;
        }

        // ── Private: expand one series ───────────────────────────────────────────

        private async Task<bool> ExpandSeriesAsync(
            CatalogItem                  item,
            Dictionary<string, BaseItem> embySeriesMap,
            Data.DatabaseManager         db,
            PluginConfiguration          config,
            CancellationToken            cancellationToken)
        {
            // Compute expected show directory path (matches CatalogSyncTask naming)
            var showDir = Path.Combine(
                config.SyncPathShows,
                CatalogSyncTask.SanitisePathPublic(BuildFolderName(item.Title, item.Year, item.ImdbId)));

            if (!Directory.Exists(showDir))
            {
                _logger.LogDebug(
                    "[EmbyStreams] EpisodeExpandTask: show dir not found for {ImdbId} — waiting for CatalogSyncTask",
                    item.ImdbId);
                return false;
            }

            // Find the Emby Series item for this catalog entry
            if (!embySeriesMap.TryGetValue(item.ImdbId, out var seriesItem))
            {
                _logger.LogDebug(
                    "[EmbyStreams] EpisodeExpandTask: {ImdbId} not yet indexed by Emby — will retry next run",
                    item.ImdbId);
                return false;
            }

            // Query episodes under the show directory (path prefix match)
            // This captures any Episode items Emby has indexed from existing .strm files.
            var episodes = GetEpisodesForSeries(showDir);

            if (episodes.Count == 0)
            {
                _logger.LogDebug(
                    "[EmbyStreams] EpisodeExpandTask: no episodes found for {ImdbId} in Emby yet", item.ImdbId);
                return false;
            }

            // Group by season → sorted episode numbers
            var bySeason = new SortedDictionary<int, SortedSet<int>>();
            foreach (var ep in episodes)
            {
                var s = ep.ParentIndexNumber;
                var e = ep.IndexNumber;
                if (!s.HasValue || s.Value <= 0 || !e.HasValue || e.Value <= 0) continue;
                if (!bySeason.ContainsKey(s.Value)) bySeason[s.Value] = new SortedSet<int>();
                bySeason[s.Value].Add(e.Value);
            }

            if (bySeason.Count == 0)
            {
                _logger.LogDebug(
                    "[EmbyStreams] EpisodeExpandTask: no valid season/episode numbers found for {ImdbId}",
                    item.ImdbId);
                return false;
            }

            // Write missing .strm files
            int written = 0;
            foreach (var kvp in bySeason)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int seasonNum    = kvp.Key;
                var episodeNums  = kvp.Value;

                var seasonDir = Path.Combine(showDir, $"Season {seasonNum:D2}");
                Directory.CreateDirectory(seasonDir);

                foreach (int epNum in episodeNums)
                {
                    var fileName = $"{CatalogSyncTask.SanitisePathPublic(item.Title)} " +
                                   $"S{seasonNum:D2}E{epNum:D2}.strm";
                    var path     = Path.Combine(seasonDir, fileName);
                    if (File.Exists(path)) continue;

                    var url = CatalogSyncTask.BuildSignedStrmUrl(config, item.ImdbId, "series", seasonNum, epNum);
                    File.WriteAllText(path, url, Encoding.UTF8);

                    // Write episode NFO file with basic info
                    var nfoPath = Path.ChangeExtension(path, ".nfo");
                    await WriteEpisodeNfoFileAsync(nfoPath, item.Title, seasonNum, epNum);
                    written++;
                }
            }

            // Build and persist seasons_json so this series is skipped on future runs
            var seasonsList = new List<object>();
            foreach (var kvp in bySeason)
                seasonsList.Add(new { season = kvp.Key, episodes = kvp.Value.ToList() });

            await db.UpdateSeasonsJsonAsync(item.ImdbId, item.Source, JsonSerializer.Serialize(seasonsList));

            _logger.LogInformation(
                "[EmbyStreams] EpisodeExpandTask: {ImdbId} ({Title}) — " +
                "{Seasons} seasons, {Written} new .strm files written",
                item.ImdbId, item.Title, bySeason.Count, written);

            return true;
        }

        // ── Private: episode query ───────────────────────────────────────────────

        /// <summary>
        /// Returns all Episode items whose path starts with <paramref name="showDir"/>.
        /// Uses a single ILibraryManager query filtered by path.
        /// </summary>
        private List<BaseItem> GetEpisodesForSeries(string showDir)
        {
            try
            {
                var all = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    Recursive        = true,
                });

                return all
                    .Where(e => !string.IsNullOrEmpty(e.Path)
                             && e.Path.StartsWith(showDir, StringComparison.OrdinalIgnoreCase)
                             && e.IndexNumber.HasValue
                             && e.ParentIndexNumber.HasValue)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] EpisodeExpandTask: episode query failed");
                return new List<BaseItem>();
            }
        }

        // ── Private: helpers ─────────────────────────────────────────────────────

        private static string BuildFolderName(string title, int? year, string? imdbId)
        {
            var sb = new StringBuilder(title);
            if (year.HasValue)                  sb.Append($" ({year})");
            if (!string.IsNullOrEmpty(imdbId))  sb.Append($" [imdbid-{imdbId}]");
            return sb.ToString();
        }

        private static async Task WriteEpisodeNfoFileAsync(
            string nfoPath,
            string seriesTitle,
            int seasonNum,
            int epNum)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<episodedata lockdata=\"false\">");
            sb.AppendLine($"  <title>Episode {epNum}</title>");
            sb.AppendLine($"  <season>{seasonNum}</season>");
            sb.AppendLine($"  <episode>{epNum}</episode>");
            sb.AppendLine($"  <showtitle>{System.Security.SecurityElement.Escape(seriesTitle)}</showtitle>");
            sb.AppendLine("</episodedata>");

            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
