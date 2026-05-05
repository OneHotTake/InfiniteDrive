using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Tasks
{
    // ── CatalogFetchResult ──────────────────────────────────────────────────────

    /// <summary>
    /// The outcome of one <see cref="ICatalogProvider"/> run.
    /// Separates the item list from health metadata so <see cref="CatalogSyncTask"/>
    /// can persist per-source reliability information without conflating it with
    /// the item data.
    /// </summary>
    public class CatalogFetchResult
    {
        /// <summary>Items fetched from this provider (may be empty on partial failure).</summary>
        public List<CatalogItem> Items { get; set; } = new List<CatalogItem>();

        /// <summary>
        /// True when the upstream endpoint was reachable.
        /// False means the manifest / API was completely unreachable; the task
        /// must NOT remove any existing items for this provider.
        /// </summary>
        public bool ProviderReachable { get; set; } = true;

        /// <summary>Human-readable error from the most recent failure, or null.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Per-catalog outcomes keyed by the sync_state source_key
        /// (e.g. <c>aio:movie:gdrive</c>).  Only populated by providers that
        /// expose individual catalog endpoints (AIOStreams).
        /// </summary>
        public Dictionary<string, CatalogOutcome> CatalogOutcomes { get; set; }
            = new Dictionary<string, CatalogOutcome>();
    }

    /// <summary>Result for a single catalog endpoint within a provider run.</summary>
    public class CatalogOutcome
    {
        /// <summary>True when the endpoint responded with usable data.</summary>
        public bool Succeeded { get; set; }

        /// <summary>Number of items returned by this catalog.</summary>
        public int ItemCount { get; set; }

        /// <summary>Error message on failure, or null.</summary>
        public string? Error { get; set; }
    }

    // ── ICatalogProvider interface ──────────────────────────────────────────────

    /// <summary>
    /// Contract for all catalog data sources (AIOStreams, Trakt, etc.).
    /// </summary>
    public interface ICatalogProvider
    {
        /// <summary>Display name used in log messages.</summary>
        string ProviderName { get; }

        /// <summary>
        /// Source key used as the primary key in <c>sync_state</c>.
        /// Must be unique across all providers.
        /// </summary>
        string SourceKey { get; }

        /// <summary>
        /// Fetches catalog items from the upstream source and reports health outcomes.
        /// Implementations must honour <paramref name="cancellationToken"/>
        /// and cap output at per-catalog limits if configured.
        /// Must never throw — all errors must be captured in <see cref="CatalogFetchResult"/>.
        /// </summary>
        Task<CatalogFetchResult> FetchItemsAsync(
            PluginConfiguration      config,
            ILogger                  logger,
            Data.DatabaseManager?    db,
            CancellationToken        cancellationToken);
    }

    // ── AIOStreams catalog provider ─────────────────────────────────────────────

    /// <summary>
    /// Fetches catalog items from all AIOStreams catalog endpoints.
    ///
    /// On each sync run it:
    /// <list type="number">
    ///   <item>Fetches the AIOStreams manifest to discover ALL configured catalogs
    ///         (covers every addon the user has enabled: Torrentio, Comet,
    ///         MediaFusion, Google Drive, TorBox Search, Prowlarr, etc.)</item>
    ///   <item>Filters the catalog list against the optional
    ///         <see cref="PluginConfiguration.AioStreamsCatalogIds"/> allowlist</item>
    ///   <item>Fetches each catalog page and maps items to <see cref="CatalogItem"/> rows</item>
    /// </list>
    ///
    /// Supported AIOStreams addons whose catalogs are automatically discovered:
    /// Torrentio, Comet, MediaFusion, Stremio-Jackett, Knightcrawler,
    /// Google Drive, TorBox Search, Prowlarr, Torznab, Newznab,
    /// Torrent Galaxy, EZTV, Knaben, SeaDex, Easynews Search, Library.
    /// </summary>
    public class AioStreamsCatalogProvider : ICatalogProvider
    {
        private readonly string? _manifestUrl;
        private readonly string _keyPrefix;
        private readonly string _sourceLabel;

        /// <summary>
        /// Creates a provider for the primary manifest.
        /// </summary>
        public AioStreamsCatalogProvider() : this(null) { }

        /// <summary>
        /// Creates a provider for a specific manifest URL.
        /// Pass null for the primary manifest (reads from config).
        /// </summary>
        public AioStreamsCatalogProvider(string? manifestUrl)
        {
            _manifestUrl = manifestUrl;
            _keyPrefix   = manifestUrl == null ? "aio" : "aio2";
            _sourceLabel = manifestUrl == null ? "Primary Manifest" : "Secondary Manifest";
        }

        /// <inheritdoc/>
        public string ProviderName => _manifestUrl == null ? "AIOStreams" : "AIOStreams (Secondary)";

        /// <inheritdoc/>
        public string SourceKey => _manifestUrl == null ? "aiostreams" : "aiostreams_secondary";

        /// <inheritdoc/>
        public async Task<CatalogFetchResult> FetchItemsAsync(
            PluginConfiguration   config,
            ILogger               logger,
            Data.DatabaseManager? db,
            CancellationToken     cancellationToken)
        {
            var result = new CatalogFetchResult();

            var manifestUrl = _manifestUrl ?? config.PrimaryManifestUrl;
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                result.ProviderReachable = false;
                result.ErrorMessage = "AIOStreams URL is not configured";
                logger.LogWarning("[InfiniteDrive] {Err}", result.ErrorMessage);
                return result;
            }

            // Build client from the specific manifest URL
            using var client = AioStreamsClientFactory.TryCreateForManifest(manifestUrl, logger);
            if (client == null)
            {
                result.ProviderReachable = false;
                result.ErrorMessage = "AIOStreams URL could not be parsed";
                logger.LogWarning("[InfiniteDrive] {Err}", result.ErrorMessage);
                return result;
            }

            client.Cooldown = Plugin.Instance?.CooldownGate;
            client.ActiveCooldownKind = CooldownKind.Default;
            if (!client.IsConfigured)
            {
                result.ProviderReachable = false;
                result.ErrorMessage = "AIOStreams client could not be configured (check URL / UUID / token)";
                logger.LogWarning("[InfiniteDrive] {Err}", result.ErrorMessage);
                return result;
            }

            // 1. Fetch manifest — failure here means the provider is unreachable.
            List<AioStreamsCatalogDef> catalogs;
            try
            {
                catalogs = await DiscoverCatalogsAsync(client, config, logger, cancellationToken, isSecondary: _manifestUrl != null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.ProviderReachable = false;
                result.ErrorMessage = $"Manifest unreachable: {ex.Message}";
                logger.LogWarning(ex, "[InfiniteDrive] AIOStreams manifest fetch failed");
                return result;
            }

            if (catalogs.Count == 0)
            {
                // Manifest returned OK but had no usable catalogs — count as reachable
                // but warn; do not remove existing items.
                result.ProviderReachable = true;
                result.ErrorMessage = "Manifest returned no eligible catalogs (check addon configuration)";
                logger.LogWarning("[InfiniteDrive] {Err}", result.ErrorMessage);
                return result;
            }

            logger.LogInformation("[InfiniteDrive] Discovered {Count} AIOStreams catalog(s) to sync ({Source})", catalogs.Count, _sourceLabel);

            // 2. Record ALL discovered catalogs in the UI before the fetch loop
            foreach (var catalog in catalogs)
            {
                var key          = $"{_keyPrefix}:{catalog.Type}:{catalog.Id}";
                var catalogLimit = GetCatalogLimit(config, key);
                if (db != null)
                    await db.RecordCatalogRunningAsync(
                        key,
                        catalog.Name ?? catalog.Id!,
                        catalog.Type!,
                        catalogLimit);
            }

            // 3. Fetch catalogs in parallel (up to 4 concurrent) for speed.
            using var catalogGate = new SemaphoreSlim(4);
            var catalogResults = new ConcurrentBag<(int Idx, string Key, List<CatalogItem> Items, CatalogOutcome Outcome, string? ManifestUrl)>();

            var catalogTasks = catalogs.Select((catalog, idx) => Task.Run(async () =>
            {
                await catalogGate.WaitAsync(cancellationToken);
                try
                {
                    var key          = $"{_keyPrefix}:{catalog.Type}:{catalog.Id}";
                    var catalogLimit = GetCatalogLimit(config, key);

                    logger.LogInformation("[InfiniteDrive] Fetching catalog {Idx}/{Total}: {Key}", idx + 1, catalogs.Count, key);

                    var (items, outcome) = await FetchOneCatalogAsync(
                        client, catalog, logger, catalogLimit, cancellationToken,
                        onProgress: db == null ? null
                            : async count => await db.UpdateCatalogProgressAsync(key, count));

                    logger.LogInformation("[InfiniteDrive] Catalog {Idx}/{Total}: {Key} → {Count} items, ok={Ok}",
                        idx + 1, catalogs.Count, key, items.Count, outcome.Succeeded);

                    // Stamp originating manifest URL for episode expansion routing
                    foreach (var catItem in items)
                        catItem.SourceManifestUrl = manifestUrl;

                    catalogResults.Add((idx, key, items, outcome, manifestUrl));
                }
                finally
                {
                    catalogGate.Release();
                }
            }, cancellationToken));

            await Task.WhenAll(catalogTasks);

            // Reassemble in original order
            foreach (var (_, key, items, outcome, _) in catalogResults.OrderBy(x => x.Idx))
            {
                result.Items.AddRange(items);
                result.CatalogOutcomes[key] = outcome;
                if (outcome.Succeeded)
                    logger.LogInformation("[InfiniteDrive] Catalog {Key} → {Count} items", key, outcome.ItemCount);
                else
                    logger.LogWarning("[InfiniteDrive] Catalog {Key} failed: {Err}", key, outcome.Error);
            }

            result.ProviderReachable = true;

            return result;
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private static async Task<List<AioStreamsCatalogDef>> DiscoverCatalogsAsync(
            AioStreamsClient client,
            PluginConfiguration config,
            ILogger logger,
            CancellationToken cancellationToken,
            bool isSecondary = false)
        {
            var manifest = await client.GetManifestAsync(cancellationToken);
            if (manifest == null)
            {
                logger.LogWarning("[InfiniteDrive] AIOStreams manifest fetch returned null — check URL and connectivity");
                return new List<AioStreamsCatalogDef>();
            }

            // ── Persist discovered manifest metadata (primary only) ───────────────
            var needsSave = false;
            if (!isSecondary)
            {
            if (!string.IsNullOrEmpty(manifest.Name)
                && manifest.Name != config.AioStreamsDiscoveredName)
            {
                config.AioStreamsDiscoveredName = manifest.Name;
                needsSave = true;
                logger.LogInformation("[InfiniteDrive] Addon name: {Name}", manifest.Name);
            }
            if (!string.IsNullOrEmpty(manifest.Version)
                && manifest.Version != config.AioStreamsDiscoveredVersion)
            {
                config.AioStreamsDiscoveredVersion = manifest.Version;
                needsSave = true;
                logger.LogInformation("[InfiniteDrive] Addon version: {Version}", manifest.Version);
            }

            // ── Stream-only mode detection ────────────────────────────────────────
            var isStreamOnly = manifest.IsStreamOnly;
            if (isStreamOnly != config.AioStreamsIsStreamOnly)
            {
                config.AioStreamsIsStreamOnly = isStreamOnly;
                needsSave = true;
            }
            if (isStreamOnly)
            {
                logger.LogInformation(
                    "[InfiniteDrive] '{Name}' is stream-only (no catalog entries). " +
                    "Library must be populated via Trakt or MDBList. Stream resolution works normally.",
                    manifest.Name ?? "AIOStreams");
            }

            // ── Non-standard stream types (library, other) ───────────────────────
            var streamResource = manifest.Resources?
                .FirstOrDefault(r => string.Equals(r.Name, "stream", StringComparison.OrdinalIgnoreCase));
            var unsupportedTypes = streamResource?.Types?
                .Where(t => t != "movie" && t != "series" && t != "anime"
                         && t != "tv"    && t != "events")
                .ToList();
            if (unsupportedTypes?.Count > 0)
            {
                // "library" type = debrid cloud library (e.g. aiostreams::library.realdebrid prefix)
                // "other" type   = generic catch-all
                // Neither is generated by InfiniteDrive .strm files — logged for future reference.
                logger.LogDebug(
                    "[InfiniteDrive] Stream resource advertises additional types not handled by InfiniteDrive: {Types}",
                    string.Join(", ", unsupportedTypes));
            }

            // ── IMDB-ID capability check ──────────────────────────────────────────
            var prefixes = manifest.StreamIdPrefixes;
            var prefixStr = prefixes.Count > 0 ? string.Join(",", prefixes) : string.Empty;
            if (prefixStr != config.AioStreamsStreamIdPrefixes)
            {
                config.AioStreamsStreamIdPrefixes = prefixStr;
                needsSave = true;
            }
            if (!manifest.SupportsImdbIds)
            {
                logger.LogWarning(
                    "[InfiniteDrive] Stream resource does not list 'tt' or 'imdb' in idPrefixes ({Prefixes}). " +
                    "InfiniteDrive generates IMDB IDs only — stream resolution may fail.",
                    prefixStr);
            }

            // ── configurationRequired warning ─────────────────────────────────────
            if (manifest.BehaviorHints?.ConfigurationRequired == true)
            {
                logger.LogWarning(
                    "[InfiniteDrive] Manifest sets configurationRequired=true. " +
                    "Complete the AIOStreams web UI configuration before streams will resolve.");
            }

            if (needsSave) Plugin.Instance?.SaveConfiguration();
            } // end primary-only config mutation

            if (manifest.Catalogs == null || manifest.Catalogs.Count == 0)
                return new List<AioStreamsCatalogDef>();

            // ── Read requestTimeout hint from manifest ────────────────────────────
            var manifestTimeout = manifest.BehaviorHints?.RequestTimeout ?? 0;
            if (manifestTimeout > 0
                && manifestTimeout != config.AioStreamsDiscoveredTimeoutSeconds)
            {
                config.AioStreamsDiscoveredTimeoutSeconds = manifestTimeout;
                Plugin.Instance?.SaveConfiguration();
                logger.LogInformation(
                    "[InfiniteDrive] Manifest behaviorHints.requestTimeout = {T}s — stored as discovered timeout",
                    manifestTimeout);
            }

            // Determine accepted catalog types from the manifest's catalog resource.
            // This automatically includes custom types like Marvel, StarWars, DC.
            // Fall back to standard types if the manifest has no catalog resource.
            var catalogResource = manifest.Resources?.FirstOrDefault(r =>
                string.Equals(r.Name, "catalog", StringComparison.OrdinalIgnoreCase));
            var acceptedTypes = catalogResource?.Types != null && catalogResource.Types.Count > 0
                ? new HashSet<string>(catalogResource.Types, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "movie", "series", "anime" };

            logger.LogDebug(
                "[InfiniteDrive] Accepted catalog types: {Types}",
                string.Join(", ", acceptedTypes));

            var all = manifest.Catalogs
                .Where(c => !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Type))
                .Where(c => acceptedTypes.Contains(c.Type!))
                .ToList();

            // Apply the optional catalog ID allowlist
            var allowedIds = ParseCatalogIdFilter(config.AioStreamsCatalogIds);
            if (allowedIds.Count > 0)
            {
                all = all
                    .Where(c => allowedIds.Contains(c.Id!, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                logger.LogInformation(
                    "[InfiniteDrive] Catalog allowlist active — {Count}/{Total} catalogs selected",
                    all.Count, manifest.Catalogs.Count);
            }

            return all;
        }

        // AIOStreams returns up to this many items per catalog page.
        internal const int AioCatalogPageSize = 100;
        internal const int AioCatalogMaxPages = 200;

        internal static async Task<(List<CatalogItem> Items, CatalogOutcome Outcome)> FetchOneCatalogAsync(
            AioStreamsClient      client,
            AioStreamsCatalogDef  catalog,
            ILogger               logger,
            int                   itemCap,
            CancellationToken     cancellationToken,
            Func<int, Task>?      onProgress = null)
        {
            var items    = new List<CatalogItem>();
            int offset   = 0;
            int pagesFetched = 0;

            try
            {
                while (items.Count < itemCap && pagesFetched < AioCatalogMaxPages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // First page uses the simple URL; subsequent pages add skip=N
                    var response = offset == 0
                        ? await client.GetCatalogAsync(catalog.Type!, catalog.Id!, cancellationToken)
                        : await client.GetCatalogAsync(catalog.Type!, catalog.Id!, null, null, offset, cancellationToken);

                    if (response?.Metas == null || response.Metas.Count == 0)
                        break;

                    foreach (var meta in response.Metas)
                    {
                        if (items.Count >= itemCap) break;
                        var item = MapMetaToItem(meta, catalog, logger);
                        if (item != null)
                            items.Add(item);
                    }

                    if (onProgress != null)
                        await onProgress(items.Count);

                    pagesFetched++;

                    // A page shorter than the page size means we have reached the end
                    if (response.Metas.Count < AioCatalogPageSize)
                        break;

                    offset += response.Metas.Count;
                }

                return (items, new CatalogOutcome { Succeeded = true, ItemCount = items.Count });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[InfiniteDrive] Failed to fetch AIOStreams catalog {Type}/{Id}",
                    catalog.Type, catalog.Id);
                return (items, new CatalogOutcome { Succeeded = false, Error = ex.Message });
            }
        }


        /// <summary>
        /// Builds a JSON array of provider IDs from known fields.
        /// Currently includes IMDB and TMDB; future sprints can add
        /// Kitsu, AniList, MAL after implementing Haglund API integration.
        /// </summary>
        private static string? BuildUniqueIdsJson(string aioId, string? tmdbId, JsonElement? meta)
        {
            var ids = new List<System.Text.Json.Nodes.JsonNode>();

            if (!string.IsNullOrEmpty(aioId))
                ids.Add(CreateProviderId("imdb", aioId));

            if (!string.IsNullOrEmpty(tmdbId))
                ids.Add(CreateProviderId("tmdb", tmdbId));

            // Sprint 100A-06: Add anime-specific provider IDs from metadata
            if (meta != null)
            {
                var anilistId = GetMetaString(meta, "anilist_id");
                var kitsuId = GetMetaString(meta, "kitsu_id");
                var malId = GetMetaString(meta, "mal_id");

                if (!string.IsNullOrEmpty(anilistId))
                    ids.Add(CreateProviderId("anilist", anilistId));
                if (!string.IsNullOrEmpty(kitsuId))
                    ids.Add(CreateProviderId("kitsu", kitsuId));
                if (!string.IsNullOrEmpty(malId))
                    ids.Add(CreateProviderId("mal", malId));
            }

            return ids.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(ids) : null;
        }

        private static System.Text.Json.Nodes.JsonNode CreateProviderId(string provider, string id)
        {
            var obj = new System.Text.Json.Nodes.JsonObject();
            obj["provider"] = provider;
            obj["id"] = id;
            return obj;
        }

        internal static CatalogItem? MapMetaToItem(
            AioStreamsMeta meta, AioStreamsCatalogDef catalog, ILogger logger)
        {
            // ── FIX-216-01: Anime items use kitsu:/anilist: IDs without IMDB ───
            var isAnimeCatalog = string.Equals(catalog.Type, "anime", StringComparison.OrdinalIgnoreCase);

            // Resolve IMDB ID — AIOStreams may use the IMDB ID directly as 'id'
            // or put it in a separate imdb_id field.
            var aioId = ResolveImdbId(meta.ImdbId ?? meta.Id);

            // For anime catalogs, accept non-IMDB IDs (kitsu:XXXXX, anilist:XXXXX, etc.)
            // when no IMDB cross-reference exists.
            var primaryId = aioId;
            if (string.IsNullOrEmpty(primaryId) && isAnimeCatalog && !string.IsNullOrEmpty(meta.Id))
            {
                primaryId = meta.Id; // e.g. "kitsu:46474"
            }

            if (string.IsNullOrEmpty(primaryId))
            {
                logger.LogDebug(
                    "[InfiniteDrive] Drop: no primary ID for '{Title}' (meta.id={MetaId}, catalog={Catalog})",
                    meta.Name ?? "Unknown", meta.Id ?? "null", catalog.Name ?? catalog.Id);
                return null;
            }

            // ── FIX-216-02: Force anime mediaType for anime catalogs ────────────
            // Items from anime catalogs always route to the anime directory,
            // regardless of their per-item type (series/movie).
            // Anime library is always created — route anime catalogs to anime directory

            string? mediaType;
            if (isAnimeCatalog)
            {
                mediaType = "anime";
            }
            else
            {
                // Non-anime: use the item's own type, falling back to catalog type.
                var rawType = (meta.Type?.ToLowerInvariant())
                           ?? (catalog.Type?.ToLowerInvariant());

                switch (rawType)
                {
                    case "channel":
                    case "tv":
                        logger.LogInformation(
                            "[InfiniteDrive] Skipping item '{Title}' - catalog type '{Type}' is not supported",
                            meta.Name ?? "Unknown", rawType);
                        return null;

                    case "movie":
                        mediaType = "movie";
                        break;

                    case "series":
                        mediaType = "series";
                        break;

                    default:
                        logger.LogWarning(
                            "[InfiniteDrive] Skipping item '{Title}' - unknown catalog type '{Type}'",
                            meta.Name ?? "Unknown", rawType);
                        return null;
                }
            }

            if (mediaType == null)
            {
                logger.LogDebug(
                    "[InfiniteDrive] Drop: no mediaType for '{Title}' (anime catalog, anime disabled?)",
                    meta.Name ?? "Unknown");
                return null;
            }

            // Filter out DUPE entries - these are placeholder/duplicate entries from AIOStreams
            var title = meta.Name ?? "Unknown";
            if (title.StartsWith("#DUPE#", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("[InfiniteDrive] Drop: DUPE entry '{Title}'", title);
                return null;
            }

            var tmdbId = meta.TmdbId ?? meta.TmdbIdAlt;
            int? year  = ParseYear(meta.ReleaseInfo);

            var now = DateTime.UtcNow.ToString("o");
            return new CatalogItem
            {
                Id          = GenerateDeterministicId(primaryId, "aiostreams"),
                AioId       = aioId,
                TmdbId       = tmdbId,
                UniqueIdsJson = BuildUniqueIdsJson(primaryId, tmdbId, null),
                Title        = meta.Name ?? "Unknown",
                Year         = year,
                MediaType    = mediaType,
                Source       = "aiostreams",
                SourceListId = catalog.Id,
                CatalogType  = isAnimeCatalog ? "anime" : (meta.Type?.ToLowerInvariant() ?? catalog.Type?.ToLowerInvariant()),
                RawMetaJson  = JsonSerializer.Serialize(meta),
                AddedAt      = now,
                UpdatedAt    = now,
                // Sprint 147: Set ItemState to Queued for RefreshTask to process
                ItemState    = ItemState.Queued,
            };
        }

        /// <summary>
        /// Normalises any IMDB-like ID string to the <c>tt</c>-prefixed form.
        /// Accepts: <c>tt1234567</c>, plain numbers, and discards non-IMDB IDs
        /// (Kitsu, TMDB-prefixed, etc.).
        /// </summary>
        internal static string ResolveImdbId(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            // Already a valid IMDB ID
            if (raw!.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                && raw.Length > 2)
                return raw;

            // Plain number → prepend tt
            if (long.TryParse(raw, out _))
                return $"tt{raw}";

            // Non-IMDB IDs (kitsu:, tmdb:, etc.) — not supported in v1
            return string.Empty;
        }

        /// <summary>
        /// Parses year from release info using YearParser.
        /// ── FIX-100B-07: GetYear with ReleaseInfo range ───────────────
        /// Handles: "2015", "2007-2019", "2020-", null/empty.
        /// </summary>
        internal static int? ParseYear(string? releaseInfo)
            => Services.YearParser.Parse(releaseInfo);

        /// <summary>
        /// Generates a deterministic ID based on imdb_id and source.
        /// This ensures the same item always gets the same ID, preventing
        /// UNIQUE constraint violations during upsert operations.
        /// </summary>
        internal static string GenerateDeterministicId(string aioId, string source)
        {
            // Use a hash of (aio_id + source) to create a deterministic ID
            // Format: {first 8 chars of hash}-{aio_id}
            using var hash = System.Security.Cryptography.MD5.Create();
            var input = $"{aioId}:{source}";
            var hashBytes = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            var hashString = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
            return $"{hashString}-{aioId}";
        }

        /// <summary>
        /// Returns true when a catalog can only be queried with a mandatory search
        /// term (extra[].name="search" and isRequired=true).  Such catalogs cannot
        /// be paginated and must be skipped during background sync.
        /// </summary>
        internal static bool RequiresSearchOnly(AioStreamsCatalogDef catalog) =>
            catalog.Extra?.Any(e =>
                string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)
                && e.IsRequired) ?? false;

        internal static HashSet<string> ParseCatalogIdFilter(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return new HashSet<string>(
                csv!.Split(',')
                   .Select(s => s.Trim())
                   .Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase);
        }

        internal static int GetCatalogLimit(PluginConfiguration config, string sourceKey)
        {
            if (!string.IsNullOrWhiteSpace(config.CatalogItemLimitsJson))
            {
                try
                {
                    var limits = System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<string, int>>(config.CatalogItemLimitsJson);
                    if (limits != null && limits.TryGetValue(sourceKey, out var perCatalogLimit)
                        && perCatalogLimit > 0)
                        return perCatalogLimit;
                }
                catch { /* fall through to global cap */ }
            }
            return int.MaxValue;
        }

        private static string? GetMetaString(JsonElement? meta, string propertyName)
        {
            if (meta == null) return null;
            var value = meta.Value;
            if (value.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                    return prop.GetString();
            }
            return null;
        }
    }

    // ── Stremio catalog addon provider ─────────────────────────────────────────

    // ── Cinemeta default catalog provider ──────────────────────────────────────

    /// <summary>
    /// Zero-config fallback catalog provider backed by Cinemeta v3
    /// (<c>https://v3-cinemeta.strem.io</c>).
    ///
    /// Auto-injected by <c>BuildProviders()</c> when no other catalog source is
    /// configured so users always have Top Movies and Top Series in their Emby
    /// library.  Can be disabled via <see cref="PluginConfiguration.EnableCinemetaDefault"/>.
    /// </summary>
    public class CinemetaDefaultProvider : ICatalogProvider
    {
        private const string CinemetaBaseUrl = "https://v3-cinemeta.strem.io";

        /// <inheritdoc/>
        public string ProviderName => "Cinemeta (default)";

        /// <inheritdoc/>
        public string SourceKey => "cinemeta_default";

        /// <inheritdoc/>
        public async Task<CatalogFetchResult> FetchItemsAsync(
            PluginConfiguration   config,
            ILogger               logger,
            Data.DatabaseManager? db,
            CancellationToken     cancellationToken)
        {
            var result = new CatalogFetchResult();

            logger.LogInformation(
                "[InfiniteDrive] No catalog source configured — using Cinemeta defaults " +
                "(https://v3-cinemeta.strem.io). Disable via EnableCinemetaDefault in settings.");

            using var client = AioStreamsClient.CreateForStremioBase(CinemetaBaseUrl, logger);

            AioStreamsManifest? manifest;
            try
            {
                manifest = await client.GetManifestAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.ProviderReachable = false;
                result.ErrorMessage = $"Cinemeta manifest unreachable: {ex.Message}";
                logger.LogWarning(ex, "[InfiniteDrive] {Err}", result.ErrorMessage);
                return result;
            }

            if (manifest?.Catalogs == null || manifest.Catalogs.Count == 0)
            {
                result.ProviderReachable = true;
                result.ErrorMessage = "Cinemeta returned no catalogs";
                logger.LogWarning("[InfiniteDrive] {Err}", result.ErrorMessage);
                return result;
            }

            // Sprint 100A-03: Catalog capability guard - recognized types set
            var recognizedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "movie", "series", "anime"
            };

            var catalogs = manifest.Catalogs
                .Where(c => !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Type))
                .Where(c => !AioStreamsCatalogProvider.RequiresSearchOnly(c))
                .Where(c => recognizedTypes.Contains(c.Type ?? string.Empty))
                .Where(c =>
                {
                    // Sprint 100A-03: Skip catalogs with required extras
                    if (c.Extra != null && c.Extra.Any(e => e.IsRequired == true))
                    {
                        logger.LogInformation(
                            "[InfiniteDrive] Skipping catalog '{Catalog}' — requires additional " +
                            "configuration in AIOStreams",
                            c.Name ?? c.Id);
                        return false;
                    }
                    return true;
                })
                .ToList();

            logger.LogInformation("[InfiniteDrive] Cinemeta: {Count} catalog(s) to sync", catalogs.Count);

            foreach (var catalog in catalogs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key   = $"cinemeta_default:{catalog.Type}:{catalog.Id}";
                var limit = AioStreamsCatalogProvider.GetCatalogLimit(config, key);
                if (db != null)
                    await db.RecordCatalogRunningAsync(
                        key,
                        catalog.Name ?? catalog.Id!,
                        catalog.Type!,
                        limit);

                var (items, outcome) = await AioStreamsCatalogProvider.FetchOneCatalogAsync(
                    client, catalog, logger, limit, cancellationToken,
                    onProgress: db == null ? null
                        : async count => await db.UpdateCatalogProgressAsync(key, count));

                // Stamp the source key so pruning and stats work correctly.
                foreach (var item in items)
                    item.Source = SourceKey;

                result.Items.AddRange(items);
                result.CatalogOutcomes[key] = outcome;

                if (outcome.Succeeded)
                    logger.LogDebug("[InfiniteDrive] Cinemeta {Key} → {Count} items", key, outcome.ItemCount);
                else
                    logger.LogWarning("[InfiniteDrive] Cinemeta {Key} failed: {Err}", key, outcome.Error);
            }

            result.ProviderReachable = true;

            return result;
        }
    }
}
