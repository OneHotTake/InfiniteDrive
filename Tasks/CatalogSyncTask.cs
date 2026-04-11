using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
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
                logger.LogWarning("[InfiniteDrive] {Err}", result.ErrorMessage);
                return result;
            }

            using var client = new AioStreamsClient(config, logger);
            client.Cooldown = Plugin.Instance?.CooldownGate;
            client.ActiveCooldownKind = CooldownKind.CatalogFetch;
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

            logger.LogInformation("[InfiniteDrive] Discovered {Count} AIOStreams catalog(s) to sync", catalogs.Count);

            // Cap sources per run from CooldownProfile (Sprint 155)
            var sourcesCap = Plugin.Instance?.CooldownGate?.Profile.CatalogSourcesPerRun ?? catalogs.Count;
            if (catalogs.Count > sourcesCap)
            {
                logger.LogInformation("[InfiniteDrive] Capping catalog sync to {Cap} of {Total} sources (profile limit)",
                    sourcesCap, catalogs.Count);
                catalogs = catalogs.Take(sourcesCap).ToList();
            }

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
                    logger.LogDebug("[InfiniteDrive] Catalog {Key} → {Count} items", key, outcome.ItemCount);
                else
                    logger.LogWarning("[InfiniteDrive] Catalog {Key} failed: {Err}", key, outcome.Error);
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
                logger.LogWarning("[InfiniteDrive] AIOStreams manifest fetch returned null — check URL and connectivity");
                return new List<AioStreamsCatalogDef>();
            }

            // ── Persist discovered manifest metadata ──────────────────────────────
            var needsSave = false;
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
                        "[InfiniteDrive] Skipping item '{Title}' - catalog type '{Type}' is not supported",
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
                        "[InfiniteDrive] Skipping item '{Title}' - unknown catalog type '{Type}'",
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
                    logger.LogDebug("[InfiniteDrive] Cinemeta {Key} → {Count} items", key, outcome.ItemCount);
                else
                    logger.LogWarning("[InfiniteDrive] Cinemeta {Key} failed: {Err}", key, outcome.Error);
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
        private const string TaskName         = "InfiniteDrive Catalog Sync";
        private const string TaskKey          = "InfiniteDriveCatalogSync";
        private const string TaskCategory     = "InfiniteDrive";

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<CatalogSyncTask> _logger;
        private readonly ILibraryManager          _libraryManager;
        private readonly ILogManager              _logManager;

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
            _logManager     = logManager;
            _logger         = new EmbyLoggerAdapter<CatalogSyncTask>(logManager.GetLogger("InfiniteDrive"));
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
                _logger.LogInformation("[InfiniteDrive] CatalogSyncTask started");
                progress.Report(0);

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    _logger.LogWarning("[InfiniteDrive] Plugin configuration not available — aborting sync");
                    return;
                }

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null)
            {
                _logger.LogWarning("[InfiniteDrive] DatabaseManager not available — aborting sync");
                return;
            }

            // 1. Build provider list
            var providers = BuildProviders(config);
            if (providers.Count == 0)
            {
                _logger.LogInformation("[InfiniteDrive] No catalog sources enabled — nothing to sync");
                progress.Report(100);
                return;
            }

            // 2. Fetch from all providers (with interval guard + health recording)
            progress.Report(5);
            var (allItems, fetchedSourceIds) = await FetchFromAllProvidersAsync(providers, config, db, cancellationToken);
            _logger.LogInformation(
                "[InfiniteDrive] Fetched {Count} raw catalog items from all sources", allItems.Count);

            // 2b. If AIOStreams was just detected as stream-only this run AND Cinemeta wasn't
            //     already scheduled, run Cinemeta immediately (same sync) rather than making
            //     the user wait until the next scheduled sync.
            if (config.EnableCinemetaDefault
                && config.AioStreamsIsStreamOnly
                && !providers.Any(p => p is CinemetaDefaultProvider))
            {
                _logger.LogInformation(
                    "[InfiniteDrive] AIOStreams detected as stream-only — running Cinemeta fallback in this sync");
                var (cinemetaItems, cinemetaIds) = await FetchFromAllProvidersAsync(
                    new List<ICatalogProvider> { new CinemetaDefaultProvider() },
                    config, db, cancellationToken);
                allItems.AddRange(cinemetaItems);
                foreach (var kvp in cinemetaIds)
                    fetchedSourceIds[kvp.Key] = kvp.Value;
                _logger.LogInformation(
                    "[InfiniteDrive] Cinemeta fallback returned {Count} items", cinemetaItems.Count);
            }

            // 3. Deduplicate and upsert
            progress.Report(20);
            cancellationToken.ThrowIfCancellationRequested();

            var deduplicated = DeduplicateItems(allItems);
            _logger.LogInformation("[InfiniteDrive] {Count} items after deduplication", deduplicated.Count);

            await UpsertItemsAsync(db, deduplicated, cancellationToken);
            progress.Report(40);

            // 3b. Prune items removed from their sources
            await PruneRemovedItemsAsync(db, fetchedSourceIds, cancellationToken);

            // 4. Check that Emby libraries cover the sync paths; warn if not
            WarnIfLibrariesMissing(config);

            // Sprint 147: CatalogSyncTask no longer writes .strm files
            // Items are persisted with ItemState = Queued for RefreshTask to process
            // RefreshTask handles: Write -> Hint -> Notify -> Verify -> Promote
            // DeepCleanTask handles: Validation, Enrichment, Token Renewal
            progress.Report(90);

            // Sprint 158: Backstop sync for all active user RSS catalogs (Trakt / MDBList).
            // Runs after the system-catalog pass. Sequentially — cooldown gate handles politeness.
            try
            {
                var userCatalogs = await db.GetAllActiveUserCatalogsAsync(cancellationToken);
                if (userCatalogs.Count > 0)
                {
                    _logger.LogInformation(
                        "[InfiniteDrive] CatalogSyncTask: syncing {Count} active user catalogs", userCatalogs.Count);
                    var userCatalogSync = new Services.UserCatalogSyncService(
                        _logManager, db, Plugin.Instance!.StrmWriterService, Plugin.Instance.CooldownGate);
                    foreach (var uc in userCatalogs)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        var result = await userCatalogSync.SyncOneAsync(uc.Id, cancellationToken);
                        _logger.LogInformation(
                            "[InfiniteDrive] UserCatalog {Id} ({Name}): ok={Ok} fetched={F} added={A} elapsed={Ms}ms",
                            uc.Id, uc.DisplayName, result.Ok, result.Fetched, result.Added, result.ElapsedMs);
                    }
                }
            }
            catch (OperationCanceledException) { /* swallow — task was cancelled */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] CatalogSyncTask: error syncing user catalogs (non-fatal)");
            }

            progress.Report(100);

            _logger.LogInformation("[InfiniteDrive] CatalogSyncTask complete");
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
                        _logger.LogWarning(ex, "[InfiniteDrive] Failed to persist last_sync_time");
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
                            "[InfiniteDrive] Skipping {Provider} — within sync interval ({Hours}h)",
                            provider.ProviderName, config.CatalogSyncIntervalHours);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[InfiniteDrive] Could not read sync state for {Key}", provider.SourceKey);
                }

                _logger.LogInformation("[InfiniteDrive] Fetching catalog from {Provider}", provider.ProviderName);

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
                    _logger.LogError(ex, "[InfiniteDrive] Provider {Provider} threw unexpectedly", provider.ProviderName);
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
                    _logger.LogDebug(ex, "[InfiniteDrive] Failed to record health for {Key}", provider.SourceKey);
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
                        _logger.LogDebug(ex, "[InfiniteDrive] Failed to record health for catalog {Key}", kvp.Key);
                    }
                }

                _logger.LogInformation(
                    "[InfiniteDrive] {Provider} returned {Count} items (reachable={Reachable})",
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
                        "[InfiniteDrive] Failed to upsert catalog item {ImdbId}", item.ImdbId);
                }
            }
            _logger.LogInformation(
                "[InfiniteDrive] Upserted {Count}/{Total} catalog items", upserted, items.Count);
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
                        "[InfiniteDrive] CatalogSyncTask: prune failed for source {Source}", sourceKey);
                    continue;
                }

                if (removedPaths.Count == 0)
                    continue;

                _logger.LogInformation(
                    "[InfiniteDrive] CatalogSyncTask: pruning {Count} removed items from source {Source}",
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
                            "[InfiniteDrive] CatalogSyncTask: could not delete {Path}", strmPath);
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

        // ── Private: .strm file I/O ──────────────────────────────────────────────

        private static void WriteStrmFile(string path, string url)
            => File.WriteAllText(path, url, new System.Text.UTF8Encoding(false));

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
                logger.LogWarning(ex, "[InfiniteDrive] BuildLibraryItemMapPublic: query failed");
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
                            "[InfiniteDrive] No Emby library points to '{Path}'. " +
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
                _logger.LogDebug(ex, "[InfiniteDrive] WarnIfLibrariesMissing: could not read virtual folders");
            }
        }

        // ── Private: library scan ────────────────────────────────────────────────

        private async Task TriggerLibraryScanAsync()
        {
            try
            {
                _logger.LogInformation("[InfiniteDrive] Triggering targeted Emby library scan");

                // Use ValidateMediaLibrary for more efficient targeted scanning
                // This validates specific paths rather than scanning all libraries
                var progress = new Progress<double>();
                await _libraryManager.ValidateMediaLibrary(progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Failed to trigger library scan");
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
