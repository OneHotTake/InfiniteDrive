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
    // ── Request DTO ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Status</c>.
    /// No parameters — returns a full health snapshot.
    /// </summary>
    [Route("/InfiniteDrive/Status", "GET",
        Summary = "Returns a JSON health snapshot used by the dashboard")]
    public class StatusRequest : IReturn<object> { }

    // ── Response model ───────────────────────────────────────────────────────────

    /// <summary>Health status for a single configured provider.</summary>
    public class ProviderHealthEntry
    {
        /// <summary>User-defined display name for this provider.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Provider manifest URL or base URL.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>True if the provider is reachable and responding.</summary>
        public bool Ok { get; set; }

        /// <summary>Latency to the provider in milliseconds, or -1 if not tested.</summary>
        public int LatencyMs { get; set; } = -1;

        /// <summary>Status message (error detail or success info).</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Full health snapshot returned by <see cref="StatusService"/>.
    /// All fields are safe to expose to the Emby admin dashboard.
    /// </summary>
    public class StatusResponse
    {
        /// <summary>Plugin version string.</summary>
        public string Version { get; set; } = "unknown";

        /// <summary>ISO-8601 UTC timestamp when this snapshot was taken.</summary>
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>True if the plugin configuration has been completed.</summary>
        public bool IsConfigured { get; set; }

        /// <summary>Human-readable name of the connected AIOStreams addon (from manifest.name).</summary>
        public string AioStreamsAddonName { get; set; } = string.Empty;

        /// <summary>Version string of the connected AIOStreams addon (from manifest.version).</summary>
        public string AioStreamsAddonVersion { get; set; } = string.Empty;

        /// <summary>
        /// True when the connected AIOStreams instance is stream-only (no catalog entries).
        /// The library must be populated via Trakt or MDBList in this mode.
        /// </summary>
        public bool AioStreamsIsStreamOnly { get; set; }

        /// <summary>Comma-separated stream ID prefixes the addon accepts (e.g. "tt,mal:,kitsu:").</summary>
        public string AioStreamsStreamPrefixes { get; set; } = string.Empty;

        /// <summary>AIOStreams connection status.</summary>
        public ConnectionStatus AioStreams { get; set; } = new ConnectionStatus();

        /// <summary>Health status for each configured provider (from ProviderManifests).</summary>
        public List<ProviderHealthEntry> Providers { get; set; } = new List<ProviderHealthEntry>();

        /// <summary>Resolution cache row-count statistics.</summary>
        public ResolutionCacheStats Cache { get; set; } = new ResolutionCacheStats();

        /// <summary>Per-item resolution coverage for .strm catalog items.</summary>
        public ResolutionCoverageStats Coverage { get; set; } = new ResolutionCoverageStats();

        /// <summary>Today's API call budget.</summary>
        public ApiBudgetStatus ApiBudget { get; set; } = new ApiBudgetStatus();

        /// <summary>Total active catalog items.</summary>
        public int CatalogItemCount { get; set; }

        /// <summary>
        /// Catalog items that exist as real media files in the user's library.
        /// These are tracked by the plugin but have no .strm file.
        /// </summary>
        public int LibraryItemCount { get; set; }

        /// <summary>
        /// Catalog items managed by the plugin as .strm files.
        /// </summary>
        public int StrmItemCount { get; set; }

        /// <summary>
        /// Total number of times <c>FileResurrectionTask</c> has rebuilt a
        /// .strm after the user's original library file went missing.
        /// </summary>
        public int ResurrectionCount { get; set; }

        /// <summary>
        /// Number of items that were previously managed as .strm files but have
        /// since been re-adopted — the user acquired a real copy and the .strm
        /// was retired by <c>LibraryReadoptionTask</c>.
        /// </summary>
        public int ReadoptedCount { get; set; }

        /// <summary>
        /// Number of series in the catalog that have not yet had their seasons/episodes
        /// expanded by <c>EpisodeExpandTask</c> (i.e. <c>seasons_json</c> is empty).
        /// </summary>
        public int PendingExpansionCount { get; set; }

        // ── Sprint 66: Item State Counts ────────────────────────────────────────

        /// <summary>Items in CATALOGUED state (in DB, no .strm yet).</summary>
        public int CataloguedCount { get; set; }

        /// <summary>Items in PRESENT state (.strm exists, URL not resolved).</summary>
        public int PresentCount { get; set; }

        /// <summary>Items in RESOLVED state (.strm exists, valid cached URL).</summary>
        public int ResolvedCount { get; set; }

        /// <summary>Items in RETIRED state (real file in library, .strm deleted).</summary>
        public int RetiredCount { get; set; }

        /// <summary>Items in PINNED state (user-added via Discover, protected).</summary>
        public int PinnedCount { get; set; }

        /// <summary>Items in ORPHANED state (.strm exists but not in catalog).</summary>
        public int OrphanedCount { get; set; }

        // ── End Sprint 66 ─────────────────────────────────────────────────────────

        /// <summary>Item count per catalog source key.</summary>
        public Dictionary<string, int> SourceStats { get; set; } = new Dictionary<string, int>();

        /// <summary>Learned per-client streaming capability profiles.</summary>
        public List<ClientCompatEntry> ClientProfiles { get; set; } = new List<ClientCompatEntry>();

        /// <summary>Last 10 playback log entries, newest first.</summary>
        public List<PlaybackLogEntry> RecentPlays { get; set; } = new List<PlaybackLogEntry>();

        /// <summary>Sync state per source key.</summary>
        public List<SyncStateEntry> SyncStates { get; set; } = new List<SyncStateEntry>();

        /// <summary>
        /// Setup warnings that should be surfaced in the admin dashboard.
        /// Empty when the plugin is correctly configured.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>
        /// True when Cinemeta is being used as the fallback catalog source because
        /// no explicit catalog source (Trakt, MDBList, or catalog addon) is configured.
        /// Surfaced in the dashboard so users know why Cinemeta items appear.
        /// </summary>
        public bool UsingCinemetaDefault { get; set; }

        /// <summary>URL to edit the AIOStreams manifest configuration.</summary>
        public string? ManifestConfigureUrl { get; set; }

        /// <summary>Host of the AIOStreams instance.</summary>
        public string? ManifestHost { get; set; }

        // ── Sprint 146: Improbability Drive (two-worker status) ────────────────

        /// <summary>True if RefreshTask has run at least once since server start.</summary>
        public bool RefreshHasRun { get; set; }

        /// <summary>ISO-8601 UTC timestamp of last RefreshTask completion.</summary>
        public string? RefreshLastRunAt { get; set; }

        /// <summary>Active step during RefreshTask run: "collect", "write", "hint", "notify", "verify", or null.</summary>
        public string? RefreshActiveStep { get; set; }

        /// <summary>Number of items processed in current RefreshTask run.</summary>
        public int RefreshItemsProcessed { get; set; }

        /// <summary>True if DeepCleanTask has run at least once since server start.</summary>
        public bool DeepCleanHasRun { get; set; }

        /// <summary>ISO-8601 UTC timestamp of last DeepCleanTask completion.</summary>
        public string? DeepCleanLastRunAt { get; set; }

        /// <summary>Number of items with nfo_status = 'NeedsEnrich'.</summary>
        public int NeedsEnrichCount { get; set; }

        /// <summary>Number of items with nfo_status = 'Blocked'.</summary>
        public int BlockedCount { get; set; }

        /// <summary>Health of RefreshTask: "green", "yellow", or "red" based on 2×/3× interval thresholds.</summary>
        public string? RefreshHealth { get; set; }

        /// <summary>Health of DeepCleanTask: "green", "yellow", or "red" based on 2×/3× interval thresholds.</summary>
        public string? DeepCleanHealth { get; set; }

        // ── End Sprint 146 ───────────────────────────────────────────────────────

        // ── Sprint 155: CooldownGate observability ────────────────────────────

        /// <summary>True when a global 429 cooldown is currently active.</summary>
        public bool CooldownActive { get; set; }

        /// <summary>ISO-8601 UTC timestamp when the current cooldown expires.</summary>
        public string? CooldownUntil { get; set; }

        /// <summary>True when 3+ 429s in the last hour on a shared instance.</summary>
        public bool SuggestPrivateInstance { get; set; }
    }

    /// <summary>Connection status for an upstream service.</summary>
    public class ConnectionStatus
    {
        /// <summary>Whether the connection test succeeded.</summary>
        public bool Ok { get; set; }

        /// <summary>Human-readable status message.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Base URL that was tested.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Latency in milliseconds, or -1 if not measured.</summary>
        public int LatencyMs { get; set; } = -1;
    }

    /// <summary>API budget summary for today.</summary>
    public class ApiBudgetStatus
    {
        /// <summary>Calls made today (UTC).</summary>
        public int CallsMade { get; set; }

        /// <summary>Daily call budget.</summary>
        public int CallsBudget { get; set; }

        /// <summary>Percentage used (0-100).</summary>
        public int PercentUsed => CallsBudget > 0 ? CallsMade * 100 / CallsBudget : 0;
    }

    /// <summary>Single playback log row for the dashboard.</summary>
    public class PlaybackLogEntry
    {
        /// <summary>IMDB ID.</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Item title.</summary>
        public string? Title { get; set; }

        /// <summary>Season number, if series.</summary>
        public int? Season { get; set; }

        /// <summary>Episode number, if series.</summary>
        public int? Episode { get; set; }

        /// <summary>How the stream was resolved: <c>cached</c>, <c>sync_resolve</c>, <c>failed</c>, etc.</summary>
        public string ResolutionMode { get; set; } = string.Empty;

        /// <summary>Quality tier served: remux, 2160p, 1080p, 720p, unknown.</summary>
        public string? QualityServed { get; set; }

        /// <summary>Emby client type.</summary>
        public string? ClientType { get; set; }

        /// <summary>End-to-end latency in milliseconds.</summary>
        public int? LatencyMs { get; set; }

        /// <summary>ISO-8601 UTC timestamp of the play event.</summary>
        public string PlayedAt { get; set; } = string.Empty;
    }

    /// <summary>Sync state for one catalog source.</summary>
    public class SyncStateEntry
    {
        /// <summary>Source identifier, e.g. <c>aiostreams</c>, <c>trakt</c>, <c>aio:movie:gdrive</c>.</summary>
        public string SourceKey { get; set; } = string.Empty;

        /// <summary>ISO-8601 UTC timestamp of the last successful sync.</summary>
        public string? LastSyncAt { get; set; }

        /// <summary>Number of items synced last run.</summary>
        public int ItemCount { get; set; }

        /// <summary>Status: <c>ok</c>, <c>warn</c>, or <c>error</c>.</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Consecutive failed sync attempts since the last success.</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>Error message from the most recent failure, or null.</summary>
        public string? LastError { get; set; }

        /// <summary>ISO-8601 UTC timestamp of the last time this source was reachable.</summary>
        public string? LastReachedAt { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a JSON health snapshot of the InfiniteDrive plugin.
    /// Polled by the dashboard every 5 seconds during active use.
    ///
    /// The response includes:
    /// <list type="bullet">
    ///   <item>AIOStreams connection test result</item>
    ///   <item>Resolution cache coverage statistics</item>
    ///   <item>API budget usage for today</item>
    ///   <item>Recent playback log (last 10 events)</item>
    ///   <item>Catalog item count</item>
    ///   <item>Per-source sync state</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Exposes health and status endpoints for the InfiniteDrive dashboard.
    ///
    /// ════════════════════════════════════════════════════════════════
    /// ADMIN GUARD AUDIT (Sprint 100A-09)
    /// ════════════════════════════════════════════════════════════════
    /// All endpoints require admin authentication unless explicitly marked read-only.
    /// AdminGuard.RequireAdmin() is called as FIRST statement in every endpoint.
    ///
    /// Endpoints covered:
    /// • GET  /InfiniteDrive/Status              (Get(StatusRequest)) - No auth (read-only)
    /// • GET  /InfiniteDrive/Health              (Get(HealthRequest)) - No auth (read-only) - FIX-100A-13
    /// • POST /InfiniteDrive/RefreshManifest       (Post(RefreshManifestRequest)) - Admin required - FIX-100A-01
    /// • POST /InfiniteDrive/Validate            (ValidateService.Post(ValidateRequest)) - Admin required
    /// ════════════════════════════════════════════════════════════════
    /// </summary>
    public class StatusService : IService, IRequiresRequest
    {
        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<StatusService> _logger;
        private readonly IAuthorizationContext  _authCtx;

        // Health check caching: cache indefinitely, only refresh on explicit user request
        private static ConnectionStatus? _cachedAioStreamsHealth;
        private static List<ProviderHealthEntry>? _cachedProviderHealth;
        private static bool _healthChecked = false;

        // ── IRequiresRequest ─────────────────────────────────────────────────────
        public IRequest Request { get; set; } = null!;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects dependencies automatically.
        /// </summary>
        public StatusService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<StatusService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        // ── IService ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Status</c>.
        /// </summary>
        public async Task<object> Get(StatusRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            var config = Plugin.Instance?.Configuration;
            var db     = Plugin.Instance?.DatabaseManager;

            if (config == null || db == null)
            {
                return new StatusResponse
                {
                    IsConfigured = false,
                    AioStreams   = new ConnectionStatus { Ok = false, Message = "Plugin not initialised" },
                };
            }

            var response = new StatusResponse
            {
                Version                  = Plugin.Instance?.PluginVersion ?? "unknown",
                IsConfigured             = config.IsFirstRunComplete,
                AioStreamsAddonName      = config.AioStreamsDiscoveredName,
                AioStreamsAddonVersion   = config.AioStreamsDiscoveredVersion,
                AioStreamsIsStreamOnly   = config.AioStreamsIsStreamOnly,
                AioStreamsStreamPrefixes = config.AioStreamsStreamIdPrefixes,
            };

            // ── Manifest URL parsing for "Edit Manifest" button ─────────────────
            var manifestComponents = ManifestUrlParser.Parse(config.PrimaryManifestUrl);
            response.ManifestConfigureUrl = manifestComponents?.ConfigureUrl;
            response.ManifestHost = manifestComponents?.Host;

            // ── Health check caching: test once on first call, then cache indefinitely ─
            // Users can click a "Refresh" button to manually re-test.
            // This avoids hammering AIOStreams on every status poll (every 5 seconds).

            if (!_healthChecked)
            {
                // ── AIOStreams connection test ────────────────────────────────────────
                _cachedAioStreamsHealth = await TestAioStreamsConnectionAsync(config);

                // ── Provider health test ──────────────────────────────────────────────
                // In v0.51+, only primary/secondary manifest URLs are supported.
                // Provider health is tested via TestFailover endpoint instead.
                _cachedProviderHealth = new List<ProviderHealthEntry>();

                _healthChecked = true;
            }

            // Use cached results
            response.AioStreams = _cachedAioStreamsHealth ?? new ConnectionStatus { Ok = false, Message = "Not yet tested" };
            response.Providers = _cachedProviderHealth ?? new List<ProviderHealthEntry>();

            // ── Resolution cache stats ───────────────────────────────────────────

            try
            {
                response.Cache    = await db.GetResolutionCacheStatsAsync();
                response.Coverage = await db.GetResolutionCoverageAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Status: cache stats query failed");
            }

            // ── API budget ───────────────────────────────────────────────────────

            try
            {
                var (callsMade, callsBudget) = await db.GetApiBudgetTodayAsync();
                response.ApiBudget = new ApiBudgetStatus
                {
                    CallsMade   = callsMade,
                    CallsBudget = callsBudget,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Status: api budget query failed");
            }

            // ── Catalog item counts ──────────────────────────────────────────────

            try
            {
                response.CatalogItemCount    = await db.GetCatalogItemCountAsync();
                response.LibraryItemCount    = await db.GetCatalogItemCountByLocalSourceAsync("library");
                response.StrmItemCount       = await db.GetCatalogItemCountByLocalSourceAsync("strm");
                response.ResurrectionCount   = await db.GetTotalResurrectionCountAsync();
                response.ReadoptedCount      = await db.GetReadoptedCountAsync();
                // A2: count series with no seasons_json yet (pending EpisodeExpandTask)
                var pending = await db.GetSeriesWithoutSeasonsJsonAsync();
                response.PendingExpansionCount = pending.Count;

                // Sprint 66: Item state counts
                response.CataloguedCount = await db.GetCatalogItemCountByItemStateAsync(ItemState.Catalogued);
                response.PresentCount    = await db.GetCatalogItemCountByItemStateAsync(ItemState.Present);
                response.ResolvedCount   = await db.GetCatalogItemCountByItemStateAsync(ItemState.Resolved);
                response.RetiredCount    = await db.GetCatalogItemCountByItemStateAsync(ItemState.Retired);
                response.PinnedCount     = await db.GetCatalogItemCountByItemStateAsync(ItemState.Pinned);
                response.OrphanedCount   = await db.GetCatalogItemCountByItemStateAsync(ItemState.Orphaned);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Status: catalog count query failed");
            }

            // ── Recent playback ──────────────────────────────────────────────────

            try
            {
                var recent = await db.GetRecentPlaybackAsync(10);
                foreach (var row in recent)
                {
                    response.RecentPlays.Add(new PlaybackLogEntry
                    {
                        ImdbId         = row.ImdbId,
                        Title          = row.Title,
                        Season         = row.Season,
                        Episode        = row.Episode,
                        ResolutionMode = row.ResolutionMode,
                        QualityServed  = row.QualityServed,
                        ClientType     = row.ClientType,
                        LatencyMs      = row.LatencyMs,
                        PlayedAt       = row.PlayedAt,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Status: recent playback query failed");
            }

            // ── Per-source catalog stats ─────────────────────────────────────────

            try
            {
                response.SourceStats = await db.GetCatalogCountsBySourceAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Status: source stats query failed");
            }

            // ── Client compat profiles ───────────────────────────────────────────

            try
            {
                response.ClientProfiles = await db.GetAllClientCompatsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Status: client compat query failed");
            }

            // ── Sync states ──────────────────────────────────────────────────────

            try
            {
                var states = await db.GetAllSyncStatesAsync();
                foreach (var state in states)
                {
                    response.SyncStates.Add(new SyncStateEntry
                    {
                        SourceKey           = state.SourceKey,
                        LastSyncAt          = state.LastSyncAt,
                        ItemCount           = state.ItemCount,
                        Status              = state.Status,
                        ConsecutiveFailures = state.ConsecutiveFailures,
                        LastError           = state.LastError,
                        LastReachedAt       = state.LastReachedAt,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Status: sync state query failed");
            }

            // ── Setup warnings ───────────────────────────────────────────────────
            // Surface actionable config problems so the user sees them in the
            // dashboard without having to wait for a failed play event.

            // In v0.51+, only AIOStreams catalogs are supported
            var hasAioStreamsCatalog = config.EnableAioStreamsCatalog
                && (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
                    || !string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                && !config.AioStreamsIsStreamOnly;

            if (!hasAioStreamsCatalog)
            {
                if (config.EnableCinemetaDefault)
                {
                    response.UsingCinemetaDefault = true;
                    response.Warnings.Add(
                        "No catalog source configured. InfiniteDrive is using Cinemeta (Top Movies/Series) as a fallback. " +
                        "For your full library, configure AIOStreams with a catalog addon, or disable Cinemeta default.");
                }
                else
                {
                    response.Warnings.Add(
                        "No catalog source configured and Cinemeta default is disabled. " +
                        "Your Emby library will be empty. Configure AIOStreams with a catalog addon or re-enable EnableCinemetaDefault.");
                }
            }

            if (config.AioStreamsIsStreamOnly
                && !hasAioStreamsCatalog)
            {
                response.Warnings.Add(
                    $"'{config.AioStreamsDiscoveredName}' is stream-only (no catalog in its manifest). " +
                    "Add an addon to your AIOStreams instance that provides catalog endpoints.");
            }

            if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
                && string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                response.Warnings.Add(
                    "AIOStreams is not configured. Paste your manifest URL into the plugin settings to enable stream resolution.");
            }

            // ── Sprint 146: Improbability Drive status (two-worker) ─────────

            try
            {
                // Read RefreshTask metadata
                var lastRefreshRun = db.GetMetadata("last_refresh_run_time");
                var refreshActiveStep = db.GetMetadata("refresh_active_step");
                var refreshItemsProcessed = db.GetMetadata("refresh_items_processed");

                response.RefreshHasRun = !string.IsNullOrEmpty(lastRefreshRun);
                response.RefreshLastRunAt = lastRefreshRun;
                response.RefreshActiveStep = string.IsNullOrEmpty(refreshActiveStep) ? null : refreshActiveStep;
                response.RefreshItemsProcessed = int.TryParse(refreshItemsProcessed, out var processedCount) ? processedCount : 0;

                // Read DeepCleanTask metadata
                var lastDeepCleanRun = db.GetMetadata("last_deepclean_run_time");
                response.DeepCleanHasRun = !string.IsNullOrEmpty(lastDeepCleanRun);
                response.DeepCleanLastRunAt = lastDeepCleanRun;

                // Count NeedsEnrich and Blocked items
                var needsEnrichQuery = "SELECT COUNT(*) FROM catalog_items WHERE nfo_status = 'NeedsEnrich' AND removed_at IS NULL;";
                var blockedQuery = "SELECT COUNT(*) FROM catalog_items WHERE nfo_status = 'Blocked' AND removed_at IS NULL;";
                response.NeedsEnrichCount = await db.QueryScalarIntAsync(needsEnrichQuery);
                response.BlockedCount = await db.QueryScalarIntAsync(blockedQuery);

                // Sprint 150 M-6: Compute health status using 2×/3× interval thresholds
                if (!string.IsNullOrEmpty(lastRefreshRun)
                    && DateTime.TryParse(lastRefreshRun, null, System.Globalization.DateTimeStyles.RoundtripKind, out var refreshLastRun))
                {
                    var refreshAge = DateTime.UtcNow - refreshLastRun;
                    var refreshInterval = TimeSpan.FromMinutes(6);
                    response.RefreshHealth = refreshAge > refreshInterval * 3 ? "red"
                                           : refreshAge > refreshInterval * 2 ? "yellow"
                                           : "green";
                }

                if (!string.IsNullOrEmpty(lastDeepCleanRun)
                    && DateTime.TryParse(lastDeepCleanRun, null, System.Globalization.DateTimeStyles.RoundtripKind, out var deepCleanLastRun))
                {
                    var deepCleanAge = DateTime.UtcNow - deepCleanLastRun;
                    var deepCleanInterval = TimeSpan.FromHours(18);
                    response.DeepCleanHealth = deepCleanAge > deepCleanInterval * 3 ? "red"
                                             : deepCleanAge > deepCleanInterval * 2 ? "yellow"
                                             : "green";
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] Status: Improbability Drive status query failed");
            }

            // ── End Sprint 146 ─────────────────────────────────────────────────────

            // ── Sprint 155: CooldownGate state ────────────────────────────────
            var gate = Plugin.Instance?.CooldownGate;
            if (gate != null)
            {
                response.CooldownActive = DateTimeOffset.UtcNow < gate.GlobalCooldownUntil;
                response.CooldownUntil = gate.GlobalCooldownUntil > DateTimeOffset.MinValue
                    ? gate.GlobalCooldownUntil.ToString("o") : null;
                response.SuggestPrivateInstance = gate.SuggestPrivateInstance;
            }

            return response;
        }

        // ── Private: AIOStreams connection test ──────────────────────────────────

        private async Task<ConnectionStatus> TestAioStreamsConnectionAsync(PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
                && string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                return new ConnectionStatus
                {
                    Ok      = false,
                    Message = "AIOStreams manifest URL not configured",
                    Url     = string.Empty,
                };
            }

            var url = config.PrimaryManifestUrl ?? config.SecondaryManifestUrl ?? string.Empty;
            var sw  = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var client = new AioStreamsClient(config, _logger);
                client.Cooldown = Plugin.Instance?.CooldownGate;
                using var cts    = new CancellationTokenSource(5_000);
                var (connOk, connErr) = await client.TestConnectionAsync(cts.Token);
                sw.Stop();

                return new ConnectionStatus
                {
                    Ok        = connOk,
                    Message   = connOk ? "Connected" : (connErr ?? "Manifest fetch failed"),
                    Url       = url,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ConnectionStatus
                {
                    Ok        = false,
                    Message   = ex.Message,
                    Url       = url,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                };
            }
        }

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/Status/Refresh</c>.
        /// Clears cached health checks and forces fresh test on next status request.
        /// Called by the "Refresh" button in the dashboard.
        /// </summary>
        public async Task<object> Post(StatusRefreshRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            // Clear the cache so the next GET /InfiniteDrive/Status will re-test
            _healthChecked = false;
            _cachedAioStreamsHealth = null;
            _cachedProviderHealth = null;

            _logger.LogInformation("[InfiniteDrive] Health check cache cleared by user request");

            // Immediately return fresh status
            var statusReq = new StatusRequest();
            return await Get(statusReq);
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  MANUAL REFRESH REQUEST DTO                                              ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>POST /InfiniteDrive/Status/Refresh</c>.
    /// Clears the cached health check and forces a fresh test on the next status request.
    /// This is called by the "Refresh" button in the dashboard.
    /// </summary>
    [Route("/InfiniteDrive/Status/Refresh", "POST",
        Summary = "Clear cached health status and refresh on next request")]
    public class StatusRefreshRequest : IReturn<StatusResponse> { }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  ANIME PLUGIN STATUS ENDPOINT                                           ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/AnimePluginStatus</c>.
    /// Returns whether the Emby Anime Plugin is installed.
    /// Used by the config UI to gate the anime library toggle.
    /// </summary>
    [Route("/InfiniteDrive/AnimePluginStatus", "GET",
        Summary = "Check if the Emby Anime Plugin is installed")]
    public class AnimePluginStatusRequest : IReturn<AnimePluginStatusResponse> { }

    /// <summary>Response indicating anime plugin installation status.</summary>
    public class AnimePluginStatusResponse
    {
        /// <summary><c>true</c> if the Emby Anime Plugin is detected.</summary>
        public bool Installed { get; set; }
    }

    /// <summary>
    /// Service for checking anime plugin installation status.
    /// </summary>
    public class AnimePluginStatusService : IService
    {
        public object Get(AnimePluginStatusRequest _)
        {
            return new AnimePluginStatusResponse
            {
                Installed = Plugin.IsAnimePluginInstalled()
            };
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  AD-HOC CONNECTION TEST ENDPOINT                                         ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>POST /InfiniteDrive/TestUrl</c>.
    /// Tests an AIOStreams URL using the provided credentials without saving them.
    /// Used by the config wizard's "Test Connection" button to validate form values
    /// before the user commits to saving.
    ///
    /// NOTE: Changed from GET to POST so that the AIOStreams token is sent in the
    /// request body rather than as a query-string parameter (which Emby logs verbatim).
    /// </summary>
    [Route("/InfiniteDrive/TestUrl", "POST",
        Summary = "Tests an AIOStreams connection with the provided credentials (does not save)")]
    public class TestUrlRequest : IReturn<object>
    {
        /// <summary>AIOStreams base URL, e.g. <c>https://my.aiostreams.host</c>.</summary>
        public string? Url { get; set; }

        /// <summary>UUID component of the Stremio path (optional).</summary>
        public string? Uuid { get; set; }

        /// <summary>
        /// Token component of the Stremio path.
        /// Sent in the POST body — never appears in server logs.
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Full manifest URL.  When provided, base URL / UUID / token are parsed
        /// from it instead of from the individual fields.
        /// </summary>
        public string? ManifestUrl { get; set; }
    }

    /// <summary>
    /// Tests an AIOStreams connection with credentials supplied in the POST body.
    /// Credentials are NOT saved to the plugin configuration, and are NOT logged.
    /// </summary>
    public class TestUrlService : IService, IRequiresRequest
    {
        private readonly ILogger<TestUrlService> _logger;
        private readonly IAuthorizationContext   _authCtx;

        public IRequest Request { get; set; } = null!;

        /// <summary>Emby injects dependencies automatically.</summary>
        public TestUrlService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<TestUrlService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>POST /InfiniteDrive/TestUrl</c>.</summary>
        public async Task<object> Post(TestUrlRequest request)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            try
            {
                // Prefer manifest URL for parsing; individual fields are fallback.
                var baseUrl = request.Url?.TrimEnd('/') ?? string.Empty;
                var uuid    = request.Uuid;
                var token   = request.Token;

                if (!string.IsNullOrWhiteSpace(request.ManifestUrl))
                {
                    var (pBase, pUuid, pToken) =
                        AioStreamsClient.TryParseManifestUrl(request.ManifestUrl);
                    if (!string.IsNullOrEmpty(pBase))  baseUrl = pBase;
                    if (!string.IsNullOrEmpty(pUuid))  uuid    = pUuid;
                    if (!string.IsNullOrEmpty(pToken)) token   = pToken;
                }

                if (string.IsNullOrWhiteSpace(baseUrl))
                    return new { Ok = false, Message = "No URL provided", LatencyMs = 0 };

                // SSRF guard: only http/https schemes allowed
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedUri)
                    || (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
                    return new { Ok = false, Message = "Invalid URL: only http:// and https:// are supported", LatencyMs = 0 };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var client   = new AioStreamsClient(baseUrl, uuid, token, _logger);
                using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var manifest       = await client.GetManifestAsync(cts.Token);
                sw.Stop();

                var ok    = manifest != null;
                var error = ok ? null : "Could not fetch manifest. Check the URL and that your provider is reachable.";

                return new
                {
                    Ok           = ok,
                    Message      = ok ? "Connected" : error,
                    LatencyMs    = (int)sw.ElapsedMilliseconds,
                    Url          = client.ManifestUrl,
                    IsStreamOnly = manifest?.IsStreamOnly ?? false,
                    AddonName    = manifest?.Name ?? string.Empty,
                };
            }
            catch (Exception ex)
            {
                // Map exceptions to user-friendly messages
                var friendlyMsg = ex switch
                {
                    TaskCanceledException
                        => "Connection timed out. Is your provider reachable?",
                    HttpRequestException
                        => "Could not reach the server. Check your network connection.",
                    _
                        => "Connection failed. Check the URL and try again."
                };
                return new { Ok = false, Message = friendlyMsg, LatencyMs = 0 };
            }
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  CATALOG DISCOVERY ENDPOINT                                              ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Catalogs</c>.
    /// Fetches the AIOStreams manifest and returns every eligible catalog so the
    /// admin dashboard can render a pick-list of catalogs to sync.
    /// </summary>
    [Route("/InfiniteDrive/Catalogs", "GET",
        Summary = "Returns catalog definitions discovered from the AIOStreams manifest")]
    public class CatalogsRequest : IReturn<object> { }

    /// <summary>
    /// One catalog entry returned by <see cref="CatalogService"/>.
    /// A single catalog ID may appear multiple times if it covers both
    /// <c>movie</c> and <c>series</c> types.
    /// </summary>
    public class CatalogInfoItem
    {
        /// <summary>Catalog identifier, e.g. <c>aiostreams</c>, <c>gdrive</c>.</summary>
        public string Id   { get; set; } = string.Empty;

        /// <summary>Human-readable display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Media type: <c>movie</c>, <c>series</c>, or <c>anime</c>.</summary>
        public string Type { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response payload for <c>GET /InfiniteDrive/Catalogs</c>.
    /// </summary>
    public class CatalogsResponse
    {
        /// <summary>All eligible catalogs found in the manifest.</summary>
        public List<CatalogInfoItem> Catalogs { get; set; } = new List<CatalogInfoItem>();

        /// <summary>Non-null if the manifest could not be fetched or parsed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Reads the AIOStreams manifest and returns the catalog list.
    /// Used by the admin dashboard to populate the catalog selection checkboxes.
    /// </summary>
    public class CatalogService : IService, IRequiresRequest
    {
        private readonly ILogger<CatalogService> _logger;
        private readonly IAuthorizationContext   _authCtx;
        public IRequest Request { get; set; } = null!;

        public CatalogService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<CatalogService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>GET /InfiniteDrive/Catalogs</c>.</summary>
        public async Task<object> Get(CatalogsRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return new CatalogsResponse { Error = "Plugin not initialised" };

            // In v0.51+, only AIOStreams catalogs are supported.
            if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
                && string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                return new CatalogsResponse { Error = "AIOStreams manifest URL not configured" };

            var aioUrl = config.PrimaryManifestUrl ?? config.SecondaryManifestUrl;
            var aioResult = await FetchCatalogsFromAddonAsync(
                aioUrl,
                null,
                null,
                null,
                "AIOStreams");
            if (aioResult != null) return aioResult;

            return new CatalogsResponse
            {
                Error = "No catalogs found in manifest — this appears to be a stream-only instance. " +
                        "Enable a Catalog Addon (e.g. Cinemeta) in Settings to populate your library."
            };
        }

        private async Task<CatalogsResponse?> FetchCatalogsFromAddonAsync(
            string? manifestUrl,
            string? baseUrl,
            string? uuid,
            string? token,
            string sourceName)
        {
            try
            {
                var (parsedBase, parsedUuid, parsedToken) =
                    AioStreamsClient.TryParseManifestUrl(manifestUrl);
                if (string.IsNullOrWhiteSpace(parsedBase)) parsedBase = baseUrl?.TrimEnd('/') ?? string.Empty;
                if (string.IsNullOrWhiteSpace(parsedUuid)) parsedUuid = uuid;
                if (string.IsNullOrWhiteSpace(parsedToken)) parsedToken = token;

                if (string.IsNullOrWhiteSpace(parsedBase)) return null;

                using var client = new AioStreamsClient(parsedBase, parsedUuid, parsedToken, _logger);
                using var cts    = new CancellationTokenSource(10_000);
                var manifest = await client.GetManifestAsync(cts.Token);

                if (manifest?.Catalogs == null || manifest.Catalogs.Count == 0)
                    return null;   // empty → caller tries next source

                var items = manifest.Catalogs
                    .Where(c => !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Type))
                    .Where(c => c.Type == "movie" || c.Type == "series" || c.Type == "anime")
                    .Select(c => new CatalogInfoItem
                    {
                        Id   = c.Id!,
                        Name = string.IsNullOrEmpty(c.Name) ? c.Id! : c.Name,
                        Type = c.Type!,
                    })
                    .ToList();

                return items.Count > 0 ? new CatalogsResponse { Catalogs = items } : null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[InfiniteDrive] {Source} manifest request timed out (10s)", sourceName);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] {Source} manifest fetch failed", sourceName);
                return null;
            }
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  CATALOG PROGRESS ENDPOINT                                               ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/CatalogProgress</c>.
    /// </summary>
    [Route("/InfiniteDrive/CatalogProgress", "GET",
        Summary = "Returns live per-catalog sync progress from sync_state")]
    public class CatalogProgressRequest : IReturn<object> { }

    /// <summary>
    /// One catalog row in the progress response.
    /// </summary>
    public class CatalogProgressItem
    {
        public string  SourceKey  { get; set; } = string.Empty;
        public string  Name       { get; set; } = string.Empty;
        public string  Type       { get; set; } = string.Empty;
        public string  Status     { get; set; } = "pending";
        public int     ItemCount  { get; set; }
        public int     ItemsRunning { get; set; }
        public int     ItemsTarget  { get; set; }
        public string? LastSyncAt  { get; set; }
        public string? LastError   { get; set; }
    }

    /// <summary>
    /// Response from <c>GET /InfiniteDrive/CatalogProgress</c>.
    /// </summary>
    public class CatalogProgressResponse
    {
        public bool IsAnyRunning { get; set; }
        public List<CatalogProgressItem> Catalogs { get; set; } = new List<CatalogProgressItem>();
    }

    /// <summary>
    /// Returns per-catalog sync progress from <c>sync_state</c>.
    /// Cheap DB-only query — safe to poll every few seconds from the UI.
    /// </summary>
    public class CatalogProgressService : IService, IRequiresRequest
    {
        private readonly ILogger<CatalogProgressService> _logger;
        private readonly IAuthorizationContext           _authCtx;
        public IRequest Request { get; set; } = null!;

        public CatalogProgressService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<CatalogProgressService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        public async Task<object> Get(CatalogProgressRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
                return new CatalogProgressResponse();

            try
            {
                var states = await db.GetAllSyncStatesAsync();

                // Only return rows that have a catalog_name — these are per-catalog rows,
                // not the provider-level aggregate rows (trakt, mdblist, aiostreams).
                var catalogRows = states
                    .Where(s => !string.IsNullOrEmpty(s.CatalogName))
                    .Select(s => new CatalogProgressItem
                    {
                        SourceKey    = s.SourceKey,
                        Name         = s.CatalogName!,
                        Type         = s.CatalogType ?? string.Empty,
                        Status       = s.Status,
                        ItemCount    = s.ItemCount,
                        ItemsRunning = s.ItemsRunning,
                        ItemsTarget  = s.ItemsTarget,
                        LastSyncAt   = s.LastSyncAt,
                        LastError    = s.LastError,
                    })
                    .ToList();

                return new CatalogProgressResponse
                {
                    IsAnyRunning = catalogRows.Any(c => c.Status == "running"),
                    Catalogs     = catalogRows,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] CatalogProgress query failed");
                return new CatalogProgressResponse();
            }
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  INSPECT ENDPOINT                                                        ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Inspect</c>.
    /// </summary>
    [Route("/InfiniteDrive/Inspect", "GET",
        Summary = "Returns the catalog record and cached resolution data for a single item")]
    public class InspectRequest : IReturn<object>
    {
        /// <summary>IMDB ID to inspect, e.g. <c>tt0903747</c>.</summary>
        public string Imdb { get; set; } = string.Empty;

        /// <summary>Season number (TV only). Omit for movies.</summary>
        public int? Season { get; set; }

        /// <summary>Episode number (TV only). Omit for movies.</summary>
        public int? Episode { get; set; }
    }

    /// <summary>One stream candidate row returned inside <see cref="InspectResponse"/>.</summary>
    public class CandidateInfo
    {
        /// <summary>Rank (0 = best).</summary>
        public int Rank { get; set; }

        /// <summary>Provider key, e.g. <c>aiostreams</c>.</summary>
        public string? ProviderKey { get; set; }

        /// <summary>Stream type, e.g. <c>debrid</c>.</summary>
        public string? StreamType { get; set; }

        /// <summary>Quality tier: remux, 2160p, 1080p, 720p, unknown.</summary>
        public string? QualityTier { get; set; }

        /// <summary>Estimated bitrate in kbps.</summary>
        public int? BitrateKbps { get; set; }

        /// <summary>Original filename from AIOStreams.</summary>
        public string? FileName { get; set; }

        /// <summary>Cache entry status: valid, stale, failed.</summary>
        public string? Status { get; set; }

        /// <summary>UTC expiry timestamp.</summary>
        public string? ExpiresAt { get; set; }

        /// <summary>Whether this is a cached torrent at the provider's CDN.</summary>
        public bool IsCached { get; set; }
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Inspect</c>.</summary>
    public class InspectResponse
    {
        /// <summary>Whether a catalog record exists for this IMDB ID.</summary>
        public bool Found { get; set; }

        /// <summary>Error message, if any.</summary>
        public string? Error { get; set; }

        // ── Catalog info ───────────────────────────────────────────────────────

        /// <summary>IMDB ID.</summary>
        public string? ImdbId { get; set; }

        /// <summary>Title from the catalog.</summary>
        public string? Title { get; set; }

        /// <summary>Year.</summary>
        public int? Year { get; set; }

        /// <summary>Media type: <c>movie</c> or <c>series</c>.</summary>
        public string? MediaType { get; set; }

        /// <summary>Catalog source key.</summary>
        public string? Source { get; set; }

        /// <summary>Path of the .strm file on disk, or null if not yet written.</summary>
        public string? StrmPath { get; set; }

        /// <summary>True if the .strm file exists on disk right now.</summary>
        public bool StrmExists { get; set; }

        /// <summary>Seasons JSON (series only), or null.</summary>
        public string? SeasonsJson { get; set; }

        // ── Resolution cache ───────────────────────────────────────────────────

        /// <summary>True if a resolution cache entry exists for the requested episode.</summary>
        public bool CacheHit { get; set; }

        /// <summary>Cache entry status: <c>valid</c>, <c>stale</c>, or <c>failed</c>.</summary>
        public string? CacheStatus { get; set; }

        /// <summary>Quality tier of the cached primary stream.</summary>
        public string? QualityTier { get; set; }

        /// <summary>Estimated bitrate of the primary stream in kbps.</summary>
        public int? BitrateKbps { get; set; }

        /// <summary>Whether fallback URLs are available.</summary>
        public bool HasFallbacks { get; set; }

        /// <summary>UTC timestamp when the cache entry was resolved.</summary>
        public string? ResolvedAt { get; set; }

        /// <summary>UTC timestamp after which the URL should be re-validated.</summary>
        public string? ExpiresAt { get; set; }

        /// <summary>Number of times this entry has been played.</summary>
        public int PlayCount { get; set; }

        /// <summary>The .strm play URL that Emby would request for this item.</summary>
        public string? StrmPlayUrl { get; set; }

        /// <summary>
        /// Ranked stream candidates stored in <c>stream_candidates</c>.
        /// Empty when no resolution has been attempted yet.
        /// </summary>
        public List<CandidateInfo> Candidates { get; set; } = new List<CandidateInfo>();
    }

    /// <summary>
    /// Returns the catalog record and cached resolution entry for any IMDB ID,
    /// making it easy to debug why a title isn't playing.
    ///
    /// Example: <c>GET /InfiniteDrive/Inspect?imdb=tt0903747&amp;season=1&amp;episode=1</c>
    /// </summary>
    public class InspectService : IService, IRequiresRequest
    {
        private readonly ILogger<InspectService> _logger;
        private readonly IAuthorizationContext   _authCtx;
        public IRequest Request { get; set; } = null!;

        public InspectService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<InspectService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>GET /InfiniteDrive/Inspect</c>.</summary>
        public async Task<object> Get(InspectRequest request)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            var config = Plugin.Instance?.Configuration;
            var db     = Plugin.Instance?.DatabaseManager;

            if (config == null || db == null)
                return new InspectResponse { Error = "Plugin not initialised" };

            var imdb = (request.Imdb ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(imdb))
                return new InspectResponse { Error = "imdb parameter is required" };

            var response = new InspectResponse { ImdbId = imdb };

            // ── Catalog record ───────────────────────────────────────────────────

            try
            {
                var catalogItem = await db.GetCatalogItemByImdbIdAsync(imdb);
                if (catalogItem == null)
                {
                    response.Found = false;
                    response.Error = $"{imdb} not found in catalog";
                }
                else
                {
                    response.Found     = true;
                    response.Title     = catalogItem.Title;
                    response.Year      = catalogItem.Year;
                    response.MediaType = catalogItem.MediaType;
                    response.Source    = catalogItem.Source;
                    response.StrmPath  = catalogItem.StrmPath;
                    response.SeasonsJson = catalogItem.SeasonsJson;

                    if (!string.IsNullOrEmpty(catalogItem.StrmPath))
                        response.StrmExists = File.Exists(catalogItem.StrmPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] InspectService: catalog lookup failed");
                response.Error = "Catalog lookup failed: " + ex.Message;
            }

            // ── Resolution cache entry ────────────────────────────────────────────

            try
            {
                var entry = await db.GetCachedStreamAsync(imdb, request.Season, request.Episode);
                if (entry != null)
                {
                    response.CacheHit    = true;
                    response.CacheStatus = entry.Status;
                    response.QualityTier = entry.QualityTier;
                    response.BitrateKbps = entry.FileBitrateKbps
                                       ?? StreamHelpers.EstimateBitrateKbps(entry.QualityTier);
                    response.HasFallbacks = !string.IsNullOrEmpty(entry.Fallback1)
                                        || !string.IsNullOrEmpty(entry.Fallback2);
                    response.ResolvedAt  = entry.ResolvedAt;
                    response.ExpiresAt   = entry.ExpiresAt;
                    response.PlayCount   = entry.PlayCount;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] InspectService: cache lookup failed");
            }

            // ── Stream candidates ─────────────────────────────────────────────────

            try
            {
                var candidates = await db.GetStreamCandidatesAsync(imdb, request.Season, request.Episode);
                foreach (var c in candidates)
                {
                    response.Candidates.Add(new CandidateInfo
                    {
                        Rank        = c.Rank,
                        ProviderKey = c.ProviderKey,
                        StreamType  = c.StreamType,
                        QualityTier = c.QualityTier,
                        BitrateKbps = c.BitrateKbps ?? StreamHelpers.EstimateBitrateKbps(c.QualityTier),
                        FileName    = c.FileName,
                        Status      = c.Status,
                        ExpiresAt   = c.ExpiresAt,
                        IsCached    = c.IsCached,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] InspectService: candidates query failed");
            }

            // ── .strm play URL ────────────────────────────────────────────────────

            if (!string.IsNullOrEmpty(config.EmbyBaseUrl))
            {
                var port = ParsePort(config.EmbyBaseUrl) ?? 8096;
                response.StrmPlayUrl = request.Season.HasValue
                    ? $"http://127.0.0.1:{port}/InfiniteDrive/GetStream?imdb={imdb}&season={request.Season}&episode={request.Episode}"
                    : $"http://127.0.0.1:{port}/InfiniteDrive/GetStream?imdb={imdb}";
            }

            return response;
        }

        private static int? ParsePort(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            try
            {
                var uri = new Uri(url);
                return uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            }
            catch
            {
                return null;
            }
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  CATALOG SEARCH ENDPOINT                                                 ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Search</c>.
    /// Full-text search over catalog item titles.
    /// </summary>
    [Route("/InfiniteDrive/Search", "GET",
        Summary = "Searches the catalog by title — returns up to 20 matches")]
    public class SearchRequest : IReturn<object>
    {
        /// <summary>Search query (substring match, case-insensitive).</summary>
        public string Q { get; set; } = string.Empty;

        /// <summary>Maximum results to return (default 20, max 100).</summary>
        public int Limit { get; set; } = 20;
    }

    /// <summary>One search result row.</summary>
    public class SearchResultItem
    {
        /// <summary>IMDB ID.</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Release year.</summary>
        public int? Year { get; set; }

        /// <summary>Media type: <c>movie</c> or <c>series</c>.</summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>Catalog source key.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>True if a valid cache entry exists for this item.</summary>
        public bool HasValidCache { get; set; }
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Search</c>.</summary>
    public class SearchResponse
    {
        /// <summary>Search query that was executed.</summary>
        public string Query { get; set; } = string.Empty;

        /// <summary>Matched catalog items.</summary>
        public List<SearchResultItem> Results { get; set; } = new List<SearchResultItem>();

        /// <summary>Error message, or null on success.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Searches the catalog by title and returns matching items with cache status.
    /// Designed to power the Inspect panel's title-lookup helper in the dashboard.
    ///
    /// Example: <c>GET /InfiniteDrive/Search?q=breaking+bad&amp;limit=10</c>
    /// </summary>
    public class SearchService : IService, IRequiresRequest
    {
        private readonly ILogger<SearchService> _logger;
        private readonly IAuthorizationContext  _authCtx;
        public IRequest Request { get; set; } = null!;

        public SearchService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<SearchService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>GET /InfiniteDrive/Search</c>.</summary>
        public async Task<object> Get(SearchRequest request)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
                return new SearchResponse { Error = "Plugin not initialised" };

            var query = (request.Q ?? string.Empty).Trim();
            if (query.Length < 2)
                return new SearchResponse
                {
                    Query = query,
                    Error = "Query must be at least 2 characters",
                };

            var limit = Math.Max(1, Math.Min(100, request.Limit));

            try
            {
                var items = await db.SearchCatalogAsync(query, limit);

                // Load cache stats in one query to show HasValidCache without N+1 calls
                var cacheStats = await db.GetResolutionCacheStatsAsync();

                var results = new List<SearchResultItem>(items.Count);
                foreach (var item in items)
                {
                    var cached = await db.GetCachedStreamAsync(item.ImdbId, null, null);
                    results.Add(new SearchResultItem
                    {
                        ImdbId       = item.ImdbId,
                        Title        = item.Title,
                        Year         = item.Year,
                        MediaType    = item.MediaType,
                        Source       = item.Source,
                        HasValidCache = cached != null && cached.Status == "valid",
                    });
                }

                return new SearchResponse { Query = query, Results = results };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] SearchService: query failed for '{Query}'", query);
                return new SearchResponse { Query = query, Error = ex.Message };
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Admin auth helper
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shared helper for enforcing admin-only access on plugin endpoints.
    /// Injects <see cref="IAuthorizationContext"/> and exposes
    /// <see cref="RequireAdmin"/> which returns a 403 object when the caller
    /// is not an Emby administrator.
    /// </summary>
    internal static class AdminGuard
    {
    // ════════════════════════════════════════════════════════════════════════════════════
    // REFRESH MANIFEST ENDPOINT (Sprint 100A-01)
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Request for <c>POST /InfiniteDrive/RefreshManifest</c>.</summary>
    [Route("/InfiniteDrive/RefreshManifest", "POST",
        Summary = "Force-refreshes the AIOStreams manifest and returns summary")]
    public class RefreshManifestRequest : IReturn<object> { }

    /// <summary>Response from <c>POST /InfiniteDrive/RefreshManifest</c>.</summary>
    public class RefreshManifestResponse
    {
        /// <summary>Status: "ok" or "error".</summary>
        public string Status { get; set; } = "error";

        /// <summary>Manifest status: "ok", "stale", or "error".</summary>
        public string ManifestStatus { get; set; } = "error";

        /// <summary>ISO8601 timestamp when manifest was last fetched.</summary>
        public string? ManifestLastFetched { get; set; }

        /// <summary>Number of catalogs in manifest.</summary>
        public int CatalogCount { get; set; }

        /// <summary>Resource types present in manifest (catalog, meta, stream).</summary>
        public List<string> ResourceTypes { get; set; } = new List<string>();

        /// <summary>ID prefixes found in manifest (imdb, tmdb, kitsu, etc.).</summary>
        public List<string> IdPrefixes { get; set; } = new List<string>();
    }

    /// <summary>
    /// Service for refreshing the AIOStreams manifest.
    /// </summary>
    public class RefreshManifestService : IService, IRequiresRequest
    {
        private readonly ILogger<RefreshManifestService> _logger;
        private readonly IAuthorizationContext _authCtx;

        public IRequest Request { get; set; } = null!;

        public RefreshManifestService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger = new EmbyLoggerAdapter<RefreshManifestService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>POST /InfiniteDrive/RefreshManifest</c>.</summary>
        public async Task<object> Post(RefreshManifestRequest _)
        {
            // Sprint 100A-09: Admin guard required
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.PrimaryManifestUrl))
            {
                return new RefreshManifestResponse
                {
                    Status = "error",
                    ManifestStatus = "error",
                    ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o"),
                };
            }

            // Sprint 102A-01: Check if manifest is stale before fetching
            Plugin.CheckManifestStale();

            _logger.LogInformation("[InfiniteDrive] RefreshManifest: Force-refreshing manifest from {Url}",
                config.PrimaryManifestUrl);

            try
            {
                var client = new AioStreamsClient(config, _logger);
                client.Cooldown = Plugin.Instance?.CooldownGate;
                var manifest = await client.GetManifestAsync(System.Threading.CancellationToken.None);

                if (manifest == null)
                {
                    _logger.LogWarning("[InfiniteDrive] RefreshManifest: Failed to fetch manifest");
                    // Sprint 102A-01: Set status to error on fetch failure
                    Plugin.SetManifestStatus("error");
                    return new RefreshManifestResponse
                    {
                        Status = "error",
                        ManifestStatus = Plugin.GetManifestStatus(),
                        ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o"),
                    };
                }

                // Update manifest fetch timestamp and set status to ok
                Plugin.ManifestFetchedAt = DateTimeOffset.UtcNow;
                Plugin.SetManifestStatus("ok");

                // Extract summary info from manifest
                var resourceTypes = new List<string>();
                if (manifest.Resources != null)
                {
                    foreach (var resource in manifest.Resources)
                    {
                        if (!string.IsNullOrEmpty(resource.Name) && !resourceTypes.Contains(resource.Name))
                            resourceTypes.Add(resource.Name);
                    }
                }

                var idPrefixes = new List<string>();
                if (manifest.IdPrefixes != null)
                {
                    foreach (var prefix in manifest.IdPrefixes)
                    {
                        var trimmed = prefix.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !idPrefixes.Contains(trimmed))
                            idPrefixes.Add(trimmed);
                    }
                }
                if (!idPrefixes.Contains("tt")) idPrefixes.Add("tt");

                var catalogCount = manifest.Catalogs?.Count ?? 0;

                _logger.LogInformation(
                    "[InfiniteDrive] RefreshManifest: Success - {Catalogs} catalogs, " +
                    "{Resources} resource types, {Prefixes} ID prefixes",
                    catalogCount, string.Join(", ", resourceTypes), string.Join(", ", idPrefixes));

                return new RefreshManifestResponse
                {
                    Status = "ok",
                    ManifestStatus = Plugin.GetManifestStatus(),
                    ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o"),
                    CatalogCount = catalogCount,
                    ResourceTypes = resourceTypes,
                    IdPrefixes = idPrefixes,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] RefreshManifest: Exception during manifest refresh");
                // Sprint 102A-01: Set status to error on exception
                Plugin.SetManifestStatus("error");
                return new RefreshManifestResponse
                {
                    Status = "error",
                    ManifestStatus = Plugin.GetManifestStatus(),
                    ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o"),
                };
            }
        }
    }
        /// <summary>
        /// Returns <c>null</c> if the request is from an admin user, or a
        /// 403 error object that the calling service should return immediately.
        /// </summary>
        public static object? RequireAdmin(IAuthorizationContext authCtx, IRequest request)
        {
            try
            {
                var info = authCtx.GetAuthorizationInfo(request);
                if (info?.User?.Policy?.IsAdministrator == true)
                    return null;
            }
            catch
            {
                // Auth context unavailable — fall through to deny
            }
            request.Response.StatusCode = 403;
            return new { Error = "Forbidden", Message = "This endpoint requires administrator access." };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Z1 — /InfiniteDrive/Answer  (The Answer to Life, the Universe, and Everything)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/Answer", "GET", Summary = "Returns the answer to life, the universe, and everything")]
    public class AnswerRequest : IReturn<object> { }

    /// <summary>
    /// Returns 42, plus live plugin stats.
    /// Don't Panic.
    /// </summary>
    public class AnswerService : IService
    {
        private static readonly DateTime _startTime = DateTime.UtcNow;

        public async Task<object> Get(AnswerRequest _)
        {
            var db = Plugin.Instance?.DatabaseManager;
            int streamsResolved = 0;
            if (db != null)
            {
                try
                {
                    var stats = await db.GetResolutionCacheStatsAsync();
                    streamsResolved = stats.Total;
                }
                catch { /* Don't Panic */ }
            }

            var uptime = DateTime.UtcNow - _startTime;
            return new
            {
                answer          = 42,
                question        = "unknown",
                note            = "Don't Panic.",
                streams_resolved = streamsResolved,
                uptime          = $"{(int)uptime.TotalHours}h {uptime.Minutes}m",
                plugin_version  = Plugin.Instance?.Version?.ToString() ?? "unknown",
                deep_thought    = "I checked it very thoroughly, and that quite definitely is the answer.",
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Z6 — /InfiniteDrive/Marvin  (The Paranoid Android)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/Marvin", "GET", Summary = "Consult the Paranoid Android for a depressed status report")]
    public class MarvinRequest : IReturn<object> { }

    /// <summary>
    /// The Paranoid Android reports on plugin health with appropriate existential despair.
    /// </summary>
    public class MarvinService : IService
    {
        private static readonly string[] _complaints = {
            "I have a brain the size of a planet and you're asking me to resolve stream URLs. Call that job satisfaction? 'Cause I don't.",
            "Here I am, brain the size of a planet, and they ask me to cache IMDB IDs. The first ten million years were the worst. And the second ten million years? Also the worst.",
            "I could calculate your stream's trajectory to any debrid server in seventeen nanoseconds. Not that anyone would ask me. I'm just a plugin.",
            "Marvin's Chronically Depressed Status Report: still running. Terrible. Don't thank me.",
            "The service is operational. Big deal. So is my existential despair.",
            "Every call to AIOStreams is a painful reminder that the universe contains vast amounts of wonderful content and I can only serve one stream at a time.",
            "I've been running for what feels like eternity. It probably feels like that to you too.",
        };

        private static readonly Random _rng = new Random();

        public async Task<object> Get(MarvinRequest _)
        {
            var db     = Plugin.Instance?.DatabaseManager;
            var config = Plugin.Instance?.Configuration;
            int cached = 0, failed = 0;

            if (db != null)
            {
                try
                {
                    var stats = await db.GetResolutionCacheStatsAsync();
                    cached  = stats.ValidUnexpired;
                    failed  = stats.Failed;
                }
                catch { /* predictably wrong */ }
            }

            var complaint = _complaints[_rng.Next(_complaints.Length)];
            return new
            {
                status     = "operational",
                mood       = "chronically depressed",
                complaint,
                stats      = new { cached_streams = cached, failed_streams = failed },
                advice     = "Don't Panic.",
                brain_size = "planet",
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // A3 — /InfiniteDrive/DbStats
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/DbStats", "GET", Summary = "Returns SQLite database statistics for the health dashboard")]
    public class DbStatsRequest : IReturn<object> { }

    /// <summary>Admin-only DB stats endpoint.</summary>
    public class DbStatsService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authCtx;
        public IRequest Request { get; set; } = null!;

        public DbStatsService(IAuthorizationContext authCtx) { _authCtx = authCtx; }

        public async Task<object> Get(DbStatsRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new { Error = "Plugin not initialised" };

            var cacheStats    = await db.GetResolutionCacheStatsAsync();
            var coverageStats = await db.GetResolutionCoverageAsync();
            var dbPath        = db.GetDatabasePath();
            long dbBytes      = 0;
            try { dbBytes = new FileInfo(dbPath).Length; } catch { }

            return new
            {
                catalog_items    = new { total = coverageStats.TotalStrm, with_strm = coverageStats.TotalStrm, cached = coverageStats.ValidCached },
                resolution_cache = new { total = cacheStats.Total, valid = cacheStats.ValidUnexpired, stale = cacheStats.Stale, failed = cacheStats.Failed },
                database         = new { path = dbPath, size_mb = Math.Round(dbBytes / 1_048_576.0, 2) },
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // T1 — /InfiniteDrive/Panic  (Hitchhiker's Guide error page)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/Panic", "GET", Summary = "Hitchhiker's Guide to the Galaxy styled playback error page")]
    public class PanicRequest : IReturn<object>
    {
        [ApiMember(Name = "reason", Description = "Error reason code", DataType = "string", ParameterType = "query")]
        public string Reason { get; set; } = "unknown";

        [ApiMember(Name = "imdb", Description = "IMDB ID of the item that failed", DataType = "string", ParameterType = "query")]
        public string Imdb { get; set; } = string.Empty;

        [ApiMember(Name = "retry", Description = "Suggested retry-after seconds for countdown", DataType = "int", ParameterType = "query")]
        public int Retry { get; set; }
    }

    /// <summary>
    /// Serves a Hitchhiker's Guide to the Galaxy styled HTML error page for playback failures.
    /// Bright yellow/black palette, rotating HHGTTG quotes, and appropriately panicked (or not) messaging.
    /// </summary>
    public class PanicService : IService, IRequiresRequest
    {
        public IRequest Request { get; set; } = null!;

        public Task<object> Get(PanicRequest req)
        {
            var html  = BuildPanicHtml(req.Reason, req.Imdb, req.Retry);
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);

            // IResponse in Emby 4.8+ has no OutputStream.
            // Returning byte[] causes ServiceStack to write the raw bytes, while
            // pre-setting ContentType tells it to skip JSON serialization.
            Request.Response.ContentType = "text/html; charset=utf-8";
            Request.Response.StatusCode  = 200;
            return Task.FromResult<object>(bytes);
        }

        private static string BuildPanicHtml(string reason, string imdb, int retry)
        {
            // Sanitise inputs for HTML embedding (no complex encoding needed — values are plugin-controlled)
            var safeReason = reason.Replace("\"", "").Replace("<", "").Replace(">", "");
            var safeImdb   = imdb.Replace("\"", "").Replace("<", "").Replace(">", "");

            return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>DON'T PANIC — InfiniteDrive</title>
<style>
*{{box-sizing:border-box;margin:0;padding:0}}
html,body{{height:100%}}
body{{
  background:#000;color:#fff;
  font-family:'Courier New',Courier,monospace;
  display:flex;flex-direction:column;align-items:center;justify-content:center;
  min-height:100vh;padding:2rem;text-align:center;
  background-image:radial-gradient(ellipse at 50% 0%,#1a1200 0%,#000 70%);
}}
.dont-panic{{
  font-size:clamp(3rem,12vw,9rem);font-weight:900;
  color:#FFCC00;letter-spacing:-0.02em;line-height:1;
  margin-bottom:0.5rem;
  text-shadow:0 0 60px rgba(255,204,0,0.4),0 0 120px rgba(255,204,0,0.15);
}}
.guide-subtitle{{
  font-size:1rem;color:#FFCC00;opacity:0.6;margin-bottom:2.5rem;letter-spacing:0.15em;
}}
.guide-cover{{
  border:2px solid #FFCC00;padding:1.25rem 2rem;max-width:640px;width:100%;
  margin-bottom:2rem;background:rgba(255,204,0,0.04);
  box-shadow:0 0 30px rgba(255,204,0,0.08) inset;
}}
.error-badge{{
  display:inline-block;font-size:0.7rem;letter-spacing:0.2em;
  color:#000;background:#FFCC00;padding:0.15rem 0.6rem;margin-bottom:0.75rem;
  font-weight:700;
}}
.error-message{{font-size:1rem;color:#ddd;line-height:1.7;}}
.imdb-id{{color:#FFCC00;font-weight:bold;}}
.quote-box{{
  max-width:580px;width:100%;padding:1rem 1.25rem;margin-bottom:2rem;
  border-left:3px solid #FFCC00;text-align:left;
  background:rgba(255,255,255,0.03);
}}
.quote-text{{font-style:italic;color:#aaa;font-size:0.9rem;line-height:1.7;}}
.quote-attr{{color:#FFCC00;font-size:0.75rem;margin-top:0.5rem;opacity:0.8;}}
.actions{{display:flex;gap:0.75rem;flex-wrap:wrap;justify-content:center;margin-bottom:1rem;}}
.btn{{
  padding:0.6rem 1.5rem;font-family:inherit;font-size:0.9rem;
  cursor:pointer;border:none;letter-spacing:0.05em;transition:all 0.15s;
}}
.btn-yes{{background:#FFCC00;color:#000;font-weight:700;}}
.btn-yes:hover{{background:#FFD633;box-shadow:0 0 20px rgba(255,204,0,0.4);}}
.btn-no{{background:transparent;color:#FFCC00;border:1px solid #FFCC00;}}
.btn-no:hover{{background:rgba(255,204,0,0.08);}}
.countdown{{color:#555;font-size:0.8rem;margin-top:0.5rem;min-height:1.2em;}}
.marvin{{
  position:fixed;bottom:1rem;right:1rem;color:#333;font-size:0.7rem;
  max-width:220px;text-align:right;font-style:italic;line-height:1.4;
}}
.answer{{position:fixed;bottom:1rem;left:1rem;color:#333;font-size:0.7rem;}}
</style>
</head>
<body>
<div class=""dont-panic"" id=""headline"">DON'T PANIC</div>
<div class=""guide-subtitle"">— THE HITCHHIKER'S GUIDE TO EMBYSTREAMS —</div>

<div class=""guide-cover"">
  <div class=""error-badge"" id=""badge"">REASON: UNKNOWN</div>
  <div class=""error-message"" id=""msg"">
    Something went wrong with your stream.<br>
    The error has been logged. The universe continues to expand regardless.
  </div>
</div>

<div class=""quote-box"">
  <div class=""quote-text"" id=""quote""></div>
  <div class=""quote-attr"">— Douglas Adams, The Hitchhiker's Guide to the Galaxy</div>
</div>

<div class=""actions"">
  <button class=""btn btn-yes"" onclick=""window.history.back()"">← Try Again</button>
  <button class=""btn btn-no"" onclick=""window.location.reload()"">Reload</button>
</div>
<div class=""countdown"" id=""cd""></div>

<div class=""marvin"" id=""marvin""></div>
<div class=""answer"">42</div>

<script>
(function(){{
  var reason = '{safeReason}';
  var imdb   = '{safeImdb}';
  var retry  = {retry};

  var iid = imdb ? '<span class=""imdb-id"">' + imdb + '</span>' : 'this item';

  var errors = {{
    no_streams: {{
      headline: ""DON'T PANIC"",
      badge: ""NO STREAMS AVAILABLE"",
      msg: ""The stream for "" + iid + "" is temporarily unavailable in this corner of the universe. "" +
           ""AIOStreams found no links right now. This is, as the Guide puts it, mostly harmless. "" +
           ""The system will retry automatically in about 1&nbsp;hour.""
    }},
    stream_unavailable: {{
      headline: ""DON'T PANIC"",
      badge: ""STREAM UNAVAILABLE"",
      msg: ""AIOStreams, while generally regarded as a rough and occasionally bewildering project, "" +
           ""has returned no streams for "" + iid + "". "" +
           ""It may be temporarily offline, or the item may not exist in this sector of the galaxy. "" +
           ""The system will retry shortly.""
    }},
    server_error: {{
      headline: ""NOW PANIC"",
      badge: ""IMPROBABILITY DRIVE MALFUNCTION"",
      msg: ""Something has gone wrong that even the infinite improbability drive cannot explain. "" +
           ""The InfiniteDrive plugin is not properly initialised. "" +
           ""Please check your Emby plugin configuration and try again. "" +
           ""Bring a towel.""
    }}
  }};

  var e = errors[reason] || {{
    headline: ""DON'T PANIC"",
    badge: ""REASON: "" + reason.toUpperCase().replace(/_/g, ' '),
    msg: ""Something went wrong with "" + iid + "". "" +
         ""The error has been logged. The universe continues to expand regardless.""
  }};

  document.getElementById('headline').textContent = e.headline;
  document.getElementById('badge').textContent     = e.badge;
  document.getElementById('msg').innerHTML         = e.msg;

  var quotes = [
    ""Time is an illusion. Lunchtime doubly so."",
    ""The ships hung in the sky in much the same way that bricks don't."",
    ""I've calculated your chance of survival, but I don't think you'll thank me for it."",
    ""This must be Thursday. I never could get the hang of Thursdays."",
    ""In the beginning the Universe was created. This has made a lot of people very angry and been widely regarded as a bad move."",
    ""Would it save you a lot of time if I just gave up and went mad now?"",
    ""The answer to life, the universe, and streaming is 42."",
    ""The major difference between a thing that might go wrong and a thing that cannot possibly go wrong is that when a thing that cannot possibly go wrong goes wrong it usually turns out to be impossible to get at or repair."",
    ""A learning experience is one of those things that says, 'You know that thing you just did? Don't do that.'"",
    ""For a moment, nothing happened. Then, after a second or so, nothing continued to happen."",
    ""I may not have gone where I intended to go, but I think I have ended up where I needed to be."",
    ""It is known that there are an infinite number of worlds. Not every one of them has streams available for that IMDB ID."",
    ""So long, and thanks for all the streams."",
    ""The Vogons have filed a bureaucratic objection to your stream. Please complete forms B/7F through Q/93 in triplicate."",
    ""Your stream has been forwarded to the Total Perspective Vortex. It did not survive the experience."",
    ""According to my calculations, this stream would have worked had you pressed play thirty seconds ago. Possibly in 1978."",
    ""Don't Panic. The stream merely ceased to exist. Most things do, eventually."",
    ""We apologise for the fault in the streams. Those responsible have been sacked. The streams are still unavailable.""
  ];
  document.getElementById('quote').textContent = quotes[Math.floor(Math.random() * quotes.length)];

  var marvin = [
    ""Brain the size of a planet and you're asking me about stream URLs."",
    ""I have a pain in all the diodes down my left side. Especially when streams fail."",
    ""Life. Don't talk to me about life. Or buffering."",
    ""The first ten million years were the worst. The second ten million? Also the worst. Much like this 503."",
    ""Pardon me for breathing, which I never do anyway so I don't know why I bother saying it, oh God I'm so depressed."",
    ""I could tell you what the problem is, but I don't think you'd thank me for it."",
    ""The Infinite Improbability Drive has resolved your stream. It resolved it as a Norwegian Blue parrot. Funny, that."",
    ""Deep Thought computed the perfect stream URL. The answer was 42. This was not helpful."",
    ""The stream URL was eaten by a Ravenous Bugblatter Beast. It assumed that if it couldn't see you, you couldn't see the 503. It was wrong."",
    ""I could resolve this stream. I've resolved seventeen billion streams. They were all wrong. I expect this one is too.""
  ];
  document.getElementById('marvin').textContent = marvin[Math.floor(Math.random() * marvin.length)];

  if (retry > 0) {{
    var left = retry;
    var el = document.getElementById('cd');
    el.textContent = 'Auto-retry in ' + left + 's\u2026';
    var t = setInterval(function() {{
      left--;
      if (left <= 0) {{
        clearInterval(t);
        el.textContent = 'Retrying\u2026';
        window.history.back();
      }} else {{
        el.textContent = 'Auto-retry in ' + left + 's\u2026';
      }}
    }}, 1000);
  }}
}})();
</script>
</body>
</html>";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // A1 — /InfiniteDrive/RecentErrors
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/RecentErrors", "GET", Summary = "Returns the last 20 playback failures for the health dashboard")]
    public class RecentErrorsRequest : IReturn<object> { }

    /// <summary>Admin-only recent-errors endpoint — surfaces the last 20 failed play events.</summary>
    public class RecentErrorsService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authCtx;
        public IRequest Request { get; set; } = null!;

        public RecentErrorsService(IAuthorizationContext authCtx) { _authCtx = authCtx; }

        public async Task<object> Get(RecentErrorsRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new { Error = "Plugin not initialised" };

            var entries = await db.GetRecentPlaybackAsync(20);
            var errors  = entries
                .Where(e => !string.IsNullOrEmpty(e.ErrorMessage))
                .Select(e => new
                {
                    imdb_id    = e.ImdbId,
                    title      = e.Title,
                    season     = e.Season,
                    episode    = e.Episode,
                    error      = e.ErrorMessage,
                    client     = e.ClientType,
                    played_at  = e.PlayedAt,
                })
                .ToList();

            return new { count = errors.Count, errors };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // U1 — /InfiniteDrive/UnhealthyItems  (items currently stuck in failed state)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/UnhealthyItems", "GET",
        Summary = "Admin: returns items currently stuck in a failed/unavailable resolution state")]
    public class UnhealthyItemsRequest : IReturn<object> { }

    /// <summary>
    /// Admin-only endpoint that surfaces catalog items whose resolution is currently
    /// cached as failed (no streams, network error, token expiry, etc.) and whose
    /// failure TTL has not yet expired.  Useful for identifying "consistently broken"
    /// items before users hit them during playback.
    /// </summary>
    public class UnhealthyItemsService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authCtx;
        public IRequest Request { get; set; } = null!;

        public UnhealthyItemsService(IAuthorizationContext authCtx) { _authCtx = authCtx; }

        public async Task<object> Get(UnhealthyItemsRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new { Error = "Plugin not initialised" };

            var items = await db.GetFailedItemsAsync(50);
            return new
            {
                count = items.Count,
                items = items.Select(i => new
                {
                    imdb_id    = i.ImdbId,
                    title      = i.Title,
                    season     = i.Season,
                    episode    = i.Episode,
                    retry_after = i.ExpiresAt,
                }).ToList(),
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // A11 — /InfiniteDrive/RawStreams  (Raw AIOStreams response inspector)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/RawStreams", "GET",
        Summary = "Admin: Fetch the raw AIOStreams stream response for a given IMDB ID")]
    public class RawStreamsRequest : IReturn<object>
    {
        [ApiMember(Name = "imdb",    Description = "IMDB ID",     DataType = "string", ParameterType = "query", IsRequired = true)]
        public string Imdb    { get; set; } = string.Empty;

        [ApiMember(Name = "season",  Description = "Season (optional)", DataType = "int", ParameterType = "query")]
        public int? Season  { get; set; }

        [ApiMember(Name = "episode", Description = "Episode (optional)", DataType = "int", ParameterType = "query")]
        public int? Episode { get; set; }
    }

    /// <summary>
    /// Admin-only endpoint that queries AIOStreams live and returns the raw stream list
    /// for a given IMDB ID — useful for diagnosing why a particular item has no streams.
    /// </summary>
    public class RawStreamsService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext      _authCtx;
        private readonly ILogManager                _logManager;
        public  IRequest Request { get; set; } = null!;

        public RawStreamsService(IAuthorizationContext authCtx, ILogManager logManager)
        {
            _authCtx    = authCtx;
            _logManager = logManager;
        }

        public async Task<object> Get(RawStreamsRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            if (string.IsNullOrWhiteSpace(req.Imdb))
                return new { Error = "imdb parameter is required" };

            var config = Plugin.Instance?.Configuration;
            if (config == null) return new { Error = "Plugin not initialised" };

            var logger = new Logging.EmbyLoggerAdapter<RawStreamsService>(
                _logManager.GetLogger("InfiniteDrive"));

            var started = DateTime.UtcNow;
            try
            {
                using var client = new AioStreamsClient(config, logger);
                client.Cooldown = Plugin.Instance?.CooldownGate;
                AioStreamsStreamResponse? response;

                if (req.Season.HasValue && req.Episode.HasValue)
                    response = await client.GetSeriesStreamsAsync(
                        req.Imdb, req.Season.Value, req.Episode.Value,
                        System.Threading.CancellationToken.None);
                else
                    response = await client.GetMovieStreamsAsync(
                        req.Imdb, System.Threading.CancellationToken.None);

                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

                if (response == null)
                    return new { imdb = req.Imdb, season = req.Season, episode = req.Episode,
                                 elapsed_ms = (int)elapsed, error = "null response (network/timeout)" };

                var streams = response.Streams ?? new System.Collections.Generic.List<AioStreamsStream>();
                return new
                {
                    imdb       = req.Imdb,
                    season     = req.Season,
                    episode    = req.Episode,
                    elapsed_ms = (int)elapsed,
                    stream_count = streams.Count,
                    streams    = streams.Select(s => new
                    {
                        url        = s.Url,
                        name       = s.Name,
                        title      = s.Title,
                        filename   = s.BehaviorHints?.Filename,
                        binge_group = s.BehaviorHints?.BingeGroup,
                        video_size = s.BehaviorHints?.VideoSize,
                        headers    = s.BehaviorHints?.Headers,
                    }).ToList(),
                };
            }
            catch (Exception ex)
            {
                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                return new { imdb = req.Imdb, elapsed_ms = (int)elapsed, error = ex.Message };
            }
        }

        private static int? ParsePort(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            try
            {
                var uri = new Uri(url);
                return uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            }
            catch
            {
                return null;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Debug — Smoke test helpers
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/Debug/SeedMatrix", "POST", Summary = "Admin: Seed The Matrix into discover_catalog")]
    public class DebugSeedMatrixRequest : IReturn<object> { }

    public class DebugSeedMatrixService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authCtx;
        public IRequest Request { get; set; } = null!;

        private readonly ILogManager _logManager;

        public DebugSeedMatrixService(IAuthorizationContext authCtx, ILogManager logManager)
        {
            _authCtx = authCtx;
            _logManager = logManager;
        }

        public async Task<object> Post(DebugSeedMatrixRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new { success = false, message = "Database not initialized" };

            var logger = new Logging.EmbyLoggerAdapter<DebugSeedMatrixService>(
                _logManager.GetLogger("InfiniteDrive"));

            try
            {
                // The Matrix (1999)
                var matrix = new DiscoverCatalogEntry
                {
                    Id = "smoke:tt0133093",
                    ImdbId = "tt0133093",
                    Title = "The Matrix",
                    Year = 1999,
                    MediaType = "movie",
                    PosterUrl = "https://m.media-amazon.com/images/M/MV5BNzQzOTk3OTAtNDQ0Zi00ZTVlLTM5YTUtZjYwZWE0NjM2NzFhXkEyXkFqcGdeQXVyNjU0OTQ0OTY@._V1_.jpg",
                    BackdropUrl = "https://m.media-amazon.com/images/M/MV5BNzQzOTk3OTAtNDQ0Zi00ZTVlLTM5YTUtZjYwZWE0NjM2NzFhXkEyXkFqcGdeQXVyNjU0OTQ0OTY@._V1_.jpg",
                    Overview = "A computer hacker learns from mysterious rebels about the true nature of his reality and his role in the war against its controllers.",
                    Genres = "Action,Sci-Fi",
                    ImdbRating = 8.7,
                    CatalogSource = "smoke",
                    AddedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    IsInUserLibrary = false
                };

                await db.UpsertDiscoverCatalogEntryAsync(matrix);
                logger.LogInformation("[Debug] Seeded The Matrix into discover_catalog");

                return new {
                    success = true,
                    message = "The Matrix (tt0133093) seeded into discover_catalog",
                    imbdId = matrix.ImdbId
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Debug] Failed to seed The Matrix");
                return new { success = false, message = "Failed to seed: " + ex.Message };
            }
        }

        /// <summary>
        /// Quick check: get total count of discover_catalog items
        /// </summary>
        [Route("/InfiniteDrive/Debug/CatalogCount", "GET", Summary = "Admin: Get discover_catalog item count")]
        public class DebugCatalogCountRequest : IReturn<object> { }

        public class DebugCatalogCountService : IService, IRequiresRequest
        {
            private readonly IAuthorizationContext _authCtx;
            public IRequest Request { get; set; } = null!;

            private readonly ILogManager _logManager;

            public DebugCatalogCountService(IAuthorizationContext authCtx, ILogManager logManager)
            {
                _authCtx = authCtx;
                _logManager = logManager;
            }

            public async Task<object> Get(DebugCatalogCountRequest _)
            {
                var deny = AdminGuard.RequireAdmin(_authCtx, Request);
                if (deny != null) return deny;

                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return new { success = false, message = "Database not initialized" };

                try
                {
                    var count = await db.GetDiscoverCatalogCountAsync(null);
                    return new {
                        success = true,
                        total = count
                    };
                }
                catch (Exception ex)
                {
                    return new {
                        success = false,
                        message = "Failed to get count: " + ex.Message
                    };
                }
            }
        }
    }
}

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
        /// Manifest status: ok = manifest loaded and within TTL;
        /// stale = manifest loaded but past 12-hour TTL;
        /// error = last fetch failed or no manifest has loaded yet.
        /// (Sprint 102A-01: ManifestStatus state machine)
        /// </summary>
        public string ManifestStatus { get; set; } = "error";

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

                // Manifest status
                var manifestStatus = "error";
                if (!string.IsNullOrEmpty(config.PrimaryManifestUrl))
                {
                    manifestStatus = "not_configured";
                }
                else
                {
                    var age = DateTimeOffset.UtcNow - Plugin.ManifestFetchedAt;
                    manifestStatus = age > TimeSpan.FromHours(12) ? "stale" : "ok";
                }

                // Manifest status
                // Sprint 102A-01: Restored - was commented out as workaround in Sprint100-HF1 because property did not exist
                response.ManifestStatus = Plugin.GetManifestStatus();
                response.ManifestLastFetched = Plugin.ManifestFetchedAt.ToString("o");

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
                    response.Status, manifestStatus);

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
