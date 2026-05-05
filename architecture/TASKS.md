# InfiniteDrive — Background Tasks Reference

> Last reconciled: 2026-05-05 (post-Sprint 519)

## Task Architecture

MarvinTask is the **sole Emby-visible scheduled task** (implements `IScheduledTask`). All other tasks are internal helpers with no `IScheduledTask` interface — they are invoked by MarvinTask or triggered internally.

Emby creates tasks via parameterless constructors using MEF. Services are accessed through `Plugin.Instance`.

## MarvinTask — Primary Orchestrator

**File:** `Tasks/MarvinTask.cs`
**Purpose:** Background maintenance orchestrator. Validates, enriches, renews tokens, and cleans up saved items. Delegates all business logic to services. Also orchestrates internal helper tasks.

**Sync Lock:** NOT acquired (runs concurrently with CatalogSync).
**Default Interval:** Every 30 minutes.

**Orchestration Responsibilities:**
- Invokes CatalogSyncTask, RefreshTask, PreCacheAioStreamsTask as internal helpers
- Runs its own maintenance phases (validation, enrichment, token renewal, save maintenance)

**4-Phase Maintenance Pipeline:**

| Phase | Pipeline Phase | Service | Description |
|-------|---------------|---------|-------------|
| Pre-flight | -- | `TryRestorePrimaryAsync()` | If on Secondary, probe primary and restore if healthy |
| 1. Validation | `"Validation"` | -- | Validates .strm files, removes orphans |
| 2. Enrichment | `"Enrichment"` | `MetadataEnrichmentService` | Trickle-enrichs items past retry cooldown |
| 3. Token Renewal | `"TokenRenewal"` | -- | Refreshes expired stream tokens in cache |
| 4. Save Maintenance | `"SaveMaintenance"` | -- | Cleans expired user saves |

**Enrichment delegate:** MarvinTask maps items needing enrichment to `EnrichmentRequest` DTOs, then calls `MetadataEnrichmentService.EnrichBatchAsync()` with a fetch function that queries AIOStreams metadata.

**Pipeline tracking:** `Plugin.Pipeline.SetPhase("Marvin", ...)` at each phase, `Plugin.Pipeline.Clear()` in finally.

---

## Internal Helper Tasks

These tasks do NOT implement `IScheduledTask`. They are plain classes invoked by MarvinTask or other internal triggers.

### CatalogSyncTask

**File:** `Tasks/CatalogSyncTask.cs` (738 lines)
**Purpose:** Syncs content from all configured sources (AIOStreams, Cinemeta, RSS feeds) into the catalog database.

**Sync Lock:** Acquired (`Plugin.SyncLock`)
**Delegates to:** `Tasks/CatalogProviders.cs` (859 lines) — `ICatalogProvider` implementations extracted from CatalogSyncTask.

**Phases:**

| Phase | Service | Description |
|-------|---------|-------------|
| BuildProviders | -- | Creates ICatalogProvider[] from config |
| Fetch | `ManifestFetcher` | Parallel fetch from all providers |
| Filter | `ManifestFilter` | Remove blocked/Your Files/digital release gate |
| Diff | `ManifestDiff` | Compare fetched vs DB |
| Process | `ItemPipelineService` | Lifecycle transitions for new items |
| User Catalogs | `UserCatalogSyncService` | Sync Trakt/MDBList RSS catalogs |
| Persist | `DatabaseManager` | Save last_sync_time in finally block |

**Pipeline tracking:** `Plugin.Pipeline.SetPhase("CatalogSync", "BuildProviders")`, `Plugin.Pipeline.SetPhase("CatalogSync", "Fetch")`, `Plugin.Pipeline.Clear()` in finally.

---

### RefreshTask

**Purpose:** Processes queued catalog items through the .strm writing and metadata enrichment pipeline.

**Sync Lock:** Acquired (`Plugin.SyncLock`)
**Default Trigger:** After CatalogSyncTask completes.

**6-Step Pipeline:**

| Step | Pipeline Phase | Service | Description |
|------|---------------|---------|-------------|
| 1. Collect | `"Collect"` | -- | Queries `catalog_items` with ItemState=Queued |
| 2. Write | `"Write"` | `StrmWriterService`, `NamingPolicyService` | Creates .strm files with signed resolve URLs |
| 3. Hint | `"Hint"` | `NfoWriterService.WriteSeedNfo()` | Writes minimal NFO for Emby matching |
| 4. Enrich | `"Enrich"` | `MetadataEnrichmentService.EnrichBatchAsync()` | Fetches full metadata, writes enriched NFOs |
| 5. Notify | `"Notify"` | -- | Notifies Emby (42-item batch bound), triggers scan |
| 6. Verify | `"Verify"` | `StreamProbeService` | Verifies stream URLs, renews tokens |

**Pipeline tracking:** `Plugin.Pipeline.SetPhase("Refresh", ...)` at each step, `Plugin.Pipeline.Clear()` in finally.

---

### PreCacheAioStreamsTask

**Purpose:** Pre-caches AIOStreams stream data and subtitles for items in the catalog. Runs as an internal helper triggered by every Marvin cycle (10 min).

**Behavior:**
- Randomizes batch order for API jitter
- Fetches streams + subtitles from AIOStreams (Jaccard-scored against release name)
- After main loop: probes 5 recent cache entries with HEAD+Range for dead-link detection
- Marks stale on probe failure → next cycle re-resolves

**Config defaults:**
- EnablePreCache = true
- PreCacheBatchSize = 42
- PreCacheTTLDays = 14

---

### CatalogProviders

**File:** `Tasks/CatalogProviders.cs` (859 lines)
**Purpose:** Extracted from CatalogSyncTask. Defines `ICatalogProvider` interface and implementations (AioStreamsCatalogProvider, CinemetaDefaultProvider, RssFeedProvider, UserCatalogProvider).

---

## Task Interaction Map

```
Scheduled Triggers (Emby Task Scheduler)
  |
  +-- Every 30min --> MarvinTask (sole IScheduledTask)
                        |
                        +-- Internal helpers (no IScheduledTask):
                        |     |
                        |     +-- CatalogSyncTask
                        |     |     |
                        |     |     +-- On completion --> RefreshTask
                        |     |                           |
                        |     |                           +-- Steps 1-6 (serial)
                        |     |
                        |     +-- PreCacheAioStreamsTask
                        |
                        +-- Own maintenance phases:
                              Validation -> Enrichment -> TokenRenewal -> SaveMaintenance
```

**Concurrency model:**
- CatalogSyncTask and RefreshTask use `Plugin.SyncLock` — cannot run concurrently.
- MarvinTask's own phases do NOT hold SyncLock — runs concurrently with sync tasks.
- `Plugin.Pipeline` provides visibility into which task/phase is active.
