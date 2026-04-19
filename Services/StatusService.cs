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
        /// Total number of times a missing library file has been replaced by a .strm.
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

        /// <summary>URL to edit the secondary AIOStreams manifest configuration.</summary>
        public string? SecondaryManifestConfigureUrl { get; set; }

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

        /// <summary>True if MarvinTask has run at least once since server start.</summary>
        public bool MarvinHasRun { get; set; }

        /// <summary>ISO-8601 UTC timestamp of last MarvinTask completion.</summary>
        public string? MarvinLastRunAt { get; set; }

        /// <summary>Number of items with nfo_status = 'NeedsEnrich'.</summary>
        public int NeedsEnrichCount { get; set; }

        /// <summary>Number of items with nfo_status = 'Blocked'.</summary>
        public int BlockedCount { get; set; }

        /// <summary>Health of RefreshTask: "green", "yellow", or "red" based on 2×/3× interval thresholds.</summary>
        public string? RefreshHealth { get; set; }

        /// <summary>Health of MarvinTask: "green", "yellow", or "red" based on 2×/3× interval thresholds.</summary>
        public string? MarvinHealth { get; set; }

        // ── End Sprint 146 ───────────────────────────────────────────────────────

        // ── Sprint 155: CooldownGate observability ────────────────────────────

        /// <summary>True when a global 429 cooldown is currently active.</summary>
        public bool CooldownActive { get; set; }

        /// <summary>ISO-8601 UTC timestamp when the current cooldown expires.</summary>
        public string? CooldownUntil { get; set; }

        /// <summary>True when 3+ 429s in the last hour on a shared instance.</summary>
        public bool SuggestPrivateInstance { get; set; }

        // ── Sprint 220: Series Gap Detection ─────────────────────────────────────

        /// <summary>Summary of the last series gap scan run.</summary>
        public GapScanSummary? SeriesGapSummary { get; set; }

        // ── Sprint 222: Episode Sync Stats ────────────────────────────────────

        /// <summary>Summary of the last catalog-first episode sync run.</summary>
        public EpisodeSyncSummary? EpisodeSyncSummary { get; set; }

        // Sprint 401: System state engine fields
        public string SystemState { get; set; } = "unconfigured";
        public string SystemStateDescription { get; set; } = string.Empty;
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

    /// <summary>Summary of the last series gap scan.</summary>
    public class GapScanSummary
    {
        public int TotalSeriesScanned { get; set; }
        public int CompleteSeriesCount { get; set; }
        public int SeriesWithGaps { get; set; }
        public int TotalMissingEpisodes { get; set; }
        public string? LastScanAt { get; set; }
        public string? LastRepairAt { get; set; }
        public int EpisodesRepairedLastRun { get; set; }
        public int EpisodesRepairedTotal { get; set; }
    }

    /// <summary>Summary of the last catalog-first episode sync run.</summary>
    public class EpisodeSyncSummary
    {
        public string? LastSyncAt { get; set; }
        public int SeriesProcessed { get; set; }
        public int EpisodesWritten { get; set; }
        public int EpisodesRemoved { get; set; }
        public int VerificationMismatches { get; set; }
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

        /// <summary>Human-readable catalog name from the manifest, resolved from SourceKey.</summary>
        public string? DisplayName { get; set; }
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
        private static Dictionary<string, string> _cachedCatalogNames = new(StringComparer.OrdinalIgnoreCase);
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

            // ── Sprint 401: System state engine ─────────────────────────────────
            var stateService = Plugin.Instance?.SystemStateService;
            if (stateService != null)
            {
                var state = await stateService.GetStateAsync();
                response.SystemState = state.State.ToString().ToLowerInvariant();
                response.SystemStateDescription = state.Description;
            }

            // ── Manifest URL parsing for "Edit Manifest" buttons ─────────────────
            var manifestComponents = ManifestUrlParser.Parse(config.PrimaryManifestUrl);
            response.ManifestConfigureUrl = manifestComponents?.ConfigureUrl;
            response.ManifestHost = manifestComponents?.Host;

            var secondaryComponents = ManifestUrlParser.Parse(config.SecondaryManifestUrl);
            response.SecondaryManifestConfigureUrl = secondaryComponents?.ConfigureUrl;

            // ── Health check caching: test once on first call, then cache indefinitely ─
            // Users can click a "Refresh" button to manually re-test.
            // This avoids hammering AIOStreams on every status poll (every 5 seconds).

            if (!_healthChecked)
            {
                // ── AIOStreams connection test ────────────────────────────────────────
                _cachedAioStreamsHealth = await TestAioStreamsConnectionAsync(config);

                // ── Provider health test ──────────────────────────────────────────────
                _cachedProviderHealth = await TestProviderHealthAsync(config);

                // ── Catalog name resolution ───────────────────────────────────────────
                _cachedCatalogNames = await FetchCatalogNamesAsync(config);

                _healthChecked = true;

                // Feed test results into state engine so state reflects reachability
                try
                {
                    var sysState = Plugin.Instance?.SystemStateService;
                    if (sysState != null)
                    {
                        if (_cachedAioStreamsHealth != null && !string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
                        {
                            await sysState.UpdateProviderTestAsync("primary",
                                _cachedAioStreamsHealth.Ok,
                                _cachedAioStreamsHealth.LatencyMs,
                                _cachedAioStreamsHealth.Message ?? "",
                                CancellationToken.None);
                        }
                        // Secondary provider from Providers list
                        if (_cachedProviderHealth != null)
                        {
                            var secondary = _cachedProviderHealth.Find(p =>
                                (p.DisplayName ?? "").IndexOf("secondary", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (secondary != null && !string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                            {
                                await sysState.UpdateProviderTestAsync("secondary",
                                    secondary.Ok, secondary.LatencyMs,
                                    secondary.Message ?? "", CancellationToken.None);
                            }
                        }
                    }
                }
                catch { /* best effort — don't fail status if state update fails */ }
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
                        DisplayName         = _cachedCatalogNames.TryGetValue(state.SourceKey, out var dn) ? dn : null,
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

                // Read MarvinTask metadata
                var lastMarvinRun = db.GetMetadata("last_marvin_run_time");
                if (string.IsNullOrEmpty(lastMarvinRun))
                {
                    // Fallback to old metadata key for backward compatibility
                    lastMarvinRun = db.GetMetadata("last_deepclean_run_time");
                }
                response.MarvinHasRun = !string.IsNullOrEmpty(lastMarvinRun);
                response.MarvinLastRunAt = lastMarvinRun;

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

                if (!string.IsNullOrEmpty(lastMarvinRun)
                    && DateTime.TryParse(lastMarvinRun, null, System.Globalization.DateTimeStyles.RoundtripKind, out var marvinLastRun))
                {
                    var marvinAge = DateTime.UtcNow - marvinLastRun;
                    var marvinInterval = TimeSpan.FromHours(18);
                    response.MarvinHealth = marvinAge > marvinInterval * 3 ? "red"
                                          : marvinAge > marvinInterval * 2 ? "yellow"
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

            // ── Sprint 220: Series gap scan summary ──────────────────────────
            var gapSnapshot = SeriesGapDetector.LastSnapshot;
            if (gapSnapshot.LastScanAt.HasValue)
            {
                response.SeriesGapSummary = new GapScanSummary
                {
                    TotalSeriesScanned   = gapSnapshot.TotalSeriesScanned,
                    CompleteSeriesCount  = gapSnapshot.CompleteSeriesCount,
                    SeriesWithGaps       = gapSnapshot.SeriesWithGaps,
                    TotalMissingEpisodes = gapSnapshot.TotalMissingEpisodes,
                    LastScanAt           = gapSnapshot.LastScanAt.Value.ToString("o"),
                    LastRepairAt         = SeriesGapRepairService.LastRepairAt?.ToString("o"),
                    EpisodesRepairedLastRun = SeriesGapRepairService.EpisodesRepairedLastRun,
                    EpisodesRepairedTotal  = SeriesGapRepairService.EpisodesRepairedTotal
                };
            }

            // ── Sprint 222: Episode sync summary ──────────────────────────────
            var syncResult = SeriesPreExpansionService.LastSyncResult;
            if (syncResult != null)
            {
                response.EpisodeSyncSummary = new EpisodeSyncSummary
                {
                    LastSyncAt              = syncResult.SyncedAt?.ToString("o"),
                    SeriesProcessed         = syncResult.SeriesProcessed,
                    EpisodesWritten         = syncResult.EpisodesWritten,
                    EpisodesRemoved         = syncResult.EpisodesRemoved,
                    VerificationMismatches  = 0
                };
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
        /// Tests both primary and secondary AIOStreams manifests and returns health entries.
        /// </summary>
        private async Task<List<ProviderHealthEntry>> TestProviderHealthAsync(PluginConfiguration config)
        {
            var results = new List<ProviderHealthEntry>();

            // Test Primary
            if (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                    using var client = new AioStreamsClient(url ?? string.Empty, uuid ?? string.Empty, token ?? string.Empty, _logger);
                    using var cts = new CancellationTokenSource(5_000);
                    var (ok, err) = await client.TestConnectionAsync(cts.Token);
                    sw.Stop();
                    results.Add(new ProviderHealthEntry
                    {
                        DisplayName = "Primary",
                        Ok = ok,
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                        Message = ok ? "Connected" : (err ?? "Failed")
                    });
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    results.Add(new ProviderHealthEntry { DisplayName = "Primary", Ok = false, LatencyMs = (int)sw.ElapsedMilliseconds, Message = ex.Message });
                }
            }

            // Test Secondary
            if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.SecondaryManifestUrl);
                    using var client = new AioStreamsClient(url ?? string.Empty, uuid ?? string.Empty, token ?? string.Empty, _logger);
                    using var cts = new CancellationTokenSource(5_000);
                    var (ok, err) = await client.TestConnectionAsync(cts.Token);
                    sw.Stop();
                    results.Add(new ProviderHealthEntry
                    {
                        DisplayName = "Secondary",
                        Ok = ok,
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                        Message = ok ? "Connected" : (err ?? "Failed")
                    });
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    results.Add(new ProviderHealthEntry { DisplayName = "Secondary", Ok = false, LatencyMs = (int)sw.ElapsedMilliseconds, Message = ex.Message });
                }
            }

            return results;
        }

        /// <summary>
        /// Fetches the AIOStreams manifest and builds a map from source key
        /// patterns (<c>aio:{type}:{catalogId}</c>) to human-readable catalog names.
        /// </summary>
        private async Task<Dictionary<string, string>> FetchCatalogNamesAsync(PluginConfiguration config)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var (url, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                if (string.IsNullOrEmpty(url)) return names;

                using var client = new AioStreamsClient(url, uuid ?? "", token ?? "", _logger);
                using var cts = new CancellationTokenSource(5_000);
                var manifest = await client.GetManifestAsync(cts.Token);
                if (manifest?.Catalogs == null) return names;

                foreach (var cat in manifest.Catalogs)
                {
                    if (string.IsNullOrEmpty(cat.Id)) continue;
                    var type = cat.Type ?? "movie";
                    // Build source key like what appears in sync_state: aio:{type}:{catalogId}
                    var key = $"aio:{type}:{cat.Id}";
                    if (!string.IsNullOrEmpty(cat.Name))
                        names[key] = cat.Name;
                }
            }
            catch
            {
                // Non-critical — display names are best-effort
            }

            return names;
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
            _cachedCatalogNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
}
