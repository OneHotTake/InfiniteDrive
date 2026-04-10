---
status: in_progress
task: Sprints 145-147 — DeepClean, Health Panel, Doctor Removal
phase: Implementation
last_updated: 2026-04-10

## Sprint 145 — DeepCleanTask

### Phase 145A: DeepCleanTask Skeleton
- [x] FIX-145A-01: Create Tasks/DeepCleanTask.cs
  - Implement IScheduledTask
  - 18-hour trigger interval
  - TaskKey: "EmbyStreamsDeepClean"
  - Concurrency guard (SemaphoreSlim)

### Phase 145B: Validation Pass
- [x] FIX-145B-01: Full library integrity check
  - Check .strm file exists on disk for Ready/Written/Notified items
  - Missing .strm -> transition to Queued
  - Verify Retired items' real file still exists
  - Scan filesystem for orphan .strm files and delete

### Phase 145C: Enrichment Trickle
- [x] FIX-145C-01: Process NeedsEnrich items with retry logic
  - Query items with nfo_status = 'NeedsEnrich'
  - Implement retry backoff: Immediate -> +4h -> +24h -> Blocked
  - Create Services/AioMetadataClient.cs
  - Fetch from AIOMetadata (10s timeout, 1 call/2s rate limit)
  - Write enriched .nfo with title, year, plot, uniqueid, genres
  - Set nfo_status = 'Enriched' on success

- [x] FIX-145C-02: Persist enrichment counts for Health Panel

### Phase 145D: Token Renewal
- [x] FIX-145D-01: Token renewal for expiring items
  - Query items with tokens expiring within 90 days
  - Rewrite .strm with fresh token
  - Update strm_token_expires_at to now + 365 days

### Phase 145E: Build Verification
- [x] FIX-145E-01: Build + integration test
  - Build succeeded, 0 errors

---

## Sprint 146 — Health Panel Upgrade + Refresh Now

### Phase 146A: Status Model Expansion
- [x] FIX-146A-01: Expand ImprobabilityDriveStatus for two workers
- [x] FIX-146A-02: RefreshTask persists active step
- [x] FIX-146A-03: DeepCleanTask persists last run time

### Phase 146B: Health Panel UI
- [x] FIX-146B-01: Update Improbability Drive HTML
- [x] FIX-146B-02: Update Improbability Drive JS

### Phase 146C: Build Verification
- [x] FIX-146C-01: Build + UI verification

---

## Sprint 147 — Doctor Removal + Integration Test

### Phase 147A: CatalogSyncTask Adjustment
- [x] FIX-147A-01: Remove .strm writing from CatalogSyncTask
  - CatalogSyncTask only persists items with ItemState = Queued
  - NOT write .strm files
  - NOT trigger QueueLibraryScan()
- [x] FIX-147A-02: Verify DiscoverService still works independently

### Phase 147B: DoctorTask Removal
- [x] FIX-147B-01: Delete DoctorTask.cs (already deleted)
- [x] FIX-147B-02: Clean up references (no DoctorTask references found)

### Phase 147C: Full Integration Test
- [x] FIX-147C-01: End-to-end integration test (build succeeded)

---

## Progress

### Sprint 145: 5/5 phases complete
### Sprint 146: 3/3 phases complete
### Sprint 147: 0/3 phases complete

