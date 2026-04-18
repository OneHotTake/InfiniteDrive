# InfiniteDrive — State Management

> Last reconciled: 2026-04-18 (post Language & Localization sprint)

## 1. Manifest State Lifecycle

**Authority:** `Plugin.Manifest` (instance of `ManifestState`, Sprint 360)

```
                    ┌──────────────────┐
                    │  Error (default)  │ ← Plugin startup, fetch failure, exception
                    └────────┬─────────┘
                             │
                   Successful fetch from
                   RefreshManifest endpoint
                             │
                             ▼
                    ┌──────────────────┐
              ┌────►│       Ok         │ ← Manifest loaded, within 12h TTL
              │     └────────┬─────────┘
              │              │
              │     12 hours pass without
              │     refresh (CheckStale)
              │              │
              │              ▼
              │     ┌──────────────────┐
              │     │      Stale       │ ← Manifest loaded but > 12h old
              │     └────────┬─────────┘
              │              │
              │     Successful refresh
              │              │
              └──────────────┘
                             │
              Any fetch failure or
              exception during refresh
                             │
                             ▼
                    ┌──────────────────┐
                    │  Error (loop)    │
                    └──────────────────┘

          ┌──────────────────┐
          │  NotConfigured   │ ← No PrimaryManifestUrl in config
          └──────────────────┘
```

### ManifestStatusState Enum

Defined in `Models/ManifestStatusState.cs`. JSON-serializable via `[EnumMember]`.

| Value | EnumMember | Meaning |
|-------|-----------|---------|
| `Error` | `"error"` | Fetch failed, never loaded, or exception during refresh. Default at startup. |
| `NotConfigured` | `"notConfigured"` | No PrimaryManifestUrl configured. |
| `Stale` | `"stale"` | Loaded successfully but > 12 hours since fetch. |
| `Ok` | `"ok"` | Loaded and within 12-hour TTL. |

### Access Pattern

```csharp
// Read status
var status = Plugin.Manifest.Status;           // ManifestStatusState enum
var fetched = Plugin.Manifest.FetchedAt;        // DateTimeOffset

// Check staleness (called before manifest operations)
Plugin.Manifest.CheckStale();                   // Sets Stale if > 12h old

// Set status
Plugin.Manifest.Status = ManifestStatusState.Ok;
Plugin.Manifest.FetchedAt = DateTimeOffset.UtcNow;
```

### Staleness Threshold

12 hours, hardcoded in `ManifestState.StaleThreshold`. The `CheckStale()` method compares `DateTimeOffset.UtcNow - FetchedAt` against this threshold.

### Consumers

| Consumer | Access Pattern |
|----------|---------------|
| SearchEndpoints (RefreshManifest) | Sets Status + FetchedAt on success/failure. Calls CheckStale() before refresh. |
| DiagnosticsEndpoints (Health) | Reads Status + FetchedAt for HealthResponse. |
| RepairUI | Reads Status for display. |
| HealthUI | Reads Status for display (fallback if API unavailable). |

## 2. Pipeline Phase Tracking

**Authority:** `Plugin.Pipeline` (instance of `PipelinePhaseTracker`, Sprint 361)

### PipelinePhase DTO

```csharp
public sealed record PipelinePhase(
    string TaskName,         // "Refresh", "Marvin", "CatalogSync"
    string PhaseName,        // "Collect", "Enrichment", "Fetch", etc.
    DateTimeOffset StartedAt,
    int ItemsTotal,
    int ItemsProcessed);
```

### Lifecycle

```
Plugin.Pipeline.SetPhase("Refresh", "Collect")
  → Creates PipelinePhase(TaskName="Refresh", PhaseName="Collect", StartedAt=now, 0, 0)
  → Thread-safe via Volatile.Write

Plugin.Pipeline.ReportProgress(47, 200)
  → Updates ItemsProcessed=47, ItemsTotal=200
  → No-op if no current phase

Plugin.Pipeline.Clear()
  → Sets Current to null
  → Called in task's finally block
```

### Tasks Reporting Phases

| Task | Phases |
|------|--------|
| RefreshTask | Collect → Write → Hint → Enrich → Notify → Verify |
| MarvinTask | Validation → Enrichment → TokenRenewal → SaveMaintenance |
| CatalogSyncTask | BuildProviders → Fetch |

### Visibility

- `Plugin.Pipeline.Current` → thread-safe read of current phase
- `HealthResponse.ActivePipeline` → exposed via `/InfiniteDrive/Health` endpoint
- Available to admin UI for real-time status display

## 3. Item Lifecycle

**Authority:** `ItemState` enum in `Models/ItemState.cs`

Managed by `ItemPipelineService.ProcessItemAsync()`.

```
Catalogued ──► Present ──► Resolved ──► Retired
     │            │            │            │
     │            │            │            └── Re-enter via RehydrationService
     │            │            │
     │            ▼            ▼
     │        Pinned       Orphaned
     │                        │
     ▼                        └── Cleaned up by HousekeepingService
  Queued
     │
     ▼
  Written ──► Notified ──► Ready
     │
     ▼
  NeedsEnrich ──► (MetadataEnrichmentService)
     │
     ▼
  Blocked ──► (permanently removed, no retry)
```

### State Descriptions

| State | Meaning | Who Sets It |
|-------|---------|-------------|
| `Catalogued` | Known to the catalog DB | CatalogSyncTask |
| `Queued` | Ready for .strm writing | CatalogSyncTask |
| `Written` | .strm file created | RefreshTask (Write step) |
| `NeedsEnrich` | Needs metadata enrichment | RefreshTask (Enrich step) |
| `Notified` | Emby notified of new item | RefreshTask (Notify step) |
| `Ready` | Fully processed, playable | RefreshTask (Verify step) |
| `Present` | Found on disk, may need reconciliation | MarvinTask (Validation) |
| `Resolved` | Stream URL resolved | ItemPipelineService |
| `Retired` | Scheduled for removal | RemovalPipeline |
| `Orphaned` | File on disk but no DB entry | HousekeepingService |
| `Pinned` | User-pinned, won't be removed | SavedService |
| `Blocked` | Permanently blocked, .strm deleted | AdminService |

## 4. Provider Failover State

**Authority:** `Plugin.Instance.ActiveProviderState` (instance of `ActiveProviderState`)

```
  Primary ──────► Secondary ──────► Primary
    │                │                  ▲
    │   Primary      │   MarvinTask     │
    │   fails        │   TryRestore     │
    │   (circuit     │   PrimaryAsync   │
    │    breaker)    │   probes primary │
    ▼                ▼                  │
  (requests go     (requests go         │
   to secondary)    to secondary)       │
                                         │
                          Primary is
                          healthy again
```

### ActiveProviderState

```csharp
public class ActiveProviderState
{
    public ActiveProvider Current { get; set; }  // Primary or Secondary
}
```

### ResolverHealthTracker (Circuit Breaker)

Per-provider circuit state. When failures exceed threshold, provider is marked "open" and skipped until `RestoreState()` is called (typically by MarvinTask).

| Method | Description |
|--------|-------------|
| `ShouldSkip(providerKey)` | Returns true if circuit is open |
| `RecordFailure(providerKey)` | Increments failure count |
| `RestoreState()` | Resets all circuits |

## 5. Sync State (Per-Source Cursor)

**Authority:** `SyncState` table in SQLite. Managed by `DatabaseManager`.

| Field | Type | Purpose |
|-------|------|---------|
| `SourceKey` | string | e.g. `aio:movie:gdrive` |
| `LastSyncAt` | DateTimeOffset | When last synced |
| `LastEtag` | string | ETag for conditional requests |
| `LastCursor` | string | Delta cursor for incremental sync |
| `ItemCount` | int | Items from this source |
| `Status` | string | Sync status |
| `ConsecutiveFailures` | int | Failure streak |
| `LastError` | string | Last error message |

Used by `CatalogSyncTask` for interval-gated incremental sync.

## 6. Language & Localization

**Authority:** `PluginConfiguration.MetadataLanguage` + `PluginConfiguration.MetadataCountryCode`
**Default:** `en` / `US`

### Where language config is used

| Consumer | Config Field | Usage |
|----------|-------------|-------|
| `AioMediaSourceProvider.SortByLanguagePreference()` | `MetadataLanguage` | Sorts version picker sources by language match |
| `AioMediaSourceProvider.BuildMediaStreams()` | — | Builds audio/subtitle `MediaStream` list from AIOStreams parsed data |
| `ResolverService.PreferLanguageMatch()` | User's `PreferredMetadataLanguage` | Prefers cached candidates matching user language at playback time |
| `ListFetcher.GetTmdbLanguage()` | `MetadataLanguage` + `MetadataCountryCode` | TMDB list API `language` parameter |
| `CertificationResolver.FetchMovieCertificationInternalAsync()` | `MetadataCountryCode` | Filters TMDB `release_dates` by country code |
| `DiscoverService.GetAudioLanguages()` | — | Populates `AudioLanguages` from `stream_candidates.languages` |

### Languages column (stream_candidates)

Added in schema V32. Comma-separated ISO 639-1 codes (e.g. `"ja,en"`). Populated when streams are resolved via `AioStreamsClient` — the `ParsedFile.Languages` array is serialized into this field. Used by `MapCandidateToSource()` to build audio `MediaStreams` for cached candidates, and by `ResolverService.PreferLanguageMatch()` for per-user language preference.
