# EmbyStreams Repository Map

_Generated 2026-04-01. Covers all `.cs` files in root, Services/, Data/, Models/, Tasks/, Logging/._

---

## Plugin.cs
> Plugin entry point; registers singleton, initialises DB and PluginSecret.

### Public Methods
- `GetPages()` — returns embedded HTML/JS config page registrations
- `GetThumbImage()` — returns embedded PNG thumbnail stream

### Key Types / Constants
- `Plugin.Instance` — singleton accessor used by all services and tasks
- `Plugin.DatabaseManager` — shared SQLite manager, ready before any task runs

---

## PluginConfiguration.cs
> All persisted plugin settings; serialised to EmbyStreams.xml by Emby.

### Public Methods
- `Validate()` — clamps all numeric fields to safe ranges after deserialisation

### Key Types / Constants
- `PrimaryManifestUrl` — full AIOStreams manifest URL (includes auth path)
- `PluginSecret` — HMAC-SHA256 key for signing .strm URLs; auto-generated
- `ProxyMode` — `auto` / `redirect` / `proxy` stream serving mode
- `EnableAnimeLibrary` — enables syncing anime content to anime path (soft requirement)
- `SyncPathAnime` — filesystem path for anime content (default: /media/embystreams/anime)
- `IsFirstRunComplete` — flag indicating completion of setup wizard
- `EmbyBaseUrl` — auto-detected from window.location.origin; defaults to localhost but warns on remote clients

---

## Configuration/configurationpage.html
> Plugin configuration UI with 5-step wizard, Sources tab, and progressive disclosure.

### Structure
- **Tab bar**: Setup, Sources, Discover, Advanced
- **Setup tab**: 5-step wizard with progress bar (Provider → Libraries → Metadata → Catalogs → Sync)
- **Sources tab**: Catalog sources table with sync status
- **Discover tab**: Search/browse interface for adding content
- **Advanced tab**: Collapsible accordions for System Status, Catalog Sync, Playback & Cache, etc.

### Wizard Steps
1. **Provider**: Manifest URL + Test Connection, Base URL (auto-detected from origin)
2. **Content Libraries**: Movies ✅, Series ✅, Anime ☐ + library names
3. **Metadata**: Language, Country, Image Language, Cinemeta toggle
4. **Catalogs**: AIOStreams catalog picker (moved from Setup page)
5. **Sync**: Summary card + "Save & Start First Sync" CTA

### CSS Classes
- `.es-wizard-progress` — 5-step progress indicator with ●━━━●━━━○ pattern
- `.es-wizard-step-content` — individual step containers with fade animation
- `.es-accordion` — collapsible sections in Advanced tab
- `.es-sources-table` — catalog sources table in Sources tab

---

## Configuration/configurationpage.js
> Client-side logic for wizard navigation, tab switching, and dashboard updates.

### Key Functions
- `showTab(view, name)` — switches between Setup/Sources/Discover/Advanced tabs
- `updateWizardProgress(view, step)` — updates progress indicator state
- `showWizardStep(view, step)` — shows/hides wizard step content
- `initWizardTab(view, cfg)` — populates wizard fields from config
- `testWizardConnection(view, type)` — tests AIOStreams connection from wizard
- `finishWizard(view)` — saves wizard config and triggers first sync
- `refreshSourcesTab(view)` — fetches and displays catalog sources from /EmbyStreams/Status
- `refreshDashboard(view)` — polls /EmbyStreams/Status for system health

### State Variables
- `_wizardStep` — current wizard step (1-5)
- `_wizardCatalogs` — loaded catalog sources for picker
- `_unsavedChanges` — tracks pending config changes

---

## Data/DatabaseManager.cs
> SQLite repository for all five tables; handles schema creation and migration.

### Public Methods
- `Initialise()` — opens DB, integrity-checks, creates/migrates schema to V16
- `UpsertCatalogItemAsync(item)` — insert-or-update catalog_items row
- `GetActiveCatalogItemsAsync()` — returns all non-removed catalog items
- `GetCatalogItemByImdbIdAsync(imdbId)` — lookup single item by IMDB ID
- `GetCatalogItemsBySourceAsync(source)` — all active items for a given source
- `PruneSourceAsync(source, currentImdbIds)` — soft-delete items no longer in feed
- `MarkCatalogItemRemovedAsync(imdbId, source)` — soft-delete single item
- `UpdateStrmPathAsync(imdbId, source, strmPath)` — record .strm file path
- `UpdateLocalPathAsync(imdbId, source, path, localSource)` — record real file location
- `GetCatalogItemCountByLocalSourceAsync(localSource)` — count by source type
- `GetCatalogItemCountByItemStateAsync(state)` — count by ItemState (Sprint 66)
- `GetReadoptedCountAsync()` — count items that gained a real file after .strm creation
- `GetItemsByLocalSourceAsync(localSource)` — items filtered by local_source value
- `GetItemsMissingStrmAsync()` — items with no strm_path and not library-owned
- `IncrementResurrectionCountAsync(imdbId, source)` — bump resurrection counter
- `GetTotalResurrectionCountAsync()` — sum of all resurrection_count values
- `GetSeriesWithoutSeasonsJsonAsync()` — series items missing episode data
- `UpdateSeasonsJsonAsync(imdbId, source, json)` — store serialised season/episode map
- `GetAllClientCompatsAsync()` — all learned per-client streaming profiles
- `ClearResolutionCacheAsync()` — wipe all resolution_cache rows
- `ResetSyncIntervalsAsync()` — clear last_sync_at so next run bypasses interval guard
- `VacuumAsync()` — run SQLite VACUUM to reclaim space
- `PurgeCatalogAsync()` — hard-delete catalog + sync_state + candidates; keep cache
- `ResetAllAsync()` — hard-delete every table; returns strm paths for disk cleanup
- `GetDatabasePath()` — returns absolute path to embystreams.db

---

## Services/AioStreamsClient.cs
> HTTP client for all AIOStreams / Stremio addon communication; implements IManifestProvider.

### Public Methods
- `AioStreamsClient(config, logger)` — builds client from PluginConfiguration manifest URL
- `AioStreamsClient(baseUrl, uuid, token, logger)` — direct constructor for tests/health-check
- `CreateForStremioBase(stremioBase, logger)` — factory for addons with no `/stremio/` prefix
- `GetManifestAsync(ct)` — fetch and parse the addon manifest.json
- `GetCatalogAsync(type, catalogId, ct)` — fetch a catalog page
- `GetCatalogAsync(type, id, search, genre, skip, ct)` — catalog with extra params
- `GetMovieStreamsAsync(imdbId, ct)` — fetch streams for a movie
- `GetSeriesStreamsAsync(imdbId, season, episode, ct)` — fetch streams for an episode
- `GetMetaAsync(type, id, ct)` — fetch detailed item metadata

### Key Types / Constants
- `AioStreamsManifest` — top-level manifest response; exposes `IsStreamOnly`, `SupportsImdbIds`
- `AioStreamsStream` — single resolved stream with URL, quality, service, parsedFile
- `AioStreamsParsedFile` — resolution, quality, codec, audio/visual tags from AIOStreams
- `AioStreamsServiceInfo` — debrid provider ID and cache status
- `ResourceListConverter` — custom JSON converter handles mixed string/object resources array

---

## Services/PlaybackService.cs
> Handles `GET /EmbyStreams/Play`; cache-first stream resolution with rate limiting.

### Public Methods
- `Get(PlayRequest req)` — resolves and serves stream via redirect or proxy; checks cache, validates URLs, rate-limits by IP and user

### Key Types / Constants
- `PlayRequest` — DTO: imdb, season, episode, episode_id params
- `PlayErrorResponse` — JSON error body with machine code and retry hint

---

## Services/SignedStreamService.cs
> Public `GET /EmbyStreams/Stream` endpoint secured by HMAC-SHA256 signature.

### Public Methods
- `Get(SignedStreamRequest req)` — validates HMAC sig, resolves stream, returns HTTP 302

### Key Types / Constants
- `SignedStreamRequest` — DTO: id, type, exp, sig, season, episode

---

## Services/StreamUrlSigner.cs
> Generates and validates HMAC-SHA256 signed URLs for .strm files.

### Public Methods
- `GenerateSignedUrl(embyBase, imdbId, type, season, episode, secret, validity)` — builds signed URL
- `ValidateSignature(id, type, season, episode, exp, sig, secret)` — constant-time HMAC check
- `GenerateSecret()` — generates 32 random bytes as base64 for PluginSecret

---

## Services/StreamProxyService.cs
> Passthrough proxy for `GET/HEAD /EmbyStreams/Stream/{ProxyId}`; Range-request aware.

### Public Methods
- `Get(StreamProxyRequest req)` — proxies or redirects stream bytes to Emby client
- `Head(StreamProxyRequest req)` — returns headers without body for content-length probing

---

## Services/ManifestUrlParser.cs
> Parses AIOStreams manifest URLs to extract configuration components.

### Public Methods
- `Parse(manifestUrl)` — extracts host, userId, configToken; generates configureUrl

### Key Types
- `ManifestUrlComponents` — parsed URL components (Host, UserId, ConfigToken, ConfigureUrl)

---

## Services/StreamResolver.cs
> Shared cache-first resolution helper for Discover channel and other non-playback callers.

### Public Methods
- `ResolveToProxyTokenAsync(imdbId, season, episode, config, db, logger, ct)` — cache-first; returns ProxySession token or null
- `GetDirectStreamUrlAsync(imdbId, season, episode, config, db, logger, ct)` — returns raw CDN URL without proxy token

---

## Services/ProxySessionStore.cs
> In-memory store for short-lived (4h TTL) proxy session tokens.

### Public Methods
- `Create(session)` — stores session, returns 32-char GUID token
- `TryGet(token)` — returns ProxySession or null if expired/unknown

### Key Types / Constants
- `ProxySession` — upstream URL, fallbacks, IMDB ID, expiry

---

## Services/CatalogDiscoverService.cs
> Syncs the discover_catalog table from AIOStreams catalog endpoints.

### Public Methods
- `SyncDiscoverCatalogAsync(ct)` — fetches all manifest catalogs, paginates, upserts entries
- `ClearSourceAsync(catalogSource)` — removes all rows for a given catalog source

---

## Services/DiscoverService.cs
> REST API handlers for the Discover browsing/search/add-to-library feature.

### Public Methods
- `Get(DiscoverBrowseRequest req)` — paginated catalog listing from local DB
- `Get(DiscoverSearchRequest req)` — searches local DB; optionally queries AIOStreams live
- `Get(DiscoverDetailRequest req)` — detailed metadata for a single item by IMDB ID
- `Post(DiscoverAddToLibraryRequest req)` — creates .strm file, triggers Emby library scan

### Key Types / Constants
- `DiscoverItem` — unified response DTO combining metadata and library status

---

## Services/DiscoverInitializationService.cs
> IServerEntryPoint; auto-triggers initial Discover sync on startup if catalog is empty.

### Public Methods
- `Run()` — hooks ItemAdded event, fires auto-sync check in background
- `Dispose()` — unsubscribes library event handlers

---

## Services/EmbyEventHandler.cs
> IServerEntryPoint; handles playback and library events for binge pre-warm and episode expansion.

### Public Methods
- `Run()` — subscribes to PlaybackStart, PlaybackStopped, ItemAdded events
- `Dispose()` — unsubscribes all event handlers

---

## Services/SingleFlight.cs
> Generic keyed deduplication guard; collapses concurrent requests for the same key.

### Public Methods
- `RunAsync(key, factory)` — first caller runs factory; subsequent callers await same result

---

## Services/HousekeepingService.cs
> Orphaned folder cleanup, .strm validity checking, bulk .strm regeneration.

### Public Methods
- `CleanupOrphanedFolders()` — removes empty `[tmdbid=...]` folders from sync paths

---

## Services/TriggerService.cs
> `POST /EmbyStreams/Trigger` — manually fires any named EmbyStreams scheduled task.

### Public Methods
- `Post(TriggerRequest req)` — validates task key, launches background task, returns immediately

### Key Types / Constants
- Accepted task keys: `catalog_sync`, `link_resolver`, `file_resurrection`, `library_readoption`

---

## Services/StatusService.cs
> `GET /EmbyStreams/Status` — returns full health/stats JSON snapshot for the dashboard.

### Key Types / Constants
- `StatusResponse` — version, AIOStreams connection, catalog counts, cache coverage, recent playback
- `StatusResponse` (Sprint 66) — includes item state counts: CataloguedCount, PresentCount, ResolvedCount, RetiredCount, PinnedCount, OrphanedCount
- `ProviderHealthEntry` — URL, reachability, latency for a single provider

---

## Services/SetupService.cs
> Setup wizard endpoints: directory creation, API key rotation, .strm file rewrite.

### Public Methods
- `Post(CreateDirectoriesRequest req)` — creates movies/shows/anime library directories
- `Post(RotateApiKeyRequest req)` — rotates PluginSecret, rewrites all .strm files

---

## Services/TestFailoverService.cs
> `GET /EmbyStreams/TestFailover` — dry-runs the full 3-layer resilience chain.

### Public Methods
- `Get(TestFailoverRequest req)` — probes primary AIOStreams, fallbacks, and direct debrid APIs

### Key Types / Constants
- `TestFailoverResponse` — Layer1/Layer2/Layer3 results with latency and status
- `FailoverLayerResult` — outcome enum, message, round-trip latency

---

## Services/WebhookService.cs
> `POST /EmbyStreams/Webhook/Sync` — accepts item-addition or re-sync triggers from external tools.

### Public Methods
- `Post(WebhookSyncRequest req)` — auto-detects payload format (direct/Jellyseerr/Radarr/Sonarr), writes .strm, queues Tier 0 resolution

### Key Types / Constants
- Accepted formats: `{"imdb":"tt..."}`, `{"source":"trakt"}`, Jellyseerr, Radarr, Sonarr payloads

---

## Services/StreamHelpers.cs
> Stream type policy table; drives cache TTL, HEAD-check, and header-forwarding behaviour.

### Key Types / Constants
- `StreamTypePolicy` — per-type policy: cache lifetime, HEAD check, header forwarding, isLive
- `StreamTypePolicy.Get(streamType)` — looks up policy; falls back to `http` for unknown types

---

## Services/ThroughputTrackingStream.cs
> Read-through stream wrapper that measures client download speed and updates client_compat.

### Key Types / Constants
- `ThroughputTrackingStream(inner, clientType, expectedKbps, logger)` — wraps upstream HTTP stream

---

## Services/IManifestProvider.cs
> Interface abstracting any Stremio-compatible addon (AIOStreams, Cinemeta, etc.).

### Public Methods
- `GetManifestAsync(ct)` — fetch manifest; null on error
- `GetCatalogAsync(type, id, ct)` — fetch catalog page
- `GetCatalogAsync(type, id, search, genre, skip, ct)` — catalog with extras
- `GetMovieStreamsAsync(imdbId, ct)` — movie stream resolution
- `GetSeriesStreamsAsync(imdbId, season, episode, ct)` — episode stream resolution
- `GetMetaAsync(type, id, ct)` — detailed item metadata

---

## Services/StremioMetadataProvider.cs
> Fetches full series metadata (all episodes) from a Stremio `meta` endpoint.

### Public Methods
- `GetFullSeriesMetaAsync(id, ct)` — returns `StremioMeta` with complete Videos array

### Key Types / Constants
- `StremioMeta` — series name, year, and Videos[] (season/episode entries)

---

## Services/SeriesPreExpansionService.cs
> Writes all episode .strm files for a series in one pass using Stremio metadata.

### Public Methods
- `ExpandSeriesFromMetadataAsync(item, config, ct)` — fetches meta, creates season folders, writes per-episode .strm files
- `WriteEpisodeNfoFileAsync()` — writes detailed episode NFO files with plot, aired date, and series context

---

## Tasks/CatalogSyncTask.cs
> Scheduled task (daily at 3 AM) that fetches catalogs and writes .strm files.

### Public Methods
- `Execute(ct, progress)` — runs all ICatalogProvider instances, upserts items, prunes removals

### Key Types / Constants
- `ICatalogProvider` — interface for catalog data sources (AIOStreams, Trakt, etc.)
- `CatalogFetchResult` — items list plus reachability and per-catalog outcome data
- `SanitisePathPublic(name)` — normalises folder/file names for filesystem safety
- `WriteNfoFileAsync()` — writes full NFO files with metadata and all unique IDs
- `WriteUniqueIds()` — writes all upstream unique IDs (imdb, tmdb, anilist, kitsu, etc.)
- Enhanced sync logging with per-type counters (movie, series, anime) and NFO write counts

---

## Tasks/EpisodeExpandTask.cs
> Scheduled task (every 4h) that writes per-episode .strm files using Emby's indexed metadata.

### Public Methods
- `Execute(ct, progress)` — finds series with no seasons_json, reads Emby episode items, writes .strm files
- `WriteEpisodeNfoFileAsync()` — writes basic episode NFO files with SxxExx naming

---

## Tasks/LinkResolverTask.cs
> Scheduled task (every 15 min) that pre-resolves stream URLs into resolution_cache.

### Public Methods
- `Execute(ct, progress)` — processes Tier 0→3 resolution queue respecting API budget limits

---

## Tasks/FileResurrectionTask.cs
> Scheduled task (every 2h) that rebuilds .strm files when a user's real media file disappears.

### Public Methods
- `Execute(ct, progress)` — checks `local_source='library'` items for missing files, rebuilds .strm

---

## Tasks/LibraryReadoptionTask.cs
> Scheduled task (every 6h) that retires .strm files when the user acquires a real media file.

### Public Methods
- `Execute(ct, progress)` — checks `local_source='strm'` items against Emby library, updates DB, optionally deletes .strm

---

## Tasks/DoctorTask.cs
> Unified catalog reconciliation engine (Sprint 66). Replaces FileResurrectionTask, LibraryReadoptionTask, and EpisodeExpandTask.

### Public Methods
- `Execute(ct, progress)` — 5-phase operation: Fetch & Diff, Write, Adopt, Health Check, Report & Signal

### Key Types / Constants
- `TaskKey` — `doctor` for triggering via `/EmbyStreams/Trigger?task=doctor`

### Phases
1. **Fetch & Diff** — Load catalog items, detect PINNED items, build change lists (toWrite, toRetire, toResolve, orphans)
2. **Write** — Create .strm files for CATALOGUED items, transition to PRESENT
3. **Adopt** — Delete .strm when real file detected in library, transition to RETIRED
4. **Health Check** — URL validation deferred to LinkResolverTask
5. **Report & Signal** — Clean orphaned .strm files, log summary stats, trigger library scan

### Item State Transitions
- CATALOGUED → PRESENT (Phase 2: .strm written)
- PRESENT → RESOLVED (LinkResolverTask: URL cached)
- RESOLVED → PRESENT (stale URL detected, re-queue for resolution)
- PRESENT/RESOLVED → RETIRED (Phase 3: real file detected, .strm deleted)
- ORPHANED → [deleted] (Phase 5: item removed from catalog, no PIN)

---

## Tasks/CatalogDiscoverTask.cs
> Scheduled task (daily at 4 AM) that syncs the discover_catalog table from AIOStreams.

### Public Methods
- `Execute(ct, progress)` — delegates to CatalogDiscoverService.SyncDiscoverCatalogAsync

---

## Tasks/MetadataFallbackTask.cs
> Daily task that back-fills full metadata (.nfo) for items missing poster/plot.

### Public Methods
- `Execute(ct, progress)` — fetches Cinemeta meta for up to 50 items, overwrites .nfo, triggers Emby refresh

---

## Models/ItemState.cs
> Item states for the Doctor reconciliation engine (Sprint 66).

### Key Types / Constants
- `Catalogued` (0) — Item exists in DB from sync, no .strm on disk yet
- `Present` (1) — .strm file exists on disk, URL not yet resolved
- `Resolved` (2) — .strm on disk + valid cached stream URL
- `Retired` (3) — Real file detected in Emby library; .strm deleted
- `Orphaned` (4) — .strm on disk but item no longer in catalog (and not PINNED)
- `Pinned` (5) — User explicitly added via Discover "Add to Library"; protected from catalog removal

### State Transitions
- CATALOGUED → PRESENT (Doctor Phase 2: writes .strm to disk)
- PRESENT → RESOLVED (Link Resolver: caches valid stream URL)
- RESOLVED → PRESENT (Doctor Phase 4: stale URL detected, re-queue)
- RESOLVED → RETIRED (Doctor Phase 3: real file found, PIN cleared)
- PINNED → RESOLVED (Discover: Add to Library → immediate resolve)
- PINNED → RETIRED (Doctor Phase 3: real file found, PIN cleared)
- ORPHANED → [deleted] (Doctor Phase 2: item removed from catalog, no PIN)

---

## Models/CatalogItem.cs
> ORM model for `catalog_items` table.

### Key Types / Constants
- `LocalSource` — `library` (user owns real file) or `strm` (plugin-managed)
- `SeasonsJson` — JSON array of `{season, episodes[]}` for series
- `ItemState` — Current state in Doctor reconciliation lifecycle (Sprint 66)
- `PinSource` — Source of PIN state when ItemState = PINNED
- `PinnedAt` — UTC timestamp when the item was pinned

---

## Models/ResolutionEntry.cs
> ORM model for `resolution_cache` table; one row per (imdb, season, episode).

### Key Types / Constants
- `QualityTier` — `remux`, `2160p`, `1080p`, `720p`, `unknown`
- `Status` — `valid` or `failed`

---

## Models/StreamCandidate.cs
> Ranked fallback stream URL in `stream_candidates` table.

### Key Types / Constants
- `Rank` — 0 = best; PlaybackService tries ascending order on failure
- `ProviderKey` — AIOStreams service.id (realdebrid, torbox, etc.)
- `StreamType` — drives StreamTypePolicy lookup

---

## Models/PlaybackEntry.cs
> One row in `playback_log`; written after every play attempt.

### Key Types / Constants
- `ResolutionMode` — `cached`, `fallback_1`, `fallback_2`, `sync_resolve`, `failed`

---

## Models/SyncState.cs
> Incremental sync cursor for a catalog source in `sync_state` table.

### Key Types / Constants
- `SourceKey` — primary key; format `aio:movie:gdrive`, `trakt:username`, etc.
- `ConsecutiveFailures` — drives error escalation in CatalogSyncTask

---

## Models/DiscoverCatalogEntry.cs
> Cached "available content" row for the Discover feature in `discover_catalog`.

---

## Models/ClientCompatEntry.cs
> Learned per-client streaming capabilities in `client_compat` table.

### Key Types / Constants
- `SupportsRedirect` — 1 = redirect works; 0 = must proxy (e.g. Samsung/LG TVs)

---

## Models/ResolutionCacheStats.cs
> Snapshot of resolution_cache row counts (total, valid, stale, failed).

---

## Models/ResolutionCoverageStats.cs
> Coverage summary: how many .strm items have valid/stale/no cache entries.

### Key Types / Constants
- `CoveragePercent` — computed property: ValidCached * 100 / TotalStrm

---

## Logging/EmbyLoggerAdapter.cs
> Adapts Emby's ILogger to MEL ILogger<T> so all log call sites write to the Emby log file.

