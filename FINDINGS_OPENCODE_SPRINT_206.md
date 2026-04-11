# Sprint 206 Findings — Data & Logic Validation

**Generated:** 2026-04-11  
**Sprint:** 206 (Data & Logic Validation)  
**Status:** Research Complete  
**Critical Issues Found:** 6

---

## Executive Summary

Sprint 206 validation revealed that **Sprints 202-205 were only partially completed**. The validation plan assumed implementations that don't exist in the codebase. Key missing features include per-user save tracking, native Emby channel, and complete admin block/unblock functionality.

---

## Phase A: User Save/Unsave Workflow

### Current Implementation

| Component | Status | Location |
|-----------|--------|----------|
| Save endpoint | Exists | `Services/DiscoverService.cs:665-792` |
| Unsave endpoint | **MISSING** | No REST endpoint exists |
| Per-user tracking | **MISSING** | No `user_item_saves` table |
| Repository layer | **MISSING** | No `ISaveRepository` / `UserSaveRepository` |

### Findings

1. **Global Save System**: Saves are stored as global flags in `catalog_items` (columns: `pin_source`, `pinned_at`, `first_added_by_user_id`) and `media_items` (columns: `saved`, `saved_at`, `saved_by`, `save_reason`)

2. **No Per-User Tracking**: All users see the same saved items. The `first_added_by_user_id` column records attribution but doesn't enable per-user visibility.

3. **Missing Unsave Functionality**: No REST endpoint exists for users to unsave items. The `UserService` was deleted in Sprint 205.

4. **Saved Folder Implementation**: Uses `media_items.saved` boolean flag - same items shown to all users.

### Code Paths Traced

- **Save**: `POST /InfiniteDrive/Discover/AddToLibrary` → `DiscoverService.AddToLibrary()` → `CatalogItem` with `ItemState.Pinned` → `DatabaseManager.UpsertCatalogItemAsync()`
- **Unsave**: No endpoint exists

### Issues Found

- `media_items.saved` is global, not per-user
- No way for users to remove saved items
- Duplicate saves overwrite timestamps (no duplicate detection)

---

## Phase B: Admin Block/Unblock Workflow

### Current Implementation

| Component | Status | Location |
|-----------|--------|----------|
| Block endpoint | **MISSING** | No `POST /InfiniteDrive/Admin/Block` |
| Unblock endpoint | Exists | `Services/AdminService.cs:113-135` |
| Blocked items view | Exists | `GET /InfiniteDrive/Admin/BlockedItems` |
| Block affects .strm | No | Files remain on disk |
| Block affects Browse/Search | No | Separate `discover_catalog` table |

### Findings

1. **Incomplete API**: Only unblock endpoint exists. Admins cannot block items via UI/API.

2. **Two Blocking Systems**:
   - Admin block: `catalog_items.blocked_at` / `catalog_items.blocked_by`
   - Automatic enrichment block: `catalog_items.nfo_status = 'Blocked'`

3. **Dead Code**: `SavedService.BlockItemAsync()` and `UnblockItemAsync()` exist but have no callers.

4. **No Filtering**: Blocked items still appear in Browse/Search results.

### Code Paths Traced

- **Unblock**: `POST /InfiniteDrive/Admin/UnblockItems` → `DatabaseManager.UnblockItemAsync()` → Clears `blocked_at`, resets `nfo_status`
- **Block**: No endpoint exists

### Issues Found

- Missing block endpoint
- UI hint is misleading ("Items blocked after failing enrichment 3 times")
- Blocking doesn't affect Browse/Search results
- Blocking doesn't delete .strm files

---

## Phase C: Marvin Cleanup Tasks

### Current Implementation

| Component | Status | Location |
|-----------|--------|----------|
| Orphaned saves cleanup | **MISSING** | No `user_item_saves` table |
| Orphaned flags cleanup | **MISSING** | No mechanism exists |
| Deleted items cleanup | **MISSING** | No mechanism exists |
| Filesystem cleanup | Exists | `MarvinTask.CleanupOrphanFilesAsync()` |

### Findings

1. **Filesystem-Only Cleanup**: `MarvinTask.cs` only cleans up orphan .strm/.nfo files on disk.

2. **No Database Cleanup**: No cleanup of orphaned database rows or flags.

3. **State Transitions**: `MarvinTask.cs` performs state transitions for missing files but doesn't clean up database records.

### Code Paths Traced

- **CleanupOrphanFilesAsync**: `MarvinTask.cs:198-267` → Scans library directories → Deletes orphan .strm files → Deletes empty folders

### Issues Found

- No cleanup of orphaned saved/blocked flags
- No cleanup of deleted items
- Orphaned flags remain indefinitely

---

## Phase D: User/Admin Actions

### User Actions (Non-Admin Accessible)

| Action | Endpoint | Status |
|--------|----------|--------|
| Browse catalog | `GET /InfiniteDrive/Discover/Browse` | Exists (un-gated) |
| Search catalog | `GET /InfiniteDrive/Discover/Search` | Exists (un-gated) |
| Item detail | `GET /InfiniteDrive/Discover/Detail` | Exists (un-gated) |
| Add to library | `POST /InfiniteDrive/Discover/AddToLibrary` | Exists (un-gated) |
| View Saved folder | **MISSING** | No native channel exists |
| Unsave item | **MISSING** | No endpoint exists |

### Admin Actions

| Action | Endpoint | Status |
|--------|----------|--------|
| View blocked items | `GET /InfiniteDrive/Admin/BlockedItems` | Exists |
| Unblock items | `POST /InfiniteDrive/Admin/UnblockItems` | Exists |
| Block items | **MISSING** | No endpoint exists |

### Findings

1. **Missing Channel**: `InfiniteDriveChannel.cs` (planned for Sprint 204) was never created.

2. **Parental Filtering**: `ApplyParentalFilter()` exists in `DiscoverService.cs:1122-1136` but is **never invoked**.

3. **Dead Code**: `SavedService.BlockItemAsync()` and `UnblockItemAsync()` exist but have no callers.

---

## Phase E: Edge Cases & Concurrency

### Edge Cases Identified

| Scenario | Current Behavior | Issue |
|----------|------------------|-------|
| Duplicate save | Overwrites timestamps | No duplicate detection |
| Multiple users save same item | Global flag, last write wins | No per-user tracking |
| Unsave non-saved item | Silent no-op | Acceptable |
| Unblock non-blocked item | Silent no-op | Acceptable |
| Admin block + user save race | Inconsistent state possible | No transaction/locking |

### Parallelization Opportunities

| Component | Current | Opportunity |
|-----------|---------|-------------|
| `MarvinTask.cs` | Sequential loop | Could use `Parallel.ForEach` |
| `AdminService.UnblockItems` | Sequential loop | Could batch in single SQL UPDATE |
| `DiscoverService.AddToLibrary` | Sequential writes | Could parallelize .strm + DB writes |

---

## Phase F: Build Verification

### Build Status: **FAILED** (14 errors)

**Errors in `DiscoverService.cs`:**

1. `StatusService.RequireAuthenticated` doesn't exist (6 occurrences)
2. Nullability mismatches (6 occurrences)
3. `DiscoverCatalogEntry.RatingLabel` property missing
4. Type conversion error (`long?` to `string`)

**Build Command:** `dotnet build -c Release`

**Impact:** The codebase doesn't compile. This is a critical blocker for any deployment.

---

## Phase G: Dead Code Scan

### Dead Code Status: **CLEAN** (except SavedService methods)

| Pattern | Expected | Actual | Status |
|---------|----------|--------|--------|
| `UserPinRepository` | 0 matches | 0 matches | ✅ Clean |
| `IPinRepository` | 0 matches | 0 matches | ✅ Clean |
| `UserItemPin` | 0 matches | 0 matches | ✅ Clean |
| `DoctorTask` | 0 matches | 0 matches | ✅ Clean |
| `DeepCleanTask` | 0 matches | 0 matches | ✅ Clean |
| `InfiniteDriveDeepClean` | 0 matches | 0 matches (except .ai/ plans) | ✅ Clean |
| `es-tab-content-discover` | 0 matches | 0 matches (except backup files) | ✅ Clean |
| `es-tab-content-mypicks` | 0 matches | 0 matches | ✅ Clean |
| `es-tab-content-mylists` | 0 matches | 0 matches | ✅ Clean |

### Residual CSS

`.es-discover-*` CSS classes remain in `configurationpage.html` (lines 72-95) but are unused since the Discover tab was removed.

### Dead Code Found

- `SavedService.BlockItemAsync()` (lines 80-99) - no callers
- `SavedService.UnblockItemAsync()` (lines 106-125) - no callers

---

## Phase H: Action Outcomes Matrix

| Action | Expected Outcome | Actual Outcome | Status |
|--------|------------------|----------------|--------|
| User saves item | Per-user save row created | Global flag updated | ❌ |
| User unsaves item | Row deleted | No endpoint exists | ❌ |
| Admin blocks item | Item hidden, .strm deleted | No endpoint exists | ❌ |
| Admin unblocks item | Item restored, .strm restored | Flag cleared, enrichment reset | ⚠️ |
| User browses catalog | Filtered by parental rating | No filtering applied | ❌ |
| User searches catalog | Filtered by parental rating | No filtering applied | ❌ |
| Marvin runs | Orphaned data cleaned | Only filesystem cleanup | ⚠️ |

**Legend:** ✅ Pass, ⚠️ Partial, ❌ Fail

---

## Phase I: Verification Checklist

### Grep Validation

| Check | Expected | Actual | Status |
|-------|----------|--------|--------|
| No `UserPinRepository` references | 0 | 0 | ✅ |
| No `DoctorTask` references | 0 | 0 | ✅ |
| No user tab buttons in HTML | 0 | 0 | ✅ |
| No user tab bodies in HTML | 0 | 0 | ✅ |
| No user tab CSS | 0 | 0 (except .es-discover-*) | ⚠️ |

### Build Check

- [ ] `dotnet build -c Release` passes
- **Status:** ❌ FAILED (14 errors)

---

## Critical Issues Summary

### 1. Build Fails (P0 - Blocker)

**Issue:** 14 compilation errors in `DiscoverService.cs`

**Impact:** Codebase doesn't compile, cannot deploy

**Fix Required:** 
- Add missing `StatusService.RequireAuthenticated` method
- Fix nullability mismatches
- Add missing `DiscoverCatalogEntry.RatingLabel` property
- Fix type conversion error

### 2. Missing Block Endpoint (P0 - Feature Gap)

**Issue:** No `POST /InfiniteDrive/Admin/Block` endpoint

**Impact:** Admins cannot block items via UI/API

**Fix Required:** Implement block endpoint in AdminService

### 3. No Per-User Save Tracking (P1 - Feature Gap)

**Issue:** All users see same saved items

**Impact:** Poor user experience, no personalization

**Fix Required:** 
- Create `user_item_saves` table
- Implement `ISaveRepository` / `UserSaveRepository`
- Update SaveService to use per-user tracking

### 4. Missing Unsave Functionality (P1 - Feature Gap)

**Issue:** No REST endpoint for unsave

**Impact:** Users cannot remove saved items

**Fix Required:** Implement unsave endpoint

### 5. Blocking Doesn't Work (P1 - Feature Gap)

**Issue:** Blocked items still appear in Browse/Search

**Impact:** Ineffective content moderation

**Fix Required:** 
- Add blocked filter to `discover_catalog` queries
- Or join with `catalog_items` to check blocked status

### 6. Dead Code in SavedService (P2 - Code Quality)

**Issue:** `BlockItemAsync`/`UnblockItemAsync` have no callers

**Impact:** Code bloat, confusion

**Fix Required:** Remove dead code or implement missing endpoints

---

## Recommendations

### Immediate Actions (Sprint 207)

1. **Fix build errors** - Codebase must compile
2. **Implement block endpoint** - Critical admin functionality
3. **Add blocking filter to Browse/Search** - Current blocking is ineffective

### Short-term Actions (Sprint 208)

1. **Implement per-user save tracking** - Create `user_item_saves` table
2. **Add unsave functionality** - REST endpoint for users
3. **Implement parental rating filtering** - Code exists but isn't invoked

### Medium-term Actions (Sprint 209+)

1. **Implement native Emby channel** - `InfiniteDriveChannel.cs`
2. **Add database cleanup to MarvinTask** - Orphaned flag cleanup
3. **Remove dead code** - Clean up SavedService methods
4. **Add transaction support** - Prevent race conditions

---

## Files Analyzed

**Services:**
- `Services/DiscoverService.cs` - Save/unsave, browse/search endpoints
- `Services/AdminService.cs` - Block/unblock endpoints
- `Services/SavedService.cs` - Save/unsave/block/unblock methods (some dead code)
- `Services/SavedBoxSetService.cs` - Saved folder population
- `Services/StatusService.cs` - Status endpoints

**Data:**
- `Data/DatabaseManager.cs` - Database operations
- `Data/Schema.cs` - Schema definitions

**Models:**
- `Models/CatalogItem.cs` - Catalog item model
- `Models/MediaItem.cs` - Media item model with saved/blocked flags

**Tasks:**
- `Tasks/MarvinTask.cs` - Cleanup and reconciliation

**Configuration:**
- `Configuration/configurationpage.html` - UI (5 tabs: Setup, Overview, Settings, Content, Marvin)
- `Configuration/configurationpage.js` - UI logic

---

## Conclusion

Sprint 206 validation revealed that the implementation is significantly behind the plan. The core per-user save system was never implemented, the native Emby channel doesn't exist, and the admin block/unblock workflow is incomplete. The build also fails with compilation errors.

**This is not a validation gap - it's an implementation gap.** The planned features were designed but not implemented. Sprint 207 should focus on fixing the build and implementing the critical missing features.

---

**Research Conducted By:** Opencode AI Assistant  
**Sprint Plan:** `.ai/sprints/sprint-206.md`  
**Codebase:** `/home/onehottake/Projects/emby/InfiniteDrive`
