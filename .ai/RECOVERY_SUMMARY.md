unknown
Copy
RECOVERY SUMMARY
================
Last confirmed implemented sprint: Sprint 130
Code exists for but may be incomplete: None (all Sprint 130 work committed)
Confirmed missing / never implemented: Sprints 146-148 (two-worker architecture)
Safe to re-plan from: Sprint 131 (or clarify Sprints 146-148 status)

---

## What Was Lost

### Files Deleted (untracked, never committed):
1. **Services/StreamEndpointService.cs** — /EmbyStreams/Stream proxy endpoint
2. **Services/ResolverService.cs** — /EmbyStreams/Resolve endpoint
3. **Services/M3u8Builder.cs** — HLS variant playlist builder
4. **Services/StrmWriterService.cs** — Extracted .strm writing from CatalogSyncTask
5. **Tasks/RefreshTask.cs** — 6-min incremental worker (Collect→Write→Hint→Notify→Verify)
6. **Tasks/DeepCleanTask.cs** — 18h maintenance worker (integrity, token renewal, enrichment)
7. **Models/ImprobabilityDriveStatus.cs** — Health panel status model (two-worker tracking)

### Architectural Changes Lost:
- **ItemState enum:** Would have changed to Refresh-era values (Queued, Written, Notified, Ready, NeedsEnrich, Retired, Pinned)
- **Database schema:** Would have added `refresh_run_log` table and `nfo_status` column
- **Strm URL format:** Would have changed to use /EmbyStreams/Stream or /Resolve with signed URLs
- **Health panel UI:** Two-worker status (Refresh + DeepClean) with Refresh Now button
- **DoctorTask removal:** DoctorTask deleted (already done), but two-worker architecture never implemented

---

## What Currently Exists (from inventory)

### Complete Sprints Committed:
- **Sprint 122-129:** Versioned Playback (schema, candidates, slots, materialization, playback, API, startup detection)
  - ✅ All tables and code committed
  - ✅ Latest fix: Polly.Core assembly loading crash

### Current Architecture (Doctor Era):
- **Tasks:** CatalogSyncTask, LinkResolverTask, FileResurrectionTask [DEPRECATED], EpisodeExpandTask, MetadataFallbackTask, etc.
- **ItemState:** Catalogued, Present, Resolved, Retired, Orphaned, Pinned
- **Database:** 18 tables, no refresh_run_log, no nfo_status in catalog_items
- **Health:** StatusService with Doctor-era status counts (Catalogued, Present, Resolved, Retired, Pinned, Orphaned)

### Build Status:
- **0 errors**, 1 pre-existing warning (EMBY_HAS_CONTENTSECTION_API define)
- **DoctorTask.cs deleted**, TriggerService.cs updated

---

## Recovery Options

1. **Re-implement Sprints 146-148 from scratch**
   - Estimated effort: 2-3 days
   - Requires: New ItemState enum, database schema updates, 7+ new files, UI changes

2. **Continue with current Doctor-era architecture**
   - DoctorTask already removed
   - LinkResolverTask handles stream resolution
   - Health panel may work with current state
   - Estimated effort: Minimal (stabilization only)

3. **Clarify direction with user**
   - Are Sprints 146-148 still planned?
   - Should two-worker architecture be implemented?
   - Or work on different features?

---

## Audit Files Created

1. **.ai/INVENTORY.md** — Complete file inventory (129 .cs files)
2. **.ai/SCHEMA.md** — All CREATE TABLE statements (18 tables)
3. **.ai/GAP_ANALYSIS.md** — Current vs planned code analysis

---

**Conclusion:** The codebase is at Sprint 130 (Versioned Playback complete) with a post-sprint fix. Sprints 146-148 work was uncommitted and is now lost. Recommend clarifying implementation direction before proceeding.
