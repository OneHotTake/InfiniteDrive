using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using EmbyStreams.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace EmbyStreams.Tasks
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
        /// and cap output at <see cref="PluginConfiguration.CatalogItemCap"/>.
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
        /// <inheritdoc/>
        public string ProviderName => "AIOStreams";

        /// <inheritdoc/>
        public string SourceKey => "aiostreams";

        /// <inheritdoc/>
        public async Task<CatalogFetchResult> FetchItemsAsync(
            PluginConfiguration   config,
            ILogger               logger,
            Data.DatabaseManager? db,
            CancellationToken     cancellationToken)
        {
            var result = new CatalogFetchResult();

            if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                result.ProviderReachable = false;
                result.ErrorMessage = "AIOStreams URL is not configured";
                logger.LogWarning("[EmbyStreams] {Err}", result.ErrorMessage);
                return result;
            }

            using var client = new AioStreamsClient(config, logger);
            if (!client.IsConfigured)
            {
                result.ProviderReachable = false;
                result.ErrorMessage = "AIOStreams client could not be configured (check URL / UUID / token)";
                logger.LogWarning("[EmbyStreams] {Err}", result.ErrorMessage);
                return result;
            }

            // 1. Fetch manifest — failure here means the provider is unreachable.
            List<AioStreamsCatalogDef> catalogs;
            try
            {
                catalogs = await DiscoverCatalogsAsync(client, config, logger, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.ProviderReachable = false;
                result.ErrorMessage = $"Manifest unreachable: {ex.Message}";
                logger.LogWarning(ex, "[EmbyStreams] AIOStreams manifest fetch failed");
                return result;
            }

            if (catalogs.Count == 0)
            {
                // Manifest returned OK but had no usable catalogs — count as reachable
                // but warn; do not remove existing items.
                result.ProviderReachable = true;
                result.ErrorMessage = "Manifest returned no eligible catalogs (check addon configuration)";
                logger.LogWarning("[EmbyStreams] {Err}", result.ErrorMessage);
                return result;
            }

            logger.LogInformation("[EmbyStreams] Discovered {Count} AIOStreams catalog(s) to sync", catalogs.Count);

            // 2. Fetch each catalog — failures are per-catalog, not provider-level.
            foreach (var catalog in catalogs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Items.Count >= config.CatalogItemCap) break;

                var key          = $"aio:{catalog.Type}:{catalog.Id}";
                var catalogLimit = GetCatalogLimit(config, key);
                if (db != null)
                    await db.RecordCatalogRunningAsync(
                        key,
                        catalog.Name ?? catalog.Id!,
                        catalog.Type!,
                        catalogLimit);

                var (items, outcome) = await FetchOneCatalogAsync(
                    client, catalog, logger, catalogLimit, cancellationToken,
                    onProgress: db == null ? null
                        : async count => await db.UpdateCatalogProgressAsync(key, count));

                result.Items.AddRange(items);
                result.CatalogOutcomes[key] = outcome;

                if (outcome.Succeeded)
                    logger.LogDebug("[EmbyStreams] Catalog {Key} → {Count} items", key, outcome.ItemCount);
                else
                    logger.LogWarning("[EmbyStreams] Catalog {Key} failed: {Err}", key, outcome.Error);
            }

            result.ProviderReachable = true;
            if (result.Items.Count > config.CatalogItemCap)
                result.Items = result.Items.GetRange(0, config.CatalogItemCap);

            return result;
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private static async Task<List<AioStreamsCatalogDef>> DiscoverCatalogsAsync(
            AioStreamsClient client,
            PluginConfiguration config,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var manifest = await client.GetManifestAsync(cancellationToken);
            if (manifest == null)
            {
                logger.LogWarning("[EmbyStreams] AIOStreams manifest fetch returned null — check URL and connectivity");
                return new List<AioStreamsCatalogDef>();
            }

            // ── Persist discovered manifest metadata ──────────────────────────────
            var needsSave = false;
            if (!string.IsNullOrEmpty(manifest.Name)
                && manifest.Name != config.AioStreamsDiscoveredName)
            {
                config.AioStreamsDiscoveredName = manifest.Name;
                needsSave = true;
                logger.LogInformation("[EmbyStreams] Addon name: {Name}", manifest.Name);
            }
            if (!string.IsNullOrEmpty(manifest.Version)
                && manifest.Version != config.AioStreamsDiscoveredVersion)
            {
                config.AioStreamsDiscoveredVersion = manifest.Version;
                needsSave = true;
                logger.LogInformation("[EmbyStreams] Addon version: {Version}", manifest.Version);
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
                    "[EmbyStreams] '{Name}' is stream-only (no catalog entries). " +
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
                // Neither is generated by EmbyStreams .strm files — logged for future reference.
                logger.LogDebug(
                    "[EmbyStreams] Stream resource advertises additional types not handled by EmbyStreams: {Types}",
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
                    "[EmbyStreams] Stream resource does not list 'tt' or 'imdb' in idPrefixes ({Prefixes}). " +
                    "EmbyStreams generates IMDB IDs only — stream resolution may fail.",
                    prefixStr);
            }

            // ── configurationRequired warning ─────────────────────────────────────
            if (manifest.BehaviorHints?.ConfigurationRequired == true)
            {
                logger.LogWarning(
                    "[EmbyStreams] Manifest sets configurationRequired=true. " +
                    "Complete the AIOStreams web UI configuration before streams will resolve.");
            }

            if (needsSave) Plugin.Instance?.SaveConfiguration();

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
                    "[EmbyStreams] Manifest behaviorHints.requestTimeout = {T}s — stored as discovered timeout",
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
                "[EmbyStreams] Accepted catalog types: {Types}",
                string.Join(", ", acceptedTypes));

            var all = manifest.Catalogs
                .Where(c => !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Type))
                .Where(c => acceptedTypes.Contains(c.Type!))
                .Where(c => !RequiresSearchOnly(c))   // skip search-query-only catalogs
                .ToList();

            // Apply the optional catalog ID allowlist
            var allowedIds = ParseCatalogIdFilter(config.AioStreamsCatalogIds);
            if (allowedIds.Count > 0)
            {
                all = all
                    .Where(c => allowedIds.Contains(c.Id!, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                logger.LogInformation(
                    "[EmbyStreams] Catalog allowlist active — {Count}/{Total} catalogs selected",
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
                    "[EmbyStreams] Failed to fetch AIOStreams catalog {Type}/{Id}",
                    catalog.Type, catalog.Id);
                return (items, new CatalogOutcome { Succeeded = false, Error = ex.Message });
            }
        }


        /// <summary>
        /// Builds a JSON array of provider IDs from known fields.
        /// Currently includes IMDB and TMDB; future sprints can add
        /// Kitsu, AniList, MAL after implementing Haglund API integration.
        /// </summary>
        private static string? BuildUniqueIdsJson(string imdbId, string? tmdbId, JsonElement? meta)
        {
            var ids = new List<System.Text.Json.Nodes.JsonNode>();

            if (!string.IsNullOrEmpty(imdbId))
                ids.Add(CreateProviderId("imdb", imdbId));

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
            // Resolve IMDB ID — AIOStreams may use the IMDB ID directly as 'id'
            // or put it in a separate imdb_id field.
            var imdbId = ResolveImdbId(meta.ImdbId ?? meta.Id);
            if (string.IsNullOrEmpty(imdbId))
                return null;

            // Prefer the item's own type field — this is set correctly even when the
            // catalog uses a custom type (Marvel, StarWars, DC, etc.).
            // Fall back to the catalog type for standard single-type catalogs.
            // ── FIX-100B-01: Anime type in catalog type switch ────────────────────
            var rawType = (meta.Type?.ToLowerInvariant())
                       ?? (catalog.Type?.ToLowerInvariant());
            var animeEnabled = Plugin.Instance?.Configuration?.EnableAnimeLibrary ?? false;

            // FIX-100B-01: Skip channel/tv with log, skip unknown types with warning
            string? mediaType;
            switch (rawType)
            {
                case "channel":
                case "tv":
                    logger.LogInformation(
                        "[EmbyStreams] Skipping item '{Title}' - catalog type '{Type}' is not supported",
                        meta.Name ?? "Unknown", rawType);
                    return null;

                case "movie":
                    mediaType = "movie";
                    break;

                case "series":
                    mediaType = "series";
                    break;

                case "anime":
                    mediaType = animeEnabled ? "anime" : null;
                    break;

                default:
                    logger.LogWarning(
                        "[EmbyStreams] Skipping item '{Title}' - unknown catalog type '{Type}'",
                        meta.Name ?? "Unknown", rawType);
                    return null;
            }

            // Custom catalog types (Marvel, StarWars, DC) contain mixed movies and
            // series. If meta.Type is not set or not a recognised value, the item
            // cannot be placed into an Emby library — skip it.
            if (mediaType == null)
                return null;

            var tmdbId = meta.TmdbId ?? meta.TmdbIdAlt;
            int? year  = ParseYear(meta.ReleaseInfo);

            return new CatalogItem
            {
                ImdbId       = imdbId,
                TmdbId       = tmdbId,
                UniqueIdsJson = BuildUniqueIdsJson(imdbId, tmdbId, null),
                Title        = meta.Name ?? "Unknown",
                Year         = year,
                MediaType    = mediaType,
                Source       = "aiostreams",
                SourceListId = catalog.Id,
                CatalogType  = rawType,
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
            return config.CatalogItemCap;
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
                "[EmbyStreams] No catalog source configured — using Cinemeta defaults " +
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
                logger.LogWarning(ex, "[EmbyStreams] {Err}", result.ErrorMessage);
                return result;
            }

            if (manifest?.Catalogs == null || manifest.Catalogs.Count == 0)
            {
                result.ProviderReachable = true;
                result.ErrorMessage = "Cinemeta returned no catalogs";
                logger.LogWarning("[EmbyStreams] {Err}", result.ErrorMessage);
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
                            "[EmbyStreams] Skipping catalog '{Catalog}' — requires additional " +
                            "configuration in AIOStreams",
                            c.Name ?? c.Id);
                        return false;
                    }
                    return true;
                })
                .ToList();

            logger.LogInformation("[EmbyStreams] Cinemeta: {Count} catalog(s) to sync", catalogs.Count);

            foreach (var catalog in catalogs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Items.Count >= config.CatalogItemCap) break;

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
                    logger.LogDebug("[EmbyStreams] Cinemeta {Key} → {Count} items", key, outcome.ItemCount);
                else
                    logger.LogWarning("[EmbyStreams] Cinemeta {Key} failed: {Err}", key, outcome.Error);
            }

            result.ProviderReachable = true;
            if (result.Items.Count > config.CatalogItemCap)
                result.Items = result.Items.GetRange(0, config.CatalogItemCap);

            return result;
        }
    }

    // ── CatalogSyncTask ─────────────────────────────────────────────────────────

    /// <summary>
    /// Scheduled task that:
    /// <list type="number">
    ///   <item>Fetches catalog items from all enabled providers</item>
    ///   <item>Deduplicates by (imdb_id, source) and upserts into <c>catalog_items</c></item>
    ///   <item>Writes .strm files in batches of 50 (60-second pause between batches)</item>
    ///   <item>Triggers an Emby library scan for the target folders</item>
    /// </list>
    ///
    /// Default schedule: every 30 minutes.
    /// </summary>
    public class CatalogSyncTask : IScheduledTask
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const int    StrmBatchSize    = 42;          // The answer was obvious
        private const int    StrmBatchPauseMs = 60_000;
        private const string TaskName         = "EmbyStreams Catalog Sync";
        private const string TaskKey          = "EmbyStreamsCatalogSync";
        private const string TaskCategory     = "EmbyStreams";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<CatalogSyncTask> _logger;
        private readonly ILibraryManager          _libraryManager;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="libraryManager"/> and
        /// <paramref name="loggerFactory"/> automatically.
        /// </summary>
        public CatalogSyncTask(
            ILibraryManager  libraryManager,
            ILogManager      logManager)
        {
            _libraryManager = libraryManager;
            _logger         = new EmbyLoggerAdapter<CatalogSyncTask>(logManager.GetLogger("EmbyStreams"));
        }

        // ── IScheduledTask ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string Name => TaskName;

        /// <inheritdoc/>
        public string Key => TaskKey;

        /// <inheritdoc/>
        public string Description =>
            "Fetches catalog from AIOStreams (all configured addons) and Trakt, then writes .strm files and triggers an Emby library scan.";

        /// <inheritdoc/>
        public string Category => TaskCategory;

        /// <inheritdoc/>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var hour = Plugin.Instance?.Configuration?.SyncScheduleHour ?? 3;
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type             = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks   = TimeSpan.FromHours(hour < 0 ? 3 : hour).Ticks,
                },
                new TaskTriggerInfo
                {
                    Type          = TaskTriggerInfo.TriggerStartup,
                },
            };
        }

        /// <inheritdoc/>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Sprint 100A-12: Startup jitter to prevent thundering herd on Emby restart
            await Task.Delay(Random.Shared.Next(0, 120_000), cancellationToken);

            // Sprint 100A-10: Acquire global sync lock to prevent concurrent catalog operations
            await Plugin.SyncLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("[EmbyStreams] CatalogSyncTask started");
                progress.Report(0);

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    _logger.LogWarning("[EmbyStreams] Plugin configuration not available — aborting sync");
                    return;
                }

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[EmbyStreams] DatabaseManager not available — aborting sync");
                return;
            }

            // 1. Build provider list
            var providers = BuildProviders(config);
            if (providers.Count == 0)
            {
                _logger.LogInformation("[EmbyStreams] No catalog sources enabled — nothing to sync");
                progress.Report(100);
                return;
            }

            // 2. Fetch from all providers (with interval guard + health recording)
            progress.Report(5);
            var (allItems, fetchedSourceIds) = await FetchFromAllProvidersAsync(providers, config, db, cancellationToken);
            _logger.LogInformation(
                "[EmbyStreams] Fetched {Count} raw catalog items from all sources", allItems.Count);

            // 2b. If AIOStreams was just detected as stream-only this run AND Cinemeta wasn't
            //     already scheduled, run Cinemeta immediately (same sync) rather than making
            //     the user wait until the next scheduled sync.
            if (config.EnableCinemetaDefault
                && config.AioStreamsIsStreamOnly
                && !providers.Any(p => p is CinemetaDefaultProvider))
            {
                _logger.LogInformation(
                    "[EmbyStreams] AIOStreams detected as stream-only — running Cinemeta fallback in this sync");
                var (cinemetaItems, cinemetaIds) = await FetchFromAllProvidersAsync(
                    new List<ICatalogProvider> { new CinemetaDefaultProvider() },
                    config, db, cancellationToken);
                allItems.AddRange(cinemetaItems);
                foreach (var kvp in cinemetaIds)
                    fetchedSourceIds[kvp.Key] = kvp.Value;
                _logger.LogInformation(
                    "[EmbyStreams] Cinemeta fallback returned {Count} items", cinemetaItems.Count);
            }

            // 3. Deduplicate and upsert
            progress.Report(20);
            cancellationToken.ThrowIfCancellationRequested();

            var deduplicated = DeduplicateItems(allItems);
            _logger.LogInformation("[EmbyStreams] {Count} items after deduplication", deduplicated.Count);

            await UpsertItemsAsync(db, deduplicated, cancellationToken);
            progress.Report(40);

            // 3b. Prune items removed from their sources
            await PruneRemovedItemsAsync(db, fetchedSourceIds, cancellationToken);

            // 4. Check that Emby libraries cover the sync paths; warn if not
            WarnIfLibrariesMissing(config);

            // 5. Write .strm files in batches, scanning after each batch
            await WriteStrmFilesAsync(db, deduplicated, config, cancellationToken, progress, async () => await TriggerLibraryScanAsync());
            progress.Report(100);

            _logger.LogInformation("[EmbyStreams] CatalogSyncTask complete");
            }
            finally
            {
                // Sprint 102A-03: Persist last sync time
                if (Plugin.Instance?.DatabaseManager != null)
                {
                    try
                    {
                        await Plugin.Instance.DatabaseManager.PersistMetadataAsync(
                            "last_sync_time",
                            DateTimeOffset.UtcNow.ToString("o"),
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[EmbyStreams] Failed to persist last_sync_time");
                    }
                }
                // Sprint 100A-10: Release global sync lock
                Plugin.SyncLock.Release();
            }
        }

        // ── Private: provider management ───────────────────────────────────────

        private static List<ICatalogProvider> BuildProviders(PluginConfiguration config)
        {
            var list = new List<ICatalogProvider>();
            if (config.EnableAioStreamsCatalog)  list.Add(new AioStreamsCatalogProvider());

            // DEFAULT-CATALOG: auto-inject Cinemeta when no catalog source is available.
            //
            // Triggers when ALL of:
            //   (a) AIOStreams either has no URL configured, or is known to be
            //       stream-only (detected on the previous sync run, e.g. DuckKota).
            //
            // This ensures users always have Top Movies and Top Series in their
            // library even before they configure a proper catalog source — "fail
            // in setup, not on family night."
            if (config.EnableCinemetaDefault)
            {
                var aioStreamsProvidesItems = config.EnableAioStreamsCatalog
                    && !string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
                    && !config.AioStreamsIsStreamOnly;

                if (!aioStreamsProvidesItems)
                    list.Add(new CinemetaDefaultProvider());
            }

            return list;
        }

        /// <summary>
        /// Fetches catalog items from all providers.
        /// Returns the flat item list AND a per-source set of IMDB IDs for every
        /// provider that completed successfully (used by the prune step).
        /// Providers that were skipped (interval guard) are NOT included in
        /// <paramref name="fetchedSourceIds"/> so they are not pruned.
        /// </summary>
        private async Task<(List<CatalogItem> items, Dictionary<string, HashSet<string>> fetchedSourceIds)>
            FetchFromAllProvidersAsync(
            List<ICatalogProvider>  providers,
            PluginConfiguration     config,
            Data.DatabaseManager    db,
            CancellationToken       cancellationToken)
        {
            var results          = new List<CatalogItem>();
            var fetchedSourceIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ── Interval guard ────────────────────────────────────────────
                // Skip providers that synced successfully within the configured
                // interval.  Providers in error state always run.
                try
                {
                    var state = await db.GetSyncStateAsync(provider.SourceKey);
                    if (ShouldSkipProvider(state, config))
                    {
                        _logger.LogInformation(
                            "[EmbyStreams] Skipping {Provider} — within sync interval ({Hours}h)",
                            provider.ProviderName, config.CatalogSyncIntervalHours);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[EmbyStreams] Could not read sync state for {Key}", provider.SourceKey);
                }

                _logger.LogInformation("[EmbyStreams] Fetching catalog from {Provider}", provider.ProviderName);

                CatalogFetchResult fetchResult;
                try
                {
                    fetchResult = await provider.FetchItemsAsync(config, _logger, db, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EmbyStreams] Provider {Provider} threw unexpectedly", provider.ProviderName);
                    fetchResult = new CatalogFetchResult
                    {
                        ProviderReachable = false,
                        ErrorMessage      = ex.Message,
                    };
                }

                results.AddRange(fetchResult.Items);

                // Track fetched IDs per source so the prune step knows what to keep.
                // Only record when provider was reachable — avoids pruning on transient errors.
                if (fetchResult.ProviderReachable && fetchResult.Items.Count > 0)
                {
                    if (!fetchedSourceIds.TryGetValue(provider.SourceKey, out var idSet))
                    {
                        idSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        fetchedSourceIds[provider.SourceKey] = idSet;
                    }
                    foreach (var item in fetchResult.Items)
                        idSet.Add(item.ImdbId);
                }

                // ── Record provider-level health ──────────────────────────────
                try
                {
                    if (fetchResult.ProviderReachable)
                        await db.RecordSyncSuccessAsync(provider.SourceKey, fetchResult.Items.Count);
                    else
                        await db.RecordSyncFailureAsync(provider.SourceKey, fetchResult.ErrorMessage ?? "Unknown error");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[EmbyStreams] Failed to record health for {Key}", provider.SourceKey);
                }

                // ── Record per-catalog health (AIOStreams per-catalog outcomes) ─
                foreach (var kvp in fetchResult.CatalogOutcomes)
                {
                    try
                    {
                        if (kvp.Value.Succeeded)
                            await db.RecordSyncSuccessAsync(kvp.Key, kvp.Value.ItemCount);
                        else
                            await db.RecordSyncFailureAsync(kvp.Key, kvp.Value.Error ?? "Unknown error");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[EmbyStreams] Failed to record health for catalog {Key}", kvp.Key);
                    }
                }

                _logger.LogInformation(
                    "[EmbyStreams] {Provider} returned {Count} items (reachable={Reachable})",
                    provider.ProviderName, fetchResult.Items.Count, fetchResult.ProviderReachable);
            }

            return (results, fetchedSourceIds);
        }

        /// <summary>
        /// Returns true if the provider should be skipped because it synced
        /// successfully within the configured interval.
        /// Providers in error state (ConsecutiveFailures > 0) always run.
        /// </summary>
        private static bool ShouldSkipProvider(SyncState? state, PluginConfiguration config)
        {
            if (state == null) return false;
            if (state.ConsecutiveFailures > 0) return false;
            if (string.IsNullOrEmpty(state.LastSyncAt)) return false;

            if (!DateTime.TryParse(state.LastSyncAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastSync))
                return false;

            return (DateTime.UtcNow - lastSync).TotalHours < config.CatalogSyncIntervalHours;
        }

        // ── Private: deduplication ──────────────────────────────────────────────

        private static List<CatalogItem> DeduplicateItems(List<CatalogItem> items)
        {
            var seen = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.ImdbId)) continue;
                var key = $"{item.ImdbId}|{item.Source}";
                seen[key] = item;
            }
            return new List<CatalogItem>(seen.Values);
        }

        // ── Private: database upsert ────────────────────────────────────────────

        private async Task UpsertItemsAsync(
            Data.DatabaseManager db,
            List<CatalogItem>    items,
            CancellationToken    cancellationToken)
        {
            var upserted = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await db.UpsertCatalogItemAsync(item);
                    upserted++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "[EmbyStreams] Failed to upsert catalog item {ImdbId}", item.ImdbId);
                }
            }
            _logger.LogInformation(
                "[EmbyStreams] Upserted {Count}/{Total} catalog items", upserted, items.Count);
        }

        // ── Private: source diff / prune ────────────────────────────────────────

        /// <summary>
        /// For each source that completed a successful fetch this run, compares the
        /// returned IMDB IDs against the previously active catalog rows.  Any item
        /// no longer present is soft-deleted in the database and its .strm file (plus
        /// the parent Season directory and show directory if they become empty) is
        /// removed from disk.
        /// </summary>
        private async Task PruneRemovedItemsAsync(
            Data.DatabaseManager                    db,
            Dictionary<string, HashSet<string>>     fetchedSourceIds,
            CancellationToken                       cancellationToken)
        {
            foreach (var kvp in fetchedSourceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourceKey      = kvp.Key;
                var currentIds     = kvp.Value;

                List<string> removedPaths;
                try
                {
                    removedPaths = await db.PruneSourceAsync(sourceKey, currentIds);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[EmbyStreams] CatalogSyncTask: prune failed for source {Source}", sourceKey);
                    continue;
                }

                if (removedPaths.Count == 0)
                    continue;

                _logger.LogInformation(
                    "[EmbyStreams] CatalogSyncTask: pruning {Count} removed items from source {Source}",
                    removedPaths.Count, sourceKey);

                foreach (var strmPath in removedPaths)
                {
                    try
                    {
                        DeleteStrmAndCleanupDirs(strmPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[EmbyStreams] CatalogSyncTask: could not delete {Path}", strmPath);
                    }
                }
            }
        }

        /// <summary>
        /// Deletes <paramref name="strmPath"/> and removes the parent directory
        /// (and its parent) if they are left empty — prevents ghost Season/show folders.
        /// </summary>
        private static void DeleteStrmAndCleanupDirs(string strmPath)
        {
            if (!File.Exists(strmPath)) return;

            File.Delete(strmPath);

            // Remove empty Season directory
            var seasonDir = Path.GetDirectoryName(strmPath);
            if (!string.IsNullOrEmpty(seasonDir)
                && Directory.Exists(seasonDir)
                && !Directory.EnumerateFileSystemEntries(seasonDir).Any())
            {
                Directory.Delete(seasonDir);

                // Remove empty show directory
                var showDir = Path.GetDirectoryName(seasonDir);
                if (!string.IsNullOrEmpty(showDir)
                    && Directory.Exists(showDir)
                    && !Directory.EnumerateFileSystemEntries(showDir).Any())
                {
                    Directory.Delete(showDir);
                }
            }
        }

        // ── Private: .strm file writing ─────────────────────────────────────────

        private async Task WriteStrmFilesAsync(
            Data.DatabaseManager db,
            List<CatalogItem>    items,
            PluginConfiguration  config,
            CancellationToken    cancellationToken,
            IProgress<double>    progress,
            Action?              triggerScan = null)
        {
            // F1: Ensure PluginSecret is initialized before any .strm writes
            // This blocks .strm generation until the secret exists, preventing unauthenticated URLs
            var secretReady = await Plugin.Instance.EnsureInitializedAsync();
            if (!secretReady)
            {
                _logger.LogWarning("[EmbyStreams] PluginSecret not available — .strm files may use unauthenticated fallback URLs");
            }

            EnsureDirectories(config);

            // Catch-up pass: merge in any DB items that were fetched in a prior
            // run but never got a .strm (e.g. because paths weren't configured
            // at the time).  Deduplicate by (imdb_id, source) so we don't
            // double-write items already in the current batch.
            try
            {
                var missing = await db.GetItemsMissingStrmAsync();
                if (missing.Count > 0)
                {
                    var currentKeys = new HashSet<string>(
                        items.Select(i => i.ImdbId + "|" + i.Source),
                        StringComparer.OrdinalIgnoreCase);
                    var catchUp = missing.Where(m => !currentKeys.Contains(m.ImdbId + "|" + m.Source)).ToList();
                    if (catchUp.Count > 0)
                    {
                        _logger.LogInformation(
                            "[EmbyStreams] Catch-up: {Count} DB items have no .strm — will write now", catchUp.Count);
                        items = items.Concat(catchUp).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[EmbyStreams] Could not load catch-up items from DB");
            }

            // Build a map of IMDB IDs that already live in the Emby library
            // as real media files (outside our plugin sync paths).
            // These items are tracked but NOT given a .strm — they are already
            // playable.  If they disappear later, the File Resurrection task
            // (Sprint 3) will rebuild the .strm automatically.
            var libraryMap = BuildLibraryItemMap(config);

            var written    = 0;
            var skipped    = 0;
            var inLibrary  = 0;
            var nfoWritten  = 0;

            // Per-type counters
            var counters = new Dictionary<string, (int written, int skipped, int inLibrary, int nfo)>
            {
                ["movie"]  = (0, 0, 0, 0),
                ["series"] = (0, 0, 0, 0),
                ["anime"]  = (0, 0, 0, 0)
            };

            var batches    = SplitIntoBatches(items, StrmBatchSize);

            for (int i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var item in batches[i])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // ── Library-aware decision ────────────────────────────
                        if (libraryMap.TryGetValue(item.ImdbId, out var libraryPath))
                        {
                            // Item already exists in user's library — record its
                            // actual path and skip .strm creation entirely.
                            await db.UpdateLocalPathAsync(
                                item.ImdbId, item.Source, libraryPath, "library");
                            _logger.LogDebug(
                                "[EmbyStreams] {Title} ({ImdbId}) already in library at {Path} — skipping .strm",
                                item.Title, item.ImdbId, libraryPath);
                            inLibrary++;
                            var key = item.MediaType ?? "movie";
                            var c = counters[key];
                            counters[key] = (c.written, c.skipped, c.inLibrary + 1, c.nfo);
                            continue;
                        }

                        // ── Write .strm as usual ──────────────────────────────
                        var strmPath = await WriteStrmFileForItemAsync(item, config);
                        if (strmPath != null)
                        {
                            await db.UpdateStrmPathAsync(item.ImdbId, item.Source, strmPath);
                            await db.UpdateLocalPathAsync(
                                item.ImdbId, item.Source, strmPath, "strm");
                            written++;
                            var key = item.MediaType ?? "movie";
                            var c = counters[key];
                            counters[key] = (c.written + 1, c.skipped, c.inLibrary, c.nfo);
                        }
                        else
                        {
                            skipped++;
                            var key = item.MediaType ?? "movie";
                            var c = counters[key];
                            counters[key] = (c.written, c.skipped + 1, c.inLibrary, c.nfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[EmbyStreams] Failed to write .strm for {ImdbId}", item.ImdbId);
                        skipped++;
                        var key = item.MediaType ?? "movie";
                        var c = counters[key];
                        counters[key] = (c.written, c.skipped + 1, c.inLibrary, c.nfo);
                    }
                }

                progress.Report(40.0 + (50.0 * (i + 1) / batches.Count));

                // Trigger an incremental scan after every batch so Emby starts
                // ingesting items immediately rather than waiting for the full
                // bulk write to complete.
                triggerScan?.Invoke();

                if (i < batches.Count - 1)
                {
                    _logger.LogInformation(
                        "[EmbyStreams] Batch {N}/{Total} written — scan queued. Pausing {Sec}s",
                        i + 1, batches.Count, StrmBatchPauseMs / 1000);
                    await Task.Delay(StrmBatchPauseMs, cancellationToken);
                }
            }

            _logger.LogInformation(
                "[EmbyStreams] .strm write complete — {Written} written, {InLib} in library (skipped), {Skipped} other skipped, {Nfo} NFO files written",
                written, inLibrary, skipped, nfoWritten);

            // Log per-type breakdown
            foreach (var kvp in counters)
            {
                if (kvp.Value.written + kvp.Value.skipped + kvp.Value.inLibrary + kvp.Value.nfo > 0)
                {
                    _logger.LogInformation(
                        "[EmbyStreams] {Type}: {Written} written, {InLib} in library, {Skipped} skipped, {Nfo} NFOs",
                        kvp.Key, kvp.Value.written, kvp.Value.inLibrary, kvp.Value.skipped, kvp.Value.nfo);
                }
            }
        }

        /// <summary>
        /// Queries the Emby library for all Movie and Series items that live
        /// <em>outside</em> the plugin's own sync paths.  Returns a dictionary
        /// mapping IMDB ID → item path so the sync can detect items the user
        /// already owns and skip .strm creation for them.
        ///
        /// Returns an empty dictionary on any error so the sync continues normally.
        /// </summary>
        private Dictionary<string, string> BuildLibraryItemMap(PluginConfiguration config)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive        = true,
                };

                var items = _libraryManager.GetItemList(query);

                // Normalise the sync path roots for prefix matching.
                var syncMovies = (config.SyncPathMovies ?? string.Empty).TrimEnd('/', '\\');
                var syncShows  = (config.SyncPathShows  ?? string.Empty).TrimEnd('/', '\\');

                foreach (var item in items)
                {
                    var path = item.Path ?? string.Empty;

                    // Skip items that came from our own plugin sync directories —
                    // those are .strm files we already manage.
                    if (!string.IsNullOrEmpty(syncMovies)
                        && path.StartsWith(syncMovies, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(syncShows)
                        && path.StartsWith(syncShows, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? imdbId = null;
                    item.ProviderIds?.TryGetValue("Imdb", out imdbId);
                    if (!string.IsNullOrEmpty(imdbId) && !map.ContainsKey(imdbId))
                        map[imdbId] = path;
                }

                _logger.LogInformation(
                    "[EmbyStreams] Library map built: {Count} existing items found (these will not get a .strm)",
                    map.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[EmbyStreams] Could not build library item map — all catalog items will be written as .strm");
            }

            return map;
        }

        private async Task<string?> WriteStrmFileForItemAsync(
            CatalogItem item, PluginConfiguration config)
        {
            // Filter out anime if disabled
            if (item.MediaType == "anime" && !config.EnableAnimeLibrary)
            {
                _logger.LogInformation(
                    "[EmbyStreams] Skipping anime item {Title} ({ImdbId}) - anime library disabled",
                    item.Title, item.ImdbId);
                return null;
            }

            // Route all content through the standard pipeline
            if (item.MediaType == "movie")
            {
                var moviePath = await WriteMovieStrmAsync(item, config);
                if (moviePath == null)
                {
                    _logger.LogWarning(
                        "[EmbyStreams] Failed to write movie {Title} ({ImdbId}) - no valid sync path configured",
                        item.Title, item.ImdbId);
                }
                return moviePath;
            }

            var seriesPath = await WriteSeriesStrmAsync(item, config);
            if (seriesPath == null)
            {
                _logger.LogWarning(
                    "[EmbyStreams] Failed to write series {Title} ({ImdbId}) - no valid sync path configured",
                    item.Title, item.ImdbId);
            }
            return seriesPath;
        }

        /// <summary>
        /// Writes .strm files for an anime item into the anime library path.
        /// Reuses the series writing logic but targets the anime sync path.
        /// </summary>
        
        private Task<string?> WriteMovieStrmAsync(CatalogItem item, PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.SyncPathMovies)) return Task.FromResult<string?>(null);

            var folder = Path.Combine(
                config.SyncPathMovies,
                SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId)));
            Directory.CreateDirectory(folder);

            var fileName = $"{SanitisePath(item.Title)}{(item.Year.HasValue ? $" ({item.Year})" : string.Empty)}.strm";
            var path = Path.Combine(folder, fileName);
            var url = BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null);
            WriteStrmFile(path, url);
            return Task.FromResult<string?>(path);
        }

        private async Task<string?> WriteSeriesStrmAsync(
            CatalogItem item, PluginConfiguration config)
        {
            // ── FIX-100B-02: Anime path routing (two-tier) ────────────────────
            // Use AnimeDetector for two-tier anime detection
            // Tier 1: catalogType == "anime"
            // Tier 2: has AniList/Kitsu/MAL without IMDB
            var isAnime = Services.AnimeDetector.IsAnime(
                item.CatalogType,
                null, // metadata not available here
                item.ImdbId);

            // Use anime path for anime content, shows path for series
            // FIX-100B-02: SyncPathAnime defaults to SyncPathShows if not configured
            var syncPath = (item.MediaType == "anime" || isAnime)
                ? (string.IsNullOrWhiteSpace(config.SyncPathAnime) ? config.SyncPathShows : config.SyncPathAnime)
                : config.SyncPathShows;

            if (string.IsNullOrWhiteSpace(syncPath)) return null;

            var showDir = Path.Combine(
                syncPath,
                SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId)));

            // ── Pre-expansion: fetch full episode list from Stremio metadata ──
            // This writes ALL episode .strm files at once using the metadata API,
            // so Emby picks up every episode on the first library scan.
            if (!string.IsNullOrWhiteSpace(config.PrimaryManifestUrl))
            {
                try
                {
                    var (baseUrl, uuid, token) = AioStreamsClient.TryParseManifestUrl(config.PrimaryManifestUrl);
                    var stremioBase = BuildStremioBaseUrl(baseUrl, uuid, token);
                    if (!string.IsNullOrWhiteSpace(stremioBase))
                    {
                        var metaProvider = new StremioMetadataProvider(stremioBase, _logger);
                        var expansionService = new SeriesPreExpansionService(
                            _libraryManager, _logger, metaProvider);

                        var expanded = await expansionService.ExpandSeriesFromMetadataAsync(
                            item, config, CancellationToken.None);
                        if (expanded)
                        {
                            _logger.LogDebug(
                                "[EmbyStreams] Pre-expanded series {ImdbId} with full episode tree",
                                item.ImdbId);
                            return showDir;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "[EmbyStreams] Pre-expansion failed for {ImdbId}, falling back to seed",
                        item.ImdbId);
                }
            }

            // Fallback 1: SeasonsJson from catalog data
            if (!string.IsNullOrEmpty(item.SeasonsJson))
                return await WriteEpisodesFromSeasonsJsonAsync(item, showDir, config);

            // Fallback 2: Seed with S01E01 so Emby indexes the show immediately.
            var seedResult = WriteEpisodeSeedStrm(item, showDir, config, 1, 1);
            if (seedResult == null)
            {
                _logger.LogWarning(
                    "[EmbyStreams] Failed to write seed episode for {Title} ({ImdbId}) - no valid sync path configured",
                    item.Title, item.ImdbId);
            }
            return seedResult;
        }

        /// <summary>
        /// Builds the Stremio base URL from manifest components.
        /// Mirrors <see cref="AioStreamsClient"/>'s BuildStremioBase logic.
        /// </summary>
        private static string BuildStremioBaseUrl(string baseUrl, string? uuid, string? token)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return string.Empty;

            if (string.Equals(uuid, "DIRECT", StringComparison.Ordinal))
                return baseUrl.TrimEnd('/');

            var hasAuth = !string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(token);
            return hasAuth
                ? $"{baseUrl}/stremio/{uuid}/{token}"
                : $"{baseUrl}/stremio";
        }

        private async Task<string?> WriteEpisodesFromSeasonsJsonAsync(
            CatalogItem item, string showDir, PluginConfiguration config)
        {
            string? primary = null;
            try
            {
                var seasons = JsonSerializer.Deserialize<List<SeasonEpisodes>>(item.SeasonsJson!);
                if (seasons != null)
                {
                    foreach (var season in seasons)
                        foreach (var ep in season.Episodes)
                        {
                            var p = WriteEpisodeSeedStrm(item, showDir, config, season.Season, ep);
                            primary ??= p;
                        }
                }
            }
            catch
            {
                primary = WriteEpisodeSeedStrm(item, showDir, config, 1, 1);
            }

            await Task.CompletedTask;
            return primary;
        }

        private string? WriteEpisodeSeedStrm(
            CatalogItem item, string showDir, PluginConfiguration config,
            int season, int episode)
        {
            var seasonDir = Path.Combine(showDir, $"Season {season:D2}");
            Directory.CreateDirectory(seasonDir);

            var fileName = $"{SanitisePath(item.Title)} S{season:D2}E{episode:D2}.strm";
            var path     = Path.Combine(seasonDir, fileName);
            var url      = BuildSignedStrmUrl(config, item.ImdbId, "series", season, episode);
            WriteStrmFile(path, url);
            return path;
        }

        private static void WriteStrmFile(string path, string url)
            => File.WriteAllText(path, url, new System.Text.UTF8Encoding(false));

        /// <summary>
        /// Writes a minimal Kodi-format .nfo file at <paramref name="nfoPath"/>.
        /// Contains only IMDB and TMDB uniqueid tags — no plot, poster, or cast.
        /// Emby reads these IDs to match the item against its scraper without
        /// relying on filename formatting.
        ///
        /// Silently skips if the file already exists (preserves third-party nfos).
        /// </summary>
        /// <param name="nfoPath">Destination path for the .nfo file.</param>
        /// <param name="item">Catalog item providing the IMDB / TMDB IDs.</param>
        /// <param name="rootElement">XML root element: <c>movie</c> or <c>tvshow</c>.</param>
        private async Task<bool> WriteNfoFileAsync(
            string nfoPath,
            CatalogItem item,
            string rootElement,
            IManifestProvider? metaProvider = null,
            CancellationToken ct = default)
        {
            // Do not overwrite an existing .nfo written by another tool.
            if (File.Exists(nfoPath)) return false;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<{rootElement} lockdata=\"false\">");

            // Add basic info
            if (!string.IsNullOrEmpty(item.Title))
                sb.AppendLine($"  <title>{System.Security.SecurityElement.Escape(item.Title)}</title>");
            if (item.Year.HasValue)
                sb.AppendLine($"  <year>{item.Year}</year>");

            // ── FIX-100B-03: NFO library tag for anime ────────────────────────
            // Add <library>anime</library> when routed to anime library
            if (item.MediaType == "anime")
            {
                sb.AppendLine("  <library>anime</library>");
            }

            // Try to fetch enhanced metadata
            JsonElement? meta = null;
            if (metaProvider != null && !string.IsNullOrEmpty(item.ImdbId))
            {
                try
                {
                    meta = await metaProvider.GetMetaAsync(rootElement == "movie" ? "movie" : "series", item.ImdbId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[EmbyStreams] Failed to fetch metadata for {ImdbId}", item.ImdbId);
                }
            }

            // Add all unique IDs from both catalog item and metadata
            WriteUniqueIds(sb, item, meta, _logger);

            // Add plot from metadata if available
            if (meta?.TryGetProperty("description", out var descProp) == true && !string.IsNullOrEmpty(descProp.GetString()))
            {
                sb.AppendLine($"  <plot>{System.Security.SecurityElement.Escape(descProp.GetString())}</plot>");
            }

            // Add genres from metadata
            if (meta?.TryGetProperty("genres", out var genresProp) == true && genresProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var genre in genresProp.EnumerateArray())
                {
                    if (genre.ValueKind == JsonValueKind.String)
                        sb.AppendLine($"  <genre>{System.Security.SecurityElement.Escape(genre.GetString())}</genre>");
                }
            }

            // Add rating from metadata
            if (meta?.TryGetProperty("imdbRating", out var ratingProp) == true && !string.IsNullOrEmpty(ratingProp.GetString()))
            {
                if (double.TryParse(ratingProp.GetString(), out var rating))
                {
                    sb.AppendLine($"  <rating>{rating}</rating>");
                }
            }

            sb.AppendLine($"</{rootElement}>");

            bool success = false;
            try
            {
                File.WriteAllText(nfoPath, sb.ToString(), new System.Text.UTF8Encoding(false));
                success = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Failed to write NFO file {Path}", nfoPath);
            }

            return success;
        }

        private static void WriteUniqueIds(StringBuilder sb, CatalogItem item, JsonElement? meta, ILogger logger)
        {
            // Sprint 100A-06: ID prefix routing with correct NFO type attributes

            // IMDB ID (always present in catalog item)
            if (!string.IsNullOrEmpty(item.ImdbId))
            {
                logger.LogDebug("[EmbyStreams] WriteUniqueIds: IMDB ID {ImdbId}", item.ImdbId);
                sb.AppendLine($"  <uniqueid type=\"Imdb\" default=\"true\">{item.ImdbId}</uniqueid>");
            }

            // TMDB ID from catalog item
            if (!string.IsNullOrEmpty(item.TmdbId))
            {
                logger.LogDebug("[EmbyStreams] WriteUniqueIds: TMDB ID {TmdbId}", item.TmdbId);
                sb.AppendLine($"  <uniqueid type=\"Tmdb\">{item.TmdbId}</uniqueid>");
            }

            // Anime-specific provider IDs from metadata
            if (!string.IsNullOrEmpty(GetMetaString(meta, "anilist_id")))
            {
                var anilistId = GetMetaString(meta, "anilist_id");
                logger.LogDebug("[EmbyStreams] WriteUniqueIds: AniList ID {AniListId}", anilistId);
                sb.AppendLine($"  <uniqueid type=\"AniList\">{anilistId}</uniqueid>");
            }
            if (!string.IsNullOrEmpty(GetMetaString(meta, "kitsu_id")))
            {
                var kitsuId = GetMetaString(meta, "kitsu_id");
                logger.LogDebug("[EmbyStreams] WriteUniqueIds: Kitsu ID {KitsuId}", kitsuId);
                sb.AppendLine($"  <uniqueid type=\"Kitsu\">{kitsuId}</uniqueid>");
            }
            if (!string.IsNullOrEmpty(GetMetaString(meta, "mal_id")))
            {
                var malId = GetMetaString(meta, "mal_id");
                logger.LogDebug("[EmbyStreams] WriteUniqueIds: MyAnimeList ID {MalId}", malId);
                sb.AppendLine($"  <uniqueid type=\"MyAnimeList\">{malId}</uniqueid>");
            }

            // Legacy provider IDs (for backward compatibility with NFO readers)
            if (!string.IsNullOrEmpty(GetMetaString(meta, "tmdb_id")))
                sb.AppendLine($"  <tmdbid>{GetMetaString(meta, "tmdb_id")}</tmdbid>");
            if (!string.IsNullOrEmpty(GetMetaString(meta, "tmdb")))
                sb.AppendLine($"  <tmdbid>{GetMetaString(meta, "tmdb")}</tmdbid>");
            if (!string.IsNullOrEmpty(GetMetaString(meta, "anilist_id")))
                sb.AppendLine($"  <anilistid>{GetMetaString(meta, "anilist_id")}</anilistid>");
            if (!string.IsNullOrEmpty(GetMetaString(meta, "kitsu_id")))
                sb.AppendLine($"  <kitsuid>{GetMetaString(meta, "kitsu_id")}</kitsuid>");
            if (!string.IsNullOrEmpty(GetMetaString(meta, "mal_id")))
                sb.AppendLine($"  <malid>{GetMetaString(meta, "mal_id")}</malid>");

            // Additional unique IDs from metadata (e.g., from AIOMetadata)
            if (meta?.TryGetProperty("uniqueids", out var uniqueIdsProp) == true && uniqueIdsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var uniqueId in uniqueIdsProp.EnumerateArray())
                {
                    if (uniqueId.TryGetProperty("provider", out var provider) &&
                        uniqueId.TryGetProperty("id", out var id) &&
                        !string.IsNullOrEmpty(provider.GetString()) &&
                        !string.IsNullOrEmpty(id.GetString()))
                    {
                        var providerName = provider.GetString() ?? string.Empty;
                        var idValue = id.GetString() ?? string.Empty;

                        // Skip if we already wrote this ID from catalog item
                        if (string.Equals(providerName, "imdb", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(idValue, item.ImdbId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.Equals(providerName, "tmdb", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(idValue, item.TmdbId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Sprint 100A-06: Map provider names to correct NFO type attributes
                        var nfoType = providerName.ToLowerInvariant() switch
                        {
                            "imdb" => "Imdb",
                            "tmdb" => "Tmdb",
                            "anilist" => "AniList",
                            "kitsu" => "Kitsu",
                            "mal" => "MyAnimeList",
                            _ => null // Unknown prefix - handle below
                        };

                        if (nfoType != null)
                        {
                            logger.LogDebug("[EmbyStreams] WriteUniqueIds: {Provider} ID {Id}", providerName, idValue);
                            sb.AppendLine($"  <uniqueid type=\"{nfoType}\">{idValue}</uniqueid>");
                        }
                        else
                        {
                            // Sprint 100A-06: Unknown provider prefix - store as-is
                            // Store as-is in catch-all unknown field (not standard NFO, but preserves data)
                            logger.LogWarning("[EmbyStreams] WriteUniqueIds: Unknown provider prefix '{Provider}' for {ImdbId}", providerName, item.ImdbId);
                            sb.AppendLine($"  <unknown_{providerName}>{idValue}</unknown_{providerName}>");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Safely extracts a string value from metadata JSON element.
        /// Returns null if element is missing or not a string.
        /// </summary>
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

        private static void WriteNfoHintFile(string nfoPath, CatalogItem item, string rootElement)
        {
            // Legacy method - writes minimal NFO for backward compatibility
            // New code should use WriteNfoFileAsync instead
            WriteNfoFileLegacy(nfoPath, item, rootElement);
        }

        private static void WriteNfoFileLegacy(string nfoPath, CatalogItem item, string rootElement)
        {
            // Do not overwrite an existing .nfo written by another tool.
            if (File.Exists(nfoPath)) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<{rootElement}>");
            if (!string.IsNullOrEmpty(item.Title))
                sb.AppendLine($"  <title>{System.Security.SecurityElement.Escape(item.Title)}</title>");
            if (item.Year.HasValue)
                sb.AppendLine($"  <year>{item.Year}</year>");
            sb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{item.ImdbId}</uniqueid>");
            if (!string.IsNullOrEmpty(item.TmdbId))
                sb.AppendLine($"  <uniqueid type=\"tmdb\">{item.TmdbId}</uniqueid>");
            sb.AppendLine($"</{rootElement}>");

            try
            {
                File.WriteAllText(nfoPath, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // .nfo is a hint only — suppress write failures silently.
            }
        }

        // ── Public static helpers (used by WebhookService) ───────────────────────

        /// <summary>
        /// Writes a single .strm file for the given catalog item and returns
        /// the path that was written, or <c>null</c> if config paths are missing.
        /// </summary>
        public static async Task<string?> WriteStrmFileForItemPublicAsync(
            CatalogItem item, PluginConfiguration config)
        {
            if (item.MediaType == "movie")
            {
                if (string.IsNullOrWhiteSpace(config.SyncPathMovies)) return null;
                var folder = Path.Combine(
                    config.SyncPathMovies,
                    SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId)));
                Directory.CreateDirectory(folder);
                var fileName = $"{SanitisePath(item.Title)}{(item.Year.HasValue ? $" ({item.Year})" : string.Empty)}.strm";
                var path     = Path.Combine(folder, fileName);
                WriteStrmFile(path, BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null));
                return path;
            }

            // Series — seed S01E01
            if (string.IsNullOrWhiteSpace(config.SyncPathShows)) return null;
            var showDir    = Path.Combine(config.SyncPathShows,
                SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId)));
            var seasonDir  = Path.Combine(showDir, "Season 01");
            Directory.CreateDirectory(seasonDir);
            var strmPath   = Path.Combine(seasonDir, $"{SanitisePath(item.Title)} S01E01.strm");
            WriteStrmFile(strmPath, BuildSignedStrmUrl(config, item.ImdbId, "series", 1, 1));
            await Task.CompletedTask;
            return strmPath;
        }

        /// <summary>Removes filesystem-unsafe characters from a path segment.</summary>
        public static string SanitisePathPublic(string input) => SanitisePath(input);

        /// <summary>
        /// Builds a dictionary mapping IMDB ID → item path for all Movie and
        /// Series items in the Emby library that live <em>outside</em> the
        /// plugin's own sync directories.
        ///
        /// Exposed as a public static helper so <see cref="LibraryReadoptionTask"/>
        /// can reuse the same logic without duplicating it.
        ///
        /// Returns an empty dictionary on any error so callers can proceed safely.
        /// </summary>
        public static Dictionary<string, string> BuildLibraryItemMapPublic(
            PluginConfiguration config,
            ILibraryManager     libraryManager,
            ILogger             logger)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive        = true,
                };

                var items      = libraryManager.GetItemList(query);
                var syncMovies = (config.SyncPathMovies ?? string.Empty).TrimEnd('/', '\\');
                var syncShows  = (config.SyncPathShows  ?? string.Empty).TrimEnd('/', '\\');

                foreach (var item in items)
                {
                    var path = item.Path ?? string.Empty;

                    if (!string.IsNullOrEmpty(syncMovies)
                        && path.StartsWith(syncMovies, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(syncShows)
                        && path.StartsWith(syncShows, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? imdbId = null;
                    item.ProviderIds?.TryGetValue("Imdb", out imdbId);
                    if (!string.IsNullOrEmpty(imdbId) && !map.ContainsKey(imdbId))
                        map[imdbId] = path;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[EmbyStreams] BuildLibraryItemMapPublic: query failed");
            }

            return map;
        }

        // ── Private: library check / warn ───────────────────────────────────────

        /// <summary>
        /// Logs a warning if the configured sync paths are not covered by any
        /// Emby virtual folder (i.e. the library hasn't been created yet).
        /// This does NOT block the sync — it just tells the user what to do.
        /// </summary>
        private void WarnIfLibrariesMissing(PluginConfiguration config)
        {
            try
            {
                var folders = _libraryManager.GetVirtualFolders();
                var allPaths = new HashSet<string>(
                    folders.SelectMany(f => f.Locations ?? Array.Empty<string>()),
                    StringComparer.OrdinalIgnoreCase);

                void Check(string? syncPath, string collectionType, string name)
                {
                    if (string.IsNullOrWhiteSpace(syncPath)) return;
                    var norm = syncPath.TrimEnd('/', '\\');
                    if (!allPaths.Any(p => string.Equals(p.TrimEnd('/', '\\'), norm, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogWarning(
                            "[EmbyStreams] No Emby library points to '{Path}'. " +
                            "Create a {Type} library via Emby Dashboard → Libraries → Add Library → " +
                            "set type to '{CollectionType}' and add path '{Path}'. " +
                            "Without this, synced .strm files will not appear in Emby.",
                            syncPath, name, collectionType, syncPath);
                    }
                }

                Check(config.SyncPathMovies, "movies", "Movies");
                Check(config.SyncPathShows,  "tvshows", "TV Shows");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[EmbyStreams] WarnIfLibrariesMissing: could not read virtual folders");
            }
        }

        // ── Private: library scan ────────────────────────────────────────────────

        private async Task TriggerLibraryScanAsync()
        {
            try
            {
                _logger.LogInformation("[EmbyStreams] Triggering targeted Emby library scan");

                // Use ValidateMediaLibrary for more efficient targeted scanning
                // This validates specific paths rather than scanning all libraries
                var progress = new Progress<double>();
                await _libraryManager.ValidateMediaLibrary(progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Failed to trigger library scan");
            }
        }

        // ── Private: helpers ────────────────────────────────────────────────────

        private static void EnsureDirectories(PluginConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(config.SyncPathMovies))
                Directory.CreateDirectory(config.SyncPathMovies);
            if (!string.IsNullOrWhiteSpace(config.SyncPathShows))
                Directory.CreateDirectory(config.SyncPathShows);
            if (config.EnableAnimeLibrary && !string.IsNullOrWhiteSpace(config.SyncPathAnime))
                Directory.CreateDirectory(config.SyncPathAnime);
        }

        /// <summary>
        /// Builds a folder name using the Emby IMDB auto-match convention:
        /// <c>{Title} ({Year}) [imdbid-{imdbId}]</c>
        ///
        /// The <c>[imdbid-ttXXXXXXX]</c> suffix causes Emby's built-in scrapers
        /// (TMDb, OMDb) to automatically fetch poster, backdrop, cast, and ratings
        /// without requiring a separate .nfo file for ID hinting.
        /// </summary>
        private static string BuildFolderName(string title, int? year, string? imdbId)
        {
            var sb = new StringBuilder(title);
            if (year.HasValue) sb.Append($" ({year})");
            // Only add [imdbid-X] for tt-prefixed IDs that Emby's scanner recognizes
            if (!string.IsNullOrEmpty(imdbId) &&
                imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                sb.Append($" [imdbid-{imdbId}]");
            return sb.ToString();
        }

        /// <summary>
        /// Generates a signed URL for the /EmbyStreams/Stream endpoint.
        /// Falls back to the legacy /EmbyStreams/Play URL if PluginSecret is not configured,
        /// so the plugin continues to function during upgrades.
        /// </summary>
        internal static string BuildSignedStrmUrl(
            PluginConfiguration config,
            string imdbId,
            string mediaType,
            int? season,
            int? episode)
        {
            // Ensure PluginSecret is initialized before accessing Configuration
            Plugin.Instance?.EnsureInitialization();

            var secret = Plugin.Instance?.Configuration?.PluginSecret;
            if (!string.IsNullOrEmpty(secret))
            {
                return Services.StreamUrlSigner.GenerateSignedUrl(
                    config.EmbyBaseUrl,
                    imdbId,
                    mediaType,
                    season,
                    episode,
                    secret,
                    TimeSpan.FromDays(config.SignatureValidityDays > 0 ? config.SignatureValidityDays : 365));
            }

            // Fallback: legacy authenticated endpoint (requires X-Emby-Token)
            var baseUrl = config.EmbyBaseUrl.TrimEnd('/');
            if (season.HasValue && episode.HasValue)
                return $"{baseUrl}/EmbyStreams/Play?imdb={imdbId}&season={season}&episode={episode}";
            return $"{baseUrl}/EmbyStreams/Play?imdb={imdbId}";
        }

        private static string SanitisePath(string input)
        {
            var invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var sb      = new StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            return sb.ToString().Trim();
        }

        private static List<List<T>> SplitIntoBatches<T>(List<T> source, int size)
        {
            var result = new List<List<T>>();
            for (int i = 0; i < source.Count; i += size)
                result.Add(source.GetRange(i, Math.Min(size, source.Count - i)));
            return result;
        }

        // ── Private helper DTOs ──────────────────────────────────────────────────

        [DataContract]
        private class SeasonEpisodes
        {
            [JsonPropertyName("season")]   public int         Season   { get; set; }
            [JsonPropertyName("episodes")] public List<int>   Episodes { get; set; } = new List<int>();
        }
    }
}
