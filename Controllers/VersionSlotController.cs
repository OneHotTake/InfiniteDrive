using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Controllers
{
    // ── Request DTOs ────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /EmbyStreams/Versions — returns current slot configuration.
    /// </summary>
    [Route("/EmbyStreams/Versions", "GET", Summary = "Get version slot configuration")]
    [Authenticated]
    public class GetVersionsRequest : IReturn<object> { }

    /// <summary>
    /// POST /EmbyStreams/Versions — update slot configuration.
    /// Body: { "enabledSlots": ["hd_broad", "4k_hdr"], "defaultSlot": "hd_broad" }
    /// </summary>
    [Route("/EmbyStreams/Versions", "POST", Summary = "Update version slot configuration")]
    [Authenticated]
    public class UpdateVersionsRequest : IReturn<object>
    {
        public List<string> EnabledSlots { get; set; } = new();
        public string DefaultSlot { get; set; } = "hd_broad";
    }

    /// <summary>
    /// POST /EmbyStreams/Versions/Rehydrate — trigger rehydration for a slot.
    /// </summary>
    [Route("/EmbyStreams/Versions/Rehydrate", "POST", Summary = "Trigger rehydration for a slot")]
    [Authenticated]
    public class TriggerRehydrationRequest : IReturn<object>
    {
        /// <summary>"AddSlot", "RemoveSlot", or "ChangeDefault"</summary>
        public string Type { get; set; } = "";
        public string SlotKey { get; set; } = "";
    }

    // ── Response DTOs ──────────────────────────────────────────────────────────

    public class VersionSlotResponse
    {
        public List<VersionSlotInfo> Slots { get; set; } = new();
        public string DefaultSlot { get; set; } = "hd_broad";
        public int EnabledCount { get; set; }
        public int MaxSlots { get; set; } = 8;
    }

    public class VersionSlotInfo
    {
        public string SlotKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Resolution { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public bool IsDefault { get; set; }
        public int SortOrder { get; set; }
    }

    public class VersionSlotUpdateResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    // ── Controller ─────────────────────────────────────────────────────────────

    /// <summary>
    /// API controller for version slot management.
    /// Provides endpoints to read, update, and trigger rehydration for quality slots.
    /// </summary>
    [Authenticated]
    public class VersionSlotController : IService, IRequiresRequest
    {
        private readonly ILogger<VersionSlotController> _logger;

        public VersionSlotController(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<VersionSlotController>(logManager.GetLogger("EmbyStreams"));
        }

        public IRequest Request { get; set; } = null!;

        // ── GET /EmbyStreams/Versions ────────────────────────────────────────

        /// <summary>
        /// Returns current slot configuration with enabled status, default, and counts.
        /// </summary>
        public object Get(GetVersionsRequest req)
        {
            _logger.LogDebug("[VersionSlotController] GET versions request");

            var repo = Plugin.Instance?.VersionSlotRepository;
            if (repo == null) return new VersionSlotResponse();
            var slots = repo.GetAllSlotsAsync(CancellationToken.None).GetAwaiter().GetResult();
            var defaultSlot = slots.FirstOrDefault(s => s.IsDefault);
            var enabledCount = slots.Count(s => s.Enabled);

            var response = new VersionSlotResponse
            {
                DefaultSlot = defaultSlot?.SlotKey ?? "hd_broad",
                EnabledCount = enabledCount,
                Slots = slots.Select(s => new VersionSlotInfo
                {
                    SlotKey = s.SlotKey,
                    Label = s.Label,
                    Resolution = s.Resolution,
                    Enabled = s.Enabled,
                    IsDefault = s.IsDefault,
                    SortOrder = s.SortOrder
                }).ToList()
            };

            return response;
        }

        // ── POST /EmbyStreams/Versions ──────────────────────────────────────

        /// <summary>
        /// Updates slot configuration: enables/disables slots and sets default.
        /// Validates: max 8 enabled, hd_broad always enabled, default must be enabled.
        /// </summary>
        public async Task<object> Post(UpdateVersionsRequest req)
        {
            _logger.LogInformation("[VersionSlotController] POST update versions: {SlotCount} slots, default={Default}",
                req.EnabledSlots.Count, req.DefaultSlot);

            var repo = Plugin.Instance?.VersionSlotRepository;
            if (repo == null)
                return new VersionSlotUpdateResponse { Success = false, Message = "Plugin not initialized" };

            // Validation: hd_broad must always be enabled
            if (!req.EnabledSlots.Contains("hd_broad"))
            {
                return new VersionSlotUpdateResponse
                {
                    Success = false,
                    Message = "hd_broad must always be enabled."
                };
            }

            // Validation: max 8 enabled slots
            if (req.EnabledSlots.Count > 8)
            {
                return new VersionSlotUpdateResponse
                {
                    Success = false,
                    Message = "Maximum 8 enabled slots allowed."
                };
            }

            // Validation: default slot must be in enabled list
            if (!string.IsNullOrEmpty(req.DefaultSlot) && !req.EnabledSlots.Contains(req.DefaultSlot))
            {
                return new VersionSlotUpdateResponse
                {
                    Success = false,
                    Message = $"Default slot '{req.DefaultSlot}' must be enabled."
                };
            }

            var allSlots = await repo.GetAllSlotsAsync(CancellationToken.None);

            foreach (var slot in allSlots)
            {
                var shouldBeEnabled = req.EnabledSlots.Contains(slot.SlotKey);
                var shouldBeDefault = slot.SlotKey == req.DefaultSlot;

                if (slot.Enabled != shouldBeEnabled || slot.IsDefault != shouldBeDefault)
                {
                    slot.Enabled = shouldBeEnabled;
                    slot.IsDefault = shouldBeDefault;
                    await repo.UpsertSlotAsync(slot, CancellationToken.None);
                }
            }

            // Ensure exactly one default
            if (!string.IsNullOrEmpty(req.DefaultSlot))
            {
                await repo.SetDefaultSlotAsync(req.DefaultSlot, CancellationToken.None);
            }

            _logger.LogInformation("[VersionSlotController] Updated version slots: {EnabledCount} enabled, default={Default}",
                req.EnabledSlots.Count, req.DefaultSlot);

            return new VersionSlotUpdateResponse
            {
                Success = true,
                Message = $"Updated {req.EnabledSlots.Count} enabled slots. Default: {req.DefaultSlot}"
            };
        }

        // ── POST /EmbyStreams/Versions/Rehydrate ────────────────────────────

        /// <summary>
        /// Triggers rehydration for a specific slot operation type.
        /// </summary>
        public async Task<object> Post(TriggerRehydrationRequest req)
        {
            _logger.LogInformation("[VersionSlotController] POST rehydrate: type={Type}, slot={SlotKey}",
                req.Type, req.SlotKey);

            var repo = Plugin.Instance?.VersionSlotRepository;
            if (repo == null)
                return new VersionSlotUpdateResponse { Success = false, Message = "Plugin not initialized" };

            if (string.IsNullOrEmpty(req.SlotKey))
            {
                return new VersionSlotUpdateResponse
                {
                    Success = false,
                    Message = "SlotKey is required."
                };
            }

            var validTypes = new HashSet<string> { "AddSlot", "RemoveSlot", "ChangeDefault" };
            if (!validTypes.Contains(req.Type))
            {
                return new VersionSlotUpdateResponse
                {
                    Success = false,
                    Message = $"Invalid rehydration type '{req.Type}'. Must be AddSlot, RemoveSlot, or ChangeDefault."
                };
            }

            var slot = await repo.GetSlotAsync(req.SlotKey, CancellationToken.None);
            if (slot == null)
            {
                return new VersionSlotUpdateResponse
                {
                    Success = false,
                    Message = $"Slot '{req.SlotKey}' not found."
                };
            }

            // Enqueue rehydration operation
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                var opJson = $"{{\"type\":\"{req.Type.ToLowerInvariant()}\",\"slotKey\":\"{req.SlotKey}\"}}";
                var config = plugin.Configuration;
                config.PendingRehydrationOperations ??= new List<string>();
                config.PendingRehydrationOperations.Add(opJson);
                plugin.SaveConfiguration();
            }

            _logger.LogInformation("[VersionSlotController] Enqueued rehydration: {Type} for slot {SlotKey}",
                req.Type, req.SlotKey);

            return new VersionSlotUpdateResponse
            {
                Success = true,
                Message = $"Rehydration '{req.Type}' enqueued for slot '{req.SlotKey}'."
            };
        }
    }
}
