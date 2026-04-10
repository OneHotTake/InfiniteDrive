using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using EmbyStreams.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Tasks
{
    /// <summary>
    /// Daily scheduled task that back-fills metadata for EmbyStreams catalog items
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

        private const string TaskName      = "EmbyStreams Metadata Fallback";
        private const string TaskKey       = "EmbyStreamsMetadataFallback";
        private const string TaskCategory  = "EmbyStreams";
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
            _logger         = new EmbyLoggerAdapter<MetadataFallbackTask>(logManager.GetLogger("EmbyStreams"));
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

            _logger.LogInformation("[EmbyStreams] MetadataFallbackTask started");
            progress.Report(0);

            var config = Plugin.Instance?.Configuration;
            var db     = Plugin.Instance?.DatabaseManager;

            if (config == null || db == null)
            {
                _logger.LogWarning("[EmbyStreams] MetadataFallbackTask: plugin not initialised — skipping");
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
                _logger.LogError(ex, "[EmbyStreams] MetadataFallbackTask: could not load catalog items");
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
                _logger.LogInformation("[EmbyStreams] MetadataFallbackTask: all items have full metadata — nothing to do");
                progress.Report(100);
                return;
            }

            _logger.LogInformation(
                "[EmbyStreams] MetadataFallbackTask: {Count} item(s) need metadata enrichment (cap={Cap})",
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
                            "[EmbyStreams] MetadataFallback: Cinemeta returned null for {ImdbId}", item.ImdbId);
                        continue;
                    }

                    var nfoPath = ResolveNfoPath(item);
                    if (nfoPath == null) continue;

                    // Sprint 101A-02: Use typed metadata deserialization
                    var typedMeta = meta.Value.ToAioMetaResponse();
                    var written = typedMeta != null
                        ? WriteFullNfoTyped(nfoPath, item, typedMeta)
                        : WriteFullNfo(nfoPath, item, meta.Value);

                    if (written)
                    {
                        enriched++;
                        _logger.LogInformation(
                            "[EmbyStreams] MetadataFallback: enriched {Title} ({ImdbId})", item.Title, item.ImdbId);

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
                    _logger.LogDebug(ex, "[EmbyStreams] MetadataFallback: error processing {ImdbId}", item.ImdbId);
                }

                progress.Report(processed * 100.0 / needsEnrichment.Count);
            }

            _logger.LogInformation(
                "[EmbyStreams] MetadataFallbackTask complete — {Enriched}/{Total} item(s) enriched",
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
        /// Writes a full Kodi-format .nfo file from the Cinemeta meta response.
        /// Always overwrites — MetadataFallbackTask is authoritative for enriched nfos.
        /// </summary>
        /// <returns><c>true</c> if the file was written successfully.</returns>
        private static bool WriteFullNfo(string nfoPath, CatalogItem item, JsonElement meta)
        {
            try
            {
                // The Cinemeta response wraps everything in a "meta" object.
                var metaObj = meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("meta", out var inner)
                    ? inner
                    : meta;

                var title       = GetString(metaObj, "name") ?? item.Title;
                var year        = GetInt(metaObj, "year") ?? item.Year;
                var description = GetString(metaObj, "description");
                var poster      = GetString(metaObj, "poster");
                var background  = GetString(metaObj, "background");
                var rating      = GetString(metaObj, "imdbRating");
                var runtime     = GetString(metaObj, "runtime");
                var tmdbId      = GetString(metaObj, "tmdbId") ?? item.TmdbId;
                var genres      = GetStringArray(metaObj, "genres");
                var cast        = GetStringArray(metaObj, "cast").Take(10).ToList();
                var director    = GetString(metaObj, "director");

                var isMovie = item.MediaType == "movie";
                var root = isMovie ? "movie" : "tvshow";

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                sb.AppendLine($"<{root}>");
                sb.AppendLine($"  <title>{XmlEscape(title)}</title>");
                if (year.HasValue)
                    sb.AppendLine($"  <year>{year.Value}</year>");

                // ── FIX-101A-04: OriginalTitle and SortTitle ─────────────────────
                // Add originaltitle and sorttitle for anime items
                if (item.MediaType == "anime" || !isMovie)
                {
                    // Try to get originaltitle from metadata
                    var originalTitle = GetString(metaObj, "originalTitle") ?? title;
                    if (!string.IsNullOrEmpty(originalTitle))
                        sb.AppendLine($"  <originaltitle>{XmlEscape(originalTitle)}</originaltitle>");

                    // Generate sorttitle by stripping articles
                    var sortTitle = BuildSortTitle(title);
                    if (!string.IsNullOrEmpty(sortTitle))
                        sb.AppendLine($"  <sorttitle>{XmlEscape(sortTitle)}</sorttitle>");
                }

                if (!string.IsNullOrEmpty(description))
                    sb.AppendLine($"  <plot>{XmlEscape(description)}</plot>");
                sb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{item.ImdbId}</uniqueid>");
                if (!string.IsNullOrEmpty(tmdbId))
                    sb.AppendLine($"  <uniqueid type=\"tmdb\">{tmdbId}</uniqueid>");
                if (!string.IsNullOrEmpty(rating))
                    sb.AppendLine($"  <rating>{XmlEscape(rating)}</rating>");
                if (!string.IsNullOrEmpty(runtime))
                    sb.AppendLine($"  <runtime>{XmlEscape(runtime)}</runtime>");
                foreach (var genre in genres)
                    sb.AppendLine($"  <genre>{XmlEscape(genre)}</genre>");
                if (!string.IsNullOrEmpty(poster))
                    sb.AppendLine($"  <thumb aspect=\"poster\">{XmlEscape(poster)}</thumb>");
                if (!string.IsNullOrEmpty(background))
                    sb.AppendLine($"  <fanart><thumb>{XmlEscape(background)}</thumb></fanart>");
                if (!string.IsNullOrEmpty(director))
                    sb.AppendLine($"  <director>{XmlEscape(director)}</director>");
                foreach (var actor in cast)
                    sb.AppendLine($"  <actor><name>{XmlEscape(actor)}</name></actor>");
                sb.AppendLine($"</{root}>");

                Directory.CreateDirectory(Path.GetDirectoryName(nfoPath)!);
                File.WriteAllText(nfoPath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Writes a full Kodi-format .nfo file using typed AioMetaResponse.
        /// Sprint 101A-02: AIOMetadata deserialization.
        /// Always overwrites — MetadataFallbackTask is authoritative for enriched nfos.
        /// </summary>
        /// <returns><c>true</c> if the file was written successfully.</returns>
        private static bool WriteFullNfoTyped(string nfoPath, CatalogItem item, AioMetaResponse metaResponse)
        {
            try
            {
                var meta = metaResponse.GetMetadata();
                if (meta == null) return false;

                var title = meta.Name ?? item.Title;
                var year = meta.Year ?? item.Year;
                var description = meta.Description;
                var poster = meta.Poster;
                var background = meta.Background;
                var rating = meta.ImdbRating;
                var runtime = meta.Runtime;
                var tmdbId = meta.TmdbId ?? item.TmdbId;
                var genres = meta.Genres ?? new List<string>();
                var cast = meta.Cast?.Take(10).ToList() ?? new List<string>();
                var director = meta.Director;

                var isMovie = item.MediaType == "movie";
                var root = isMovie ? "movie" : "tvshow";

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                sb.AppendLine($"<{root}>");
                sb.AppendLine($"  <title>{XmlEscape(title)}</title>");
                if (year.HasValue)
                    sb.AppendLine($"  <year>{year.Value}</year>");

                // ── FIX-101A-04: OriginalTitle and SortTitle ─────────────────────
                // Add originaltitle and sorttitle for anime items
                if (item.MediaType == "anime" || !isMovie)
                {
                    // Use OriginalTitle from metadata or fall back to title
                    var originalTitle = meta.OriginalTitle ?? title;
                    if (!string.IsNullOrEmpty(originalTitle))
                        sb.AppendLine($"  <originaltitle>{XmlEscape(originalTitle)}</originaltitle>");

                    // Generate sorttitle by stripping articles
                    var sortTitle = BuildSortTitle(title);
                    if (!string.IsNullOrEmpty(sortTitle))
                        sb.AppendLine($"  <sorttitle>{XmlEscape(sortTitle)}</sorttitle>");
                }

                if (!string.IsNullOrEmpty(description))
                    sb.AppendLine($"  <plot>{XmlEscape(description)}</plot>");
                sb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{item.ImdbId}</uniqueid>");
                if (!string.IsNullOrEmpty(tmdbId))
                    sb.AppendLine($"  <uniqueid type=\"tmdb\">{tmdbId}</uniqueid>");
                if (!string.IsNullOrEmpty(rating))
                    sb.AppendLine($"  <rating>{XmlEscape(rating)}</rating>");
                if (!string.IsNullOrEmpty(runtime))
                    sb.AppendLine($"  <runtime>{XmlEscape(runtime)}</runtime>");
                foreach (var genre in genres)
                    sb.AppendLine($"  <genre>{XmlEscape(genre)}</genre>");
                if (!string.IsNullOrEmpty(poster))
                    sb.AppendLine($"  <thumb aspect=\"poster\">{XmlEscape(poster)}</thumb>");
                if (!string.IsNullOrEmpty(background))
                    sb.AppendLine($"  <fanart><thumb>{XmlEscape(background)}</thumb></fanart>");
                if (!string.IsNullOrEmpty(director))
                    sb.AppendLine($"  <director>{XmlEscape(director)}</director>");
                foreach (var actor in cast)
                    sb.AppendLine($"  <actor><name>{XmlEscape(actor)}</name></actor>");
                sb.AppendLine($"</{root}>");

                Directory.CreateDirectory(Path.GetDirectoryName(nfoPath)!);
                File.WriteAllText(nfoPath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
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
                _logger.LogDebug(ex, "[EmbyStreams] MetadataFallback: could not trigger folder refresh for {Path}", nfoPath);
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

        /// <summary>
        /// Builds a sort title by stripping leading articles (The, A, An).
        /// Sprint 101A-04: OriginalTitle and SortTitle in all NFO paths.
        /// </summary>
        private static string? BuildSortTitle(string? title)
        {
            if (string.IsNullOrEmpty(title)) return null;

            var trimmed = title.Trim();

            // Strip leading articles
            var articles = new[] { "The ", "A ", "An " };
            foreach (var article in articles)
            {
                if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(article.Length).Trim();
                }
            }

            return trimmed;
        }

        private static string XmlEscape(string s)
            => s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
    }
}
