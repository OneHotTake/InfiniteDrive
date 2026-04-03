# Architecture Review Findings — EmbyStreams vs Gelato vs AIOStreams

**Date:** 2026-04-02
**Scope:** Cross-project comparison; analysis only, no production code changes.

> **Note:** `/workspace/emby-debrid/aiostreams` contains AmigaOS streaming scripts, not the Stremio addon.
> AIOStreams patterns are inferred from the EmbyStreams client contract (`AioStreamsClient.cs`)
> and the Stremio addon protocol specification.

---

## A. Metadata Construction

### EmbyStreams
- Builds item metadata from AIOStreams catalog endpoints → `catalog_items` SQLite table
- Writes `.strm` files (URL pointers) + `.nfo` files (IMDB/TMDB unique IDs) to filesystem
- Emby's native scraper reads `.nfo` IDs to match items against TMDB
- Catalog sync normalizes folder/file names via `SanitisePathPublic()`
- Series metadata fetched from Stremio `meta` endpoint for episode expansion
- **Normalization:** IMDB ID is the primary key throughout; TMDB/AniList/Kitsu stored in NFO only

### Gelato
- Inserts items **directly into Emby's BaseItem tree** (not `.strm` files)
- `GelatoManager.InsertMeta()` creates actual Emby library items with StremioMeta data
- Uses Emby's own `ProviderIds` system (IMDB, TMDB) for matching
- `FindByProviderIds()` searches across multiple ID namespaces for deduplication
- **Normalization:** Multi-ID lookup (IMDB → TMDB fallback) for matching; no separate DB layer

### AIOStreams (Inferred)
- Manifest declares catalog types and supported ID prefixes
- Returns `meta` objects with Stremio-standard fields (id, name, year, posters, etc.)
- AIOStreams handles title/year/season matching internally; EmbyStreams consumes results

### What EmbyStreams Can Adopt
- **Multi-ID matching** (from Gelato): Currently relies solely on IMDB ID; adding TMDB/AniList fallback lookup would improve match rates for anime and non-English titles
- **Direct BaseItem insertion** (from Gelato): Would eliminate `.strm` indirection but is a major architectural change — not recommended short-term
- **Per-user metadata overrides** (from Gelato): `UserConfig.ApplyOverrides()` pattern could allow per-user library paths or quality preferences

---

## B. Stream Resolution

### EmbyStreams (PlaybackService.cs — 4-layer)
1. **SQLite cache lookup** — `resolution_cache` table, status-based branching
2. **Fresh cache hit** — serve directly if < 70% of TTL consumed (< 100ms)
3. **Stale/aging cache** — `HEAD Range: bytes=0-0` probe → try ranked fallback candidates
4. **Cache miss** — synchronous AIOStreams call (30-60s timeout), round-robin primary/secondary providers

- **Fallbacks:** Ranked candidate list (`stream_candidates` table), up to `CandidatesPerProvider * providers` URLs per item
- **Retries:** Round-robin across primary → secondary manifest URLs; per-provider retry with `continue`
- **Timeouts:** `Max(configured, manifest-hint)`, capped at 60s

### Gelato (GelatoStremioProvider.cs)
1. **No caching** — streams fetched fresh from Stremio `stream` endpoint on every playback
2. **Simple:** `GetStreamsAsync()` → `List<StremioStream>` → filter valid → write to Emby DB
3. **No fallbacks:** Single provider, single stream resolution attempt
4. **Timeout:** Hardcoded 30s HTTP client timeout

### AIOStreams (Inferred)
- Single manifest URL → stream endpoint per item
- Returns 80+ streams per request (multiple debrid providers, quality tiers)
- Client-side ranking/filtering by quality, provider, codec

### Most Efficient Pattern
**EmbyStreams' multi-layer cache + ranked fallbacks** is the most resilient. Gelato's no-cache approach is simpler but makes every playback dependent on the addon being online. Recommendation: Keep EmbyStreams' approach; consider adding a **stale-while-revalidate** mode for smoother UX during provider outages.

---

## C. Catalog Management

### EmbyStreams
- **Population:** `CatalogSyncTask` (daily at 3 AM UTC) fetches all manifest catalogs → `catalog_items` table
- **Incremental sync:** `sync_state` table tracks per-source cursor (`SourceKey = "aio:movie:catalogId"`)
- **Invalidation:** `PruneSourceAsync()` soft-deletes items no longer in feed; `CatalogSyncIntervalHours` (24h default) throttles re-fetches
- **Doctor reconciliation:** 5-phase engine (Fetch → Write → Adopt → Health → Report) manages item lifecycle states

### Gelato
- **Population:** `GelatoCatalogSyncTask` fetches catalogs, inserts items directly via `GelatoManager.InsertMeta()`
- **No separate catalog DB:** Items live in Emby's own library database
- **Sync series:** `SyncSeriesTask` runs periodically to update episode trees
- **TTL:** `StreamTTL` config field (default 3600s = 1h) for stream freshness

### Deadlock-Risk Patterns in EmbyStreams
- **None critical.** SQLite write gate (`SemaphoreSlim(1,1)`) properly serializes writes
- Transactions use `RunInTransaction()` inside the write gate — no nesting
- No sync-over-async patterns detected
- Rate limiting uses simple `lock` with short critical sections

---

## D. Configuration Audit

| # | Field | Status | Notes |
|---|-------|--------|-------|
| 1 | `PrimaryManifestUrl` | USED | Core — all AIOStreams communication |
| 2 | `SecondaryManifestUrl` | USED | PlaybackService round-robin fallback |
| 3 | `EnableAioStreamsCatalog` | USED | CatalogSyncTask gate |
| 4 | `AioStreamsCatalogIds` | USED | Per-catalog filtering |
| 5 | `AioStreamsAcceptedStreamTypes` | USED | StreamHelpers filter |
| 6 | `EmbyBaseUrl` | USED | .strm file generation |
| 7 | `EmbyApiKey` | USED | .strm auth header |
| 8 | `SyncPathMovies` | USED | Filesystem write target |
| 9 | `SyncPathShows` | USED | Filesystem write target |
| 10 | `LibraryNameMovies` | USED | SetupService library creation |
| 11 | `LibraryNameSeries` | USED | SetupService library creation |
| 12 | `SignatureValidityDays` | USED | StreamUrlSigner |
| 13 | `EnableAnimeLibrary` | USED | Catalog sync routing |
| 14 | `SyncPathAnime` | USED | Conditional on EnableAnimeLibrary |
| 15 | `SkipFutureEpisodes` | USED | SeriesPreExpansionService |
| 16 | `FutureEpisodeBufferDays` | USED | SeriesPreExpansionService |
| 17 | `DefaultSeriesSeasons` | USED | SeriesPreExpansionService fallback |
| 18 | `DefaultSeriesEpisodesPerSeason` | USED | SeriesPreExpansionService fallback |
| 19 | `EnableNfoHints` | USED | CatalogSyncTask NFO generation |
| 20 | `CacheLifetimeMinutes` | USED | PlaybackService + LinkResolverTask |
| 21 | `ApiDailyBudget` | USED | LinkResolverTask rate limiting |
| 22 | `MaxConcurrentResolutions` | USED | LinkResolverTask semaphore |
| 23 | `ApiCallDelayMs` | USED | LinkResolverTask throttle |
| 24 | `CatalogItemCap` | USED | CatalogSyncTask limit |
| 25 | `CatalogItemLimitsJson` | USED | Per-source overrides |
| 26 | `CatalogSyncIntervalHours` | USED | Sync throttle |
| 27 | `ProxyMode` | USED | PlaybackService + StreamProxyService |
| 28 | `MaxConcurrentProxyStreams` | USED | StreamProxyService concurrency |
| 29 | `PluginSecret` | USED | HMAC signing |
| 30 | `WebhookSecret` | USED | WebhookService auth |
| 31 | `ProviderPriorityOrder` | USED | StreamHelpers ranking |
| 32 | `CandidatesPerProvider` | USED | StreamHelpers + PlaybackService |
| 33 | `MaxFallbacksToStore` | **UNUSED** | Legacy field; ignored when `CandidatesPerProvider > 0` (always true since v0.60+). Still validated in `Validate()`. |
| 34 | `SyncResolveTimeoutSeconds` | USED | PlaybackService sync resolve |
| 35 | `AioStreamsDiscoveredTimeoutSeconds` | USED | Auto-populated from manifest |
| 36 | `AioStreamsDiscoveredName` | USED | StatusService display |
| 37 | `AioStreamsDiscoveredVersion` | USED | StatusService display |
| 38 | `AioStreamsIsStreamOnly` | USED | Catalog sync gate |
| 39 | `AioStreamsStreamIdPrefixes` | **MISCONFIGURED** | Populated during sync but never consumed for filtering. Dead config. |
| 40 | `EnableCinemetaDefault` | USED | Auto-inject Cinemeta |
| 41 | `FilterAdultCatalogs` | USED | Catalog sync filter |
| 42 | `NextUpLookaheadEpisodes` | USED | EmbyEventHandler pre-warm |
| 43 | `SyncScheduleHour` | USED | Catalog sync scheduling |
| 44 | `DeleteStrmOnReadoption` | USED | DoctorTask |
| 45 | `IsFirstRunComplete` | USED | UI wizard state |

**Summary:** 43 USED, 1 UNUSED (`MaxFallbacksToStore`), 1 MISCONFIGURED (`AioStreamsStreamIdPrefixes`)

---

## E. Reusable Constructs from Gelato

| # | Pattern | Source | Description | Complexity | Risk |
|---|---------|--------|-------------|------------|------|
| 1 | Per-User Config Overrides | Gelato `UserConfig.ApplyOverrides()` | Allow different Emby users to have different provider URLs, library paths, or quality prefs | M | LOW |
| 2 | Direct BaseItem Insertion | Gelato `GelatoManager.InsertMeta()` | Skip `.strm` files entirely; insert items into Emby library tree directly | L | HIGH (architecture change) |
| 3 | Multi-ID Provider Lookup | Gelato `FindByProviderIds()` | Match items by IMDB, TMDB, AniList, Kitsu in a single pass | S | LOW |
| 4 | In-Memory Manifest Cache | Gelato `_manifest` field | Cache parsed manifest in-memory to avoid re-fetching on every call | S | LOW |
| 5 | Per-Catalog Config Objects | Gelato `CatalogConfig` | Structured per-catalog config (enabled, maxItems, url) instead of flat JSON string | S | LOW |

---

## F. Developer + UX Gaps

### What Gelato Exposes That EmbyStreams Doesn't
- **Per-user configuration**: Different users can have different Stremio addon URLs and library paths. EmbyStreams is server-wide only.
- **P2P streaming toggle**: Gelato has explicit P2P enable/speed controls. EmbyStreams delegates all provider selection to AIOStreams.
- **Collection creation**: Gelato can auto-create Emby collections from Stremio catalogs (`CreateCollections`, `MaxCollectionItems`). EmbyStreams has no collection feature.
- **Subtitle integration**: Gelato fetches and injects Stremio subtitles via `SubtitleManagerDecorator`. EmbyStreams doesn't handle subtitles.
- **Search disable**: Gelato allows disabling search per-user. EmbyStreams' Discover is always-on.

### Friction in EmbyStreams Config That Gelato Avoids
- **Flat string config**: `AioStreamsCatalogIds` is a comma-separated string; `CatalogItemLimitsJson` is raw JSON. Gelato uses structured `CatalogConfig` objects with a UI per-row editor.
- **No config validation feedback**: EmbyStreams `Validate()` silently clamps values. Gelato's config page shows validation errors inline.
- **Stream type filtering**: `AioStreamsAcceptedStreamTypes` is a comma string that users must know valid values for. No UI picker.
- **35+ config fields**: Overwhelming for new users. Gelato's ~18 fields with progressive disclosure is more approachable.

### What Would Make EmbyStreams Better
1. **Per-catalog config cards** instead of raw JSON strings — structured, validated, deletable
2. **Stale-while-revalidate** playback mode — serve stale cached URL immediately, refresh in background
3. **Collection auto-creation** — group catalog items into Emby collections for better library organization
4. **Subtitle passthrough** — forward Stremio subtitle URLs to Emby clients
5. **Multi-ID matching** — reduce "item not found" errors for anime and non-English content

---

## Deadlock Risk

| Location | Pattern | Risk |
|----------|---------|------|
| PlaybackService.cs:1019 | `lock (RateLimitLock)` — simple dictionary rate limiter | LOW |
| UnauthenticatedStreamService.cs:180 | `lock (RateLimitLock)` — localhost-only rate limiter | LOW |
| DatabaseManager.cs:57 | `SemaphoreSlim _dbWriteGate(1,1)` — SQLite write serialization | LOW |
| LinkResolverTask.cs:109 | `SemaphoreSlim(concurrency)` — API call throttle | LOW |
| DatabaseManager.cs (multiple) | `RunInTransaction()` inside write gate | LOW |

**No sync-over-async patterns found.** No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` calls detected.
**No nested locks.** All synchronization primitives are single-level with proper try/finally cleanup.
**Overall deadlock risk: LOW.**
