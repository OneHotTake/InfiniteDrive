# InfiniteDrive — DTO Schemas

> Last reconciled: 2026-05-04 (post-Sprint 516)

All types are defined in the `InfiniteDrive` namespace unless otherwise noted.

## AIOStreams DTOs (Models/AioStreams.cs)

**File:** `Models/AioStreams.cs` (535 lines)
**Purpose:** 18 DTOs + exception types extracted from AioStreamsClient. Central DTO file for all AIOStreams API interactions.

Contains request/response types for AIOStreams manifest, catalog, meta, and stream endpoints.

## Cache Entry DTOs

### StreamCandidate (consolidated DTO)

**File:** `Models/StreamCandidate.cs`
**Purpose:** Unified stream candidate DTO — 3 former DTOs merged into one. Used for both live resolution results and cache entries.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | UUID primary key |
| `ImdbId` | `string` | AIOStreams item ID |
| `Season` | `int?` | Season number; null for movies |
| `Episode` | `int?` | Episode number; null for movies |
| `Rank` | `int` | Zero-based rank (0 = best) |
| `ProviderKey` | `string` | Provider service key (e.g. `realdebrid`) |
| `StreamType` | `string` | Stream type (`debrid`, `usenet`, `http`) |
| `Url` | `string` | Direct playable HTTP URL |
| `HeadersJson` | `string?` | JSON-serialized HTTP headers |
| `QualityTier` | `string?` | Quality tier (`remux`, `2160p`, `1080p`, etc.) |
| `FileName` | `string?` | Original filename from AIOStreams |
| `FileSize` | `long?` | File size in bytes |
| `BitrateKbps` | `int?` | Bitrate in kbps |
| `IsCached` | `bool` | True when cached at provider CDN |
| `InfoHash` | `string?` | SHA1 info-hash |
| `FileIdx` | `int?` | File index within torrent |
| `StreamKey` | `string?` | Stable deduplication key |
| `BingeGroup` | `string?` | Binge-group identifier |
| `Languages` | `string?` | Comma-separated ISO 639-1 audio language codes (e.g. `"ja,en"`) |
| `ResolvedAt` | `string` | UTC timestamp when resolved |
| `ExpiresAt` | `string` | UTC timestamp when URL should be re-validated |
| `Status` | `string` | `valid`, `suspect`, or `failed` |

**Note:** `StreamCandidate` is mapped to/from the `stream_resolution_cache` table.

### CachedStreamEntry

**File:** `Models/CachedStreamEntry.cs`
**Purpose:** Cache entry representation. `TmdbKey` stores a compound key (naming legacy — not always TMDB).

## Item DTOs

### CatalogItem

**File:** `Models/CatalogItem.cs`
**Table:** `catalog_items`

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Primary key |
| `ImdbId` | `string?` | AIOStreams primary ID (confusing column name — not always IMDB) |
| `TmdbId` | `string?` | TMDB ID |
| `TvdbId` | `string?` | TVDB ID |
| `UniqueIdsJson` | `string?` | All unique IDs serialized |
| `Title` | `string` | Display title |
| `Year` | `int?` | Release year |
| `MediaType` | `string` | `"movie"`, `"series"`, `"anime"` |
| `Source` | `string?` | Source identifier |
| `SourceListId` | `string?` | ID in source catalog |
| `SeasonsJson` | `string?` | Season/episode data (series) |
| `StrmPath` | `string?` | Path to .strm file |
| `LocalPath` | `string?` | Library folder path |
| `LocalSource` | `string?` | Source type label |
| `ItemState` | `ItemState` | Current lifecycle state |
| `PinSource` | `string?` | Which source pinned this |
| `PinnedAt` | `DateTimeOffset?` | When pinned |
| `EpisodesExpanded` | `bool` | Whether episodes are expanded |

## Policy DTOs

### GracePeriodPolicy

**File:** `Models/GracePeriodPolicy.cs`
**Purpose:** Shared removal policy for items pending removal with configurable grace period.

## API Response DTOs

### HealthResponse

**Route:** `GET /InfiniteDrive/Health`

| Field | Type | Description |
|-------|------|-------------|
| `Status` | `string` | `"ok"`, `"stale"`, or `"error"` |
| `ManifestLastFetched` | `string?` | ISO 8601 timestamp |
| `ManifestStatus` | `ManifestStatusState` | Manifest health enum |
| `CatalogCount` | `int` | Number of catalogs |
| `CatalogsSkipped` | `List<CatalogSkippedEntry>` | Skipped catalogs with reasons |
| `StreamResolutionSuccessRate` | `float` | Success rate (0.0-1.0) |
| `LastSyncTime` | `string?` | ISO 8601 last sync time |
| `ActivePipeline` | `PipelinePhase?` | Current task phase, or null if idle |

### RefreshManifestResponse

**Route:** `POST /InfiniteDrive/RefreshManifest`

| Field | Type | Description |
|-------|------|-------------|
| `Status` | `string` | `"ok"` or `"error"` |
| `ManifestStatus` | `ManifestStatusState` | Current manifest status |
| `ManifestLastFetched` | `string` | ISO 8601 timestamp |
| `CatalogCount` | `int` | Number of catalogs found |
| `ResourceTypes` | `List<string>` | Resource types from manifest |
| `IdPrefixes` | `List<string>` | ID prefixes found in manifest |

## Pipeline DTOs

### PipelinePhase

**File:** `Models/PipelinePhaseTracker.cs`
**Access:** `Plugin.Pipeline.Current`

| Field | Type | Description |
|-------|------|-------------|
| `TaskName` | `string` | Which task: `"Refresh"`, `"Marvin"`, `"CatalogSync"` |
| `PhaseName` | `string` | Current phase: `"Collect"`, `"Enrichment"`, `"Fetch"`, etc. |
| `StartedAt` | `DateTimeOffset` | When this phase started |
| `ItemsTotal` | `int` | Total items (0 if unknown) |
| `ItemsProcessed` | `int` | Items processed so far |

Thread-safe via `Volatile` read/write. Immutable — `ReportProgress` creates a new record via `with` expression.

### EnrichmentRequest

**File:** `Services/MetadataEnrichmentService.cs`
**Consumers:** MarvinTask, RefreshTask -> MetadataEnrichmentService.EnrichBatchAsync()

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Catalog item ID |
| `ImdbId` | `string?` | IMDb ID (tt prefix) |
| `Title` | `string` | Item title |
| `Year` | `int?` | Release year |
| `RetryCount` | `int` | Number of enrichment attempts |
| `NextRetryAt` | `long?` | Unix timestamp for next retry |
| `CatalogItem` | `CatalogItem?` | Full item for NFO writing |

### EnrichmentResult

```csharp
public record EnrichmentResult(int EnrichedCount, int BlockedCount, int SkippedCount);
```

## Resolution DTOs

### ResolutionResult

**File:** `Models/ResolutionResult.cs`

| Field | Type | Description |
|-------|------|-------------|
| `StreamUrl` | `string?` | Resolved URL, or null on failure |
| `Status` | `ResolutionStatus` | Success/Throttled/ContentMissing/ProviderDown |
| `RetryAfter` | `TimeSpan?` | Wait duration for Throttled results |
| `Entry` | `ResolutionEntry?` | Cached entry if produced |

## State Enums

### ManifestStatusState

**File:** `Models/ManifestStatusState.cs`

```csharp
[EnumMember(Value = "error")]         Error = 0
[EnumMember(Value = "notConfigured")] NotConfigured = 1
[EnumMember(Value = "stale")]         Stale = 2
[EnumMember(Value = "ok")]            Ok = 3
```

### ItemState

**File:** `Models/ItemState.cs`

`Catalogued`, `Present`, `Resolved`, `Retired`, `Orphaned`, `Pinned`, `Queued`, `Written`, `Notified`, `Ready`, `NeedsEnrich`, `Blocked`

### ResolutionStatus

**File:** `Models/ResolutionResult.cs`

| Value | Meaning |
|-------|---------|
| `Success` | Stream URL resolved |
| `Throttled` | Provider returned 429 — skip cycle, don't delete .strm |
| `ContentMissing` | Both providers returned 404 — safe to delete |
| `ProviderDown` | Timeout/connection failure — try next provider, don't delete |

### CooldownKind

**File:** `Services/CooldownGate.cs`

`Default`, `SeriesMeta`

### InstanceType

**File:** `Services/CooldownGate.cs`

`Shared`, `Private` — auto-detected from manifest URL.

### SourceType

**File:** `Models/SourceType.cs`

`BuiltIn`, `Aio`, `UserRss`

### StreamQuality

**File:** `Models/StreamQuality.cs`

`FHD_4K`, `FHD`, `HD`, `SD`, `Unknown`, `None`

### PipelineTrigger

**File:** `Models/PipelineTrigger.cs`

`Sync`, `Play`, `WatchEpisode`, `UserSave`, `UserBlock`, `UserRemove`, `GraceExpiry`, `YourFiles`, `Admin`, `Retry`

### MediaIdType

**File:** `Models/MediaIdType.cs`

`Tmdb`, `Imdb`, `Tvdb`, `AniList`, `AniDB`, `Kitsu`

### SaveReason

**File:** `Models/SaveReason.cs`

`Explicit`, `WatchedEpisode`, `AdminOverride`

### FailureReason

**File:** `Models/FailureReason.cs`

`None`, `NoStreamsFound`, `MetadataFetchFailed`, `FileWriteError`, `EmbyIndexTimeout`, `DigitalReleaseGate`, `Blocked`
