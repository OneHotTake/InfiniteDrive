# Gap Analysis — Current Codebase vs Lost Sprints

**Date:** 2026-04-08

---

## Last Confirmed Implemented Sprint: **Sprint 130**

**Commit:** `4070e27 feat: Versioned Playback — Sprints 122-129 (schema, candidates, slots, materialization, playback, API, startup detection)`

**Latest Fix:** `61da793 fix: resolve Polly.Core assembly crash on startup`

---

## Files That Exist (from inventory)

| File | Exists | Sprint Context |
|-------|---------|----------------|
| Controllers/VersionSlotController.cs | ✅ YES | Sprint 122-129 |
| Models/VersionSlot.cs | ✅ YES | Sprint 122-129 |
| Models/MaterializedVersion.cs | ✅ YES | Sprint 122-129 |
| Models/VersionSnapshot.cs | ✅ YES | Sprint 122-129 |
| Models/Candidate.cs | ✅ YES | Sprint 122-129 |
| Data/VersionSlotRepository.cs | ✅ YES | Sprint 122-129 |
| Data/MaterializedVersionRepository.cs | ✅ YES | Sprint 122-129 |
| Data/SnapshotRepository.cs | ✅ YES | Sprint 122-129 |
| Data/CandidateRepository.cs | ✅ YES | Sprint 122-129 |
| Services/SignedStreamService.cs | ✅ YES | Sprint 122-129 |
| Services/VersionMaterializer.cs | ✅ YES | Sprint 122-129 |
| Services/VersionPlaybackService.cs | ✅ YES | Sprint 122-129 |
| Services/VersionPlaybackStartupDetector.cs | ✅ YES | Sprint 122-129 |
| Controllers/ItemsController.cs | ✅ YES | Pre-Sprint 130 |

---

## Files Missing (from Lost Sprints 146-148)

| File | Status | Sprint Context |
|-------|---------|----------------|
| Services/StreamEndpointService.cs | ❌ MISSING | Sprint 146-148 |
| Services/ResolverService.cs | ❌ MISSING | Sprint 146-148 |
| Services/M3u8Builder.cs | ❌ MISSING | Sprint 146-148 |
| Services/StrmWriterService.cs | ❌ MISSING | Sprint 147 |
| Tasks/RefreshTask.cs | ❌ MISSING | Sprint 146-148 |
| Tasks/DeepCleanTask.cs | ❌ MISSING | Sprint 146-148 |
| Models/ImprobabilityDriveStatus.cs | ❌ MISSING | Sprint 146 |

---

## Current State Machine (Doctor Era)

The current `ItemState` enum uses Doctor-era values:
```csharp
public enum ItemState
{
    Catalogued = 0,  // Item in DB, no .strm yet
    Present = 1,     // .strm exists, URL not resolved
    Resolved = 2,    // .strm + valid cached URL
    Retired = 3,     // Real file in library, .strm deleted
    Orphaned = 4,    // .strm exists but not in catalog
    Pinned = 5        // User-added via Discover
}
```

**Status:** This is Doctor-era architecture (Sprint 66). The two-worker Refresh/DeepClean architecture (Sprints 146-148) would use different states:
- Queued → Written → Notified → Ready pipeline
- NeedsEnrich for enrichment failures

---

## Database Schema vs Lost Sprint Requirements

**Current Schema (18 tables):**
- ✅ version_slots, candidates, version_snapshots, materialized_versions (Sprint 122-129)
- ✅ item_pipeline_log, stream_candidates (general pipeline)
- ✅ catalog_items with Doctor-era item_state column
- ❌ NO refresh_run_log table (for RefreshTask run tracking)
- ❌ NO nfo_status column in catalog_items (for enrichment status)
- ❌ NO is_pinned column in catalog_items (different PIN model)

---

## Architectural Gaps

### What's Present vs What Was Planned

| Feature | Current State | Planned (Sprints 146-148) | Gap |
|---------|---------------|-------------------------------|-----|
| Health panel (Improbability Drive) | ❌ Not in code | UI with two-worker status | Missing |
| Refresh worker (6-min incremental) | ❌ Not in code | IScheduledTask, Collect→Write→Hint→Notify→Verify pipeline | Missing |
| DeepClean worker (18h maintenance) | ❌ Not in code | IScheduledTask, integrity checks, token renewal | Missing |
| Doctor task | ❌ Deleted (correct) | Replaced by Refresh+DeepClean | Resolved |
| Strm writer service | ❌ Static in CatalogSyncTask | Extracted to StrmWriterService | Missing |
| HLS manifest builder | ❌ Not in code | M3u8Builder for variant playlists | Missing |
| Stream endpoint (proxy) | ❌ Not in code | StreamEndpointService for /EmbyStreams/Stream | Missing |
| Resolver service | ❌ Not in code | ResolverService for /EmbyStreams/Resolve | Missing |
| ItemState: Refresh era | ❌ Doctor era | Queued→Written→Notified→Ready→NeedsEnrich | Gap |
| Stream URL format | ? /EmbyStreams/play with token | /EmbyStreams/Stream or /Resolve with signed URLs | Gap |

---

## Critical Blocking Question

**Is the two-worker Refresh/DeepClean architecture still planned?**

The current codebase uses:
- `DoctorTask` (deleted)
- `LinkResolverTask` (stream URL resolution)
- `FileResurrectionTask` (missing file detection)
- Various other tasks for sync, expansion, etc.

But there's NO:
- `RefreshTask.cs` (6-min incremental worker)
- `DeepCleanTask.cs` (18h maintenance worker)
- New `ItemState` values for Refresh pipeline
- `StrmWriterService.cs` (extracted .strm writing)
- Health panel UI updates

---

## Recovery Recommendations

### Option 1: Re-implement Sprints 146-148 from scratch
- High effort
- Requires new ItemState enum and database schema changes
- Multiple new files (RefreshTask, DeepCleanTask, etc.)
- UI changes needed

### Option 2: Continue with current Doctor-era architecture
- DoctorTask was deleted but LinkResolverTask remains
- FileResurrectionTask [DEPRECATED] exists
- Health/status may already work with current state
- Lower risk, faster to stabilize

### Option 3: Clarify implementation approach with user
- Confirm whether Sprints 146-148 are still planned
- Determine if two-worker architecture is still desired
- Or if different direction should be taken

---

## Summary

**Last Confirmed Sprint:** Sprint 130 (Versioned Playback)
**Code Status:** Healthy build, 0 errors
**Lost Work:** Sprints 146-148 (Health Panel, Doctor Removal, Inline Enrich) — uncommitted files deleted
**Safe to Re-plan From:** Sprint 131 (any feature not in Sprints 122-130) or Sprint 130+1 (if planned)
