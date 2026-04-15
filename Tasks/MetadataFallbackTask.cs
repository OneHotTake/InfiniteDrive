using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Daily scheduled task that back-fills metadata for InfiniteDrive catalog items
    /// whose .nfo files contain only minimal ID hints (no poster, plot, or title).
    ///
    /// For each qualifying item the task:
    /// <list type="number">
    ///   <item>Fetches full metadata from Cinemeta v3 (<c>/meta/{type}/{imdb}.json</c>).</item>
    ///   <item>Overwrites the .nfo with a complete Kodi-format file containing title,
    ///         year, plot, genres, poster URL, IMDB rating, and runtime.</item>
    ///   <item>Triggers a targeted Emby library refresh for the item's folder so Emby
    ///         picks up the enriched .nfo without a full library scan.</item>
    /// </list>
    ///
    /// This is a safety net for cases where Emby's built-in scraper cannot match
    /// an item by filename alone — particularly useful for non-English titles or
    /// older releases where the filename format diverges from Emby's expectations.
    ///
    /// Caps: 50 items per run, 500 ms delay between Cinemeta API calls.
    /// Default schedule: daily.
    /// </summary>
    public class MetadataFallbackTask : IScheduledTask
    {
        // ── Constants ────────────────────────────────────────────────────────────

        private const string TaskName      = "InfiniteDrive Metadata Fallback";
        private const string TaskKey       = "InfiniteDriveMetadataFallback";
        private const string TaskCategory  = "InfiniteDrive";
        private const string CinemetaBase  = "https://v3-cinemeta.strem.io";
        private const int    DelayMs       = 500; // Legacy — no longer used, kept for reference

        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ILogger<MetadataFallbackTask> _logger;
        private readonly ILibraryManager               _libraryManager;

        // ── Constructor ──────────────────────────────────────────────────────────

        /// <summary>Emby injects dependencies automatically.</summary>
        public MetadataFallbackTask(
            ILibraryManager libraryManager,
            ILogManager     logManager)
        {
            _libraryManager = libraryManager;
            _logger         = new EmbyLoggerAdapter<MetadataFallbackTask>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── IScheduledTask ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Fetches full metadata (poster, plot, genres) from Cinemeta for catalog items " +
            "whose .nfo files have no artwork yet, then triggers a targeted Emby library refresh.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            new[]
            {
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks,
                }
            };

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Sprint 100A-12: Startup jitter to prevent thundering herd on Emby restart
            await Task.Delay(Random.Shared.Next(0, 120_000), cancellationToken);

            _logger.LogInformation("[InfiniteDrive] MetadataFallbackTask started");
            progress.Report(0);

            var config = Plugin.Instance?.Configuration;
            var db     = Plugin.Instance?.DatabaseManager;

            if (config == null || db == null)
            {
                _logger.LogWarning("[InfiniteDrive] MetadataFallbackTask: plugin not initialised — skipping");
                return;
            }

            // Collect all active catalog items that have a .strm path on disk
            List<CatalogItem> allItems;
            try
            {
                allItems = await db.GetActiveCatalogItemsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] MetadataFallbackTask: could not load catalog items");
                return;
            }

            // Filter to items that need metadata enrichment:
            //   - Have a strm_path referencing a file that exists
            //   - Their .nfo either does not exist or lacks a <thumb> element
            var enrichmentCap = Plugin.Instance?.CooldownGate?.Profile.EnrichmentPerRun ?? 50;
            var needsEnrichment = allItems
                .Where(item => NeedsMetadataEnrichment(item, config))
                .Take(enrichmentCap)
                .ToList();

            if (needsEnrichment.Count == 0)
            {
                _logger.LogInformation("[InfiniteDrive] MetadataFallbackTask: all items have full metadata — nothing to do");
                progress.Report(100);
                return;
            }

            _logger.LogInformation(
                "[InfiniteDrive] MetadataFallbackTask: {Count} item(s) need metadata enrichment (cap={Cap})",
                needsEnrichment.Count, enrichmentCap);

            using var client = new AioStreamsClient(CinemetaBase, string.Empty, string.Empty, _logger);
            client.Cooldown = Plugin.Instance?.CooldownGate;
            client.ActiveCooldownKind = CooldownKind.Cinemeta;

            int processed = 0;
            int enriched  = 0;

            foreach (var item in needsEnrichment)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;

                try
                {
                    var type = item.MediaType == "movie" ? "movie" : "series";
                    var meta = await client.GetMetaAsync(type, item.ImdbId, cancellationToken);

                    if (meta == null)
                    {
                        _logger.LogDebug(
                            "[InfiniteDrive] MetadataFallback: Cinemeta returned null for {ImdbId}", item.ImdbId);
                        continue;
                    }

                    var nfoPath = ResolveNfoPath(item);
                    if (nfoPath == null) continue;

                    // Sprint 101A-02: Use typed metadata deserialization
                    var typedMeta = meta.Value.ToAioMetaResponse();
                    AioMeta? aioMeta = typedMeta?.GetMetadata();

                    // Fallback: construct AioMeta from raw JSON if typed deserialization failed
                    if (aioMeta == null)
                    {
                        var metaObj = meta.Value.ValueKind == JsonValueKind.Object
                            && meta.Value.TryGetProperty("meta", out var inner)
                            ? inner : meta.Value;
                        aioMeta = new AioMeta
                        {
                            Name = GetString(metaObj, "name") ?? item.Title,
                            Year = GetInt(metaObj, "year") ?? item.Year,
                            Description = GetString(metaObj, "description"),
                            Poster = GetString(metaObj, "poster"),
                            Background = GetString(metaObj, "background"),
                            ImdbRating = GetString(metaObj, "imdbRating"),
                            Runtime = GetString(metaObj, "runtime"),
                            TmdbId = GetString(metaObj, "tmdbId") ?? item.TmdbId,
                            Genres = GetStringArray(metaObj, "genres").ToList(),
                            Cast = GetStringArray(metaObj, "cast").Take(10).ToList(),
                            Director = GetString(metaObj, "director"),
                            OriginalTitle = GetString(metaObj, "originalTitle"),
                        };
                    }

                    var written = false;
                    try
                    {
                        NfoWriterService.WriteEnrichedNfo(nfoPath, item, aioMeta);
                        written = true;
                    }
                    catch { /* NfoWriterService handles errors internally */ }

                    if (written)
                    {
                        enriched++;
                        _logger.LogInformation(
                            "[InfiniteDrive] MetadataFallback: enriched {Title} ({ImdbId})", item.Title, item.ImdbId);

                        // Trigger a targeted folder refresh so Emby picks up the new .nfo.
                        TriggerFolderRefresh(nfoPath);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[InfiniteDrive] MetadataFallback: error processing {ImdbId}", item.ImdbId);
                }

                progress.Report(processed * 100.0 / needsEnrichment.Count);
            }

            _logger.LogInformation(
                "[InfiniteDrive] MetadataFallbackTask complete — {Enriched}/{Total} item(s) enriched",
                enriched, needsEnrichment.Count);
            progress.Report(100);
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when a catalog item has a .strm file on disk but its
        /// sibling .nfo either does not exist or contains only ID hints (no &lt;thumb&gt;).
        /// </summary>
        private static bool NeedsMetadataEnrichment(CatalogItem item, PluginConfiguration config)
        {
            var nfoPath = ResolveNfoPath(item);
            if (nfoPath == null) return false;   // item has no managed .strm — skip

            if (!File.Exists(nfoPath)) return true;

            // Check whether the existing .nfo already has artwork (a <thumb> element).
            try
            {
                var content = File.ReadAllText(nfoPath);
                return !content.Contains("<thumb", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves the path to the .nfo file for a catalog item using the recorded
        /// <c>strm_path</c>.  Returns <c>null</c> if the path is unavailable.
        ///
        /// Movies: replaces <c>.strm</c> extension with <c>.nfo</c>.
        /// Series: walks up from the Season folder to the show root and returns
        ///         <c>tvshow.nfo</c>.
        /// </summary>
        private static string? ResolveNfoPath(CatalogItem item)
        {
            if (string.IsNullOrEmpty(item.StrmPath)) return null;
            if (!File.Exists(item.StrmPath)) return null;

            if (item.MediaType != "series")
                return Path.ChangeExtension(item.StrmPath, ".nfo");

            // Series: strm is in a Season sub-folder — walk up one level to the show root.
            var seasonDir = Path.GetDirectoryName(item.StrmPath);
            if (string.IsNullOrEmpty(seasonDir)) return null;

            var showDir = Path.GetDirectoryName(seasonDir);
            if (string.IsNullOrEmpty(showDir)) return null;

            return Path.Combine(showDir, "tvshow.nfo");
        }

        /// <summary>
        /// Asks Emby to refresh the library folder containing the .nfo so the
        /// enriched metadata is picked up without a full scan.
        /// </summary>
        private void TriggerFolderRefresh(string nfoPath)
        {
            try
            {
                var folder = Path.GetDirectoryName(nfoPath);
                if (string.IsNullOrEmpty(folder)) return;

                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Path      = folder,
                    Recursive = false,
                });

                foreach (var libItem in items)
                {
                    _libraryManager.QueueLibraryScan();
                    break;   // one scan per folder is sufficient
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] MetadataFallback: could not trigger folder refresh for {Path}", nfoPath);
            }
        }

        // ── JSON helpers ─────────────────────────────────────────────────────────

        private static string? GetString(JsonElement obj, string key)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(key, out var prop)) return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }

        private static int? GetInt(JsonElement obj, string key)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(key, out var prop)) return null;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v)) return v;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var sv)) return sv;
            return null;
        }

        private static List<string> GetStringArray(JsonElement obj, string key)
        {
            var list = new List<string>();
            if (obj.ValueKind != JsonValueKind.Object) return list;
            if (!obj.TryGetProperty(key, out var prop)) return list;
            if (prop.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in prop.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String)
                    list.Add(el.GetString() ?? string.Empty);
            return list;
        }
    }
}
