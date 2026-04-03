using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using EmbyStreams.Services;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace EmbyStreams.Tasks
{
    /// <summary>
    /// Scheduled task that syncs the Discover catalog from AIOStreams.
    /// Runs on a configurable schedule (default daily) to keep the available
    /// content list fresh for users to browse and add to their library.
    /// </summary>
    public class CatalogDiscoverTask : IScheduledTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string TaskName = "EmbyStreams Discover Sync";
        private const string TaskKey = "EmbyStreamsCatalogDiscover";
        private const string TaskCategory = "EmbyStreams";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<CatalogDiscoverTask> _logger;
        private readonly CatalogDiscoverService _discoverService;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="logManager"/> automatically.
        /// </summary>
        public CatalogDiscoverTask(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<CatalogDiscoverTask>(logManager.GetLogger("EmbyStreams"));

            var db = Plugin.Instance.DatabaseManager;
            _discoverService = new CatalogDiscoverService(logManager, db);
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Syncs the Discover catalog from AIOStreams, keeping available content fresh for browsing and adding to the library.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Sprint 100A-12: Startup jitter to prevent thundering herd on Emby restart
            await Task.Delay(Random.Shared.Next(0, 120_000), cancellationToken);

            try
            {
                _logger.LogInformation("[Discover] Task execution started");
                progress.Report(0);

                await _discoverService.SyncDiscoverCatalogAsync(cancellationToken);

                progress.Report(100);
                _logger.LogInformation("[Discover] Task execution completed");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Discover] Task execution cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Task execution failed");
                throw;
            }
        }

        /// <inheritdoc/>
        public bool IsHidden => false;

        /// <inheritdoc/>
        public bool IsEnabled => true;

        /// <inheritdoc/>
        public bool IsLogged => true;
    }
}
