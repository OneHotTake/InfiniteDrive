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
    public class CatalogsRequest : IReturn<object>
    {
        /// <summary>
        /// Optional manifest URL override. When provided, this URL is used instead of
        /// the saved configuration. Used by the wizard before config is saved.
        /// </summary>
        [ApiMember(Name = "ManifestUrl", Description = "Override manifest URL (used by wizard before config is saved)",
            DataType = "string", ParameterType = "query", IsRequired = false)]
        public string? ManifestUrl { get; set; }
    }

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
        public async Task<object> Get(CatalogsRequest request)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return new CatalogsResponse { Error = "Plugin not initialised" };

            // Prefer the live URL from the wizard, fall back to saved config
            var aioUrl = request.ManifestUrl
                ?? config.PrimaryManifestUrl
                ?? config.SecondaryManifestUrl;

            if (string.IsNullOrWhiteSpace(aioUrl))
                return new CatalogsResponse { Error = "AIOStreams manifest URL not configured" };

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
}
