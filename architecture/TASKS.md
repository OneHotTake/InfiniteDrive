# InfiniteDrive — Background Tasks Reference

> Last reconciled: 2026-04-15 (post Sprint 362)

All tasks implement `IScheduledTask` and are instantiated by Emby's MEF framework via parameterless constructors. Services are accessed through `Plugin.Instance`.

## Primary Tasks

### CatalogSyncTask

**Purpose:** Syncs content from all configured sources (AIOStreams, Cinemeta, RSS feeds) into the catalog database.

**Sync Lock:** Acquired (`Plugin.SyncLock`)
**Default Interval:** 1 hour (configurable via `CatalogSyncIntervalHours`)
**Startup Delay:** 0–120 seconds random jitter to prevent thundering herd.

**Phases:**

| Phase | Service | Description |
|-------|---------|-------------|
| BuildProviders | — | Creates ICatalogProvider[] from config |
| Fetch | `ManifestFetcher` | Parallel fetch from all providers → CatalogFetchResult |
| Filter | `ManifestFilter` | Remove blocked/Your Files/digital release gate |
| Diff | `ManifestDiff` | Compare fetched vs DB → new/removed/unchanged |
| Process | `ItemPipelineService` | Lifecycle transitions for new items |
| User Catalogs | `UserCatalogSyncService` | Sync Trakt/MDBList RSS catalogs |
| Persist | `DatabaseManager` | Save last_sync_time in finally block |

**Pipeline tracking:** `Plugin.Pipeline.SetPhase("CatalogSync", "BuildProviders")`, `Plugin.Pipeline.SetPhase("CatalogSync", "Fetch")`, `Plugin.Pipeline.Clear()` in finally.

---

### RefreshTask

**Purpose:** Processes queued catalog items through the .strm writing and metadata enrichment pipeline.

**Sync Lock:** Acquired (`Plugin.SyncLock`)
**Running Gate:** `SemaphoreSlim(1,1)` — prevents concurrent Refresh executions.
**Default Trigger:** After CatalogSyncTask completes.

**6-Step Pipeline:**

| Step | Pipeline Phase | Service | Description |
|------|---------------|---------|-------------|
| 1. Collect | `"Collect"` | — | Queries `catalog_items` with ItemState=Queued |
| 2. Write | `"Write"` | `StrmWriterService`, `NamingPolicyService` | Creates .strm files with signed resolve URLs |
| 3. Hint | `"Hint"` | `NfoWriterService.WriteSeedNfo()` | Writes minimal NFO for Emby matching |
| 4. Enrich | `"Enrich"` | `MetadataEnrichmentService.EnrichBatchAsync()` | Fetches full metadata, writes enriched NFOs |
| 5. Notify | `"Notify"` | — | Notifies Emby (42-item batch bound), triggers scan |
| 6. Verify | `"Verify"` | `StreamProbeService` | Verifies stream URLs, renews tokens |

**Conditional steps:** Write/Hint/Enrich only run if Collect returned items. Notify/Verify always run.

**Pipeline tracking:** `Plugin.Pipeline.SetPhase("Refresh", ...)` at each step boundary, `Plugin.Pipeline.Clear()` in finally.

**Progress reporting:**
- Step 1 → 16%, Step 2 → 33%, Step 3 → 50%, Step 4 → 67%, Step 5 → 83%, Step 6 → 100%
- Also persists `refresh_active_step` and `refresh_items_processed` to plugin_metadata table.

---

### MarvinTask

**Purpose:** Background maintenance orchestrator. Validates, enriches, renews tokens, and cleans up saved items. Delegates all business logic to services.

**Sync Lock:** NOT acquired (runs concurrently with CatalogSync).
**Default Interval:** Every 30 minutes.

**4-Phase Pipeline:**

| Phase | Pipeline Phase | Service | Description |
|-------|---------------|---------|-------------|
| Pre-flight | — | `TryRestorePrimaryAsync()` | If on Secondary, probe primary and restore if healthy |
| 1. Validation | `"Validation"` | — | Validates .strm files, removes orphans |
| 2. Enrichment | `"Enrichment"` | `MetadataEnrichmentService` | Trickle-enrichs items past retry cooldown |
| 3. Token Renewal | `"TokenRenewal"` | — | Refreshes expired stream tokens in cache |
| 4. Save Maintenance | `"SaveMaintenance"` | — | Cleans expired user saves |

**Enrichment delegate:** MarvinTask maps items needing enrichment to `EnrichmentRequest` DTOs, then calls `MetadataEnrichmentService.EnrichBatchAsync()` with a fetch function that queries AIOStreams metadata.

**Pipeline tracking:** `Plugin.Pipeline.SetPhase("Marvin", ...)` at each phase, `Plugin.Pipeline.Clear()` in finally.

---

### RemovalTask

**Purpose:** Handles item removal with grace periods.

**Sync Lock:** Acquired (`Plugin.SyncLock`)
**Delegates to:** `RemovalPipeline`

### CollectionSyncTask

**Purpose:** Syncs user collections to Emby.

**Delegates to:** `CollectionSyncService`

### EpisodeExpandTask

**Purpose:** Expands series catalog items to individual episodes.

**Delegates to:** `SeriesPreExpansionService`

### RehydrationTask

**Purpose:** Re-processes items that failed during initial pipeline.

**Delegates to:** `RehydrationService`

### YourFilesTask

**Purpose:** Scans and processes user's "Your Files" content.

**Sync Lock:** Acquired (`Plugin.SyncLock`)

### LibraryReadoptionTask

**Purpose:** Post-library-scan reconciliation.

**Delegates to:** `LibraryPostScanReadoptionService`

### CatalogDiscoverTask

**Purpose:** Syncs Discover catalog for user browsing.

**Delegates to:** `CatalogDiscoverService`

### SeriesGapScanTask / SeriesGapRepairTask

**Status:** `[Obsolete]` — superseded by EpisodeDiffService.

### LinkResolverTask

**Purpose:** Emby link resolution (legacy).

**Note:** Does NOT use `Plugin.SyncLock`.

## Task Interaction Map

```
Scheduled Triggers
  │
  ├── Startup → CatalogSyncTask
  │               │
  │               └── On completion → RefreshTask
  │                                  │
  │                                  └── Steps 1-6 (serial)
  │
  ├── Every 30min → MarvinTask
  │                   │
  │                   └── Phases 1-4 (serial, no SyncLock)
  │
  ├── Every 1h → CatalogSyncTask (interval-gated)
  │
  └── On-demand → Any task via Emby task scheduler
```

**Concurrency model:**
- Tasks using `Plugin.SyncLock` cannot run concurrently (CatalogSync, Refresh, Removal, Collection, YourFiles).
- MarvinTask does NOT hold SyncLock — runs concurrently with sync tasks.
- `Plugin.Pipeline` provides visibility into which task/phase is active, regardless of lock state.
