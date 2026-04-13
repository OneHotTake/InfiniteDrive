# Sprint 147 — Doctor Removal + Integration Test

**Version:** v4.0 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 146

---

## Overview

Deprecate and remove `DoctorTask`, clean up orphaned references, and run a full integration test to verify the two-worker architecture replaces all DoctorTask functionality. Update CatalogSyncTask to hand off .strm writing to RefreshTask.

### Why This Exists

DoctorTask's responsibilities are now split between RefreshTask (6-min incremental cycle) and DeepCleanTask (18h validation cycle). Keeping DoctorTask active creates conflicts:
- Both DoctorTask and RefreshTask would try to write .strm files
- Plugin.SyncLock contention between Doctor and Refresh workers
- Confusing health panel (which worker did what?)

This sprint cleanly removes DoctorTask and adjusts CatalogSyncTask to stop writing .strm files (it should only fetch catalog data and persist items as Queued for RefreshTask to process).

---

## Phase 147A — CatalogSyncTask Adjustment

### FIX-147A-01: Remove .strm writing from CatalogSyncTask

**File:** `Tasks/CatalogSyncTask.cs` (modify)

**What:**
1. CatalogSyncTask should:
   - Fetch catalog items from AIOStreams (unchanged)
   - Persist items to catalog_items with `ItemState = Queued` (changed from Catalogued — items go straight into the Refresh pipeline)
   - NOT write .strm files — leave that to RefreshTask Step 2
   - NOT trigger `QueueLibraryScan()` — RefreshTask handles notification
   - Update ingestion_state watermark after each successful fetch
2. Remove or disable the .strm writing path:
   - Remove the `WriteStrmFilesAsync` call chain from `Execute`
   - Move `WriteStrmFileForItemPublicAsync` from `CatalogSyncTask` to a shared `Services/StrmWriterService.cs` (DiscoverService and any other callers depend on it — static method on a task class is not a stable API surface)
3. After CatalogSyncTask completes a fetch, let the 6-minute RefreshTask cycle handle processing naturally (no explicit trigger needed)

**Depends on:** Sprint 143 (RefreshTask handles writing)

### FIX-147A-02: Verify DiscoverService still works independently

**File:** `Services/DiscoverService.cs` (review, minimal changes if needed)

**What:**
1. DiscoverService writes its own .strm + .nfo for manual "Add to Library" items
2. Verify DiscoverService does not depend on DoctorTask
3. Verify DiscoverService sets `ItemState = Pinned` (not Queued)
4. Verify DiscoverService sets `nfo_status = 'Hinted'` after NFO write
5. If DiscoverService calls any DoctorTask methods, redirect to equivalent RefreshTask logic

**Depends on:** FIX-147A-01

---

## Phase 147B — DoctorTask Removal

### FIX-147B-01: Delete DoctorTask.cs

**File:** `Tasks/DoctorTask.cs` (delete)

**What:**
1. Delete the entire file (~870 lines)
2. Verify no other files reference `DoctorTask` directly (grep for "DoctorTask", "EmbyStreamsDoctor", "SummonMarvin")
3. Update "Summon Marvin" button (Improbability Drive panel only) to trigger RefreshTask instead of DoctorTask (JS change). Verify no other entrypoints call SummonMarvin or DoctorTask — grep `SummonMarvin`, `EmbyStreamsDoctor` in all .cs, .js, .html files
4. Update `StatusService.cs` — remove Doctor-specific metadata reads, use RefreshTask metadata instead
5. Remove `last_doctor_run_time` plugin_metadata key (replaced by `last_refresh_run_time`)

**Risk mitigation:** Before deletion, verify all DoctorTask functionality is covered:
- Phase 1 (Fetch & Diff): CatalogSyncTask + RefreshTask Step 1 (Collect)
- Phase 2 (Write): RefreshTask Step 2 (Write)
- Phase 3 (Adopt): RefreshTask Step 4 (Notify) + DeepCleanTask (retirement on real file detection)
- Phase 4 (Health Check): DeepCleanTask integrity pass
- Phase 5 (Clean Orphans): DeepCleanTask orphan cleanup
- Phase 6 (Token Rotation): DeepCleanTask token renewal

**Depends on:** FIX-147A-01, Sprint 145 (DeepCleanTask covers all DoctorTask phases)

### FIX-147B-02: Clean up references

**File:** Various (grep-driven cleanup)

**What:**
1. Grep for "Doctor" in all .cs files — remove/update references
2. Grep for "EmbyStreamsDoctor" in JS/HTML — update to "EmbyStreamsRefresh"
3. Remove `BuildFolderName`, `FindStrmFiles`, `WriteTierStrmAsync` if they were DoctorTask-only and are now duplicated in RefreshTask
4. Update `Services/StatusService.cs` ImprobabilityDriveStatus derivation to not reference Doctor
5. Update `.ai/REPO_MAP.md` to remove DoctorTask entry
6. Remove `doctor_last_run.json` persistence logic (replaced by refresh_run_log table)

**Depends on:** FIX-147B-01

---

## Phase 147C — Full Integration Test

### FIX-147C-01: End-to-end integration test

**What:**
1. `dotnet build -c Release` — 0 errors, 0 new warnings
2. `./emby-reset.sh` — server starts, plugin loads, no missing type errors
3. Verify DoctorTask does not appear in Scheduled Tasks
4. Verify CatalogSyncTask runs daily and persists items as Queued (not Catalogued)
5. Verify RefreshTask runs every 6 minutes and processes Queued items through the full pipeline:
   - Queued -> Written -> Notified -> Ready
6. Verify DeepCleanTask runs every 18 hours:
   - Integrity check (missing .strm detection)
   - Orphan cleanup
   - Token renewal
   - Enrichment with retry backoff
7. Verify DiscoverService "Add to Library" still works independently
8. Verify Improbability Drive shows two-worker status correctly
9. Verify "Refresh Now" button triggers RefreshTask
10. Verify "Summon Marvin" triggers RefreshTask (not the deleted DoctorTask)
11. Test token rotation: manually set strm_token_expires_at to near-expiry, verify DeepCleanTask renews
12. Test enrichment: set nfo_status='NeedsEnrich', verify DeepCleanTask processes with backoff
13. Verify RehydrationTask is unaffected by the Doctor removal (no dependency on DoctorTask)
14. Build output: single `EmbyStreams.dll`, 0 errors, 0 warnings

**Depends on:** FIX-147B-02

---

## Sprint 147 Dependencies

- **Previous Sprint:** 146 (Health Panel Upgrade)
- **Blocked By:** Sprint 146
- **Blocks:** Nothing — this is the final sprint in the Library Worker series

---

## Sprint 147 Completion Criteria

- [ ] `Tasks/DoctorTask.cs` deleted
- [ ] No references to DoctorTask or EmbyStreamsDoctor remain in .cs files
- [ ] CatalogSyncTask no longer writes .strm files (only persists catalog items)
- [ ] CatalogSyncTask no longer triggers QueueLibraryScan
- [ ] `WriteStrmFileForItemPublicAsync` moved to `Services/StrmWriterService.cs` (DiscoverService updated to call shared service)
- [ ] DiscoverService works independently (manual add-to-library)
- [ ] "Summon Marvin" triggers RefreshTask instead of DoctorTask
- [ ] All DoctorTask functionality covered by RefreshTask + DeepCleanTask
- [ ] Full pipeline works: CatalogSyncTask fetch -> RefreshTask writes -> DeepClean validates
- [ ] RehydrationTask unaffected by Doctor removal (no dependency)
- [ ] Build output is single EmbyStreams.dll (0 errors, 0 warnings)
- [ ] Server starts cleanly with no missing type errors
- [ ] .ai/REPO_MAP.md updated

---

## Sprint 147 Notes

**Files deleted:** 1 (`Tasks/DoctorTask.cs`)
**Files created:** 1 (`Services/StrmWriterService.cs` — extracted from CatalogSyncTask)
**Files modified:** ~4 (`Tasks/CatalogSyncTask.cs`, `Services/DiscoverService.cs`, `Configuration/configurationpage.js`, `Services/StatusService.cs`)
**Files reviewed:** ~3 (`Services/DiscoverService.cs`, `Plugin.cs`, `Tasks/RehydrationTask.cs`)

**Risk assessment:** MEDIUM. Deleting DoctorTask is safe only because Sprints 143-145 replicate all its functionality. The risk is in edge cases: if any functionality was missed, it surfaces during the integration test. The checklist in FIX-147C-01 explicitly maps each DoctorTask phase to its replacement.

**Design decisions:**
- CatalogSyncTask keeps its daily fetch schedule but stops writing files. It becomes a pure "data ingestion" task.
- DiscoverService is not modified to use RefreshTask — it needs immediate feedback (user clicks "Add to Library" -> item appears right away)
- "Summon Marvin" is redirected to RefreshTask. The Marvin quote API endpoint (`/EmbyStreams/Marvin`) is unchanged.

**Rollback plan:** If critical issues are found after deployment, restore DoctorTask.cs and revert CatalogSyncTask changes. RefreshTask and DeepCleanTask can coexist with DoctorTask temporarily (different task keys, SyncLock serializes access).

---
