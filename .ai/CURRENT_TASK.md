---
status: complete
task: Sprint 115 Complete — Removal Pipeline
next_action: Proceed to Sprint 116 (Collection Management) or other pending sprints
# SUMMARY: Sprint 115 implements removal pipeline with grace period and Coalition rule compliance.
# PHASES: 115A (RemovalService) ✓, 115B (RemovalPipeline) ✓, 115C (RemovalTask) ✓, 115D (RemovalController) ✓
# DATABASE: Added GetItemsByGraceStartedAsync, UpdateMediaItemAsync methods to DatabaseManager
# BUILD: ✅ Success - 0 warnings, 0 errors
last_updated: 2026-04-06

---

## Sprint 115 — Removal Pipeline (v3.3)

**Status:** COMPLETE | **Build:** ✅ Success

### Sprint 115 Summary

Sprint 115 implements removal pipeline that cleans up items no longer in manifest with no enabled sources. Respects Coalition rule and user overrides with 7-day grace period.

**Components Created:**
- [x] RemovalService - Manages item removal with grace period
- [x] RemovalPipeline - Processes expired grace period items
- [x] RemovalTask - Scheduled task running every 1 hour
- [x] RemovalController - Admin API for manual removal

**Database Methods Added:**
- [x] GetItemsByGraceStartedAsync - Returns items where grace_started_at IS NOT NULL
- [x] UpdateMediaItemAsync - Updates media item including GraceStartedAt field

**Phase 115A - RemovalService**
- [x] FIX-115A-01: Create RemovalService
- MarkForRemovalAsync starts grace period (GraceStartedAt = now)
- MarkForRemovalAsync respects Saved/Blocked/EnabledSource
- RemoveItemAsync checks grace period expiration
- RemoveItemAsync respects Coalition rule (double-check)
- RemoveItemAsync deletes .strm file
- GetStrmPath resolves three separate paths (movies/, series/, anime/)
- RemovalResult record with Success/Failure static methods

**Phase 115B - RemovalPipeline**
- [x] FIX-115B-01: Create RemovalPipeline
- ProcessExpiredGraceItemsAsync gets all items with active grace period
- Checks grace period expiration
- Coalition rule check uses ItemHasEnabledSourceAsync (single JOIN query)
- Reverts items that should stay (cancel grace period)
- Removes items that should go
- Reports summary with breakdown
- Uses UpsertMediaItemAsync for item updates

**Phase 115C - RemovalTask**
- [x] FIX-115C-01: Create RemovalTask
- Implements IScheduledTask with proper Execute(CancellationToken, IProgress<double>) signature
- Implements GetDefaultTriggers() returning 1-hour interval
- Uses SyncLock to avoid conflicts with sync
- Reports progress at 0%, 100%
- Logs summary

**Phase 115D - RemovalController**
- [x] FIX-115D-01: Create RemovalController
- POST /mark starts grace period
- POST /remove removes item
- POST /process processes all expired grace items
- GET /list lists grace period items
- Uses CancellationToken.None (Emby SDK limitation)

---

## Sprint 109-121 Status

| Sprint | Status | Notes |
|---------|---------|----------|
| 109 | Complete | Database schema and models |
| 110 | Complete | Services layer with database integration |
| 111 | Complete | Sync pipeline (fetch → filter → diff → process) |
| 112 | Complete | Stream resolution and playback |
| 113 | Complete | Saved/Blocked User Actions (basic) |
| 114 | Complete | Your Files Detection |
| 115 | Complete | Removal Pipeline |
| 116-121 | Pending | Ready to begin |

---

## Files Created in Sprint 115

**Models:**
- Models/RemovalResult.cs - Removal operation result record

**Services:**
- Services/RemovalService.cs - Item removal with grace period
- Services/RemovalPipeline.cs - Grace period item processing

**Tasks:**
- Tasks/RemovalTask.cs - Scheduled cleanup task (every 1 hour)

**Controllers:**
- Controllers/RemovalController.cs - API endpoints for removal operations

**Updated Files:**
- Data/DatabaseManager.cs - Added GetItemsByGraceStartedAsync, UpdateMediaItemAsync methods

**Build Status:** ✅ Success (0 warnings, 0 errors)

**TODO:** IsPlayed check commented out due to Emby SDK property/method ambiguity. Requires further investigation of BaseItem API.
