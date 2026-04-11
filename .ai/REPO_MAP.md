# InfiniteDrive Repository Map

_Updated 2026-04-11 (Sprints 200+201: Wizard UX + Backend Wiring). Covers all `.cs` files in root, Services/, Data/, Models/, Tasks/, Logging/._

---

## Plugin.cs
> Plugin entry point; registers singleton, initialises DB and PluginSecret.

### Public Methods
- `GetPages()` — returns embedded HTML/JS config page registrations
- `GetThumbImage()` — returns embedded PNG thumbnail stream

### Key Types / Constants
- `Plugin.Instance` — singleton accessor used by all services and tasks
- `Plugin.DatabaseManager` — shared SQLite manager, ready before any task runs
- `Plugin.CooldownGate` — HTTP throttling gate (Sprint 155)

---

## PluginConfiguration.cs
> All persisted plugin settings; serialised to InfiniteDrive.xml by Emby.

### Public Methods
- `Validate()` — clamps all numeric fields to safe ranges after deserialisation

### Key Types / Constants
- `PrimaryManifestUrl` — full AIOStreams manifest URL (includes auth path)
- `SecondaryManifestUrl` — backup AIOStreams manifest URL
- `EnableBackupAioStreams` — gates whether SecondaryManifestUrl is used as fallback (Sprint 201)
- `SystemRssFeedUrls` — newline-separated system-wide RSS feed URLs, visible to all users (Sprint 200)
- `PluginSecret` — HMAC-SHA256 key for signing .strm URLs; auto-generated
- `ProxyMode` — `auto` / `redirect` / `proxy` stream serving mode
- `EnableAnimeLibrary` — enables syncing anime content to anime path (soft requirement)
- `SyncPathAnime` — filesystem path for anime content (default: /media/embystreams/anime)
- `IsFirstRunComplete` — flag indicating completion of setup wizard
- `EmbyBaseUrl` — auto-detected from window.location.origin; defaults to localhost but warns on remote clients
- `ResolvedInstanceType` — auto-detected InstanceType (Shared/Private) from manifest URL (Sprint 155)

---

## Configuration/configurationpage.html
> Plugin configuration UI with 5-step wizard, Sources tab, and progressive disclosure.

### Structure
- **Tab bar**: Setup, Overview, Settings, Content, Marvin (Discover/My Picks/My Lists hidden, pending Sprint 205 deletion)
- **Setup tab**: 5-step wizard with progress bar (Provider → Libraries → Metadata → Sources → Sync)
- **Overview tab**: System health, sources table, resolution coverage, background tasks, debug tools (collapsible)
- **Settings tab**: 5 flat cards (Sources, Playback & Cache, Library Paths, Security, Danger Zone) — no accordions
- **Content tab**: Blocked Items + Content Mgmt merged (unblock table, force sync button, slot management)
- **Marvin tab**: Marvin reconciliation engine (summon task, refresh status, enrichment summary, cooldown badge, task runner)
- **Discover/My Picks/My Lists tabs**: Hidden pending Sprint 205 (channel must ship first)

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
- `refreshSourcesTab(view)` — fetches and displays catalog sources from /InfiniteDrive/Status
- `refreshDashboard(view)` — polls /InfiniteDrive/Status for system health

### State Variables
- `_wizardStep` — current wizard step (1-5)
- `_wizardCatalogs` — loaded catalog sources for picker
- `_unsavedChanges` — tracks pending config changes

---

## Data/Schema.cs (Sprint 109)
> v3.3 database schema definitions for 9 tables (media_items, media_item_ids, sources, source_memberships, collections, stream_resolution_log, item_pipeline_log, schema_version, home_section_tracking).

### Key Types / Constants
- `Schema.CurrentSchemaVersion` = 1
- `Schema.Tables` - IReadOnlyList<TableDefinition> with all table CREATE SQL
- `TableDefinition` - Table name and CREATE SQL statement

### Tables
- media_items - Core item table with ItemStatus, SaveReason, FailureReason
- media_item_ids - Multi-provider ID matching (imdb, tmdb, tvdb, anilist, anidb, kitsu)
- sources - Enabled/Disabled sources with ShowAsCollection flag
- source_memberships - Which items belong to which sources
- collections - Emby BoxSet references
- stream_resolution_log - Resolution history for debugging
- item_pipeline_log - Item lifecycle event log
- schema_version - Schema version tracking
- home_section_tracking - Per-user per-rail section tracking

## Data/DatabaseInitializer.cs (Sprint 109)
> Initializes v3.3 database schema from scratch. Clean initialization with no migration from v20.

### Public Methods
- `Initialize(dbPath)` - Creates all 9 tables, enables WAL mode, sets schema version = 1

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
- `GetBlockedItemsAsync()` — items with blocked_at IS NOT NULL (Sprint 150)
- `UnblockItemAsync(itemId)` — clear tombstone, reset to NeedsEnrich (Sprint 150)
- `GetUserPinnedImdbIdsAsync(userId)` — HashSet of IMDB IDs pinned by user (Sprint 150 H-4)
- `GetCatalogItemsByIdsAsync(ids)` — batch fetch by primary key list (Sprint 150)

---

## Services/CooldownGate.cs
> HTTP throttling gate for AIOStreams/Cinemeta calls. Replaces scattered Task.Delay(ApiCallDelayMs).

### Key Types
- `InstanceType` enum — Shared / Private
- `CooldownKind` enum — CatalogFetch / StreamResolve / Enrichment / Cinemeta
- `CooldownProfile` — compiled-in throttle constants per instance type
- `CooldownGate` — singleton gate: WaitAsync (pre-call throttle), Tripped (429 backoff), three-strikes tracking

### Public Methods
- `WaitAsync(CooldownKind, CancellationToken)` — sleeps base+jitter, respects global cooldown
- `Tripped(TimeSpan?)` — sets global cooldown on 429, emits progress event, tracks strikes
- `ParseRetryAfter(string?)` — static helper to parse Retry-After header

---

## Models/UserCatalog.cs (Sprint 158)
> POCO for user_catalogs table rows. Represents a public Trakt/MDBList RSS feed owned by a user.

### Key Properties
- `Id`, `OwnerUserId`, `Service` (trakt/mdblist), `RssUrl`, `DisplayName`
- `Active` — soft-delete flag; false means excluded from sync and deprecation pending
- `LastSyncedAt`, `LastSyncStatus`

---

## Services/RssFeedParser.cs (Sprint 158)
> Parse-only RSS parser for public Trakt and MDBList feeds. No HTTP — input is raw XML string.

### Key Types
- `RssItem` record: Title, Year, ImdbId (extracted via regex), Link, Summary
- Hard cap: 1000 items per feed

### Public Methods
- `Parse(xml, logger, out feedTitle, out skippedNoImdb)` — parses RSS/Atom XML, returns items
- `DetectService(rssUrl)` — returns "trakt" or "mdblist", throws ArgumentException for unknown hosts

---

## Services/UserCatalogSyncService.cs (Sprint 158)
> Syncs user-owned RSS catalogs into catalog_items. Used by both CatalogSyncTask backstop and impatient-user Refresh endpoint.

### Key Types
- `UserCatalogSyncResult` — ok, fetched, added, updated, removed, skippedNoImdb, elapsedMs, error

### Public Methods
- `SyncOneAsync(catalogId, ct)` — fetches feed, parses, upserts catalog items, writes .strm files
- `SyncAllForOwnerAsync(ownerUserId, ct)` — sequentially syncs all active catalogs for one user

---

## Services/UserCatalogsService.cs (Sprint 158)
> REST API for user-owned public RSS catalogs. All endpoints require authenticated user.

### Endpoints
- `GET /InfiniteDrive/User/Catalogs` — returns caller's active catalogs + limit
- `POST /InfiniteDrive/User/Catalogs/Add` — validate URL, fetch feed, insert row, eager sync
- `POST /InfiniteDrive/User/Catalogs/Remove` — soft-delete (active=0), ownership check
- `POST /InfiniteDrive/User/Catalogs/Refresh` — synchronous single or all-catalog refresh

---

## Services/IdResolverService.cs (Sprint 160)
> Normalises raw manifest IDs to canonical provider IDs. Never throws, never returns null.

### Key Types
- `ResolvedIds` record: CanonicalId, ImdbId, TmdbId, TvdbId, AniDbId, RawMetaJson

### Resolution Chain
1. Parse manifestId: tt → fast path; tmdb_/tmdb:, tvdb_/tvdb:, kitsu:, mal:, imdb: prefixes
2. Call source addon `/meta/{type}/{id}.json` (1.5s timeout) → parse imdb_id, tmdb_id, tvdb_id
3. AIOMetadata fallback if still no tt
4. CanonicalId: tt > tmdb_ > tvdb_ > native

### Public Methods
- `ResolveAsync(manifestId, addonBaseUrl, mediaType, ct)` — full resolution chain

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
> Handles `GET /InfiniteDrive/Play`; cache-first stream resolution with rate limiting.

### Public Methods
- `Get(PlayRequest req)` — resolves and serves stream via redirect or proxy; checks cache, validates URLs, rate-limits by IP and user

### Key Types / Constants
- `PlayRequest` — DTO: imdb, season, episode, episode_id params
- `PlayErrorResponse` — JSON error body with machine code and retry hint

---

## Services/SignedStreamService.cs
> Public `GET /InfiniteDrive/Stream` endpoint secured by HMAC-SHA256 signature.

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
> Passthrough proxy for `GET/HEAD /InfiniteDrive/Stream/{ProxyId}`; Range-request aware.

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

## Services/AdminService.cs (Sprint 150)
> Admin-only REST endpoints for blocked catalog item management.

### Endpoints
- `GET /InfiniteDrive/Admin/BlockedItems` — returns all admin-blocked items
- `POST /InfiniteDrive/Admin/UnblockItems` — resets blocked items to NeedsEnrich, clears tombstone

---

## Services/UserService.cs (Sprint 150)
> User-facing REST endpoints for pin management (My Picks).

### Endpoints
- `GET /InfiniteDrive/User/MyPins` — returns user's playback and discover pins with metadata
- `POST /InfiniteDrive/User/RemovePins` — removes selected pins for the current user

---

## Services/DiscoverService.cs
> REST API handlers for the Discover browsing/search/add-to-library feature.
> Sprint 150 H-4: All browse/search/detail handlers load per-user pin status and pass to MapToDiscoverItem.

### Public Methods
- `Get(DiscoverBrowseRequest req)` — paginated catalog listing from local DB
- `Get(DiscoverSearchRequest req)` — searches local DB; optionally queries AIOStreams live
- `Get(DiscoverDetailRequest req)` — detailed metadata for a single item by IMDB ID
- `Post(DiscoverAddToLibraryRequest req)` — creates .strm file, triggers Emby library scan

### Key Types / Constants
- `DiscoverItem` — unified response DTO combining metadata and library status
- `MapToDiscoverItem(entry, userPinnedImdbIds?)` — per-user InLibrary when ids provided
- `TryGetCurrentUserId()` — extracts user ID from IAuthorizationContext

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
> `POST /InfiniteDrive/Trigger` — manually fires any named EmbyStreams scheduled task.

### Public Methods
- `Post(TriggerRequest req)` — validates task key, launches background task, returns immediately

### Key Types / Constants
- Accepted task keys: `catalog_sync`, `link_resolver`, `file_resurrection`, `library_readoption`

---

## Services/StatusService.cs
> `GET /InfiniteDrive/Status` — returns full health/stats JSON snapshot for the dashboard.

### Key Types / Constants
- `StatusResponse` — version, AIOStreams connection, catalog counts, cache coverage, recent playback
- `StatusResponse` (Sprint 66) — includes item state counts: CataloguedCount, PresentCount, ResolvedCount, RetiredCount, PinnedCount, OrphanedCount
- `ProviderHealthEntry` — URL, reachability, latency for a single provider

---

## Services/LibraryProvisioningService.cs (Sprint 201)
> Creates and verifies Emby virtual folder libraries for InfiniteDrive. Fully rewritten in Sprint 201 — no stubs, uses ILibraryManager.AddVirtualFolder. Idempotent — safe to call on every wizard run.

### Public Methods
- `EnsureLibrariesProvisionedAsync()` — creates disk directories and Emby library entries; skips already-registered paths
- `ProvisionOneAsync(name, contentType, path)` — internal; creates one library (movies/tvshows/mixed anime)

### Key Features
- Uses `ILibraryManager.AddVirtualFolder` SDK call — real Emby library registration
- Creates disk directories if missing
- Checks existing virtual folders to avoid duplicates
- Anime library uses empty contentType (mixed type) when enabled

---

## Services/SetupService.cs
> Setup wizard endpoints: directory creation, API key rotation, .strm file rewrite, library provisioning.

### Public Methods
- `Post(ProvisionLibrariesRequest req)` — creates Emby library entries via LibraryProvisioningService (Sprint 201)
- `Post(CreateDirectoriesRequest req)` — creates movies/shows/anime library directories
- `Post(RotateApiKeyRequest req)` — rotates PluginSecret, rewrites all .strm files

---

## Services/TestFailoverService.cs
> `GET /InfiniteDrive/TestFailover` — dry-runs the full 3-layer resilience chain.

### Public Methods
- `Get(TestFailoverRequest req)` — probes primary AIOStreams, fallbacks, and direct debrid APIs

### Key Types / Constants
- `TestFailoverResponse` — Layer1/Layer2/Layer3 results with latency and status
- `FailoverLayerResult` — outcome enum, message, round-trip latency

---

## Services/StrmWriterService.cs (Sprint 156)
> Unified service for writing .strm files to disk with consistent attribution.

### Public Methods
- `WriteAsync(item, originSourceType, ownerUserId, ct)` — writes .strm file with NFO, persists first_added_by_user_id
- `BuildSignedStrmUrl(config, imdbId, mediaType, season, episode, quality)` — generates signed URL for /InfiniteDrive/resolve
- `SanitisePathPublic(input)` — public wrapper for filesystem path sanitisation

### Key Types / Constants
- Uses resolve tokens with 365-day validity for .strm files
- First-writer-wins attribution via `first_added_by_user_id` column

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

## Services/YourFilesScanner.cs (Sprint 114)
> Scans library for user-added files ("Your Files"), filters out InfiniteDrive-managed items (.strm files).

### Public Methods
- `ScanAsync(ct)` — scans library for Movie/Episode items, filters out EmbyStreams items
- `IsEmbyStreamsItem(item)` — checks if item is InfiniteDrive-managed (.strm or embystreams provider ID)

### Key Features
- Filters out .strm files
- Filters out items with "embystreams" provider ID
- Returns user-added files for matching

---

## Services/YourFilesMatcher.cs (Sprint 114)
> Matches "Your Files" items against media_item_ids table using multi-provider ID matching.

### Public Methods
- `MatchAsync(yourFilesItems, ct)` — matches items against media_item_ids by provider ID
- `FindMatchingMediaItemAsync(item, ct)` — finds matching MediaItem by provider ID
- `DetermineMatchType(item)` — determines match type (Imdb, Tmdb, Tvdb, AniList, AniDB, Kitsu, Other)

### Key Types / Constants
- `YourFilesMatchType` — enum for match types (priority: Imdb > Tmdb > Tvdb > AniList > AniDB > Kitsu)
- `YourFilesMatchResult` — record containing YourFilesItem, MediaItem, and MatchType

### Matching Priority
1. IMDB (most reliable)
2. TMDB
3. TVDB
4. AniList
5. AniDB
6. Kitsu

---

## Services/YourFilesConflictResolver.cs (Sprint 114)
> Resolves conflicts between "Your Files" items and EmbyStreams items using coalition rules.

### Public Methods
- `ResolveAsync(match, ct)` — resolves a match according to coalition rules
- `DeleteStrmFileAsync(item)` — deletes .strm file for superseded items

### Key Types / Constants
- `ConflictResolution` — enum for resolution outcomes (KeepBlocked, SupersededWithEnabledSource, SupersededWithoutEnabledSource, SupersededConflict)

### Coalition Rule Implementation
- Blocked → KeepBlocked (never supersede user's explicit block)
- Saved + enabled source → SupersededConflict (admin review needed)
- Active + enabled source → SupersededWithEnabledSource (supersede stream, keep item)
- No enabled source → SupersededWithoutEnabledSource (delete .strm)

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
- `TaskKey` — `doctor` for triggering via `/InfiniteDrive/Trigger?task=doctor`

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

## Tasks/YourFilesTask.cs (Sprint 114)
> Scheduled task (every 6 hours) that reconciles "Your Files" with EmbyStreams items.

### Public Methods
- `Execute(ct, progress)` — runs 4-phase reconciliation (scan → match → resolve → report)
- `GetDefaultTriggers()` — returns 6-hour interval trigger

### Key Types / Constants
- `YourFilesSummary` — summary record with TotalScanned, TotalMatches, KeptBlocked, SupersededWithEnabledSource, SupersededWithoutEnabledSource, SupersededConflict

### Phases
1. **Scan** (0-25%): Scan library for user-added files
2. **Match** (25-50%): Match items against media_item_ids
3. **Resolve** (50-75%): Resolve conflicts per coalition rules
4. **Report** (75-100%): Log summary statistics

---

## Models/RemovalResult.cs (Sprint 115)
> Result record for removal operations.

### Key Types / Constants
- `RemovalResult(bool IsSuccess, string Message)` — operation result with Success()/Failure() static constructors

---

## Services/RemovalService.cs (Sprint 115)
> Manages item removal with grace period and Coalition rule compliance.

### Public Methods
- `MarkForRemovalAsync(itemId, ct)` — starts 7-day grace period
- `RemoveItemAsync(itemId, ct)` — removes item if grace period expired
- `RemoveStrmFileAsync(item)` — deletes .strm file from disk
- `RemoveFromEmbyAsync(item, ct)` — removes from Emby library (TODO: IsPlayed check)
- `GetStrmPath(item)` — resolves to movies/, series/, anime/ based on media type

### Key Types / Constants
- `RemovalResult` — result record from Models/RemovalResult.cs
- `_gracePeriod` — 7-day TimeSpan
- Coalition rule check via `ItemHasEnabledSourceAsync()` — single JOIN query

---

## Services/RemovalPipeline.cs (Sprint 115)
> Pipeline for processing expired grace period items.

### Public Methods
- `ProcessExpiredGraceItemsAsync(ct)` — processes all grace period items

### Key Types / Constants
- `RemovalPipelineResult` — summary record with TotalProcessed, RemovedCount, CancelledCount, ExtendedCount, SuccessCount, FailureCount, Results

### Phases
1. **Get Items**: Fetch all items with active grace period
2. **Check Expiration**: Verify grace period expired
3. **Coalition Rule**: Single JOIN query via `ItemHasEnabledSourceAsync()`
4. **Revert or Remove**: Cancel grace for items with enabled source, remove for others

---

## Tasks/RemovalTask.cs (Sprint 115)
> Scheduled task for removal pipeline cleanup (every 1 hour).

### Public Methods
- `Execute(ct, progress)` — runs removal pipeline with SyncLock
- `GetDefaultTriggers()` — returns 1-hour interval trigger

---

## Controllers/RemovalController.cs (Sprint 115)
> API endpoints for removal operations.

### Public Methods
- `POST /mark` — starts grace period
- `POST /remove` — removes item (if grace expired)
- `POST /process` — processes all expired grace items
- `GET /list` — lists grace period items

### Key Types / Constants
- Uses `CancellationToken.None` (Emby SDK limitation: IRequest has no AbortToken)

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

## Models/MediaIdType.cs (Sprint 109)
> Enum defining supported external provider types for media identification (Tmdb, Imdb, Tvdb, AniList, AniDB, Kitsu).

### Key Types / Constants
- Parse(string) - Parses lowercase string to MediaIdType
- ToLowerString() - Returns lowercase string representation

---

## Models/MediaId.cs (Sprint 109)
> Value type representing a media identifier with provider type and value. Format: "type:value" (e.g., "imdb:tt123456").

### Key Methods
- Parse(string input) - Parses "type:value" format into MediaId
- TryParse(string input, out MediaId mediaId) - Safe parsing with boolean result
- ToString() - Returns "type:value" format
- IEquatable<MediaId> - Full equality implementation

---

## Models/ItemStatus.cs (Sprint 109)
> Lifecycle states for media items (Known, Resolved, Hydrated, Created, Indexed, Active, Failed, Deleted).

### Key Methods
- CanTransitionTo(ItemStatus targetStatus) - Validates state transitions
- IsTerminal() - Checks if status is terminal (no further transitions)
- IsErrorState() - Checks if status is an error
- ToDisplayString() - User-friendly display string

---

## Models/FailureReason.cs (Sprint 109)
> Reason codes for why an item failed to process.

### Key Methods
- ToDisplayString() - User-friendly display string
- ToDescription() - Detailed description for logging/UI
- IsRecoverable() - Checks if failure can be retried

---

## Models/PipelineTrigger.cs (Sprint 109)
> Events or actions that trigger the item pipeline.

### Key Methods
- ToDisplayString() - User-friendly display
- ToDescription() - Detailed description for logging
- IsUserTriggered() - Checks if trigger was user-initiated
- IsHighPriority() - Checks if trigger is high-priority

---

## Models/SaveReason.cs (Sprint 109)
> Reason why an item was saved by the user.

### Key Methods
- ToDisplayString() - User-friendly display
- ToDescription() - Detailed description
- IsAutomatic() - Checks if save was automatic

---

## Models/SourceType.cs (Sprint 109)
> Type of source for catalog content (BuiltIn, Aio, Trakt, MdbList).

### Key Methods
- Parse(string value) - Parses string to SourceType
- ToLowerString() - Returns lowercase string
- ToDisplayString() - User-friendly display

---

## Models/MediaItem.cs (Sprint 109)
> Core entity representing a media item in the v3.3 system.

### Key Fields
- Id - TEXT UUID primary key
- PrimaryId - MediaId with type and value
- MediaType - "movie" or "series"
- Status - ItemStatus lifecycle state
- Saved/Blocked - Boolean states (not status values)
- EmbyItemId - TEXT GUID for Emby integration
- StrmPath/NfoPath - File paths

### Key Methods
- MarkSaved() - Marks item as saved
- MarkBlocked() - Marks item as blocked
- MarkFailed() - Marks item as failed
- SetStatus() - Updates status with transition validation

---

## Models/Source.cs (Sprint 109)
> Represents a content source (catalog) in the v3.3 system.

### Key Fields
- Id - TEXT UUID primary key
- Name/Url/Type - Source identification
- Enabled/ShowAsCollection - State flags
- MaxItems/SyncIntervalHours/LastSyncedAt - Sync metadata
- EmbyCollectionId/CollectionName - BoxSet metadata

### Key Methods
- MarkSynced() - Updates last synced timestamp
- Toggle/Enable/Disable() - State management

---

## Models/AioStreamsPrefixDefaults.cs (Sprint 109)
> Default prefix mappings for AIOStreams media ID types.

### Key Types / Constants
- DefaultPrefixMap - Dictionary mapping MediaIdType to prefix string
- GetPrefix(MediaIdType) - Gets prefix for type
- ToAioStreamsPath(MediaId) - Formats MediaId as AIOStreams path

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

---

## Sprint Plans (.ai/SPRINT_NNN.md)

### Sprints 105-108
**Status:** Superseded by Sprint 109

Originally planned as extension of v20 architecture. Superseded after v3.3 design review determined a full architectural breaking change is required.

### Sprint 109 — Foundation & Migration (v3.3)
**File:** `.ai/SPRINT_109.md`
**Risk:** HIGH (breaking change, full wipe migration)

**Phases:**
- Phase 109A — New Database Schema (7 tables)
- Phase 109B — Core Domain Models (MediaId, ItemStatus, enums)
- Phase 109C — Migration from v20 to v3 Schema

**Key Changes:**
- MediaId system replaces IMDB-only keys
- ItemStatus lifecycle machine
- Sources model replaces Catalog model
- Saved/Blocked states replace PIN model
- Your Files detection via media_item_ids

### Sprint 110 — Services Layer (v3.3)
**File:** `.ai/SPRINT_110.md`
**Risk:** MEDIUM

**Services:**
- ItemPipelineService — Item lifecycle orchestration
- StreamResolver — AIOStreams resolution and ranking
- MetadataHydrator — Cinemeta/AIOMetadata
- YourFilesReconciler — Your Files detection
- SourcesService — Source management
- CollectionsService — BoxSet management
- SavedService — Save/Block actions

### Sprint 111 — Sync Pipeline (v3.3)
**File:** `.ai/SPRINT_111.md`
**Risk:** MEDIUM

**Flow:** fetch → filter → diff → process → handle removed

**Components:**
- ManifestFetcher — AIOStreams with TTL
- ManifestFilter — Filter blocked/duplicate/over-cap
- ManifestDiff — Manifest vs database
- SyncTask — Full pipeline orchestration

### Sprint 112 — Stream Resolution and Playback (v3.3)
**File:** `.ai/SPRINT_112.md`
**Risk:** MEDIUM

**Components:**
- PlaybackService — Cache-first resolution
- StreamCache — TTL-based caching
- StreamUrlSigner — HMAC-SHA256 signing
- ProgressStreamer — SSE progress events

### Sprint 113 — Saved/Blocked User Actions (v3.3)
**File:** `.ai/SPRINT_113.md`
**Risk:** LOW

**Components:**
- SavedRepository — Persist saved/blocked state
- SavedActionService — Action logic with Coalition rule
- SavedController — Admin API
- Saved UI — Config page UI

### Sprint 114 — Your Files Detection (v3.3) ✅ Complete
**File:** `.ai/SPRINT_114.md`
**Risk:** MEDIUM

**Components:**
- YourFilesScanner — Scans library for user files, filters out .strm items
- YourFilesMatcher — Multi-provider ID matching (IMDB → TMDB → TVDB → AniList → AniDB → Kitsu)
- YourFilesConflictResolver — Conflict resolution with coalition rules
- YourFilesTask — Scheduled reconciliation (every 6 hours)

### Sprint 115 — Removal Pipeline (v3.3) ✅ Complete
**File:** `.ai/SPRINT_115.md`
**Risk:** LOW

**Components:**
- RemovalService — Mark and remove items
- RemovalPipeline — Process removed items
- RemovalTask — Scheduled cleanup
- RemovalController — Admin API

### Sprint 116 — Collection Management (v3.3)
**File:** `.ai/SPRINT_116.md`
**Risk:** LOW

**Components:**
- BoxSetRepository — Persist BoxSet metadata
- BoxSetService — Emby BoxSet API wrapper
- CollectionSyncService — Sync sources to BoxSets
- CollectionTask — Scheduled sync

### Sprint 117 — Admin UI (v3.3)
**File:** `.ai/SPRINT_117.md`
**Risk:** LOW

**Tabs:**
- Sources — Enable/disable sources
- Collections — View/sync collections
- Saved — Saved items
- Blocked — Blocked items
- Actions — Manual actions
- Logs — Pipeline logs

### Sprint 118 — Home Screen Rails (v3.3)
**File:** `.ai/SPRINT_118.md`
**Risk:** LOW

**Rail Types:**
- Saved — User-saved items
- New — Recently added
- Collections — Emby BoxSets
- RecentlyResolved — Fresh streams

### Sprint 119 — API Endpoints (v3.3)
**File:** `.ai/SPRINT_119.md`
**Risk:** LOW

**Controllers:**
- StatusController — Plugin status
- SourcesController — Source management
- CollectionsController — Collection management
- ItemsController — Item queries
- ActionsController — Manual actions
- LogsController — Log retrieval

### Sprint 120 — Logging (v3.3)
**File:** `.ai/SPRINT_120.md`
**Risk:** LOW

**Components:**
- PipelineLogger — Item lifecycle events
- ResolutionLogger — Stream resolution events
- LogRepository — Persist logs
- LogRetentionService — Cleanup old logs
- LogRetentionTask — Scheduled cleanup

**Retention:**
- Pipeline logs: 30 days
- Resolution logs: 7 days

**File:** `.ai/SPRINT_122.md` | **Risk:** HIGH | **Depends:** Sprint 121 |

| `### Sprint 123 — File Materialization (.strm/.nfo writing + rehydration) | **Status:** Planned | **Risk:** HIGH | **Depends:** Sprint 122 |

 |
| `### Sprint 124 — Playback Endpoint Changes | **Status:** Planned | **Risk:** MEDIUM | **Depends:** Sprint 123 |
 |
| `### Sprint 125 — UI: Wizard Step 3 ( Stream Quality) | **Status:** Planned | **Risk:** LOW | **Depends:** Sprint 122, **Depends:** Sprint 121 |
 |
| `### Sprint 126 — UI: Settings Page ( Stream Versions) | **Status:** Planned | **Risk:** LOW | **Depends:** Sprint 125, **Depends:** Sprint 121 | |
 |
| `### Sprint 127 - Startup Detection (Server Address) | **Status:** Planned | **Risk:** LOW | **Depends:** Sprint 124, **Depends:** Sprint 121 | |
 |
| `### Sprint 128 — Plugin Registration + Build + Test) | **Status:** Planned | **Risk:** LOW | **Depends:** Sprint 127 | **Depends:** Sprint 121 | |
 |
| `### Sprint 129 — Build Verification | **Status:** Planned | **Risk:** LOW | **Depends:** Sprint 128 | **Depends:** Sprint 121 | |
|
  `### Sprint 130 — Integration Testing | **Status:** Planned | **Risk:** MEDIUM | **Depends:** Sprint 129, **Depends:** Sprint 121 |
 |
|
  **Key Components:**
  - 4 new database tables (version_slots, candidates, version_snapshots, materialized_versions)

 - 4 new ORM models
VersionSlot, Candidate, VersionSnapshot, MaterializedVersion)
  - 4 new repository classes
VersionSlotRepository, CandidateRepository, VersionSnapshotRepository, MaterializedVersionRepository)
    - Candidate normalizer ( normalizes raw AIOStreams streams)
    - Slot matcher ( filters + ranks candidates against slot policies)
    - Versioned playback service ( extends PlaybackService with slot parameter)
    - Rehydration service ( orchestrates add/remove/reename)
    - Version materializer ( writes .strm/.nfo files with slot suffixes)
  - Startup detector ( server address change → URL rewrite)
    - Wizard Step 3 ( Stream Quality)
    - Settings page ( Stream Versions section)
    - Plugin configuration ( versioning preferences)    - Build + test verification
 |
|
    `### Sprint 122 — Versioned Playback (Schema, Data, Models)
 Candidate Normalizer, Slot Matcher)
 Playback, Rehydration, UI, Startup Detection, Build + Test) |
| `### Sprint 123 — File Materialization (.strm/.nfo Writing + rehydration) | **Status:** Planning | **Risk:** HIGH | **Depends:** Sprint 122, |
| `### Sprint 124 — Playback Endpoint Changes | **Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 123, |
| `### Sprint 125 — UI: Wizard Step 3 ( Stream Quality) | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 122, **Depends:** Sprint 121 | |
| `### Sprint 126 — UI: Settings Page ( Stream Versions) | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 125, **Depends:** Sprint 121 | |
 | `### Sprint 127 — Startup Detection (Server Address) | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 124, **Depends:** Sprint 121 | |
| `### Sprint 128 — Plugin Registration + Build + Test) | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 127, **Depends:** Sprint 121 | |
| `### Sprint 129 — Build Verification | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 128, **Depends:** Sprint 121 | |
| `### Sprint 130 — Integration Testing | **Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 129, **Depends:** Sprint 121 | |
|
    **See Also:**
    - `.ai/SPRINT_122.md` — Sprint 122 details
    - `.ai/SPRINT_123.md` — Sprint 123 details ( and subsequent)
    - `docs/VERSIONED_PLAYBACK.md` — Design spec ( |
|
    `## v3.3 Summary`
 |
|
    **Sprints:** 109-130 (22 sprints) |
    **Status:** Planning complete for Sprints 122-130 |
    **Release Target:** v3.3.0 |

    **Breaking Change:** Full database reset required` |

    **Key Features:** |
    - Versioned playback with 7 quality slots per - Multi-version .strm/.nfo per file generation |
    - Candidate normalization from AIOStreams payloads |
    - Slot-based stream resolution with fallback ladder |
    - Catalog-wide rehydration (add/remove/reename) |
    - Wizard quality profile selection |
    - Settings version management with confirmation dialogs |
    - Server address change detection |
    - Maximum 8 enabled slots enforcement |

    **See Also:**
    - `.ai/SPRINT_109.md` through `.ai/SPRINT_121.md` — Sprint details
    - `.ai/SPRINT_122.md` through `.ai/SPRINT_130.md` — Versioned playback sprints
    - `docs/VERSIONED_PLAYBACK.md` — Design spec |
**File:** `.ai/SPRINT_121.md`
**Risk:** LOW

**Test Categories:**
- Migration Tests — v20 → v3
- Sync Pipeline Tests — Full flow
- Playback Tests — Resolution and signing
- User Action Tests — Save/Block
- Your Files Tests — Detection
- E2E Test Plan — Manual scenarios
