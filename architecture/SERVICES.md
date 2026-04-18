# InfiniteDrive — Service Inventory

> Last reconciled: 2026-04-18 (post Language & Localization sprint)

## Decomposed API Endpoints (Services/Api/)

The monolithic `StatusService.cs` (2655 lines) was decomposed in Sprint 357 into four files:

### StatusService.cs (812 lines)

**Route:** `/InfiniteDrive/Status`
**Purpose:** Plugin status information endpoint.

| Method | Description |
|--------|-------------|
| `GetStatus()` | Returns current plugin status as JSON |

### CatalogEndpoints.cs (520 lines)

**Route:** `/InfiniteDrive/Catalogs`
**Purpose:** Catalog management and inspection.

| Class | Key Methods |
|-------|-------------|
| `CatalogService` | `Get()` — returns available catalogs from manifest |
| `CatalogProgressService` | Progress streaming for catalog syncs |
| `InspectService` | Catalog item inspection |

### DiagnosticsEndpoints.cs (1020 lines)

**Route:** `/InfiniteDrive/Health`, `/InfiniteDrive/Diagnostics`, and related
**Purpose:** Health checks, diagnostics, debugging.

| Class | Key Methods |
|-------|-------------|
| `HealthService` | `GetHealth()` — returns `HealthResponse` with manifest status, stream resolution stats, and `ActivePipeline` |
| `PanicService` | Emergency reset/cleanup |
| `DbStatsService` | Database statistics |
| `RecentErrorsService` | Recent error log retrieval |
| `UnhealthyItemsService` | Items in failed states |
| `RawStreamsService` | Raw stream data inspection |
| `DebugSeedMatrixService` | Debug matrix for seed items |
| `DebugCatalogCountService` | Catalog count verification |
| `AnimePluginStatusService` | Anime plugin status |
| `TestUrlService` | URL connectivity testing |
| `AnswerService` | Health check responses |
| `MarvinService` | Marvin task status queries |

### SearchEndpoints.cs (334 lines)

**Route:** `/InfiniteDrive/Search`, `/InfiniteDrive/RefreshManifest`
**Purpose:** Content search and manifest refresh.

| Class | Key Methods |
|-------|-------------|
| `SearchService` | `GetSearch()` — searches AIOStreams catalogs |
| `RefreshManifestService` | `GetRefreshManifest()`, `PostRefreshManifest()` — force-refresh manifest from configured URL |

## Core Business Services (Services/)

### NamingPolicyService (static)

**Purpose:** Single source of truth for filesystem naming conventions. Sprint 354.

| Method | Signature | Description |
|--------|-----------|-------------|
| `BuildFolderName` | `(string title, int? year, string? imdbId, string? tmdbId, string? tvdbId, string mediaType) → string` | Emby auto-match: `{Title} ({Year}) [imdbid-{id}]` |
| `BuildFolderName` | `(string title, int? year, string? imdbId) → string` | Convenience overload |
| `BuildFolderName` | `(CatalogItem item) → string` | Convenience overload |
| `SanitisePath` | `(string input) → string` | Removes filesystem-unsafe characters |

**ID priority:** IMDb (tt prefix) > TVDB (series/anime) > TMDB > title+year only.

### NfoWriterService (static)

**Purpose:** Single authority for all NFO file generation. Sprint 356. Two quality levels.

| Method | Signature | Description |
|--------|-----------|-------------|
| `WriteSeedNfo` | `(string strmPath, CatalogItem item, string? sourceType)` | Minimal NFO: IDs + title. For initial discovery. |
| `WriteSeedEpisodeNfo` | `(string strmPath, string seriesTitle, int season, int episode, string? episodeTitle)` | Minimal episode NFO |
| `WriteEnrichedNfo` | `(string nfoPath, AioEnrichedMeta meta, CatalogItem item)` | Full metadata NFO with cast, plot, ratings |

All XML encoding uses `SecurityElement.Escape`. No manual escaping anywhere.

### MetadataEnrichmentService (static)

**Purpose:** Shared retry/backoff/rate-limit logic for metadata enrichment. Sprint 359.

**Retry schedule:** 4h → 24h → block at 3 retries. 2s delay between API calls. 429 breaks immediately.

| Method | Signature | Description |
|--------|-----------|-------------|
| `EnrichBatchAsync` | `(List<EnrichmentRequest>, Func<EnrichmentRequest, CT, Task<EnrichedMetadata?>>, DatabaseManager, ILogger, CT) → Task<EnrichmentResult>` | Batch enrichment with retry gating |

**Input DTO:** `EnrichmentRequest` — see [DTO_SCHEMAS.md](DTO_SCHEMAS.md).
**Output:** `EnrichmentResult(EnrichedCount, BlockedCount, SkippedCount)`.

### ManifestState (instance, on Plugin.Manifest)

**Purpose:** Single authority for manifest status and staleness. Sprint 360.

| Property/Method | Type | Description |
|----------------|------|-------------|
| `Status` | `ManifestStatusState` | Current status enum (Error/NotConfigured/Stale/Ok) |
| `FetchedAt` | `DateTimeOffset` | Timestamp of last successful fetch |
| `CheckStale()` | `void` | Sets Status to Stale if > 12 hours since FetchedAt |

**Access pattern:** `Plugin.Manifest.Status`, `Plugin.Manifest.FetchedAt`, `Plugin.Manifest.CheckStale()`.
**Replaces:** `Plugin.GetManifestStatus()`, `Plugin.SetManifestStatus()`, `Plugin.CheckManifestStale()`, `Plugin.ManifestFetchedAt` — all deleted.

### PipelinePhaseTracker (instance, on Plugin.Pipeline)

**Purpose:** Real-time task phase visibility for diagnostics and admin UI. Sprint 361.

| Property/Method | Type | Description |
|----------------|------|-------------|
| `Current` | `PipelinePhase?` | Thread-safe snapshot of active phase |
| `SetPhase(taskName, phaseName)` | `void` | Sets current phase, resets progress counters |
| `ReportProgress(processed, total)` | `void` | Updates progress counters on current phase |
| `Clear()` | `void` | Nulls current phase (task complete/failed) |

Thread-safety via `Volatile.Read`/`Volatile.Write`. No DB writes, no events.

**DTO:** `PipelinePhase(TaskName, PhaseName, StartedAt, ItemsTotal, ItemsProcessed)` — see [DTO_SCHEMAS.md](DTO_SCHEMAS.md).

### ItemPipelineService

**Purpose:** Manages item lifecycle transitions: Known → Resolved → Hydrated → Created → Indexed → Active.

| Method | Signature | Description |
|--------|-----------|-------------|
| `ProcessItemAsync` | `(MediaItem, PipelineTrigger, CT) → Task<ItemPipelineResult>` | Full pipeline for a single item |

**Dependencies:** DatabaseManager, StreamResolver, MetadataHydrator, DigitalReleaseGateService.

### StrmWriterService

**Purpose:** Unified .strm file writer with version slot support.

| Method | Signature | Description |
|--------|-----------|-------------|
| `WriteAsync` | `(CatalogItem, SourceType, userId?, CT)` | Creates .strm with signed URL |

### StreamResolutionHelper

**Purpose:** Shared stream resolution with provider fallback.

| Method | Signature | Description |
|--------|-----------|-------------|
| `SyncResolveViaProvidersAsync` | `(…) → Task<ResolutionResult?>` | Resolves via primary → secondary with circuit breaker |

Returns `ResolutionResult` with structured failure modes (Success/Throttled/ContentMissing/ProviderDown).

### ResolverHealthTracker

**Purpose:** Circuit breaker and health tracking for stream providers.

| Method | Description |
|--------|-------------|
| `ShouldSkip(providerKey)` | Check if provider should be skipped |
| `RecordFailure(providerKey)` | Record a failure, possibly open circuit |
| `RestoreState()` | Reset circuit state |

### CooldownGate

**Purpose:** HTTP throttling with configurable cooldown periods.

| Method | Description |
|--------|-------------|
| `WaitAsync()` | Wait for current cooldown |
| `SetActiveCooldownKind(kind)` | Set the active cooldown type |

### ManifestFetcher

**Purpose:** Fetches catalog manifests from configured sources.

| Method | Description |
|--------|-------------|
| `FetchManifestAsync(url, ct)` | Fetch single manifest |
| `FetchAllManifestsAsync(config, ct)` | Fetch all configured manifests |

### SeriesPreExpansionService

**Purpose:** Expands series catalog items to individual episodes before .strm writing.

| Method | Description |
|--------|-------------|
| `ExpandSeriesAsync(item, ct)` | Expand a series to episodes |

### EpisodeDiffService / EpisodeRemovalService

**Purpose:** Episode-level diff and removal.

| Method | Description |
|--------|-------------|
| `DetectChanges(stored, incoming)` | Compare episode lists |
| `RemoveEpisodeFiles(paths)` | Remove individual .strm files |

### DiscoverService

**Purpose:** User-facing Discover UI for browsing and adding content.

| Method | Description |
|--------|-------------|
| `Browse(query)` | Browse available content |
| `Search(query)` | Search by text |
| `AddToLibrary(itemId, userId)` | Add item to user's library |
| `RemoveFromLibrary(itemId, userId)` | Remove item from user's library |

Responses include `AudioLanguages` field for previously-resolved items (populated from `stream_candidates.languages`).

### SavedService

**Purpose:** Per-user saved items management.

| Method | Description |
|--------|-------------|
| `SaveItemAsync(userId, itemId, reason)` | Save an item for a user |
| `UnsaveItemAsync(userId, itemId)` | Unsave an item |

### ResolverService / StreamEndpointService

**Purpose:** HTTP endpoint handlers for stream resolution.

| Service | Route | Description |
|---------|-------|-------------|
| `ResolverService` | `/InfiniteDrive/resolve?token=...` | Sync pipeline movie resolution |
| `StreamEndpointService` | `/InfiniteDrive/Stream?id=...&sig=...` | Series episode streaming |

**Language-aware resolution (Language sprint):** ResolverService uses `IAuthorizationContext` to read the authenticated user's `PreferredMetadataLanguage`. When multiple cached candidates exist with different languages, candidates whose `Languages` field matches the user's preference are selected first. Falls through to rank-order if no match.

### AioMediaSourceProvider

**Purpose:** Populates Emby's version picker with live AIOStreams streams. Implements `IMediaSourceProvider`.

| Method | Description |
|--------|-------------|
| `GetMediaSources(item, ct)` | Returns `List<MediaSourceInfo>` for an item |
| `MapStreamToSource(stream)` | Maps `AioStreamsStream` → `MediaSourceInfo` with populated `MediaStreams` |
| `MapCandidateToSource(candidate)` | Maps `StreamCandidate` → `MediaSourceInfo` with audio `MediaStreams` from `Languages` field |

**MediaStreams population (Language sprint):** `MapStreamToSource()` builds `MediaStreams` list from:
- Audio streams: one per language in `ParsedFile.Languages`, with title `"lang - channels audioTags"`
- Subtitle streams: one per `Subtitles[]` entry, marked `IsExternal` with `DeliveryUrl` pointing to subtitle URL

Sources are sorted by configured `MetadataLanguage` preference. Matching audio streams are marked `IsDefault = true`.

### CertificationResolver

**Purpose:** Fetches MPAA/TV certifications from TMDB for discover catalog items.

**Country locale (Language sprint):** Uses `PluginConfiguration.MetadataCountryCode` (default `"US"`) instead of hardcoded `"US"` to filter `release_dates` results.

### ListFetcher

**Purpose:** URL-sniffing dispatcher for external list providers (MDBList, Trakt, TMDB, AniList).

**TMDB locale (Language sprint):** TMDB list API calls use `language={MetadataLanguage}-{MetadataCountryCode}` instead of hardcoded `language=en-US`.
