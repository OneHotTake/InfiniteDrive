using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Users;

namespace InfiniteDrive.Services
{
    // ── Request/Response DTOs ────────────────────────────────────────────────────

    /// <summary>
    /// Request object for testing on-demand stream resolution.
    /// </summary>
    [Route("/InfiniteDrive/Discover/TestStreamResolution", "GET",
        Summary = "Test on-demand stream resolution for an item")]
    public class DiscoverTestStreamResolutionRequest : IReturn<DiscoverTestStreamResolutionResponse>
    {
        /// <summary>IMDb ID of the item to resolve (required).</summary>
        [ApiMember(Name = "aioId", Description = "AIOStreams ID", DataType = "string", ParameterType = "query")]
        public string AioId { get; set; } = "";

        /// <summary>Season number (for TV series).</summary>
        [ApiMember(Name = "season", Description = "Season number", DataType = "int", ParameterType = "query")]
        public int? Season { get; set; }

        /// <summary>Episode number (for TV series).</summary>
        [ApiMember(Name = "episode", Description = "Episode number", DataType = "int", ParameterType = "query")]
        public int? Episode { get; set; }
    }


    /// <summary>
    /// Response from <c>GET /InfiniteDrive/Discover/TestStreamResolution</c>.
    /// </summary>
    public class DiscoverTestStreamResolutionResponse
    {
        /// <summary>Whether stream resolution was successful.</summary>
        public bool Success { get; set; }

        /// <summary>Error message if resolution failed.</summary>
        public string? Error { get; set; }

        /// <summary>Proxy session token for streaming.</summary>
        public string? ProxyToken { get; set; }

        /// <summary>Direct stream URL using the proxy token.</summary>
        public string? StreamUrl { get; set; }

        /// <summary>Emby web URL for the item (for reference).</summary>
        public string? EmbyUrl { get; set; }
    }

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Discover/Browse</c>.
    /// Returns paginated discover catalog entries.
    /// </summary>
    [Route("/InfiniteDrive/Discover/Browse", "GET",
        Summary = "Browse available items in the Discover catalog")]
    public class DiscoverBrowseRequest : IReturn<DiscoverBrowseResponse>
    {
        /// <summary>Maximum number of items to return (default 50, max 200).</summary>
        [ApiMember(Name = "limit", Description = "Page size", DataType = "int", ParameterType = "query")]
        public int Limit { get; set; } = 50;

        /// <summary>Number of items to skip (for pagination).</summary>
        [ApiMember(Name = "offset", Description = "Skip N items", DataType = "int", ParameterType = "query")]
        public int Offset { get; set; } = 0;
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Discover/Browse</c>.</summary>
    public class DiscoverBrowseResponse
    {
        /// <summary>Items in this page.</summary>
        public List<DiscoverItem> Items { get; set; } = new();

        /// <summary>Total number of items available (for pagination).</summary>
        public int Total { get; set; }

        /// <summary>Current page offset.</summary>
        public int Offset { get; set; }
    }

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Discover/Search</c>.
    /// Searches local catalog and optionally AIOStreams live.
    /// </summary>
    [Route("/InfiniteDrive/Discover/Search", "GET",
        Summary = "Search available items in Discover catalog")]
    public class DiscoverSearchRequest : IReturn<DiscoverSearchResponse>
    {
        /// <summary>Search query (use ?q= or ?Query= parameter).</summary>
        [ApiMember(Name = "q", Description = "Search query", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Query { get; set; } = string.Empty;

        /// <summary>Alias for Query to support ?q= parameter binding.</summary>
        public string Q { get => Query; set => Query = value; }

        /// <summary>Filter by media type: <c>movie</c>, <c>series</c>, or omit for all.</summary>
        [ApiMember(Name = "type", Description = "Media type filter", DataType = "string", ParameterType = "query")]
        public string? Type { get; set; }

        /// <summary>Force live AIOStreams search even if local results are good (default false).</summary>
        [ApiMember(Name = "live", Description = "Force live search", DataType = "bool", ParameterType = "query")]
        public bool Live { get; set; } = false;
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Discover/Search</c>.</summary>
    public class DiscoverSearchResponse
    {
        /// <summary>Merged and deduplicated search results.</summary>
        public List<DiscoverItem> Items { get; set; } = new();
    }

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Discover/Detail</c>.
    /// Fetches detailed metadata for a specific item.
    /// </summary>
    [Route("/InfiniteDrive/Discover/Detail", "GET",
        Summary = "Get detailed metadata for a Discover item")]
    public class DiscoverDetailRequest : IReturn<DiscoverDetailResponse>
    {
        /// <summary>AIOStreams primary ID.</summary>
        [ApiMember(Name = "aioId", Description = "AIOStreams ID", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string AioId { get; set; } = string.Empty;
    }

    /// <summary>Response from <c>GET /InfiniteDrive/Discover/Detail</c>.</summary>
    public class DiscoverDetailResponse
    {
        /// <summary>The detailed item.</summary>
        public DiscoverItem? Item { get; set; }
    }

    /// <summary>
    /// Request object for <c>POST /InfiniteDrive/Discover/AddToLibrary</c>.
    /// Creates a .strm file and adds item to the library.
    /// </summary>
    [Route("/InfiniteDrive/Discover/AddToLibrary", "POST",
        Summary = "Add a Discover item to the user's library")]
    public class DiscoverAddToLibraryRequest : IReturn<DiscoverAddToLibraryResponse>
    {
        /// <summary>AIOStreams primary ID.</summary>
        [ApiMember(Name = "aioId", Description = "AIOStreams ID", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string AioId { get; set; } = string.Empty;

        /// <summary>Media type: <c>movie</c> or <c>series</c>.</summary>
        [ApiMember(Name = "type", Description = "Media type", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Type { get; set; } = string.Empty;

        /// <summary>Display title (used for .strm filename).</summary>
        [ApiMember(Name = "title", Description = "Display title", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Title { get; set; } = string.Empty;

        /// <summary>Release year (optional, used for .strm filename).</summary>
        [ApiMember(Name = "year", Description = "Release year", DataType = "int", ParameterType = "query")]
        public int? Year { get; set; }

        /// <summary>User-chosen playlist name. Defaults to "My InfiniteDrive" if not specified.</summary>
        [ApiMember(Name = "playlistName", Description = "Playlist name", DataType = "string", ParameterType = "query")]
        public string? PlaylistName { get; set; }
    }

    /// <summary>Response from <c>POST /InfiniteDrive/Discover/AddToLibrary</c>.</summary>
    public class DiscoverAddToLibraryResponse
    {
        /// <summary><c>true</c> if the item was added successfully.</summary>
        public bool Ok { get; set; }

        /// <summary>Path to the created .strm file.</summary>
        public string? StrmPath { get; set; }

        /// <summary><c>true</c> when the item is queued but Marvin hasn't assigned a stream yet.</summary>
        public bool IsPending { get; set; }

        /// <summary>Error message if <c>Ok</c> is <c>false</c>.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Request object for <c>POST /InfiniteDrive/Discover/RemoveFromLibrary</c>.
    /// Removes item from current user's saved library.
    /// </summary>
    [Route("/InfiniteDrive/Discover/RemoveFromLibrary", "POST",
        Summary = "Remove item from current user's saved library")]
    public class DiscoverRemoveFromLibraryRequest : IReturn<DiscoverRemoveFromLibraryResponse>
    {
        /// <summary>AIOStreams primary ID.</summary>
        [ApiMember(Name = "aioId", Description = "AIOStreams ID", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string AioId { get; set; } = string.Empty;

        /// <summary>User-chosen playlist name. Defaults to "My InfiniteDrive" if not specified.</summary>
        [ApiMember(Name = "playlistName", Description = "Playlist name", DataType = "string", ParameterType = "query")]
        public string? PlaylistName { get; set; }
    }

    /// <summary>Response from <c>POST /InfiniteDrive/Discover/RemoveFromLibrary</c>.</summary>
    public class DiscoverRemoveFromLibraryResponse
    {
        /// <summary><c>true</c> if the item was removed successfully.</summary>
        public bool Ok { get; set; }

        /// <summary>Error message if <c>Ok</c> is <c>false</c>.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// A single item in the Discover catalog (used in browse/search responses).
    /// Combines metadata from discover_catalog and library status.
    /// </summary>
    public class DiscoverItem
    {
        /// <summary>AIOStreams primary ID.</summary>
        public string AioId { get; set; } = string.Empty;

        /// <summary>Display title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Release year or first-air year.</summary>
        public int? Year { get; set; }

        /// <summary><c>movie</c> or <c>series</c>.</summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>Poster image URL.</summary>
        public string? PosterUrl { get; set; }

        /// <summary>Backdrop image URL.</summary>
        public string? BackdropUrl { get; set; }

        /// <summary>Overview/synopsis text.</summary>
        public string? Overview { get; set; }

        /// <summary>Comma-separated genre names.</summary>
        public string? Genres { get; set; }

        /// <summary>IMDb rating (0-10).</summary>
        public double? ImdbRating { get; set; }

        /// <summary>MPAA/TV rating (e.g., "PG-13", "R", "TV-MA").</summary>
        public string? Certification { get; set; }

        /// <summary><c>true</c> if already in user's Emby library with a stream assigned.</summary>
        public bool InLibrary { get; set; }

        /// <summary><c>true</c> if requested/queued but stream not yet assigned by Marvin.</summary>
        public bool IsPending { get; set; }

        /// <summary>Emby internal item ID (for navigation to detail page).</summary>
        public string? EmbyItemId { get; set; }

        /// <summary>Source catalog (for tracking).</summary>
        public string CatalogSource { get; set; } = string.Empty;

        /// <summary>Comma-separated audio language codes from previous stream resolution (e.g. "ja,en").</summary>
        public string? AudioLanguages { get; set; }
    }

    // ── Rails DTOs ─────────────────────────────────────────────────────────────

    [Route("/InfiniteDrive/Discover/Rails", "GET", Summary = "Get default rails for discover page")]
    public class DiscoverRailsRequest : IReturn<DiscoverRailsResponse>
    {
        /// <summary>Filter: movie, series, or empty for both.</summary>
        public string? Type { get; set; }
    }

    public class DiscoverRailsResponse
    {
        public List<DiscoverRail> Rails { get; set; } = new();
    }

    public class DiscoverRail
    {
        public string Title { get; set; } = string.Empty;
        public string? Type { get; set; }
        public List<DiscoverItem> Items { get; set; } = new();
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles Discover browsing and search requests.
    /// Provides REST API for the Discover channel UI.
    /// </summary>
    public class DiscoverService : IService, IRequiresRequest
    {
        private readonly ILogger<DiscoverService> _logger;
        private readonly DatabaseManager _db;
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authCtx;
        private readonly StrmWriterService _strmWriter;
        private readonly IUserManager _userManager;

        // ── Cinemeta background cache ───────────────────────────────────────────
        private static List<DiscoverRail>? _cinemetaCache;
        private static DateTime _cinemetaCacheTime = DateTime.MinValue;
        private static readonly TimeSpan CinemetaCacheTtl = TimeSpan.FromHours(6);
        private static int _cinemetaRefreshing = 0; // interlocked flag

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Emby injects dependencies automatically.
        /// </summary>
        public DiscoverService(ILogManager logManager, ILibraryManager libraryManager, IAuthorizationContext authCtx, IUserManager userManager)
        {
            _logger = new EmbyLoggerAdapter<DiscoverService>(logManager.GetLogger("InfiniteDrive"));
            _db = Plugin.Instance.DatabaseManager;
            _libraryManager = libraryManager;
            _authCtx = authCtx;
            _strmWriter = Plugin.Instance.StrmWriterService;
            _userManager = userManager;
        }

        // ── Handlers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Discover/Browse</c>.
        /// Returns paginated catalog entries from the local discover_catalog table.
        /// </summary>
        public async Task<object> Get(DiscoverBrowseRequest req)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                var limit = Math.Min(Math.Max(req.Limit, 1), 200);
                var offset = Math.Max(req.Offset, 0);

                var entries = await _db.GetDiscoverCatalogAsync(limit, offset);
                var maxRating = GetUserMaxParentalRating();
                var filtered = ApplyParentalFilter(entries, maxRating);
                var items = (await Task.WhenAll(filtered.Select(e => MapToDiscoverItemAsync(e, null)))).ToList();
                var total = await _db.GetDiscoverCatalogCountAsync();

                return new DiscoverBrowseResponse
                {
                    Items = items,
                    Total = total,
                    Offset = offset
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error browsing Discover catalog");
                throw;
            }
        }

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Discover/Search</c>.
        /// Live AIOStreams search — no local cache. Falls back to Cinemeta if AIOStreams fails.
        /// </summary>
        public async Task<object> Get(DiscoverSearchRequest req)
        {
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                if (string.IsNullOrWhiteSpace(req.Query))
                    return new DiscoverSearchResponse { Items = new() };

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                    return new DiscoverSearchResponse { Items = new() };

                var type = string.IsNullOrEmpty(req.Type) ? null : req.Type.ToLowerInvariant();

                // 1. Live AIOStreams search (manifest-routed)
                var items = await GetLiveSearchResultsAsync(req.Query, type);

                // Filter out AIOStreams error sentinel items (id prefix "aiostreamserror")
                items = items
                    .Where(i => !i.AioId.StartsWith("aiostreamserror", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // 2. Local catalog DB search — InfiniteDrive items + library-sourced items
                var db = Plugin.Instance?.DatabaseManager;
                if (db != null)
                {
                    var catalogMatches = await db.SearchCatalogItemsByTitleAsync(req.Query, 20);
                    foreach (var ci in catalogMatches)
                        items.Add(CatalogItemToDiscoverItem(ci));
                }

                // 3. Emby library title search — catches items not managed by InfiniteDrive
                items.AddRange(SearchEmbyLibrary(req.Query, type, 20));

                // 3b. Relevance gate. AIOStreams/Cinemeta live search is fuzzy and returns
                //     titles that share nothing with the query (e.g. "spider" → "What We Hide").
                //     Keep an item only if its title shares at least one significant token
                //     (≥3 normalized chars) with the query. Permissive by design — we only
                //     drop results with zero overlap, never legitimate fuzzy matches.
                var qTokens = (req.Query ?? string.Empty)
                    .Split(new[] { ' ', '\t', '-', ':', '.' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeTitle)
                    .Where(t => t.Length >= 3)
                    .Distinct()
                    .ToList();
                if (qTokens.Count > 0)
                {
                    items = items
                        .Where(i =>
                        {
                            var nt = NormalizeTitle(i.Title);
                            return nt.Length > 0 && qTokens.Any(t => nt.Contains(t));
                        })
                        .ToList();
                }

                // 4. Batch library lookup to fill InLibrary on live results not already marked
                var allMetas = items
                    .Where(i => !i.InLibrary && !i.IsPending)
                    .Select(i => new AioStreamsMeta { Id = i.AioId })
                    .ToList();
                if (allMetas.Count > 0)
                {
                    var aioIdLookup = allMetas.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    var libraryMap = BatchLibraryLookup(allMetas);
                    foreach (var item in items)
                    {
                        if (!item.InLibrary && !item.IsPending && libraryMap.TryGetValue(item.AioId, out var lib))
                        {
                            item.InLibrary  = lib.Item1;
                            item.EmbyItemId ??= lib.Item2;
                        }
                    }

                    // Pending pass: items in catalog but without a stream yet
                    var pendingIds = _db.GetPendingCatalogAioIds(aioIdLookup.Where(id => id != null).Select(id => id!).ToList());
                    foreach (var item in items)
                    {
                        if (!item.InLibrary && !item.IsPending && pendingIds.Contains(item.AioId))
                            item.IsPending = true;
                    }
                }

                // 5. Deduplicate — two passes:
                //    Pass 1: by AioId (same ID from multiple sources)
                //    Pass 2: by normalized title+year (same content with different IDs, e.g. IMDB vs kitsu)
                //    When collapsing, prefer: anime > series > movie, then InLibrary, then higher rating
                var byAioId = items
                    .GroupBy(i => i.AioId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.InLibrary ? 2 : x.IsPending ? 1 : 0).First())
                    .ToList();

                // A TMDB-sourced copy often has a null Year while its IMDB twin carries one,
                // which split them into two cards. Resolve a null year to the title's single
                // known year so the copies collapse; titles with several distinct years
                // (e.g. remakes) keep a "—" bucket so genuinely different films stay separate.
                // Keyed by media-type family (movie vs series/anime) so a movie and a like-named
                // series never merge — but anime and series, which are the same kind of work
                // across ID systems, still collapse together.
                static string Family(DiscoverItem i) => i.MediaType == "movie" ? "m" : "s";

                var yearsByTitle = byAioId
                    .Where(i => i.Year.HasValue)
                    .GroupBy(i => NormalizeTitle(i.Title) + "" + Family(i))
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Year!.Value).Distinct().ToList());

                string DedupKey(DiscoverItem i)
                {
                    var fam = Family(i);
                    var nt = NormalizeTitle(i.Title);
                    if (i.Year.HasValue) return nt + "|" + fam + "|" + i.Year.Value;
                    if (yearsByTitle.TryGetValue(nt + "" + fam, out var ys) && ys.Count == 1)
                        return nt + "|" + fam + "|" + ys[0];
                    return nt + "|" + fam + "|—";
                }

                var deduped = byAioId
                    .GroupBy(DedupKey)
                    .Select(g =>
                    {
                        var winner = g
                            .OrderByDescending(x => x.MediaType switch { "anime" => 3, "series" => 2, _ => 1 })
                            .ThenByDescending(x => x.InLibrary ? 2 : x.IsPending ? 1 : 0)
                            .ThenByDescending(x => x.Year.HasValue ? 1 : 0)
                            .ThenByDescending(x => x.ImdbRating ?? 0)
                            .First();
                        // Merge facts across the collapsed copies so the surviving card is complete.
                        winner.InLibrary = g.Any(x => x.InLibrary);
                        winner.IsPending = !winner.InLibrary && g.Any(x => x.IsPending);
                        winner.Year ??= g.FirstOrDefault(x => x.Year.HasValue)?.Year;
                        // Anime ID is the primary driver — if any duplicate has an anime-prefixed ID,
                        // the canonical entry is anime regardless of what the winner's stored type says
                        if (winner.MediaType != "anime" && g.Any(x => IsAnimePrefixedId(x.AioId)))
                            winner.MediaType = "anime";
                        return winner;
                    })
                    .OrderBy(x => x.InLibrary ? 0 : x.IsPending ? 1 : 2)
                    .ThenBy(x => x.Title)
                    .Take(50)
                    .ToList();

                return new DiscoverSearchResponse { Items = deduped };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching Discover catalog");
                throw;
            }
        }

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Discover/Rails</c>.
        /// Always returns instantly: DB rails are built inline; Cinemeta rails come
        /// from a static in-memory cache that refreshes in the background every 6 h.
        /// </summary>
        public async Task<object> Get(DiscoverRailsRequest req)
        {
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                var type = string.IsNullOrEmpty(req.Type) ? null : req.Type.ToLowerInvariant();
                var rails = new List<DiscoverRail>();

                // ── DB rails (instant, ~2 ms each) ─────────────────────────────
                // "Recently Added" only. The former "In Your Library" rail was redundant:
                // nearly every catalog item is InLibrary, every card already carries an
                // "In Library" badge, and Emby's own library views cover browsing what you
                // own — Discover is for finding new titles.
                var recentItems = await _db.GetRecentCatalogItemsAsync(20, type);

                if (recentItems.Count > 0)
                {
                    var rail = new DiscoverRail { Title = "Recently Added", Type = type ?? "movie" };
                    foreach (var ci in recentItems)
                        rail.Items.Add(CatalogItemToDiscoverItem(ci));
                    rails.Add(rail);
                }

                // ── Cinemeta rails (cache hit = instant, miss = background refresh) ──
                var now = DateTime.UtcNow;
                var cacheAge = now - _cinemetaCacheTime;
                if (_cinemetaCache != null)
                {
                    // Serve cached rails, filtered by type if requested
                    foreach (var cached in _cinemetaCache)
                        if (type == null || cached.Type == type)
                            rails.Add(cached);

                    // If cache is stale, kick off a background refresh
                    if (cacheAge > CinemetaCacheTtl)
                        _ = RefreshCinemetaCacheAsync();
                }
                else
                {
                    // Cold start — trigger background refresh; user gets DB rails this time
                    _ = RefreshCinemetaCacheAsync();
                }

                return new DiscoverRailsResponse { Rails = rails };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching rails");
                throw;
            }
        }

        /// <summary>
        /// Fetches Cinemeta top 20 movie + series in the background and stores result in
        /// the static cache. Uses an interlocked flag so only one refresh runs at a time.
        /// </summary>
        private async Task RefreshCinemetaCacheAsync()
        {
            // Only one refresh at a time
            if (Interlocked.CompareExchange(ref _cinemetaRefreshing, 1, 0) != 0)
                return;

            try
            {
                _logger.LogDebug("[Discover] Refreshing Cinemeta rail cache");
                using var client = AioStreamsClient.CreateForStremioBase(
                    "https://v3-cinemeta.strem.io", _logger);

                var allMetas = new List<(string t, AioStreamsMeta m)>();
                foreach (var t in new[] { "movie", "series" })
                {
                    var metas = await client.GetCinemetaTopAsync(t, 20, CancellationToken.None);
                    foreach (var m in metas) allMetas.Add((t, m));
                }

                var libraryIds = BatchLibraryLookup(allMetas.Select(x => x.m).ToList());

                // Cross-check against our catalog — items known as anime are excluded from movie/series rails
                var allAioIds = allMetas
                    .Select(x => x.m.ImdbId ?? x.m.Id ?? "")
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var animeInCatalog = _db.GetAnimeMediaTypeAioIds(allAioIds);

                var fresh = new List<DiscoverRail>();
                foreach (var t in new[] { "movie", "series" })
                {
                    var label    = t == "movie" ? "Popular Movies" : "Popular Shows";
                    var railMetas = allMetas.Where(x => x.t == t).Select(x => x.m).ToList();
                    if (railMetas.Count == 0) continue;
                    var rail = new DiscoverRail { Title = label, Type = t };
                    foreach (var meta in railMetas)
                    {
                        var aioId = meta.ImdbId ?? meta.Id ?? "";
                        // Skip items our catalog has classified as anime — they belong on the anime rail
                        if (animeInCatalog.Contains(aioId)) continue;
                        rail.Items.Add(MapMetaToDiscoverItem(meta, libraryIds));
                    }
                    if (rail.Items.Count > 0)
                        fresh.Add(rail);
                }

                _cinemetaCache     = fresh;
                _cinemetaCacheTime = DateTime.UtcNow;
                _logger.LogDebug("[Discover] Cinemeta rail cache refreshed ({Count} rails)", fresh.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Discover] Cinemeta cache refresh failed (non-fatal)");
            }
            finally
            {
                Interlocked.Exchange(ref _cinemetaRefreshing, 0);
            }
        }

        /// <summary>
        /// Maps a catalog DB item to a DiscoverItem, reading rich metadata from RawMetaJson.
        /// </summary>
        private static DiscoverItem CatalogItemToDiscoverItem(CatalogItem ci)
        {
            string? poster = null, backdrop = null, overview = null;
            if (!string.IsNullOrEmpty(ci.RawMetaJson))
            {
                try
                {
                    var meta = System.Text.Json.JsonSerializer.Deserialize<AioMeta>(ci.RawMetaJson);
                    poster   = meta?.Poster;
                    backdrop = meta?.Background;
                    overview = meta?.Description;
                }
                catch { /* non-critical */ }
            }
            // Anime ID is the primary driver — override stored media_type if AioId is anime-prefixed
            var mediaType = IsAnimePrefixedId(ci.AioId) ? "anime" : ci.MediaType;
            var onDisk = !string.IsNullOrEmpty(ci.StrmPath);
            return new DiscoverItem
            {
                AioId       = ci.AioId,
                Title       = ci.Title,
                Year        = ci.Year,
                MediaType   = mediaType,
                PosterUrl   = poster,
                BackdropUrl = backdrop,
                Overview    = overview,
                InLibrary   = onDisk,
                IsPending   = !onDisk,
                CatalogSource = "local:catalog"
            };
        }

        /// <summary>
        /// Searches Emby library by title substring.
        /// Uses in-memory filter since this SDK version does not expose SearchTerm on InternalItemsQuery.
        /// </summary>
        private List<DiscoverItem> SearchEmbyLibrary(string query, string? mediaType, int limit)
        {
            try
            {
                var includeTypes = mediaType switch
                {
                    "movie"  => new[] { "Movie" },
                    "series" => new[] { "Series" },
                    "anime"  => new[] { "Series" },
                    _        => new[] { "Movie", "Series" }
                };

                var allItems = _libraryManager.GetItemList(
                    new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        IncludeItemTypes = includeTypes,
                        Recursive = true
                    });

                var results = new List<DiscoverItem>();
                foreach (var emby in allItems)
                {
                    if (emby.Name == null ||
                        !emby.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? imdbId = null, tmdbId = null;
                    emby.ProviderIds?.TryGetValue("IMDB", out imdbId);
                    emby.ProviderIds?.TryGetValue("TMDB", out tmdbId);
                    var aioId = imdbId ?? (tmdbId != null ? $"tmdb:{tmdbId}" : null);
                    if (string.IsNullOrEmpty(aioId)) continue;

                    var mType = emby.GetType().Name.Equals("Series", StringComparison.OrdinalIgnoreCase)
                        ? "series" : "movie";

                    results.Add(new DiscoverItem
                    {
                        AioId       = aioId,
                        Title       = emby.Name,
                        Year        = emby.ProductionYear,
                        MediaType   = mType,
                        InLibrary   = true,
                        EmbyItemId  = emby.Id.ToString("N"),
                        CatalogSource = "library"
                    });

                    if (results.Count >= limit) break;
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Discover] Emby library title search failed (non-fatal)");
                return new();
            }
        }

        /// <summary>
        /// Queries AIOStreams live for search results across all search-capable catalogs.
        /// Caches results back to the database and returns as DiscoverItems.
        /// </summary>
        private async Task<List<DiscoverItem>> GetLiveSearchResultsAsync(string query, string? mediaType)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return new();

            // Resolve active client + manifest: try primary, fall back to secondary on empty/unreachable
            AioStreamsClient? resolvedClient = null;
            AioStreamsManifest? resolvedManifest = null;

            var primaryClient = AioStreamsClientFactory.Create(_logger);
            primaryClient.Cooldown = Plugin.Instance?.CooldownGate;
            var primaryManifest = await primaryClient.GetManifestAsync(CancellationToken.None);
            if (primaryManifest?.Catalogs?.Count > 0)
            {
                resolvedClient  = primaryClient;
                resolvedManifest = primaryManifest;
            }
            else if (!string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
            {
                _logger.LogWarning("[Discover] Primary manifest empty/unreachable — trying secondary");
                var secondaryClient = AioStreamsClientFactory.TryCreateForManifest(config.SecondaryManifestUrl, _logger);
                if (secondaryClient != null)
                {
                    secondaryClient.Cooldown = Plugin.Instance?.CooldownGate;
                    var secondaryManifest = await secondaryClient.GetManifestAsync(CancellationToken.None);
                    if (secondaryManifest?.Catalogs?.Count > 0)
                    {
                        resolvedClient   = secondaryClient;
                        resolvedManifest = secondaryManifest;
                    }
                }
            }

            if (resolvedClient == null || resolvedManifest == null)
                return new();

            var client   = resolvedClient;
            var manifest = resolvedManifest;

            using (client)
            {
                try
                {
                    // manifest already fetched and validated above — skip re-fetch
                    if (manifest?.Catalogs == null || manifest.Catalogs.Count == 0)
                        return new();

                    // Filter to search-capable catalogs
                    var searchableCatalogs = manifest.Catalogs
                        .Where(c => c.Extra != null && c.Extra.Any(e => e.Name == "search"))
                        .ToList();

                    if (searchableCatalogs.Count == 0)
                        return new();

                    // Filter to applicable catalogs (by media type if specified)
                    var applicableCatalogs = searchableCatalogs
                        .Where(c => mediaType == null || c.Type!.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Fan out to all applicable catalogs in parallel (10s per-catalog timeout)
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var catalogTasks = applicableCatalogs.Select(async catalogDef =>
                    {
                        try
                        {
                            var response = await client.GetCatalogAsync(
                                catalogDef.Type!,
                                catalogDef.Id!,
                                searchQuery: query,
                                genre: null,
                                skip: null,
                                cancellationToken: cts.Token);
                            return (catalogDef, response);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Live search timeout/fail for catalog {Catalog}", catalogDef.Id);
                            return (catalogDef, null);
                        }
                    }).ToList();

                    // Supplement with Cinemeta — broader IMDB-based coverage for movie/series
                    // (restored: was present in old SearchLiveAsync fallback, dropped when we
                    //  switched to manifest-discovered catalog IDs)
                    var cinemetaClient = AioStreamsClient.CreateForStremioBase("https://v3-cinemeta.strem.io", _logger);
                    var cinemetaTypes = mediaType == "anime" ? Array.Empty<string>()
                        : mediaType != null      ? new[] { mediaType }
                        :                          new[] { "movie", "series" };
                    foreach (var cType in cinemetaTypes)
                    {
                        var cDef = new AioStreamsCatalogDef { Type = cType, Id = "top", Name = "Cinemeta" };
                        var capturedType = cType;
                        var capturedDef  = cDef;
                        catalogTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var r = await cinemetaClient.GetCatalogAsync(
                                    capturedType, "top", query, null, null, cts.Token);
                                return (capturedDef, r);
                            }
                            catch { return (capturedDef, (AioStreamsCatalogResponse?)null); }
                        }));
                    }

                    var catalogResults = await Task.WhenAll(catalogTasks);

                    var liveResults = new Dictionary<string, (DiscoverItem Item, string CatalogSource)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (catalogDef, response) in catalogResults)
                    {
                        if (response?.Metas == null) continue;
                        foreach (var meta in response.Metas)
                        {
                            if (string.IsNullOrWhiteSpace(meta.Id) && string.IsNullOrWhiteSpace(meta.ImdbId))
                                continue;

                            var aioId = meta.ImdbId ?? meta.Id ?? "";
                            if (string.IsNullOrWhiteSpace(aioId))
                                continue;

                            if (!liveResults.ContainsKey(aioId))
                            {
                                var isAnimeCatalog = string.Equals(catalogDef.Type, "anime", StringComparison.OrdinalIgnoreCase)
                                    || IsAnimePrefixedId(aioId);
                                var item = new DiscoverItem
                                {
                                    AioId = aioId,
                                    Title = meta.Name ?? "",
                                    Year = ParseYear(meta.ReleaseInfo),
                                    MediaType = isAnimeCatalog ? "anime" : (meta.Type ?? catalogDef.Type ?? "movie"),
                                    PosterUrl = meta.Poster,
                                    BackdropUrl = meta.Background,
                                    Overview = meta.Description,
                                    Genres = meta.Genres != null && meta.Genres.Count > 0 ? string.Join(", ", meta.Genres) : null,
                                    ImdbRating = string.IsNullOrEmpty(meta.ImdbRating) || !double.TryParse(meta.ImdbRating, out var r) ? null : r,
                                    InLibrary = false,
                                    CatalogSource = $"search:{query}"
                                };
                                liveResults[aioId] = (item, catalogDef.Id!);
                                _ = CacheLiveSearchResultAsync(aioId, meta, catalogDef);
                            }
                        }
                    }

                    return liveResults.Values.Select(x => x.Item).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch live search results");
                    return new();
                }
            }
        }

        /// <summary>
        /// Caches a live search result to the database for future queries.
        /// </summary>
        private async Task CacheLiveSearchResultAsync(string aioId, AioStreamsMeta meta, AioStreamsCatalogDef catalogDef)
        {
            try
            {
                var isAnimeCatalog = string.Equals(catalogDef.Type, "anime", StringComparison.OrdinalIgnoreCase)
                    || IsAnimePrefixedId(aioId);
                var entry = new DiscoverCatalogEntry
                {
                    Id = $"aio:{catalogDef.Type}:{aioId}",
                    AioId = aioId,
                    Title = meta.Name ?? "",
                    Year = ParseYear(meta.ReleaseInfo),
                    MediaType = isAnimeCatalog ? "anime" : (meta.Type ?? catalogDef.Type ?? "movie"),
                    PosterUrl = meta.Poster,
                    BackdropUrl = meta.Background,
                    Overview = meta.Description,
                    Genres = meta.Genres != null && meta.Genres.Count > 0 ? string.Join(", ", meta.Genres) : null,
                    ImdbRating = string.IsNullOrEmpty(meta.ImdbRating) || !double.TryParse(meta.ImdbRating, out var r) ? null : r,
                    CatalogSource = catalogDef.Id!,
                    AddedAt = DateTime.UtcNow.ToString("o"),
                    IsInUserLibrary = false
                };
                await _db.UpsertDiscoverCatalogEntryAsync(entry);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cache live search result for {AioId}", aioId);
            }
        }

        /// <summary>
        /// Parses year from ReleaseInfo string (format: "2022" or "2022–").
        /// </summary>
        private static int? ParseYear(string? releaseInfo)
        {
            if (string.IsNullOrEmpty(releaseInfo))
                return null;
            var yearStr = releaseInfo.Split('–', '–')[0].Trim();
            return int.TryParse(yearStr, out var y) ? y : null;
        }

        /// <summary>
        /// Debug endpoint to test catalog queries
        /// </summary>
        [Route("/InfiniteDrive/Debug/TestCatalog", "GET", Summary = "Debug catalog query")]
        public object TestCatalog()
        {
            try
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null)
                    return new { error = "Database not available" };

                var movies = db.GetDiscoverCatalogMovies(0, 3);
                var tvShows = db.GetDiscoverCatalogTvShows(0, 3);

                var sampleMovies = movies.Take(3).ToList();
                _logger.LogInformation("[Debug] Found {Count} movies", sampleMovies.Count);

                foreach (var movie in sampleMovies)
                {
                    var name = movie.GetType().GetProperty("Name")?.GetValue(movie);
                    var id = movie.GetType().GetProperty("Id")?.GetValue(movie);
                    var type = movie.GetType().GetProperty("Type")?.GetValue(movie);
                    _logger.LogInformation("[Debug] Movie: {Name} ({Id}) Type: {Type}", name, id, type);
                }

                return new
                {
                    movieCount = movies.Count,
                    tvShowCount = tvShows.Count,
                    sampleMovies = sampleMovies.Select(m => new
                    {
                        name = m.GetType().GetProperty("Name")?.GetValue(m),
                        id = m.GetType().GetProperty("Id")?.GetValue(m),
                        type = m.GetType().GetProperty("Type")?.GetValue(m),
                        imageUrl = m.GetType().GetProperty("ImageUrl")?.GetValue(m)
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TestCatalog");
                return new { error = "An internal error occurred. Check server logs." };
            }
        }

        /// <summary>
        /// Debug endpoint to test what the channel returns
        /// </summary>
        [Route("/InfiniteDrive/Debug/TestChannel", "GET", Summary = "Test channel item retrieval")]
        public object TestChannel()
        {
            try
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null)
                    return new { error = "Database not available" };

                // Test GetDiscoverCatalogMovies
                var movies = db.GetDiscoverCatalogMovies(0, 3);
                _logger.LogInformation("[Debug] Found {Count} movies from database", movies.Count);

                var result = new List<object>();
                foreach (var movie in movies)
                {
                    var name = movie.GetType().GetProperty("Name")?.GetValue(movie);
                    var id = movie.GetType().GetProperty("Id")?.GetValue(movie);
                    var type = movie.GetType().GetProperty("Type")?.GetValue(movie);

                    result.Add(new {
                        name = name ?? "No name",
                        id = id ?? "No id",
                        type = type ?? "No type"
                    });
                }

                return new
                {
                    success = true,
                    movieCount = movies.Count,
                    sampleMovies = result
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TestChannel");
                return new { error = "An internal error occurred. Check server logs." };
            }
        }

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/Discover/Detail</c>.
        /// Returns detailed metadata for a single item.
        /// </summary>
        public async Task<object> Get(DiscoverDetailRequest req)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                if (string.IsNullOrWhiteSpace(req.AioId))
                {
                    return new DiscoverDetailResponse { Item = null };
                }

                // Try cached entry first
                var entry = await _db.GetDiscoverCatalogEntryByAioIdAsync(req.AioId);
                if (entry != null)
                {
                    var maxRating = GetUserMaxParentalRating();
                    var filtered = ApplyParentalFilter(new List<DiscoverCatalogEntry> { entry }, maxRating);
                    var cached = filtered.Count > 0 ? await MapToDiscoverItemAsync(filtered[0], null) : null;

                    // If cached entry has overview, return it directly
                    if (cached != null && !string.IsNullOrWhiteSpace(cached.Overview))
                        return new DiscoverDetailResponse { Item = cached };

                    // Otherwise fall through to live fetch to enrich
                }

                // Live meta fetch from AIOStreams for full description
                using (var client = AioStreamsClientFactory.Create(_logger))
                {
                    client.Cooldown = Plugin.Instance?.CooldownGate;
                    // Use anime type for anime-prefixed IDs (kitsu, anilist, mal, anidb, etc.)
                    var id = req.AioId ?? "";
                    var type = IsAnimePrefixedId(id)
                        ? "anime"
                        : entry?.MediaType ?? "movie";
                    try
                    {
                        // Bound the live enrichment fetch — a detail view must not hang the
                        // UI for 30s. If AIOStreams is slow/cooldowned, degrade gracefully.
                        using var detailCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var metaResp = await client.GetMetaAsyncTyped(type, req.AioId, detailCts.Token);
                        if (metaResp?.Meta != null)
                        {
                            var m = metaResp.Meta;
                            var item = new DiscoverItem
                            {
                                AioId = m.ImdbId ?? m.Id ?? req.AioId,
                                Title = m.Name ?? "",
                                Year = ParseYear(m.ReleaseInfo),
                                MediaType = m.Type.ToString().ToLowerInvariant(),
                                PosterUrl = m.Poster,
                                BackdropUrl = m.Background,
                                Overview = m.Description,
                                Genres = m.Genres != null && m.Genres.Count > 0 ? string.Join(", ", m.Genres) : null,
                                ImdbRating = string.IsNullOrEmpty(m.ImdbRating) || !double.TryParse(m.ImdbRating, out var r) ? null : r,
                                InLibrary = false,
                                CatalogSource = "live_meta"
                            };

                            // Check library status
                            try
                            {
                                var providerIds = BuildProviderIdPairs(item.AioId);
                                var match = _libraryManager.GetItemList(
                                    new MediaBrowser.Controller.Entities.InternalItemsQuery
                                    {
                                        AnyProviderIdEquals = providerIds,
                                        IncludeItemTypes = item.MediaType == "series"
                                            ? new[] { "Series" }
                                            : new[] { "Movie" },
                                        Recursive = true
                                    }).FirstOrDefault();
                                if (match != null)
                                {
                                    item.InLibrary = true;
                                    item.EmbyItemId = match.Id.ToString("N");
                                }
                            } catch { }

                            return new DiscoverDetailResponse { Item = item };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Live meta fetch failed for {AioId}", req.AioId);
                    }
                }

                // Return cached entry as fallback even without overview
                if (entry != null)
                {
                    var maxRating2 = GetUserMaxParentalRating();
                    var filtered2 = ApplyParentalFilter(new List<DiscoverCatalogEntry> { entry }, maxRating2);
                    var fallback = filtered2.Count > 0 ? await MapToDiscoverItemAsync(filtered2[0], null) : null;
                    return new DiscoverDetailResponse { Item = fallback };
                }

                return new DiscoverDetailResponse { Item = null };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Discover detail");
                throw;
            }
        }

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/Discover/AddToLibrary</c>.
        /// Creates a .strm file in the appropriate library folder and creates a catalog_item entry.
        /// </summary>
        public async Task<object> Post(DiscoverAddToLibraryRequest req)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                // Validate inputs
                if (string.IsNullOrWhiteSpace(req.AioId) ||
                    string.IsNullOrWhiteSpace(req.Type) ||
                    string.IsNullOrWhiteSpace(req.Title))
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = false,
                        Error = "AioId, Type, and Title are required"
                    };
                }

                // Check if already in library — only skip the .strm write if the file is
                // actually on disk. Even then, still surface it in THIS user's playlist/saves
                // (the item may be globally present but not yet in the caller's collection).
                var existing = await _db.GetCatalogItemByAioIdAsync(req.AioId);
                if (existing != null
                    && !string.IsNullOrEmpty(existing.StrmPath)
                    && File.Exists(existing.StrmPath))
                {
                    await AddPerUserSurfaceAsync(req.AioId, req.PlaylistName, existing.StrmPath, TryGetCurrentUserId());
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = true,
                        StrmPath = existing.StrmPath,
                        Error = "Item is already in your library"
                    };
                }

                // Determine target directory based on media type
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = false,
                        Error = "Plugin configuration not available"
                    };
                }

                var targetDir = req.Type.ToLowerInvariant() switch
                {
                    "movie" => config.SyncPathMovies,
                    "series" => config.SyncPathShows,
                    "anime" => config.SyncPathAnime,
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = false,
                        Error = $"Library path not configured or does not exist: {targetDir}"
                    };
                }

                // Get current user ID for attribution
                var callerUserId = TryGetCurrentUserId();

                // Pull metadata from discover_catalog so the item passes the
                // raw_meta_json IS NOT NULL filter on the rails queries.
                var now = DateTime.UtcNow.ToString("o");
                var discoverEntry = await _db.GetDiscoverCatalogEntryByAioIdAsync(req.AioId);
                string? rawMetaJson = null;
                if (discoverEntry != null)
                {
                    rawMetaJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        name       = discoverEntry.Title,
                        poster     = discoverEntry.PosterUrl,
                        background = discoverEntry.BackdropUrl,
                        description = discoverEntry.Overview,
                        imdbRating = discoverEntry.ImdbRating?.ToString(),
                        genres     = discoverEntry.Genres?.Split(',').Select(g => g.Trim()).ToList(),
                    });
                }

                // Reuse existing catalog item if present (just needs a .strm written).
                // Create a new one only if the item isn't in the catalog at all.
                CatalogItem catalogItem;
                if (existing != null)
                {
                    catalogItem = existing;
                    if (rawMetaJson != null) catalogItem.RawMetaJson = rawMetaJson;
                }
                else
                {
                    catalogItem = new CatalogItem
                    {
                        Id        = Guid.NewGuid().ToString(),
                        AioId     = req.AioId,
                        Title     = req.Title,
                        Year      = req.Year,
                        MediaType = req.Type.ToLowerInvariant(),
                        Source    = "discover",
                        RawMetaJson = rawMetaJson,
                    };
                }

                // Write .strm files via StrmWriterService (Sprint 156)
                var strmPath = await _strmWriter.WriteAsync(
                    catalogItem,
                    SourceType.Aio,
                    callerUserId,
                    CancellationToken.None);

                if (strmPath == null)
                {
                    return new DiscoverAddToLibraryResponse
                    {
                        Ok = false,
                        Error = "Failed to write .strm file - check sync paths in configuration"
                    };
                }

                // Update catalog_item with strmPath and local source
                catalogItem.StrmPath = strmPath;
                catalogItem.LocalPath = strmPath;
                catalogItem.LocalSource = "strm";
                await _db.UpsertCatalogItemAsync(catalogItem);

                // Update discover_catalog to mark as in library
                await _db.UpdateDiscoverCatalogLibraryStatusAsync(req.AioId, true);

                // Surface in the caller's playlist + per-user saves.
                await AddPerUserSurfaceAsync(req.AioId, req.PlaylistName, strmPath, callerUserId);

                _logger.LogInformation("Added {AioId} to library", req.AioId);

                // Auto-trigger library refresh in background (fire-and-forget)
                // This ensures that new .strm file is indexed automatically without user action
                TriggerLibraryRefreshAsync(targetDir);

                return new DiscoverAddToLibraryResponse
                {
                    Ok        = true,
                    StrmPath  = strmPath,
                    IsPending = true   // stream not yet assigned; Marvin will populate on next pass
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding item to library: {AioId}", req.AioId);
                return new DiscoverAddToLibraryResponse
                {
                    Ok = false,
                    Error = "An internal error occurred. Check server logs."
                };
            }
        }

        /// <summary>
        /// Surfaces an item in the caller's per-user collection: writes the
        /// collection_membership row (so it joins the user's playlist on reconcile),
        /// adds it to the Emby playlist immediately if the item is already indexed,
        /// and records a per-user save. Safe to call for already-in-library items.
        /// Never throws — surfacing failures must not fail the add.
        /// </summary>
        private async Task AddPerUserSurfaceAsync(string aioId, string? playlistName, string? strmPath, string? callerUserId)
        {
            if (string.IsNullOrEmpty(callerUserId)) return;
            var pl = !string.IsNullOrWhiteSpace(playlistName) ? playlistName! : "My InfiniteDrive";

            try
            {
                await _db.UpsertCollectionMembershipBatchAsync(
                    new List<(string, string?, string, string, string?)>
                    {
                        (pl, null, aioId, "discover", callerUserId)
                    }, CancellationToken.None);

                // If the Emby item already exists, add it to the playlist now.
                if (Plugin.Instance?.PlaylistService != null)
                {
                    var libraryManager = Plugin.Instance.LibraryManager;
                    if (libraryManager != null)
                    {
                        var query = new InternalItemsQuery
                        {
                            AnyProviderIdEquals = BuildProviderIdPairs(aioId),
                            IncludeItemTypes = new[] { "Movie", "Series" },
                            Limit = 1,
                        };
                        var results = libraryManager.GetItemList(query);
                        if (results != null && results.Length > 0 && results[0] != null)
                        {
                            var embyItemId = results[0]!.Id;
                            await Plugin.Instance.PlaylistService.AddItemToPlaylistAsync(
                                pl, embyItemId, callerUserId, CancellationToken.None);

                            var pending = await _db.GetPendingCollectionMembershipsAsync(CancellationToken.None);
                            var match = pending.FirstOrDefault(p => p.AioId == aioId && p.UserId == callerUserId);
                            if (match != default)
                                await _db.UpdateCollectionMembershipEmbyItemIdAsync(match.Id, embyItemId.ToString("N"), CancellationToken.None);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Per-user playlist surface deferred for {AioId} — Marvin will reconcile", aioId);
            }

            // Per-user save (best-effort; deferred to SyncTask if not yet indexed).
            try
            {
                var (providerKey, providerValue) = ParseAioIdForProvider(aioId);
                var mediaItem = await _db.FindMediaItemByProviderIdAsync(providerKey, providerValue, CancellationToken.None);
                if (mediaItem != null)
                {
                    await _db.UpsertUserSaveAsync(callerUserId, mediaItem.Id, "explicit", null, CancellationToken.None);
                    await _db.SyncGlobalSavedFlagAsync(mediaItem.Id, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Per-user save deferred for {AioId}", aioId);
            }
        }

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/Discover/RemoveFromLibrary</c>.
        /// Removes item from current user's saved library.
        /// </summary>
        public async Task<object> Post(DiscoverRemoveFromLibraryRequest req)
        {
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                if (string.IsNullOrWhiteSpace(req.AioId))
                {
                    return new DiscoverRemoveFromLibraryResponse
                    {
                        Ok = false,
                        Error = "AioId is required"
                    };
                }

                var callerUserId = TryGetCurrentUserId();
                if (string.IsNullOrEmpty(callerUserId))
                {
                    return new DiscoverRemoveFromLibraryResponse
                    {
                        Ok = false,
                        Error = "Unable to identify user"
                    };
                }

                var playlistName = !string.IsNullOrWhiteSpace(req.PlaylistName) ? req.PlaylistName : "My InfiniteDrive";

                // The user-facing "remove" is membership + playlist, mirroring how Add
                // surfaced it. media_items is OPTIONAL enrichment — do NOT require it
                // (Add writes membership even when no media_item row exists yet).
                bool hadMembership = false;
                try
                {
                    hadMembership = await _db.CollectionMembershipExistsAsync(req.AioId, callerUserId, CancellationToken.None);
                }
                catch { /* best-effort presence check */ }

                // Resolve the Emby item (for playlist removal) via provider id.
                try
                {
                    var libraryManager = Plugin.Instance?.LibraryManager;
                    var playlistService = Plugin.Instance?.PlaylistService;
                    if (libraryManager != null && playlistService != null)
                    {
                        var results = libraryManager.GetItemList(new InternalItemsQuery
                        {
                            AnyProviderIdEquals = BuildProviderIdPairs(req.AioId),
                            IncludeItemTypes = new[] { "Movie", "Series" },
                            Limit = 1,
                        });
                        if (results != null && results.Length > 0 && results[0] != null)
                            await playlistService.RemoveItemFromPlaylistAsync(playlistName, results[0]!.Id, callerUserId, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Playlist removal failed for {AioId}", req.AioId);
                }

                // Always clear the user's membership for this item.
                await _db.RemoveCollectionMembershipAsync(req.AioId, callerUserId, CancellationToken.None);
                await _db.UpdateDiscoverCatalogLibraryStatusAsync(req.AioId, false);

                // Optional: clear per-user save if a media_item exists.
                var (providerKey, providerValue) = ParseAioIdForProvider(req.AioId);
                var mediaItem = await _db.FindMediaItemByProviderIdAsync(providerKey, providerValue, CancellationToken.None);
                if (mediaItem != null)
                {
                    await _db.DeleteUserSaveAsync(callerUserId, mediaItem.Id, CancellationToken.None);
                    await _db.SyncGlobalSavedFlagAsync(mediaItem.Id, CancellationToken.None);
                }

                if (!hadMembership && mediaItem == null)
                {
                    return new DiscoverRemoveFromLibraryResponse
                    {
                        Ok = false,
                        Error = "That item isn't in your library."
                    };
                }

                _logger.LogInformation("Removed {AioId} from library for user {UserId}", req.AioId, callerUserId);
                return new DiscoverRemoveFromLibraryResponse { Ok = true };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing item from library: {AioId}", req.AioId);
                return new DiscoverRemoveFromLibraryResponse
                {
                    Ok = false,
                    Error = "An internal error occurred. Check server logs."
                };
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Triggers a library refresh for the folder where .strm files are created.
        /// This runs in the background (fire-and-forget) to ensure items are indexed automatically.
        /// </summary>
        private void TriggerLibraryRefreshAsync(string folderPath)
        {
            try
            {
                // Fire-and-forget library refresh
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Triggering library refresh for {Path}", folderPath);

                        // Request Emby to rescan the library
                        // This will detect the new .strm file and index it automatically
                        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None);

                        _logger.LogInformation("Library refresh triggered");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error triggering library refresh");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queuing library refresh");
            }
        }

        /// <summary>
        /// Returns the Emby user ID from the current request context.
        /// </summary>
        private string? TryGetCurrentUserId()
        {
            try
            {
                var user = _authCtx.GetAuthorizationInfo(Request).User;
                return user?.Id.ToString("N");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Maps an AioStreamsMeta to a DiscoverItem, checking Emby library for InLibrary status.
        /// </summary>
        private async Task<DiscoverItem> MapMetaToDiscoverItemAsync(AioStreamsMeta meta)
        {
            var aioId = meta.ImdbId ?? meta.Id ?? "";
            string? embyItemId = null;
            var inLibrary = false;

            if (!string.IsNullOrWhiteSpace(aioId))
            {
                try
                {
                    var providerIds = BuildProviderIdPairs(aioId);
                    var match = _libraryManager.GetItemList(
                        new MediaBrowser.Controller.Entities.InternalItemsQuery
                        {
                            AnyProviderIdEquals = providerIds,
                            IncludeItemTypes = meta.Type == "series" || meta.Type == "anime"
                                ? new[] { "Series" }
                                : new[] { "Movie" },
                            Recursive = true
                        }).FirstOrDefault();
                    if (match != null)
                    {
                        inLibrary = true;
                        embyItemId = match.Id.ToString("N");
                    }
                }
                catch { /* non-critical */ }
            }

            return await Task.FromResult(new DiscoverItem
            {
                AioId = aioId,
                Title = meta.Name ?? "",
                Year = ParseYear(meta.ReleaseInfo),
                MediaType = meta.Type ?? "movie",
                PosterUrl = meta.Poster,
                BackdropUrl = meta.Background,
                Overview = meta.Description,
                Genres = meta.Genres != null && meta.Genres.Count > 0 ? string.Join(", ", meta.Genres) : null,
                ImdbRating = string.IsNullOrEmpty(meta.ImdbRating) || !double.TryParse(meta.ImdbRating, out var r) ? null : r,
                InLibrary = inLibrary,
                EmbyItemId = embyItemId,
                CatalogSource = "live"
            });
        }

        /// <summary>
        /// Batch library lookup — one query for all items, returns map of aioId → (inLibrary, embyItemId).
        /// Dramatically faster than N individual GetItemList calls.
        /// </summary>
        private Dictionary<string, (bool inLibrary, string? embyItemId)> BatchLibraryLookup(List<AioStreamsMeta> metas)
        {
            var result = new Dictionary<string, (bool, string?)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Build provider ID pairs for all items
                var allPairs = new List<KeyValuePair<string, string>>();
                var aioIdList = new List<string>();
                foreach (var meta in metas)
                {
                    var aioId = meta.ImdbId ?? meta.Id ?? "";
                    aioIdList.Add(aioId);
                    if (!string.IsNullOrWhiteSpace(aioId))
                        allPairs.AddRange(BuildProviderIdPairs(aioId));
                }

                if (allPairs.Count == 0)
                    return result;

                // Single query for all provider IDs
                var matches = _libraryManager.GetItemList(
                    new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        AnyProviderIdEquals = allPairs.ToArray(),
                        IncludeItemTypes = new[] { "Series", "Movie" },
                        Recursive = true
                    });

                // Index matches by their provider IDs for fast lookup
                var matchByProvider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in matches)
                {
                    foreach (var kv in item.ProviderIds)
                    {
                        if (!string.IsNullOrEmpty(kv.Value))
                        {
                            // Store both "provider:value" and raw value as keys
                            matchByProvider[$"{kv.Key}:{kv.Value}"] = item.Id.ToString("N");
                            matchByProvider[kv.Value] = item.Id.ToString("N");
                        }
                    }
                }

                // Map each aioId to its library status
                foreach (var aioId in aioIdList)
                {
                    if (string.IsNullOrWhiteSpace(aioId) || result.ContainsKey(aioId))
                        continue;

                    var pairs = BuildProviderIdPairs(aioId);
                    var found = false;
                    foreach (var p in pairs)
                    {
                        var key = $"{p.Key}:{p.Value}";
                        if (matchByProvider.TryGetValue(key, out var embyId))
                        {
                            result[aioId] = (true, embyId);
                            found = true;
                            break;
                        }
                        if (matchByProvider.TryGetValue(p.Value, out embyId))
                        {
                            result[aioId] = (true, embyId);
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        result[aioId] = (false, null);
                }

                // Fallback: check InfiniteDrive catalog_items for items written to disk
                // but not yet scanned by Emby (covers the gap between add and library scan)
                var catalogAioIds = _db.GetCatalogItemAioIdsWithStrmPath(aioIdList);
                foreach (var aioId in catalogAioIds)
                {
                    if (!result.ContainsKey(aioId) || !result[aioId].Item1)
                        result[aioId] = (true, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InfiniteDrive] BatchLibraryLookup non-fatal error");
            }

            return result;
        }

        /// <summary>
        /// Pure mapping function — no library lookup. Use after BatchLibraryLookup.
        /// </summary>
        private static DiscoverItem MapMetaToDiscoverItem(AioStreamsMeta meta,
            Dictionary<string, (bool inLibrary, string? embyItemId)> libraryMap)
        {
            var aioId = meta.ImdbId ?? meta.Id ?? "";
            var (inLibrary, embyItemId) = libraryMap.TryGetValue(aioId, out var v) ? v : (false, null);

            return new DiscoverItem
            {
                AioId = aioId,
                Title = meta.Name ?? "",
                Year = ParseYear(meta.ReleaseInfo),
                MediaType = meta.Type ?? "movie",
                PosterUrl = meta.Poster,
                BackdropUrl = meta.Background,
                Overview = meta.Description,
                Genres = meta.Genres != null && meta.Genres.Count > 0 ? string.Join(", ", meta.Genres) : null,
                ImdbRating = string.IsNullOrEmpty(meta.ImdbRating) || !double.TryParse(meta.ImdbRating, out var r) ? null : r,
                InLibrary = inLibrary,
                EmbyItemId = embyItemId,
                CatalogSource = "live"
            };
        }

        /// <summary>
        /// Maps a DiscoverCatalogEntry to a DiscoverItem (response DTO).
        /// Looks up the Emby internal item ID for library items.
        /// userPinnedImdbIds: when non-null, overrides InLibrary with per-user pin status.
        /// </summary>
        private async Task<DiscoverItem> MapToDiscoverItemAsync(DiscoverCatalogEntry entry, HashSet<string>? userPinnedImdbIds = null)
        {
            var inLibrary = userPinnedImdbIds != null
                ? (!string.IsNullOrEmpty(entry.AioId) && userPinnedImdbIds.Contains(entry.AioId))
                : entry.IsInUserLibrary;

            string? embyItemId = null;
            if (inLibrary && !string.IsNullOrWhiteSpace(entry.AioId))
            {
                try
                {
                    var providerIds = BuildProviderIdPairs(entry.AioId);
                    var match = _libraryManager.GetItemList(
                        new MediaBrowser.Controller.Entities.InternalItemsQuery
                        {
                            AnyProviderIdEquals = providerIds,
                            IncludeItemTypes = entry.MediaType == "series"
                                ? new[] { "Series" }
                                : new[] { "Movie" },
                            Recursive = true
                        }).FirstOrDefault();
                    if (match != null)
                        embyItemId = match.Id.ToString("N");
                }
                catch (Exception ex)
                {
                    // Non-critical failure to look up Emby item - log and continue
                    _logger.LogDebug(ex, "[InfiniteDrive] DiscoverService: Failed to look up Emby item for {AioId}", entry.AioId);
                }
            }

            return new DiscoverItem
            {
                AioId = entry.AioId,
                Title = entry.Title,
                Year = entry.Year,
                MediaType = entry.MediaType,
                PosterUrl = entry.PosterUrl,
                BackdropUrl = entry.BackdropUrl,
                Overview = entry.Overview,
                Genres = entry.Genres,
                ImdbRating = entry.ImdbRating,
                Certification = entry.Certification,
                InLibrary = inLibrary,
                EmbyItemId = embyItemId,
                CatalogSource = entry.CatalogSource,
                AudioLanguages = await GetAudioLanguagesAsync(entry.AioId),
            };
        }

        private async Task<string?> GetAudioLanguagesAsync(string aioId)
        {
            try
            {
                var candidates = await _db.GetStreamCandidatesAsync(aioId, null, null);
                var langs = candidates?
                    .Where(c => c.Status == "valid" && !string.IsNullOrEmpty(c.Languages))
                    .Select(c => c.Languages)
                    .FirstOrDefault();
                return langs;
            }
            catch { return null; }
        }

        /// <summary>
        /// Sanitizes a string for use as a filename.
        /// Removes or replaces characters invalid in filenames.
        /// </summary>
        private static string SanitizeFilename(string filename)
        {
            var invalidChars = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            var result = filename;
            foreach (var c in invalidChars)
            {
                result = result.Replace(c, '_');
            }
            return result.Trim().TrimEnd('.');
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

        /// <summary>
        /// Test endpoint for on-demand stream resolution.
        /// Demonstrates StreamResolver integration for on-demand playback.
        /// </summary>
        [Route("/InfiniteDrive/Discover/TestStreamResolution", "GET")]
        public async Task<object> Get(DiscoverTestStreamResolutionRequest req)
        {
            // Sprint 204: Un-gate endpoint - allow authenticated users, not just admins
            var deny = AdminGuard.RequireAuthenticated(_authCtx, Request);
            if (deny != null) return deny;

            try
            {
                if (string.IsNullOrWhiteSpace(req.AioId))
                {
                    return new DiscoverTestStreamResolutionResponse
                    {
                        Success = false,
                        Error = "AioId is required",
                        ProxyToken = null,
                        StreamUrl = null
                    };
                }

                _logger.LogInformation("[Discover] Testing stream resolution for {AioId}", req.AioId);

                var config = Plugin.Instance?.Configuration;
                var db = Plugin.Instance?.DatabaseManager;
                if (config == null || db == null)
                {
                    return new DiscoverTestStreamResolutionResponse
                    {
                        Success = false,
                        Error = "Plugin not initialized",
                        ProxyToken = null,
                        StreamUrl = null
                    };
                }

                // Check cache first
                var cached = await db.GetCachedStreamAsync(req.AioId, req.Season, req.Episode);
                var candidates = cached != null
                    ? await db.GetStreamCandidatesAsync(req.AioId, req.Season, req.Episode)
                    : new List<StreamCandidate>();

                // If valid cache hit, use it
                if (cached?.Status != "valid" || string.IsNullOrEmpty(cached.ExpiresAt) ||
                    DateTime.TryParse(cached.ExpiresAt, out var expiry) && DateTime.UtcNow > expiry)
                {
                    // Cache miss or stale - sync resolve
                    var playReq = new PlayRequest
                    {
                        AioId = req.AioId,
                        Season = req.Season,
                        Episode = req.Episode
                    };
                    var resolved = await StreamResolutionHelper.SyncResolveViaProvidersAsync(
                        playReq, config, db, _logger, Plugin.Instance?.ResolverHealthTracker, CancellationToken.None);
                    if (resolved.Status == ResolutionStatus.Success && resolved.Entry != null)
                    {
                        candidates = await db.GetStreamCandidatesAsync(req.AioId, req.Season, req.Episode);
                        cached = resolved.Entry;
                    }
                }

                if (cached == null || string.IsNullOrEmpty(cached.StreamUrl))
                {
                    return new DiscoverTestStreamResolutionResponse
                    {
                        Success = false,
                        Error = "No stream available",
                        ProxyToken = null,
                        StreamUrl = null
                    };
                }

                // Use direct stream URL
                var streamUrl = cached.StreamUrl;

                _logger.LogInformation("[Discover] Stream resolution successful for {AioId}", req.AioId);

                return new DiscoverTestStreamResolutionResponse
                {
                    Success = true,
                    ProxyToken = null,
                    StreamUrl = streamUrl,
                    EmbyUrl = $"http://127.0.0.1:8096/web/#/details/{req.AioId}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Error testing stream resolution for {AioId}", req.AioId ?? "null");
                return new DiscoverTestStreamResolutionResponse
                {
                    Success = false,
                    Error = "An internal error occurred. Check server logs.",
                    ProxyToken = null,
                    StreamUrl = null
                };
            }
        }

        /// <summary>
        /// Parses an AIO ID to determine the appropriate provider key/value for Emby library lookup.
        /// </summary>
        private static (string key, string value) ParseAioIdForProvider(string aioId)
        {
            if (aioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                return ("imdb", aioId);
            if (aioId.Contains(':'))
            {
                var parts = aioId.Split(':');
                if (parts.Length == 2)
                    return (parts[0].ToLowerInvariant(), parts[1]);
            }
            return ("imdb", aioId);
        }

        /// <summary>
        /// Detects anime-specific ID prefixes from AIOStreams IdParser.
        /// </summary>
        /// <summary>Strips punctuation/spaces/case for title-based dedup.</summary>
        private static string NormalizeTitle(string? title)
            => new string((title ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static bool IsAnimePrefixedId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var c = char.ToLowerInvariant(id[0]);
            return c switch
            {
                'k' => id.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase),
                'a' => id.StartsWith("anilist:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("anidb:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("anidb_id:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("anidbid:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("animeplanet:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("ap:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("acd:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("anisearch:", StringComparison.OrdinalIgnoreCase),
                'm' => id.StartsWith("mal:", StringComparison.OrdinalIgnoreCase),
                'n' => id.StartsWith("notifymoe:", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("nm:", StringComparison.OrdinalIgnoreCase),
                's' => id.StartsWith("simkl:", StringComparison.OrdinalIgnoreCase),
                'l' => id.StartsWith("livechart:", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        /// <summary>
        /// Builds an array of provider ID pairs for Emby library AnyProviderIdEquals queries.
        /// </summary>
        private static KeyValuePair<string, string>[] BuildProviderIdPairs(string aioId)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            if (aioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                pairs.Add(new KeyValuePair<string, string>("Imdb", aioId));
            else if (aioId.Contains(':'))
            {
                var parts = aioId.Split(':');
                if (parts.Length == 2)
                {
                    // e.g. "kitsu:46474" → try provider key "kitsu" with value "46474"
                    pairs.Add(new KeyValuePair<string, string>(parts[0], parts[1]));
                    // Also try the raw value with common keys
                    pairs.Add(new KeyValuePair<string, string>(parts[0].ToLowerInvariant(), parts[1]));
                }
            }
            else
                pairs.Add(new KeyValuePair<string, string>("Imdb", aioId));

            return pairs.ToArray();
        }

        // ── Parental Filter Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Applies parental rating filter to a list of discover catalog entries.
        /// Filters out items that exceed the user's maximum allowed parental rating.
        /// </summary>
        private List<DiscoverCatalogEntry> ApplyParentalFilter(List<DiscoverCatalogEntry> items, int maxRating)
        {
            var config = Plugin.Instance?.Configuration;
            var hideUnrated = config?.HideUnratedContent ?? false;
            var blockUnrated = config?.BlockUnratedForRestricted ?? false;

            // Unrestricted user: only apply global HideUnratedContent toggle
            if (maxRating >= 999)
            {
                if (!hideUnrated)
                    return items;
                return items.Where(item => !string.IsNullOrEmpty(item.Certification)).ToList();
            }

            return items.Where(item =>
            {
                // Global admin toggle: hide unrated from everyone
                if (hideUnrated && string.IsNullOrEmpty(item.Certification))
                    return false;

                var itemRating = ParseRating(item.Certification);

                // Rated item: block if exceeds user's ceiling
                if (itemRating < 999)
                {
                    return itemRating <= maxRating;
                }

                // Unrated item for restricted user: check server toggle
                if (itemRating >= 999)
                {
                    return !blockUnrated; // Show only if NOT blocking unrated
                }

                return true;
            }).ToList();
        }

        /// <summary>
        /// Gets the maximum allowed parental rating for the current request user.
        /// Returns 999 for unrestricted access (no ceiling).
        /// </summary>
        private int GetUserMaxParentalRating()
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return 999; // Unauthenticated = unrestricted for now
                }

                // Load user from Emby user manager
                var user = _userManager.GetUserById(userId);
                if (user?.Policy == null)
                {
                    return 999; // User or policy not found = unrestricted
                }

                // Return user's MaxParentalRating (null means unrestricted)
                return user.Policy.MaxParentalRating ?? 999;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ParentalFilter] Failed to get user max parental rating, defaulting to unrestricted");
                return 999;
            }
        }

        /// <summary>
        /// Maps rating labels to numeric values for comparison.
        /// Higher values = more restrictive content.
        /// Unknown/null ratings return 999 (fail-closed = restricted).
        /// </summary>
        private int ParseRating(string? ratingLabel)
        {
            if (string.IsNullOrEmpty(ratingLabel))
            {
                return 999; // Unknown = restricted
            }

            // Map rating labels to numeric values for comparison
            return ratingLabel switch
            {
                "G" => 100,
                "TV-Y" => 100,
                "TV-G" => 200,
                "PG" => 300,
                "TV-PG" => 300,
                "PG-13" => 400,
                "TV-14" => 500,
                "R" => 600,
                "TV-MA" => 700,
                "NC-17" => 800,
                _ => 999
            };
        }

        private string? GetUserId() => TryGetCurrentUserId();
    }
}
