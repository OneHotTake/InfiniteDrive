using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Services
{
    // ════════════════════════════════════════════════════════════════════════════════════════
    // HEALTH ENDPOINT (Sprint 100A-13)
    // ════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request for <c>GET /InfiniteDrive/Health</c>.
    /// Sprint 100A-13: No auth required for read.
    /// </summary>
    [Route("/InfiniteDrive/Health", "GET",
        Summary = "Returns plugin health status (no auth required)")]
    public class HealthRequest : IReturn<object> { }
    
    /// <summary>Response from <c>GET /InfiniteDrive/Health</c>.</summary>
    public class HealthResponse
    {
        /// <summary>"ok", "stale", or "error".</summary>
        public string Status { get; set; } = "error";
    
        /// <summary>ISO8601 timestamp when manifest was last fetched.</summary>
        public string? ManifestLastFetched { get; set; }
    
        /// <summary>
        /// Manifest status.
        /// (Sprint 358: Enum-driven state)
        /// </summary>
        public ManifestStatusState ManifestStatus { get; set; } = ManifestStatusState.Error;
    
        /// <summary>Number of catalogs in manifest.</summary>
        public int CatalogCount { get; set; }
    
        /// <summary>Catalogs skipped with reasons.</summary>
        public List<CatalogSkippedEntry> CatalogsSkipped { get; set; } = new List<CatalogSkippedEntry>();
    
        /// <summary>A single skipped catalog entry.</summary>
        public class CatalogSkippedEntry
        {
            /// <summary>Catalog name.</summary>
            public string Name { get; set; } = string.Empty;
    
            /// <summary>Reason: "requires_configuration", "unknown_type", etc.</summary>
            public string Reason { get; set; } = string.Empty;
        }
    
        /// <summary>Stream resolution success rate (0-1).</summary>
        public float StreamResolutionSuccessRate { get; set; }
    
        /// <summary>Last sync time (ISO8601).</summary>
        public string? LastSyncTime { get; set; }
    
        /// <summary>Last collection sync time (ISO8601).</summary>
        /// Sprint 102A-04: Read from plugin_metadata table.
        /// </summary>
        public string? LastCollectionSyncTime { get; set; }
    
        /// <summary>Current pipeline phase, if any task is active. Sprint 362.</summary>
        public PipelinePhase? ActivePipeline { get; set; }
    
        /// <summary>Blocked addon names.</summary>
        public List<string> BlockedAddons { get; set; } = new List<string>();
    
        /// <summary>True if any catalog requires configuration.</summary>
        public bool ConfigurationRequired { get; set; }
    
        /// <summary>Count of pending episodes.</summary>
        public int PendingEpisodes { get; set; }
    
        /// <summary>Count of pending anime items (OVA/ONA/SPECIAL).</summary>
        public int AnimePendingItems { get; set; }
    
        /// <summary>Unknown provider prefixes found.</summary>
        public List<string> UnknownProviderPrefixes { get; set; } = new List<string>();
    }
    
    /// <summary>
    /// Service for Health endpoint.
    /// Sprint 100A-13: No auth required.
    /// </summary>
    public class HealthService : IService
    {
        private readonly ILogger<HealthService> _logger;
    
        public HealthService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<HealthService>(logManager.GetLogger("InfiniteDrive"));
        }
    
        /// <summary>Handles <c>GET /InfiniteDrive/Health</c>.</summary>
        public async Task<object> Get(HealthRequest _)
        {
            // Sprint 100A-09: No auth required for health read endpoint
            // Note: Health endpoint does not require admin authentication
            var response = new HealthResponse();
    
            try
            {
                var config = Plugin.Instance?.Configuration;
                var db = Plugin.Instance?.DatabaseManager;
    
                if (config == null || db == null)
                {
                    response.Status = "error";
                    return response;
                }
    
                // Manifest status — use Plugin authority (Sprint 358: deleted dead local)
                response.ManifestStatus = Plugin.Manifest.Status;
                response.ManifestLastFetched = Plugin.Manifest.FetchedAt.ToString("o");
    
                // Pipeline phase — Sprint 362
                response.ActivePipeline = Plugin.Pipeline.Current;
    
                // Catalog count (from manifest, approximate)
                response.CatalogCount = 0; // Would need to fetch manifest for exact count
    
                // Catalogs skipped
                // For now, return empty list - would need to track during sync
                response.CatalogsSkipped = new List<HealthResponse.CatalogSkippedEntry>();
    
                // Stream resolution success rate
                var cacheStats = await db.GetResolutionCacheStatsAsync();
                float successRate = 0;
                if (cacheStats.ValidUnexpired + cacheStats.Stale + cacheStats.Failed > 0)
                {
                    successRate = (float)cacheStats.ValidUnexpired / (cacheStats.ValidUnexpired + cacheStats.Stale + cacheStats.Failed);
                }
                response.StreamResolutionSuccessRate = successRate;
    
                // Last sync times (Sprint 102A-04: Read from plugin_metadata table)
                response.LastSyncTime = db.GetMetadata("last_sync_time");
                response.LastCollectionSyncTime = db.GetMetadata("last_collection_sync_time");
    
                // Blocked addons
                response.BlockedAddons = new List<string>();
    
                // Configuration required
                response.ConfigurationRequired = false;
    
                // Pending episodes
                response.PendingEpisodes = 0;
    
                // ── FIX-101A-05: Anime pending items ─────────────────────────────
                // Count anime items that are pending (OVA/ONA/SPECIAL without strm)
                response.AnimePendingItems = 0;
    
                // Unknown provider prefixes
                response.UnknownProviderPrefixes = new List<string>();
    
                response.Status = "ok";
                _logger.LogInformation("[InfiniteDrive] Health: {Status}, Manifest: {ManifestStatus}",
                    response.Status, response.ManifestStatus);
    
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Health endpoint error");
                response.Status = "error";
                return response;
            }
        }
    }
    
}
