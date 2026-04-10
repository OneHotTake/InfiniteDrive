using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Detects when real media files supersede EmbyStreams .strm files and retires the .strm.
    /// Fires after every Emby library scan for immediate re-adoption detection.
    /// Complements the scheduled LibraryReadoptionTask (24h safety net).
    /// </summary>
    public class LibraryPostScanReadoptionService : ILibraryPostScanTask
    {
        private const int MaxItemsPerPostScanRun = 500;

        private readonly ILogger<LibraryPostScanReadoptionService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;

        /// <summary>
        /// Display name for this post-scan task.
        /// </summary>
        public string Name => "EmbyStreams Re-adoption Check";

        /// <summary>
        /// Unique identifier for this task.
        /// </summary>
        public string Key => "EmbyStreamsPostScanReadoption";

        public LibraryPostScanReadoptionService(
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<LibraryPostScanReadoptionService>(
                logManager.GetLogger("EmbyStreams"));
        }

        /// <summary>
        /// Runs after every Emby library scan completes.
        /// Checks for real-file-vs-strm collisions and retires .strm files accordingly.
        /// </summary>
        public async Task Run(
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            try
            {
                // Bounded: only process items currently served as .strm
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null)
                {
                    _logger.LogWarning("[EmbyStreams] DatabaseManager not available — skipping re-adoption");
                    return;
                }
                var strmItems = await db.GetItemsByLocalSourceAsync("strm");
                if (!strmItems.Any())
                {
                    progress?.Report(100);
                    return;
                }

                // Cap processing for large catalogs
                int processedCount = strmItems.Count;
                bool capped = false;
                if (strmItems.Count > MaxItemsPerPostScanRun)
                {
                    _logger.LogWarning(
                        "[EmbyStreams] Large catalog ({Count} strm items) — post-scan re-adoption " +
                        "limited to {Max} items. Remainder handled by scheduled task.",
                        strmItems.Count, MaxItemsPerPostScanRun);
                    strmItems = strmItems.Take(MaxItemsPerPostScanRun).ToList();
                    capped = true;
                }

                int processed = 0;
                int readopted = 0;

                foreach (var item in strmItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (IsSupersededByRealFile(item))
                    {
                        await ReadoptItemAsync(db, item, cancellationToken);
                        readopted++;
                        _logger.LogInformation(
                            "[EmbyStreams] Re-adopted {Title} — real file detected post-scan",
                            item.Title);
                    }

                    processed++;
                    progress?.Report((double)processed / strmItems.Count * 100);
                }

                if (readopted > 0)
                    _logger.LogInformation(
                        "[EmbyStreams] Post-scan re-adoption complete: {Count} items retired",
                        readopted);
                else if (!capped)
                    _logger.LogDebug(
                        "[EmbyStreams] Post-scan re-adoption: {Count} items checked, no real files found",
                        processedCount);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[EmbyStreams] Post-scan re-adoption cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Error in post-scan re-adoption");
                throw;
            }
        }

        /// <summary>
        /// Checks if Emby library contains a real media file matching this item's provider ID.
        /// </summary>
        private bool IsSupersededByRealFile(CatalogItem item)
        {
            KeyValuePair<string, string>? providerId = null;

            if (!string.IsNullOrEmpty(item.ImdbId))
                providerId = new KeyValuePair<string, string>("imdb", item.ImdbId);
            else if (!string.IsNullOrEmpty(item.TmdbId))
                providerId = new KeyValuePair<string, string>("tmdb", item.TmdbId);

            if (!providerId.HasValue)
                return false; // Cannot match without a provider ID

            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                AnyProviderIdEquals = new[] { providerId.Value },
                IsVirtualItem = false,
                Recursive = true
            };

            var matches = _libraryManager.GetItemList(query);

            // A real file exists if there's a non-.strm match
            return matches.Any(m =>
                m.Path != null &&
                !m.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Retires the item by deleting .strm (if configured) and updating catalog state.
        /// </summary>
        private async Task ReadoptItemAsync(Data.DatabaseManager db, CatalogItem item, CancellationToken ct)
        {
            // Delete .strm if configured to do so
            if (Plugin.Instance?.Configuration?.DeleteStrmOnReadoption == true)
            {
                if (!string.IsNullOrEmpty(item.StrmPath) && File.Exists(item.StrmPath))
                {
                    File.Delete(item.StrmPath);
                    _logger.LogDebug("[EmbyStreams] Deleted .strm for re-adopted item: {Path}",
                        item.StrmPath);
                }
            }

            // Update catalog item state
            item.ItemState = ItemState.Retired;
            item.LocalSource = "library";
            await db.UpsertCatalogItemAsync(item, ct);
        }
    }
}
