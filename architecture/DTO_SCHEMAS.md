# InfiniteDrive — DTO Schemas

> Last reconciled: 2026-04-15 (post Sprint 362)

All types are defined in the `InfiniteDrive.Models` namespace unless otherwise noted.

## API Response DTOs

### HealthResponse

**Defined in:** `Services/Api/DiagnosticsEndpoints.cs`
**Route:** `GET /InfiniteDrive/Health`

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Status` | `string` | `"error"` | `"ok"`, `"stale"`, or `"error"` |
| `ManifestLastFetched` | `string?` | null | ISO 8601 timestamp of last manifest fetch |
| `ManifestStatus` | `ManifestStatusState` | `Error` | Manifest health enum (Error/NotConfigured/Stale/Ok) |
| `CatalogCount` | `int` | 0 | Number of catalogs in manifest |
| `CatalogsSkipped` | `List<CatalogSkippedEntry>` | empty | Skipped catalogs with reasons |
| `StreamResolutionSuccessRate` | `float` | 0 | Success rate (0.0–1.0) |
| `LastSyncTime` | `string?` | null | ISO 8601 last sync time |
| `LastCollectionSyncTime` | `string?` | null | ISO 8601 last collection sync time |
| `ActivePipeline` | `PipelinePhase?` | null | Current task phase, or null if idle |
| `BlockedAddons` | `List<string>` | empty | Blocked addon names |
| `ConfigurationRequired` | `bool` | false | True if any catalog needs configuration |

#### CatalogSkippedEntry (nested)

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Catalog name |
| `Reason` | `string` | `"requires_configuration"`, `"unknown_type"`, etc. |

### RefreshManifestResponse

**Defined in:** `Services/Api/SearchEndpoints.cs`
**Route:** `POST /InfiniteDrive/RefreshManifest`

| Field | Type | Description |
|-------|------|-------------|
| `Status` | `string` | `"ok"` or `"error"` |
| `ManifestStatus` | `ManifestStatusState` | Current manifest status enum |
| `ManifestLastFetched` | `string` | ISO 8601 timestamp |
| `CatalogCount` | `int` | Number of catalogs found |
| `ResourceTypes` | `List<string>` | Resource types: `"catalog"`, `"meta"`, `"stream"` |
| `IdPrefixes` | `List<string>` | ID prefixes found in manifest |

## Pipeline DTOs

### PipelinePhase

**Defined in:** `Models/PipelinePhaseTracker.cs`
**Access:** `Plugin.Pipeline.Current`

| Field | Type | Description |
|-------|------|-------------|
| `TaskName` | `string` | Which task is running: `"Refresh"`, `"Marvin"`, `"CatalogSync"` |
| `PhaseName` | `string` | Current phase: `"Collect"`, `"Enrichment"`, `"Fetch"`, etc. |
| `StartedAt` | `DateTimeOffset` | When this phase started |
| `ItemsTotal` | `int` | Total items in this phase (0 if unknown) |
| `ItemsProcessed` | `int` | Items processed so far |

Thread-safe via `Volatile` read/write. Immutable — `ReportProgress` creates a new record via `with` expression.

### EnrichmentRequest

**Defined in:** `Services/MetadataEnrichmentService.cs`
**Consumers:** MarvinTask, RefreshTask → MetadataEnrichmentService.EnrichBatchAsync()

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Id` | `string` | `""` | Catalog item ID |
| `ImdbId` | `string?` | null | IMDb ID (tt prefix) |
| `Title` | `string` | `""` | Item title |
| `Year` | `int?` | null | Release year |
| `RetryCount` | `int` | 0 | Number of enrichment attempts |
| `NextRetryAt` | `long?` | null | Unix timestamp for next retry |
| `CatalogItem` | `CatalogItem?` | null | Full item for NFO writing |

### EnrichmentResult

**Defined in:** `Services/MetadataEnrichmentService.cs`

```csharp
public record EnrichmentResult(int EnrichedCount, int BlockedCount, int SkippedCount);
```

## Resolution DTOs

### ResolutionResult

**Defined in:** `Models/ResolutionResult.cs`

| Field | Type | Description |
|-------|------|-------------|
| `StreamUrl` | `string?` | Resolved URL, or null on failure |
| `Status` | `ResolutionStatus` | Success/Throttled/ContentMissing/ProviderDown |
| `RetryAfter` | `TimeSpan?` | Wait duration for Throttled results |
| `Entry` | `ResolutionEntry?` | Cached entry if produced |

### ResolutionStatus Enum

| Value | Meaning |
|-------|---------|
| `Success` | Stream URL resolved |
| `Throttled` | Provider returned 429 — skip cycle, don't delete .strm |
| `ContentMissing` | Both providers returned 404 — safe to delete |
| `ProviderDown` | Timeout/connection failure — try next provider, don't delete |

### ResolverRequest

**Defined in:** `Models/ResolverRequest.cs`
**Route:** `GET /InfiniteDrive/Resolve`

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | IMDb ID or other provider ID |
| `Quality` | `string?` | Requested quality tier |
| `IdType` | `string?` | ID type (imdb, tmdb, etc.) |
| `Season` | `int?` | Season number (series) |
| `Episode` | `int?` | Episode number (series) |
| `Token` | `string` | HMAC authentication token |

### StreamEndpointRequest

**Defined in:** `Models/StreamEndpointRequest.cs`
**Route:** `GET /InfiniteDrive/Stream`

| Field | Type | Description |
|-------|------|-------------|
| `Token` | `string` | Authentication token |
| `Url` | `string` | Stream URL |

## Item DTOs

### CatalogItem

**Defined in:** `Models/CatalogItem.cs`
**Table:** `catalog_items`

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Primary key |
| `ImdbId` | `string?` | IMDb ID |
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

### MediaItem

**Defined in:** `Models/MediaItem.cs`
**Table:** `media_items`

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `long` | Primary key |
| `PrimaryId` | `MediaId` | Composite ID (type + value) |
| `MediaType` | `string` | `"movie"` or `"series"` |
| `Title` | `string` | Display title |
| `Status` | `ItemStatus` | Pipeline status |
| `Saved` | `bool` | Whether user has saved this |
| `Blocked` | `bool` | Whether admin has blocked this |
| `EmbyItemId` | `string?` | Emby's internal ID |
| `StrmPath` | `string?` | Path to .strm file |

## State Enums

### ManifestStatusState

**Defined in:** `Models/ManifestStatusState.cs`

```csharp
[EnumMember(Value = "error")]         Error = 0
[EnumMember(Value = "notConfigured")] NotConfigured = 1
[EnumMember(Value = "stale")]         Stale = 2
[EnumMember(Value = "ok")]            Ok = 3
```

### ItemState

**Defined in:** `Models/ItemState.cs`

`Catalogued`, `Present`, `Resolved`, `Retired`, `Orphaned`, `Pinned`, `Queued`, `Written`, `Notified`, `Ready`, `NeedsEnrich`, `Blocked`

### ItemStatus

**Defined in:** `Models/ItemStatus.cs`

`Known`, `Resolved`, `Hydrated`, `Created`, `Indexed`, `Active`, `Failed`, `Deleted`

### FailureReason

**Defined in:** `Models/FailureReason.cs`

`None`, `NoStreamsFound`, `MetadataFetchFailed`, `FileWriteError`, `EmbyIndexTimeout`, `DigitalReleaseGate`, `Blocked`

### ResolutionStatus

**Defined in:** `Models/ResolutionResult.cs`

`Success`, `Throttled`, `ContentMissing`, `ProviderDown`

### PipelineTrigger

**Defined in:** `Models/PipelineTrigger.cs`

`Sync`, `Play`, `WatchEpisode`, `UserSave`, `UserBlock`, `UserRemove`, `GraceExpiry`, `YourFiles`, `Admin`, `Retry`

### SourceType

**Defined in:** `Models/SourceType.cs`

`BuiltIn`, `Aio`, `UserRss`

### StreamQuality

**Defined in:** `Models/StreamQuality.cs`

`FHD_4K`, `FHD`, `HD`, `SD`, `Unknown`, `None`

### MediaIdType

**Defined in:** `Models/MediaIdType.cs`

`Tmdb`, `Imdb`, `Tvdb`, `AniList`, `AniDB`, `Kitsu`

### SaveReason

**Defined in:** `Models/SaveReason.cs`

`Explicit`, `WatchedEpisode`, `AdminOverride`
