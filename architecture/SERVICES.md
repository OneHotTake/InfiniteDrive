# InfiniteDrive — Service Inventory

> Last reconciled: 2026-05-04 (post-Sprint 516)

## Decomposed API Endpoints (Services/Api/)

The monolithic `StatusService.cs` was decomposed into individual endpoint services. Each is a self-contained class handling one domain.

### StatusService.cs

**Route:** `/InfiniteDrive/Status`
**Purpose:** Plugin status information endpoint.

| Method | Description |
|--------|-------------|
| `GetStatus()` | Returns current plugin status as JSON |

### CatalogEndpoints.cs

**Route:** `/InfiniteDrive/Catalogs`
**Purpose:** Catalog management and inspection.

| Class | Key Methods |
|-------|-------------|
| `CatalogService` | `Get()` — returns available catalogs from manifest |
| `CatalogProgressService` | Progress streaming for catalog syncs |
| `InspectService` | Catalog item inspection |

### SearchEndpoints.cs

**Route:** `/InfiniteDrive/Search`, `/InfiniteDrive/RefreshManifest`
**Purpose:** Content search and manifest refresh.

| Class | Key Methods |
|-------|-------------|
| `SearchService` | `GetSearch()` — searches AIOStreams catalogs |
| `RefreshManifestService` | `GetRefreshManifest()`, `PostRefreshManifest()` — force-refresh manifest from configured URL |

### Diagnostics Endpoints (decomposed into individual services)

Each class is a standalone service file under `Services/Api/`:

| Service | Route / Purpose |
|---------|----------------|
| `HealthService` | `/InfiniteDrive/Health` — health response with manifest status, stream resolution stats, `ActivePipeline` |
| `PanicService` | Emergency reset/cleanup |
| `DbStatsService` | Database statistics |
| `RecentErrorsService` | Recent error log retrieval |
| `UnhealthyItemsService` | Items in failed states |
| `RawStreamsService` | Raw stream data inspection |
| `MarvinService` | Marvin task status queries |
| `TestUrlService` | URL connectivity testing |
| `AnswerService` | Health check responses |
| `AnimePluginStatusService` | Anime plugin status |

## Core Business Services (Services/)

### AioMediaSourceProvider (3-file partial class)

**Purpose:** Populates Emby's version picker with live AIOStreams streams. Implements `IMediaSourceProvider`. Secure playback via `RequiresOpening = true`.

| File | Lines | Purpose |
|------|-------|---------|
| `AioMediaSourceProvider.cs` | 966 | `GetMediaSources()` — main entry point |
| `AioMediaSourceProvider.Open.cs` | 430 | `OpenMediaSource()` — CDN URL materialization |
| `AioMediaSourceProvider.StreamBuilding.cs` | 389 | Stream builder helpers |

| Method | Description |
|--------|-------------|
| `GetMediaSources(item, ct)` | Returns `List<MediaSourceInfo>` with `RequiresOpening = true`, `OpenToken` = CDN URL |
| `OpenMediaSource(openToken, currentLiveStreams, ct)` | Validates token, returns `InfiniteDriveLiveStream` with materialized `MediaSourceInfo` |
| `MapStreamToSource(stream)` | Maps `AioStreamsStream` to `MediaSourceInfo` |
| `MapCandidateToSource(candidate)` | Maps `StreamCandidate` to `MediaSourceInfo` |

**Security:**
- `GetMediaSources()` sets `RequiresOpening = true`, `Path = ""`, `OpenToken = cdnUrl`
- CDN URLs never appear in picker display
- `OpenMediaSource()` validates token is HTTP/HTTPS URL, materializes CDN URL

### ResolverService (2-file partial class)

| File | Lines | Purpose |
|------|-------|---------|
| `ResolverService.cs` | 349 | Resolve endpoint handler |
| `ResolverService.Cache.cs` | 267 | Cache read/write operations |

### AioStreamsClient + Factory

**AioStreamsClient.cs** (882 lines) — HTTP client for AIOStreams API calls.

**AioStreamsClientFactory** — Factory pattern for creating client instances:

| Method | Description |
|--------|-------------|
| `Create()` | Create default client |
| `CreateForProvider()` | Create client for a specific provider |
| `TryCreateForManifest()` | Create client from manifest URL, with auto-detection |

### StreamCacheService

**Purpose:** Cached stream read/write and `BuildMediaSources` operations.

### StreamResolutionHelper

**Purpose:** Shared stream resolution with provider fallback.

| Method | Description |
|--------|-------------|
| `SyncResolveViaProvidersAsync()` | Resolves via primary then secondary with circuit breaker |

Returns `ResolutionResult` with structured failure modes (Success/Throttled/ContentMissing/ProviderDown).

### StreamHelpers

**File:** `StreamHelpers.cs` (634 lines)
**Purpose:** Quality/backoff/ranking helpers. Centralized `CooldownGate.ParseRetryAfter` and exponential backoff.

### CandidateNormalizer

**File:** `CandidateNormalizer.cs` (440 lines)
**Purpose:** Three-tier metadata parser for stream candidates.

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

**CooldownKind enum:** `Default`, `SeriesMeta` (collapsed from 4 values).
**InstanceType:** `Shared`, `Private`. Auto-detected from manifest URL.

### StrmWriterService

**File:** `StrmWriterService.cs` (429 lines)
**Purpose:** Unified .strm file writer. Pre-cache sources use `Path=""` + `RequiresOpening` + `OpenToken`.

| Method | Description |
|--------|-------------|
| `WriteAsync(CatalogItem, SourceType, userId?, CT)` | Creates .strm with signed URL |

### NamingPolicyService (static)

**Purpose:** Single source of truth for filesystem naming conventions.

| Method | Description |
|--------|-------------|
| `BuildFolderName(...)` | Emby auto-match format: `{Title} ({Year}) [imdbid-{id}]` |
| `SanitisePath(input)` | Removes filesystem-unsafe characters |

**ID priority:** IMDb (tt prefix) > TVDB (series/anime) > TMDB > title+year only.

### NfoWriterService (static)

**Purpose:** Single authority for all NFO file generation. Two quality levels.

| Method | Description |
|--------|-------------|
| `WriteSeedNfo(...)` | Minimal NFO: IDs + title. For initial discovery. |
| `WriteSeedEpisodeNfo(...)` | Minimal episode NFO |
| `WriteEnrichedNfo(...)` | Full metadata NFO with cast, plot, ratings |

### MetadataEnrichmentService (static)

**Purpose:** Shared retry/backoff/rate-limit logic for metadata enrichment.

**Retry schedule:** 4h -> 24h -> block at 3 retries. 2s delay between API calls. 429 breaks immediately.

| Method | Description |
|--------|-------------|
| `EnrichBatchAsync(...)` | Batch enrichment with retry gating |

**Output:** `EnrichmentResult(EnrichedCount, BlockedCount, SkippedCount)`.

### ManifestState (instance, on Plugin.Manifest)

**Purpose:** Single authority for manifest status and staleness.

| Property/Method | Type | Description |
|----------------|------|-------------|
| `Status` | `ManifestStatusState` | Current status enum (Error/NotConfigured/Stale/Ok) |
| `FetchedAt` | `DateTimeOffset` | Timestamp of last successful fetch |
| `CheckStale()` | `void` | Sets Status to Stale if > 12 hours since FetchedAt |

### PipelinePhaseTracker (instance, on Plugin.Pipeline)

**Purpose:** Real-time task phase visibility for diagnostics and admin UI.

| Property/Method | Type | Description |
|----------------|------|-------------|
| `Current` | `PipelinePhase?` | Thread-safe snapshot of active phase |
| `SetPhase(taskName, phaseName)` | `void` | Sets current phase, resets progress counters |
| `ReportProgress(processed, total)` | `void` | Updates progress counters on current phase |
| `Clear()` | `void` | Nulls current phase (task complete/failed) |

### InfiniteDriveLiveStream

**Purpose:** `ILiveStream` wrapper returned from `AioMediaSourceProvider.OpenMediaSource()`. Carries resolved `MediaSourceInfo` for Emby to play directly.

| Member | Description |
|--------|-------------|
| `MediaSource` | Resolved CDN stream with `Path = cdnUrl`, `RequiresOpening = false` |
| `UniqueId` | Unique stream identifier |
| `EnableStreamSharing` | `false` |
| `SupportsCopyTo` | `false` |

### GracePeriodPolicy

**File:** `Models/GracePeriodPolicy.cs`
**Purpose:** Shared removal policy for items pending removal with configurable grace period.

### DiscoverService

**Purpose:** User-facing Discover UI for browsing and adding content.

### SavedService

**Purpose:** Per-user saved items management.

### SeriesPreExpansionService

**Purpose:** Expands series catalog items to individual episodes before .strm writing.

### EpisodeDiffService

**Purpose:** Episode-level diff and removal.

### CertificationResolver

**Purpose:** Fetches MPAA/TV certifications from TMDB for discover catalog items. Uses `PluginConfiguration.MetadataCountryCode` (default `"US"`).

### ListFetcher

**Purpose:** URL-sniffing dispatcher for external list providers (MDBList, Trakt, TMDB, AniList). TMDB calls use `language={MetadataLanguage}-{MetadataCountryCode}`.
