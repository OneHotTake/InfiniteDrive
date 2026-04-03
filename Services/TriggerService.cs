using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using EmbyStreams.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmbyStreams.Services
{
    // ── Request / Response DTOs ──────────────────────────────────────────────────

    /// <summary>
    /// Request object for <c>POST /EmbyStreams/Trigger</c>.
    ///
    /// Pass the task key as a query parameter, e.g.:
    /// <c>POST /EmbyStreams/Trigger?task=catalog_sync</c>
    ///
    /// Accepted values:
    /// <list type="bullet">
    ///   <item><c>catalog_sync</c> — <see cref="CatalogSyncTask"/></item>
    ///   <item><c>link_resolver</c> — <see cref="LinkResolverTask"/></item>
    ///   <item><c>file_resurrection</c> — <see cref="FileResurrectionTask"/></item>
    ///   <item><c>library_readoption</c> — <see cref="LibraryReadoptionTask"/></item>
    /// </list>
    /// </summary>
    [Route("/EmbyStreams/Trigger", "POST",
        Summary = "Manually triggers a named EmbyStreams scheduled task in the background")]
    public class TriggerRequest : IReturn<object>
    {
        /// <summary>Task identifier (see accepted values in class summary).</summary>
        public string Task { get; set; } = string.Empty;

        /// <summary>If true, bypasses sync interval checks (for catalog_sync task).</summary>
        public bool Force { get; set; }
    }

    /// <summary>Response from <c>POST /EmbyStreams/Trigger</c>.</summary>
    public class TriggerResponse
    {
        /// <summary><c>ok</c> if the task was queued, <c>error</c> otherwise.</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>The task that was queued.</summary>
        public string Task { get; set; } = string.Empty;

        /// <summary>Human-readable result message.</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request to clear sync states so the next sync runs immediately.
    /// </summary>
    [Route("/EmbyStreams/Setup/ClearSyncStates", "POST",
        Summary = "Clear sync states to force next sync to run immediately")]
    public class ClearSyncStatesRequest : IReturn<TriggerResponse> { }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Exposes a POST endpoint that lets the admin dashboard manually fire any
    /// EmbyStreams scheduled task without waiting for its next scheduled run.
    ///
    /// Tasks run in a background <see cref="System.Threading.Tasks.Task"/> with a
    /// 30-minute timeout, matching the pattern already used by
    /// <see cref="WebhookService"/>.  The endpoint returns immediately; callers
    /// can monitor progress via the <c>GET /EmbyStreams/Status</c> dashboard.
    /// </summary>
    public partial class TriggerService : IService, IRequiresRequest
    {
        // ── Constants ────────────────────────────────────────────────────────────

        private const string TaskCatalogSync          = "catalog_sync";
        private const string TaskCatalogDiscover     = "catalog_discover";
        private const string TaskLinkResolver         = "link_resolver";
        private const string TaskFileResurrection     = "file_resurrection";
        private const string TaskLibraryReadoption    = "library_readoption";
        private const string TaskEpisodeExpand        = "episode_expand";
        private const string TaskDoctor               = "doctor";         // Sprint 66: unified reconciliation engine
        private const string TaskClearClientProfiles  = "clear_client_profiles";
        private const string TaskForceSyncReset       = "force_sync";   // A7: bypass interval guard
        private const string TaskClearCache           = "clear_cache";  // A4: nuke resolution cache
        private const string TaskDeadLinkScan         = "dead_link_scan"; // W7: proactive dead-URL detection
        private const string TaskPurgeCatalog         = "purge_catalog";  // wipe catalog + .strm files
        private const string TaskResetAll             = "reset_all";      // nuclear clean slate
        private const string TaskResetWizard          = "reset_wizard";   // re-show first-run wizard

        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ILogger<TriggerService>  _logger;
        private readonly ILibraryManager          _libraryManager;
        private readonly ILogManager              _logManager;
        private readonly IAuthorizationContext    _authCtx;

        // ── IRequiresRequest ─────────────────────────────────────────────────────
        public IRequest Request { get; set; } = null!;

        // ── Constructor ──────────────────────────────────────────────────────────

        /// <summary>Emby injects dependencies automatically.</summary>
        public TriggerService(
            ILibraryManager      libraryManager,
            ILogManager          logManager,
            IAuthorizationContext authCtx)
        {
            _libraryManager = libraryManager;
            _logManager     = logManager;
            _authCtx        = authCtx;
            _logger         = new EmbyLoggerAdapter<TriggerService>(logManager.GetLogger("EmbyStreams"));
        }

        // ── IService ─────────────────────────────────────────────────────────────

        /// <summary>Handles <c>POST /EmbyStreams/Trigger?task={key}</c>.</summary>
        public object Post(TriggerRequest request)
        {
            // All trigger tasks require admin authentication
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var taskKey = (request.Task ?? string.Empty).Trim().ToLowerInvariant();

            switch (taskKey)
            {
                case TaskCatalogSync:
                    FireAndForget(ct => new CatalogSyncTask(_libraryManager, _logManager)
                        .Execute(ct, new Progress<double>()), taskKey);
                    break;

                case TaskCatalogDiscover:
                    FireAndForget(ct => new CatalogDiscoverTask(_logManager)
                        .Execute(ct, new Progress<double>()), taskKey);
                    break;

                case TaskLinkResolver:
                    FireAndForget(ct => new LinkResolverTask(_logManager)
                        .Execute(ct, new Progress<double>()), taskKey);
                    break;

#pragma warning disable CS0618 // Type is obsolete (kept for backward compatibility)
                case TaskFileResurrection:
                    FireAndForget(ct => new FileResurrectionTask(_libraryManager, _logManager)
                        .Execute(ct, new Progress<double>()), taskKey);
                    break;

                case TaskLibraryReadoption:
                    FireAndForget(ct => new LibraryReadoptionTask(_libraryManager, _logManager)
                        .Execute(ct, new Progress<double>()), taskKey);
                    break;

                case TaskEpisodeExpand:
                    FireAndForget(ct => new EpisodeExpandTask(_libraryManager, _logManager)
                        .Execute(ct, new Progress<double>()), taskKey);
                    break;
#pragma warning restore CS0618

                // Sprint 66: Doctor — unified catalog reconciliation engine
                case TaskDoctor:
                    FireAndForget(ct => new DoctorTask(_libraryManager, _logManager)
                        .Execute(ct, new Progress<double>()), taskKey);
                    break;

                case TaskClearClientProfiles:
                    FireAndForget(async ct =>
                    {
                        var db = Plugin.Instance?.DatabaseManager;
                        if (db != null)
                        {
                            await db.ClearAllClientProfilesAsync();
                            _logger.LogInformation(
                                "[EmbyStreams] All client compatibility profiles cleared via dashboard");
                        }
                    }, taskKey);
                    break;

                // A7 — reset interval guard so next catalog sync fetches everything fresh
                case TaskForceSyncReset:
                    FireAndForget(async ct =>
                    {
                        var db = Plugin.Instance?.DatabaseManager;
                        if (db != null)
                        {
                            await db.ResetSyncIntervalsAsync();
                            _logger.LogInformation(
                                "[EmbyStreams] Sync interval guard reset — next catalog sync will fetch all sources");
                        }
                    }, taskKey);
                    break;

                // A4 + A6 — clear resolution cache and vacuum
                case TaskClearCache:
                    FireAndForget(async ct =>
                    {
                        var db = Plugin.Instance?.DatabaseManager;
                        if (db != null)
                        {
                            await db.ClearResolutionCacheAsync();
                            await db.VacuumAsync();
                            _logger.LogInformation(
                                "[EmbyStreams] Resolution cache cleared and database vacuumed");
                        }
                    }, taskKey);
                    break;

                // W7 — proactive dead-URL detection: range-probe all valid cache entries
                // and mark stale anything that returns 4xx / connection-refused.
                case TaskDeadLinkScan:
                    FireAndForget(async ct =>
                    {
                        var db     = Plugin.Instance?.DatabaseManager;
                        var config = Plugin.Instance?.Configuration;
                        if (db == null || config == null) return;

                        var validEntries = await db.GetValidCacheEntriesAsync();
                        int staled = 0;
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

                        foreach (var entry in validEntries)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (string.IsNullOrEmpty(entry.StreamUrl)) continue;
                            try
                            {
                                var msg = new HttpRequestMessage(HttpMethod.Get, entry.StreamUrl);
                                msg.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                                using var resp = await http.SendAsync(
                                    msg, HttpCompletionOption.ResponseHeadersRead, ct);
                                var code = (int)resp.StatusCode;
                                if (code == 401 || code == 403 || code == 404 || code == 410)
                                {
                                    await db.MarkStreamStaleAsync(
                                        entry.ImdbId, entry.Season, entry.Episode);
                                    staled++;
                                    _logger.LogDebug(
                                        "[EmbyStreams] DeadLinkScan: {Imdb} returned {Code} — marked stale",
                                        entry.ImdbId, code);
                                }
                            }
                            catch { /* network error — leave as valid, playback will handle it */ }

                            await Task.Delay(200, ct); // gentle pacing
                        }
                        _logger.LogInformation(
                            "[EmbyStreams] DeadLinkScan: checked {Total} entries, marked {Staled} stale",
                            validEntries.Count, staled);
                    }, taskKey);
                    break;

                // Wipe catalog DB rows + sync_state + candidates + all .strm/.nfo on disk.
                // Resolution cache is preserved so re-sync can reuse existing cached URLs.
                case TaskPurgeCatalog:
                    FireAndForget(async ct =>
                    {
                        var db     = Plugin.Instance?.DatabaseManager;
                        var config = Plugin.Instance?.Configuration;
                        if (db == null || config == null) return;

                        var strmPaths = await db.PurgeCatalogAsync();
                        await db.VacuumAsync();

                        int deleted = DeleteStrmFiles(
                            strmPaths,
                            config.SyncPathMovies,
                            config.SyncPathShows,
                            _logger);

                        _logger.LogInformation(
                            "[EmbyStreams] PurgeCatalog: {Items} catalog rows removed, " +
                            "{Files} .strm/.nfo files deleted from disk",
                            strmPaths.Count, deleted);
                    }, taskKey);
                    break;

                // Full clean slate: wipe ALL tables + all .strm/.nfo on disk + VACUUM.
                case TaskResetAll:
                    FireAndForget(async ct =>
                    {
                        var db     = Plugin.Instance?.DatabaseManager;
                        var config = Plugin.Instance?.Configuration;
                        if (db == null || config == null) return;

                        var strmPaths = await db.ResetAllAsync();
                        await db.VacuumAsync();

                        int deleted = DeleteStrmFiles(
                            strmPaths,
                            config.SyncPathMovies,
                            config.SyncPathShows,
                            _logger);

                        _logger.LogInformation(
                            "[EmbyStreams] ResetAll: all tables cleared, " +
                            "{Files} .strm/.nfo files deleted from disk",
                            deleted);
                    }, taskKey);
                    break;

                // Reset the first-run wizard flag so the wizard is shown again on next load.
                case TaskResetWizard:
                    FireAndForget(async ct =>
                    {
                        var config = Plugin.Instance?.Configuration;
                        if (config == null) return;

                        config.IsFirstRunComplete = false;
                        Plugin.Instance?.SaveConfiguration();
                        _logger.LogInformation(
                            "[EmbyStreams] Wizard reset — IsFirstRunComplete set to false");

                        await Task.CompletedTask;
                    }, taskKey);
                    break;

                default:
                    return new TriggerResponse
                    {
                        Status  = "error",
                        Task    = taskKey,
                        Message = $"Unknown task '{taskKey}'. Valid keys: " +
                                  $"{TaskCatalogSync}, {TaskCatalogDiscover}, " +
                                  $"{TaskLinkResolver}, " +
                                  $"{TaskFileResurrection}, {TaskLibraryReadoption}, " +
                                  $"{TaskEpisodeExpand}, {TaskDoctor}, {TaskClearClientProfiles}, " +
                                  $"{TaskForceSyncReset}, {TaskClearCache}, {TaskDeadLinkScan}, " +
                                  $"{TaskPurgeCatalog}, {TaskResetAll}, {TaskResetWizard}",
                    };
            }

            return new TriggerResponse
            {
                Status  = "ok",
                Task    = taskKey,
                Message = $"Task '{taskKey}' started in the background.",
            };
        }

        /// <summary>
        /// Clears all sync states so the next catalog sync runs immediately.
        /// </summary>
        public async Task<TriggerResponse> Post(ClearSyncStatesRequest request)
        {
            // SEC-3: Admin-only — clears all sync states and triggers catalog sync.
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null)
                return new TriggerResponse
                {
                    Status  = "error",
                    Message = "Admin authentication required"
                };

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                return new TriggerResponse
                {
                    Status = "error",
                    Message = "Database not available"
                };
            }

            try
            {
                await db.ClearAllSyncStatesAsync();
                _logger.LogInformation("[EmbyStreams] All sync states cleared - next catalog sync will run immediately");
                
                // Trigger a sync immediately
                FireAndForget(ct => new CatalogSyncTask(_libraryManager, _logManager)
                    .Execute(ct, new Progress<double>()), "catalog_sync");
                
                return new TriggerResponse
                {
                    Status  = "ok",
                    Message = "Sync states cleared - catalog sync started",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Failed to clear sync states");
                return new TriggerResponse
                {
                    Status  = "error",
                    Message = $"Failed to clear sync states: {ex.Message}"
                };
            }
        }

        // ── Private ──────────────────────────────────────────────────────────────

        private void FireAndForget(Func<CancellationToken, Task> taskFactory, string taskKey)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                _logger.LogInformation("[EmbyStreams] TriggerService: starting '{Task}'", taskKey);
                try
                {
                    await taskFactory(cts.Token);
                    _logger.LogInformation("[EmbyStreams] TriggerService: '{Task}' finished", taskKey);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[EmbyStreams] TriggerService: '{Task}' timed out (30 min)", taskKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EmbyStreams] TriggerService: '{Task}' failed", taskKey);
                }
            });
        }

        /// <summary>
        /// Deletes the physical .strm and sibling .nfo file for each path in
        /// <paramref name="strmPaths"/>, then removes the parent directory if it
        /// becomes empty.  Also bulk-deletes any remaining .strm/.nfo files found
        /// under <paramref name="moviesRoot"/> and <paramref name="showsRoot"/> so
        /// that a PurgeCatalog or ResetAll leaves no orphaned files even for items
        /// whose strm_path was never written to the DB.
        /// </summary>
        /// <returns>Total number of files deleted.</returns>
        private static int DeleteStrmFiles(
            System.Collections.Generic.List<string> strmPaths,
            string moviesRoot,
            string showsRoot,
            ILogger<TriggerService> logger)
        {
            int count = 0;

            // Delete files recorded in DB
            foreach (var path in strmPaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        count++;
                    }

                    // Delete the sibling .nfo (same base name, different extension)
                    var nfo = Path.ChangeExtension(path, ".nfo");
                    if (File.Exists(nfo))
                    {
                        File.Delete(nfo);
                        count++;
                    }

                    // Remove parent directory if now empty
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)
                        && Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[EmbyStreams] DeleteStrmFiles: could not delete {Path}", path);
                }
            }

            // Sweep the root directories for any orphaned .strm/.nfo not tracked in DB
            foreach (var root in new[] { moviesRoot, showsRoot })
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*.strm", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); count++; } catch { /* best effort */ }
                    }
                    foreach (var file in Directory.EnumerateFiles(root, "*.nfo", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); count++; } catch { /* best effort */ }
                    }

                    // Remove now-empty subdirectories (top-down pass)
                    foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                                 .OrderByDescending(d => d.Length))
                    {
                        try
                        {
                            if (Directory.GetFileSystemEntries(dir).Length == 0)
                                Directory.Delete(dir);
                        }
                        catch { /* best effort */ }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[EmbyStreams] DeleteStrmFiles: sweep failed for root {Root}", root);
                }
            }

            return count;
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  CACHE INVALIDATION ENDPOINT                                             ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request for <c>POST /EmbyStreams/Invalidate</c>.
    /// Marks one resolution cache entry as stale so it is re-resolved on the next
    /// LinkResolverTask run (or immediately on the next play).
    /// </summary>
    [Route("/EmbyStreams/Invalidate", "POST",
        Summary = "Marks a resolution cache entry as stale, forcing re-resolution")]
    public class InvalidateRequest : IReturn<object>
    {
        /// <summary>IMDB ID (required).</summary>
        public string Imdb    { get; set; } = string.Empty;
        /// <summary>Season number (TV series). Omit for movies.</summary>
        public int?   Season  { get; set; }
        /// <summary>Episode number (TV series). Omit for movies.</summary>
        public int?   Episode { get; set; }
    }

    /// <summary>Response for <c>POST /EmbyStreams/Invalidate</c> and <c>/EmbyStreams/Queue</c>.</summary>
    public class CacheActionResponse
    {
        /// <summary><c>ok</c> or <c>error</c>.</summary>
        public string Status  { get; set; } = string.Empty;
        /// <summary>Human-readable result.</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request for <c>POST /EmbyStreams/Queue</c>.
    /// Adds an item to the Tier 0 (immediate) resolution queue.
    /// </summary>
    [Route("/EmbyStreams/Queue", "POST",
        Summary = "Adds an item to the Tier 0 immediate resolution queue")]
    public class QueueRequest : IReturn<object>
    {
        /// <summary>IMDB ID (required).</summary>
        public string Imdb    { get; set; } = string.Empty;
        /// <summary>Season number (TV series). Omit for movies.</summary>
        public int?   Season  { get; set; }
        /// <summary>Episode number (TV series). Omit for movies.</summary>
        public int?   Episode { get; set; }
    }

    /// <summary>
    /// Exposes manual cache management for the admin dashboard and API consumers.
    /// </summary>
    public class CacheManagementService : IService, IRequiresRequest
    {
        private readonly ILogger<CacheManagementService> _logger;
        private readonly IAuthorizationContext           _authCtx;

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        /// <summary>Emby injects dependencies automatically.</summary>
        public CacheManagementService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<CacheManagementService>(logManager.GetLogger("EmbyStreams"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>POST /EmbyStreams/Invalidate</c>.</summary>
        public async Task<object> Post(InvalidateRequest request)
        {
            // SEC-3: Admin-only — directly modifies the resolution cache.
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db  = Plugin.Instance?.DatabaseManager;
            var imdb = (request.Imdb ?? string.Empty).Trim();

            if (db == null)  return Err("Plugin not initialised");
            if (string.IsNullOrEmpty(imdb)) return Err("imdb parameter is required");

            try
            {
                await db.MarkStreamStaleAsync(imdb, request.Season, request.Episode);
                _logger.LogInformation(
                    "[EmbyStreams] CacheManagementService: invalidated {Imdb} S{S}E{E}",
                    imdb, request.Season, request.Episode);

                return new CacheActionResponse
                {
                    Status  = "ok",
                    Message = $"Marked stale: {imdb}" +
                              (request.Season.HasValue ? $" S{request.Season:D2}E{request.Episode:D2}" : string.Empty),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Invalidate failed for {Imdb}", imdb);
                return Err(ex.Message);
            }
        }

        /// <summary>Handles <c>POST /EmbyStreams/Queue</c>.</summary>
        public async Task<object> Post(QueueRequest request)
        {
            // SEC-3: Admin-only — queues a Tier 0 resolution job (triggers AIOStreams API call).
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db  = Plugin.Instance?.DatabaseManager;
            var imdb = (request.Imdb ?? string.Empty).Trim();

            if (db == null)  return Err("Plugin not initialised");
            if (string.IsNullOrEmpty(imdb)) return Err("imdb parameter is required");

            try
            {
                await db.QueueForResolutionAsync(imdb, request.Season, request.Episode, "tier0");
                _logger.LogInformation(
                    "[EmbyStreams] CacheManagementService: queued tier0 {Imdb} S{S}E{E}",
                    imdb, request.Season, request.Episode);

                return new CacheActionResponse
                {
                    Status  = "ok",
                    Message = $"Queued for immediate resolution: {imdb}" +
                              (request.Season.HasValue ? $" S{request.Season:D2}E{request.Episode:D2}" : string.Empty),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Queue failed for {Imdb}", imdb);
                return Err(ex.Message);
            }
        }

        private static CacheActionResponse Err(string msg) =>
            new CacheActionResponse { Status = "error", Message = msg };
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  SETUP VALIDATION ENDPOINT                                               ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>Request for <c>POST /EmbyStreams/Validate</c>.</summary>
    [Route("/EmbyStreams/Validate", "POST",
        Summary = "Validates plugin configuration — checks paths and AIOStreams connectivity")]
    public class ValidateRequest : IReturn<object> { }

    /// <summary>One check result within a <see cref="ValidateResponse"/>.</summary>
    public class ValidationCheck
    {
        /// <summary>Short identifier, e.g. <c>movies_path</c>.</summary>
        public string Check   { get; set; } = string.Empty;
        /// <summary><c>ok</c>, <c>warn</c>, or <c>error</c>.</summary>
        public string Status  { get; set; } = string.Empty;
        /// <summary>Human-readable message.</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Response from <c>POST /EmbyStreams/Validate</c>.</summary>
    public class ValidateResponse
    {
        /// <summary><c>ok</c> if all checks passed, <c>warn</c> if any warnings, <c>error</c> if any errors.</summary>
        public string Status { get; set; } = "ok";
        /// <summary>Individual check results.</summary>
        public System.Collections.Generic.List<ValidationCheck> Checks { get; set; }
            = new System.Collections.Generic.List<ValidationCheck>();
    }

    /// <summary>
    /// Runs a series of setup sanity checks and returns results for each one.
    /// Called by the first-run wizard and the Settings tab.
    /// </summary>
    public class ValidateService : IService, IRequiresRequest
    {
        private readonly ILogger<ValidateService> _logger;
        private readonly IAuthorizationContext    _authCtx;

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        /// <summary>Emby injects dependencies automatically.</summary>
        public ValidateService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<ValidateService>(logManager.GetLogger("EmbyStreams"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>POST /EmbyStreams/Validate</c>.</summary>
        public async Task<object> Post(ValidateRequest _)
        {
            // SEC-3: Admin-only — makes outbound network call to AIOStreams and
            // reads SyncPath config; should not be accessible to non-admin users.
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var config = Plugin.Instance?.Configuration;
            var resp   = new ValidateResponse();

            if (config == null)
            {
                resp.Status = "error";
                resp.Checks.Add(new ValidationCheck
                {
                    Check   = "plugin",
                    Status  = "error",
                    Message = "Plugin not initialised",
                });
                return resp;
            }

            // ── Movies path ───────────────────────────────────────────────────────
            CheckPath(resp, "movies_path", "Movies folder", config.SyncPathMovies);

            // ── Shows path ────────────────────────────────────────────────────────
            CheckPath(resp, "shows_path", "Shows folder", config.SyncPathShows);

            // ── AIOStreams connectivity ────────────────────────────────────────────
            await CheckAioStreamsAsync(resp, config);

            // ── Database ─────────────────────────────────────────────────────────
            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                resp.Checks.Add(new ValidationCheck
                {
                    Check   = "database",
                    Status  = "error",
                    Message = "DatabaseManager is null — plugin may not have initialised",
                });
            }
            else
            {
                resp.Checks.Add(new ValidationCheck
                {
                    Check   = "database",
                    Status  = "ok",
                    Message = "SQLite database is accessible",
                });
            }

            // Roll up overall status
            bool hasError = false, hasWarn = false;
            foreach (var c in resp.Checks)
            {
                if (c.Status == "error") hasError = true;
                else if (c.Status == "warn") hasWarn = true;
            }
            resp.Status = hasError ? "error" : hasWarn ? "warn" : "ok";

            return resp;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static void CheckPath(ValidateResponse resp, string checkId, string label, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                resp.Checks.Add(new ValidationCheck
                {
                    Check   = checkId,
                    Status  = "warn",
                    Message = $"{label} is not configured",
                });
                return;
            }

            if (!Directory.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(path);
                    resp.Checks.Add(new ValidationCheck
                    {
                        Check   = checkId,
                        Status  = "ok",
                        Message = $"{label}: created '{path}'",
                    });
                }
                catch (Exception ex)
                {
                    resp.Checks.Add(new ValidationCheck
                    {
                        Check   = checkId,
                        Status  = "error",
                        Message = $"{label} '{path}' does not exist and could not be created: {ex.Message}",
                    });
                }
                return;
            }

            // Check write access
            try
            {
                var probe = Path.Combine(path, ".embystreams_probe");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                resp.Checks.Add(new ValidationCheck
                {
                    Check   = checkId,
                    Status  = "ok",
                    Message = $"{label}: '{path}' exists and is writable",
                });
            }
            catch (Exception ex)
            {
                resp.Checks.Add(new ValidationCheck
                {
                    Check   = checkId,
                    Status  = "error",
                    Message = $"{label} '{path}' exists but is not writable: {ex.Message}",
                });
            }
        }

        private async Task CheckAioStreamsAsync(ValidateResponse resp, PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                resp.Checks.Add(new ValidationCheck
                {
                    Check   = "aiostreams",
                    Status  = "warn",
                    Message = "AIOStreams URL not configured",
                });
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var client = new AioStreamsClient(config, _logger);
                using var cts    = new CancellationTokenSource(5_000);
                var (ok, err) = await client.TestConnectionAsync(cts.Token);
                sw.Stop();

                resp.Checks.Add(new ValidationCheck
                {
                    Check   = "aiostreams",
                    Status  = ok ? "ok" : "error",
                    Message = ok
                        ? $"AIOStreams reachable ({sw.ElapsedMilliseconds} ms)"
                        : $"AIOStreams unreachable: {err}",
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                resp.Checks.Add(new ValidationCheck
                {
                    Check   = "aiostreams",
                    Status  = "error",
                    Message = $"AIOStreams check failed: {ex.Message}",
                });
            }
        }
    }

    // ── Housekeeping endpoints (Sprint 60) ─────────────────────────────────────

    [Route("/EmbyStreams/Housekeeping/OrphanedFolders", "POST",
        Summary = "Clean up orphaned [tmdbid=...] folders")]
    public class HousekeepingOrphanedFoldersRequest : IReturn<object> { }

    [Route("/EmbyStreams/Housekeeping/ExpiredStrm", "GET",
        Summary = "List .strm files with expired HMAC signatures")]
    public class HousekeepingExpiredStrmRequest : IReturn<object> { }

    [Route("/EmbyStreams/Housekeeping/StrmCount", "GET",
        Summary = "Count total .strm files in media paths")]
    public class HousekeepingStrmCountRequest : IReturn<object> { }

    public partial class TriggerService
    {
        public object Post(HousekeepingOrphanedFoldersRequest _)
        {
            var svc = new HousekeepingService(_logManager);
            var removed = svc.CleanupOrphanedFolders();
            return new { status = "ok", removed };
        }

        public object Get(HousekeepingExpiredStrmRequest _)
        {
            var svc = new HousekeepingService(_logManager);
            var expired = svc.FindExpiredStrmFiles();
            return new { status = "ok", expired, count = expired.Count };
        }

        public object Get(HousekeepingStrmCountRequest _)
        {
            var svc = new HousekeepingService(_logManager);
            var count = svc.CountStrmFiles();
            return new { status = "ok", count };
        }
    }
}
