using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Detects when real media files supersede InfiniteDrive .strm files and retires the .strm.
    /// Fires after every Emby library scan for immediate re-adoption detection.
    /// Also runs from Marvin as the scheduled safety net.
    /// </summary>
    public class LibraryPostScanReadoptionService : ILibraryPostScanTask
    {
        private readonly ILogger<LibraryPostScanReadoptionService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;

        /// <summary>
        /// Display name for this post-scan task.
        /// </summary>
        public string Name => "InfiniteDrive Re-adoption Check";

        /// <summary>
        /// Unique identifier for this task.
        /// </summary>
        public string Key => "InfiniteDrivePostScanReadoption";

        public LibraryPostScanReadoptionService(
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<LibraryPostScanReadoptionService>(
                logManager.GetLogger("InfiniteDrive"));
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
                    _logger.LogWarning("[InfiniteDrive] DatabaseManager not available — skipping re-adoption");
                    return;
                }
                var strmItems = await db.GetItemsByLocalSourceAsync("strm");
                if (!strmItems.Any())
                {
                    progress?.Report(100);
                    return;
                }

                int processedCount = strmItems.Count;
                int processed = 0;
                int readopted = 0;

                var preference = new OwnedMediaPreferenceService(_libraryManager, _logger);
                foreach (var item in strmItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (await preference.RetireIfOwnedAsync(db, item, cancellationToken))
                    {
                        readopted++;
                        _logger.LogInformation(
                            "[InfiniteDrive] Re-adopted {Title} — real file detected post-scan",
                            item.Title);
                    }

                    processed++;
                    progress?.Report((double)processed / strmItems.Count * 100);
                }

                if (readopted > 0)
                    _logger.LogInformation(
                        "[InfiniteDrive] Post-scan re-adoption complete: {Count} items retired",
                        readopted);
                else
                    _logger.LogDebug(
                        "[InfiniteDrive] Post-scan re-adoption: {Count} items checked, no real files found",
                        processedCount);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[InfiniteDrive] Post-scan re-adoption cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Error in post-scan re-adoption");
                throw;
            }
        }

    }
}
