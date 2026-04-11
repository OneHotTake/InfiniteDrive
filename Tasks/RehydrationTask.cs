using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Scheduled task that materializes versioned stream files for configured quality slots.
    /// Runs daily; also triggerable on-demand via Content Mgmt tab or POST /InfiniteDrive/Trigger.
    /// <para>
    /// Reads <see cref="PluginConfiguration.PendingRehydrationOperations"/> from config,
    /// parses each JSON entry, and delegates to <see cref="RehydrationService"/>.
    /// On success the operation is removed from the queue; on failure it stays for retry.
    /// </para>
    /// </summary>
    public class RehydrationTask : IScheduledTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string TaskName = "InfiniteDrive Rehydration";
        private const string TaskKey = "embystreams_rehydration";
        private const string TaskCategory = "InfiniteDrive";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<RehydrationTask> _logger;
        private readonly ILibraryManager _libraryManager;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="libraryManager"/> and
        /// <paramref name="logManager"/> automatically.
        /// </summary>
        public RehydrationTask(
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = new EmbyLoggerAdapter<RehydrationTask>(logManager.GetLogger("InfiniteDrive"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Materializes versioned stream files for configured quality slots";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks,
                    MaxRuntimeTicks = TimeSpan.FromHours(2).Ticks
                }
            };
        }

        /// <inheritdoc/>
        public async Task Execute(CancellationToken ct, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                _logger.LogWarning("[RehydrationTask] Plugin instance not available");
                return;
            }

            var config = plugin.Configuration;
            if (config.PendingRehydrationOperations == null
                || config.PendingRehydrationOperations.Count == 0)
            {
                _logger.LogInformation("[RehydrationTask] No pending rehydration operations");
                return;
            }

            var rehydrationService = new RehydrationService(_logger);
            var operations = new List<string>(config.PendingRehydrationOperations);
            var completed = new HashSet<int>();
            int total = operations.Count;

            _logger.LogInformation(
                "[RehydrationTask] Processing {Count} pending rehydration operations",
                total);

            for (int i = 0; i < operations.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var json = operations[i];
                RehydrationOperation? op = null;

                try
                {
                    op = JsonSerializer.Deserialize<RehydrationOperation>(json);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "[RehydrationTask] Failed to parse operation JSON: {Json}", json);
                    completed.Add(i);
                    continue;
                }

                if (op == null || string.IsNullOrEmpty(op.SlotKey))
                {
                    _logger.LogWarning(
                        "[RehydrationTask] Invalid operation (missing type or slotKey): {Json}", json);
                    completed.Add(i);
                    continue;
                }

                RehydrationResult result;

                try
                {
                    var innerProgress = new Progress<double>(p =>
                        progress.Report((i + p / 100.0) / total * 100));

                    result = op.Type?.ToLowerInvariant() switch
                    {
                        "addslot" => await rehydrationService.AddSlotAsync(
                            op.SlotKey, innerProgress, ct),
                        "removeslot" => await rehydrationService.RemoveSlotAsync(
                            op.SlotKey, innerProgress, ct),
                        "changedefault" => await rehydrationService.ChangeDefaultAsync(
                            op.SlotKey, innerProgress, ct),
                        _ => new RehydrationResult
                        {
                            Success = false,
                            Message = $"Unknown operation type: {op.Type}"
                        }
                    };
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation(
                        "[RehydrationTask] Cancelled during operation {Index}/{Total}",
                        i + 1, total);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[RehydrationTask] Operation {Index}/{Total} failed: {Json}",
                        i + 1, total, json);
                    // Keep in queue for retry
                    continue;
                }

                if (result.Success)
                {
                    _logger.LogInformation(
                        "[RehydrationTask] Operation {Type} '{SlotKey}' succeeded: {Message}",
                        op.Type, op.SlotKey, result.Message);
                    completed.Add(i);
                }
                else
                {
                    _logger.LogWarning(
                        "[RehydrationTask] Operation {Type} '{SlotKey}' failed: {Message}",
                        op.Type, op.SlotKey, result.Message);
                    // Keep in queue for retry
                }
            }

            // Remove completed operations from config
            if (completed.Count > 0)
            {
                var remaining = new List<string>();
                for (int i = 0; i < operations.Count; i++)
                {
                    if (!completed.Contains(i))
                        remaining.Add(operations[i]);
                }

                config.PendingRehydrationOperations = remaining;
                plugin.SaveConfiguration();

                _logger.LogInformation(
                    "[RehydrationTask] {Completed} operations completed, {Remaining} remaining",
                    completed.Count, remaining.Count);
            }

            // Trigger library scan
            await TriggerLibraryScanAsync();

            progress.Report(100);
        }

        /// <inheritdoc/>
        public bool IsHidden => false;

        /// <inheritdoc/>
        public bool IsEnabled => true;

        /// <inheritdoc/>
        public bool IsLogged => true;

        // ── Private helpers ─────────────────────────────────────────────────────

        private async Task TriggerLibraryScanAsync()
        {
            try
            {
                _logger.LogInformation("[RehydrationTask] Triggering Emby library scan");
                var progress = new Progress<double>();
                await _libraryManager.ValidateMediaLibrary(progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RehydrationTask] Failed to trigger library scan");
            }
        }

        // ── DTO for JSON deserialization ─────────────────────────────────────────

        private class RehydrationOperation
        {
            public string? Type { get; set; }
            public string SlotKey { get; set; } = string.Empty;
        }
    }
}
