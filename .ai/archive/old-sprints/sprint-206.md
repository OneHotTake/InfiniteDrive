# Sprint 206 — Data & Logic Validation

**Version:** v1.0 | **Status:** Active | **Risk:** LOW | **Depends:** Sprints 202, 203, 204, 205
**Owner:** QA/Validation | **Target:** Post-Sprint 205 Verification | **PR:** N/A

---

## Overview

Sprints 202–205 implement the admin UX restructure and discover-as-channel. This sprint validates that the actual system behavior matches the documented intent. Not a UI checklist ("does the button exist?"), but a **logic validation** that confirms the subsystems work end-to-end: users can save/unsave items, admins can block/unblock, Marvin cleanup enforces eventual consistency.

**Problem statement:** Implementation is complete, but we need assurance that the interactions between users, admins, and the background task (Marvin) preserve data integrity and behave predictably under edge cases.

**Why now:** After Sprints 202–205 ship, before declaring the feature set complete.

**High-level approach:** Five phases of validation. Each tests a critical user journey (save, unsave, block, unblock, cleanup) by tracing the code path, confirming the data writes, and asserting the expected state afterward.

### What the Research Found

- `ItemState.Pinned` enum (Sprint 202 decision) — tracks items users have explicitly saved.
- `user_item_saves` table (Sprint 202 creates) — per-user save tracking.
- `media_items.saved` column (exists) — global flag indicating "at least one user has saved this".
- Blocking mechanism (Sprint 203/204) — admin can block items; blocked items disappear from Browse/Search but remain in Saved folder with "Blocked" badge.
- Marvin task (Sprint 202 renames DeepCleanTask → MarvinTask) — eventual consistency cleanup.

### Breaking Changes

None — this is validation only, no code changes.

### Non-Goals

- ❌ Fix bugs found (report them, defer to future sprints).
- ❌ Add missing features (validation only).
- ❌ Performance tuning or load testing.

---

## Phase A — User Save/Unsave Logic

### FIX-206A-01: Trace user save workflow

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**User story:** Non-admin user clicks "Save" on an item in Channel → Lists.

**Trace the code path:**
1. Start: `Services/InfiniteDriveChannel.cs` — user clicks item
2. UI calls `POST /InfiniteDrive/Discover/AddToLibrary?id=<imdb_id>`
3. Endpoint: `Services/DiscoverService.cs:677` (un-gated in Sprint 204)
   - Read `userId` from auth context
   - Fetch item from `DatabaseManager.GetCatalogItemByImdbIdAsync(imdbId)`
   - Call `ISaveRepository.AddSave(userId, itemId, SaveSource.Discover)`
4. `UserSaveRepository.AddSave()` writes row to `user_item_saves` table: `(user_id, media_item_id, saved_at, save_source)`
5. Does the code also set `media_items.saved = true` globally? YES or NO?

**Assertions:**
- `user_item_saves` row created for the user
- `media_items.saved` reflects correct state (single user saved → true; all unsaved → false)
- `ItemState` is NOT changed (save ≠ state transition)
- Response returns success (200 or 201)

**If logic is wrong:**
- Multiple users saving same item → `media_items.saved` inconsistency
- User unsaves → other users' saves still visible (correct) BUT `media_items.saved` might incorrectly become false

---

### FIX-206A-02: Trace user unsave workflow

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**User story:** User clicks "Unsave" on a saved item.

**Trace the code path:**
1. UI calls `POST /InfiniteDrive/User/Saves/Remove?id=<imdb_id>`
2. Endpoint: `Services/UserService.cs` (confirm location after Sprint 202 rename)
   - Read `userId` from auth context
   - Call `ISaveRepository.RemoveSave(userId, mediaItemId)`
3. `UserSaveRepository.RemoveSave()` deletes row from `user_item_saves` where `user_id = userId AND media_item_id = mediaItemId`
4. After deletion, does code check remaining saves and update `media_items.saved`?
   - Query: `SELECT COUNT(*) FROM user_item_saves WHERE media_item_id = mediaItemId`
   - If count == 0: set `media_items.saved = false`
   - If count > 0: keep `media_items.saved = true`

**Assertions:**
- Row deleted from `user_item_saves`
- If User A unsaves but User B still has it saved: `media_items.saved` stays true
- If last user unsaves: `media_items.saved` becomes false
- User A's "Saved" folder does NOT show the item after unsave
- User B's "Saved" folder still shows it (if they saved it)

**If logic is wrong:**
- User A unsaves → item disappears from User B's saves (data loss)
- Last user unsaves but `media_items.saved` stays true (orphaned state)

---

## Phase B — Admin Block/Unblock Logic

### FIX-206B-01: Trace admin block workflow

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**Admin story:** Admin opens Content tab, clicks "Block" on an item (e.g., for nudity).

**Trace the code path:**
1. UI calls admin endpoint to block item (confirm location in Sprint 203: probably `POST /InfiniteDrive/Admin/Block?id=<imdb_id>`)
2. Endpoint marks item as blocked (confirm mechanism: separate table? column on `catalog_items`? `media_items`?)
3. After blocking:
   - Item's .strm file is deleted from disk (aggressive removal)
   - Item's directory is removed if empty
   - Emby library scan triggered so Emby no longer sees it
4. Does blocking affect `user_item_saves` rows?
   - Save rows should remain (user's save persists)
   - But item should display "Blocked" badge in Channel "Saved" folder

**Assertions:**
- .strm file deleted from filesystem
- Item no longer appears in Channel → Lists → Browse/Search
- Item still appears in Channel → Saved folder (for users who saved it) with "Blocked" badge
- User can still click "Unsave" on a blocked item
- `user_item_saves` rows are preserved (no cascading delete)
- `media_items.saved` state is preserved

**If logic is wrong:**
- Blocking deletes save rows → user loses their save record (bad)
- Blocking doesn't remove .strm → item still visible to Emby (security issue)
- Blocked item doesn't show "Blocked" badge → user doesn't know why it's blocked

---

### FIX-206B-02: Trace admin unblock workflow

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**Admin story:** Admin decides to unblock an item (reviewed content, safe to show).

**Trace the code path:**
1. UI calls `POST /InfiniteDrive/Admin/Unblock?id=<imdb_id>`
2. Endpoint clears blocked flag (confirm mechanism)
3. Item should re-appear in Browse/Search if it meets other filters
4. Does unblocking trigger .strm re-write?
   - Item was previously written to disk (ItemState → Queued → Present → Resolved)
   - After unblock, should .strm be restored, or re-synced on next refresh?

**Assertions:**
- Item no longer marked as blocked
- Item reappears in Channel → Lists on next refresh
- Users who saved it still see it in Saved folder (without "Blocked" badge)
- `user_item_saves` rows unchanged
- If `.strm` was deleted, re-create it or trigger refresh

**If logic is wrong:**
- Unblocking doesn't restore item state → permanently broken
- Item reappears but has stale cached URL → playback fails

---

## Phase C — Marvin Cleanup: Orphaned Saves

### FIX-206C-01: Trace orphaned save cleanup

**File:** Code analysis (no changes)
**Estimated effort:** L
**What:**

**Marvin story:** Background task runs and finds inconsistency.

**Scenario:** A user deleted their Emby account (or save row was corrupted), but `user_item_saves` row still exists.

**Trace the Marvin cleanup path:**
1. Marvin queries: `SELECT * FROM user_item_saves WHERE user_id NOT IN (SELECT user_id FROM emby_users)`
2. For each orphaned row:
   - Delete the `user_item_saves` row
   - Check remaining saves: `SELECT COUNT(*) FROM user_item_saves WHERE media_item_id = X`
   - If count == 0: set `media_items.saved = false`
   - Log: "Cleaned up orphaned save for deleted user"

**Assertions:**
- Orphaned `user_item_saves` rows are deleted
- `media_items.saved` is correct after cleanup
- Marvin logs the action

**If logic is wrong:**
- Orphaned rows accumulate → database bloat
- `media_items.saved = true` despite no valid users having saved → misleading state

---

### FIX-206C-02: Trace orphaned `media_items.saved` cleanup

**File:** Code analysis (no changes)
**Estimated effort:** L
**What:**

**Scenario:** A bug caused `media_items.saved = true` but no rows in `user_item_saves`.

**Trace Marvin cleanup path (reverse direction):**
1. Marvin queries: `SELECT * FROM media_items WHERE saved = true AND media_item_id NOT IN (SELECT DISTINCT media_item_id FROM user_item_saves)`
2. For each orphaned flag:
   - Set `media_items.saved = false`
   - Log: "Cleaned up orphaned save flag (no saves exist)"

**Assertions:**
- Orphaned `saved = true` flags are corrected
- No false positives (items with actual saves are left alone)

**If logic is wrong:**
- Orphaned flags remain true → incorrect "Saved" count display
- Legitimate saves are cleared (false positive cleanup)

---

### FIX-206C-03: Trace orphaned items (deleted from catalog)

**File:** Code analysis (no changes)
**Estimated effort:** L
**What:**

**Scenario:** Item is deleted from catalog (catalog sync removes it), but users still have it saved.

**Trace Marvin cleanup path:**
1. Marvin queries: `SELECT * FROM user_item_saves WHERE media_item_id NOT IN (SELECT media_item_id FROM media_items)`
2. For each orphaned save:
   - Delete the `user_item_saves` row (item is gone, can't save it anymore)
   - Log: "Cleaned up save for deleted item"

**Assertions:**
- Saves for deleted items are cleaned up
- User won't see "404" when opening Saved folder
- Database stays consistent

**If logic is wrong:**
- Dead save rows accumulate
- User sees "Saved" item, clicks it, gets 404

---

## Phase D — User & Admin Action Validation

### FIX-206D-01: Trace all user actions in Channel

**File:** Code analysis (no changes)
**Estimated effort:** L
**What:**

**User actions in InfiniteDriveChannel:**
1. Browse Lists folder → fetches via `DatabaseManager.GetDiscoverCatalogAsync()`
2. Search within Lists → `DatabaseManager.SearchDiscoverCatalogAsync()`
3. View item detail → `DiscoverService.GET /Discover/Detail`
4. Save item → `DiscoverService.POST /Discover/AddToLibrary`
5. Unsave item → `UserService.POST /InfiniteDrive/User/Saves/Remove`
6. View Saved folder → queries `ISaveRepository.GetSaves(userId)`
7. Parental filter applied at each step

**Trace each action:**
- Confirm endpoint exists and is un-gated (non-admin)
- Confirm parental rating filter is applied
- Confirm response format is correct (JSON, ChannelItem, etc.)
- Confirm no admin-only guard remains

**Assertions:**
- All 7 user actions are traceable end-to-end
- No 403/401 responses for non-admin users
- Each action produces expected side effects (save writes row, unsave deletes row)
- Parental filter blocks R-rated items for PG-13 user

**If logic is wrong:**
- User sees 403 (gating not removed)
- User can see R-rated content (filter not applied)
- Action succeeds but DB write didn't happen

---

### FIX-206D-02: Trace all admin actions in Content tab

**File:** Code analysis (no changes)
**Estimated effort:** L
**What:**

**Admin actions in Content tab (Sprint 203):**
1. View blocked items table → query `blocked_catalog_items` or similar
2. Block an item → `POST /InfiniteDrive/Admin/Block?id=<imdb_id>`
   - Marks item as blocked
   - Deletes .strm file
   - Triggers library scan
3. Unblock an item → `POST /InfiniteDrive/Admin/Unblock?id=<imdb_id>`
   - Clears blocked flag
   - Re-syncs item (or re-creates .strm)
4. Force refresh item → `POST /InfiniteDrive/Admin/ForceRefresh?id=<imdb_id>`
5. View item audit log (who added it via Discover)

**Trace each action:**
- Confirm endpoint exists and is admin-only
- Confirm action's side effects (file deletion, DB updates, scan trigger)
- Confirm error handling (item not found, already blocked, etc.)

**Assertions:**
- All admin actions are traceable
- Only admins can block/unblock (403 for non-admin)
- Block deletes .strm; unblock restores it
- Blocked items show in admin view but not in user Browse

**If logic is wrong:**
- Non-admin can block items (security issue)
- Block doesn't delete .strm (item still playable)
- Unblock doesn't restore .strm (item broken)

---

### FIX-206D-03: Validate lookup/reference hierarchy

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**Data hierarchy to validate:**
```
catalog_items (raw source entries: TMDB, IMDB, AioStreams)
  ↓
media_items (deduplicated: one Batman Begins)
  ↓
user_item_saves (per-user save tracking)
  ↓
Channel "Saved" folder (current user's saves only)
```

**Trace the lookup chain for a save/unsave operation:**
1. User saves item with IMDB id → look up in `catalog_items`
2. Resolve to `media_item_id` (the deduplicated ID)
3. Write `user_item_saves` row with `media_item_id`, not catalog_item_id
4. Update `media_items.saved` global flag
5. On unsave, reverse the chain

**Fallback hierarchy:**
1. Primary ID: IMDB → look up in `catalog_items` by imdb_id
2. Fallback: TMDB → look up in `catalog_items` by tmdb_id
3. Fallback: AioStreams key → look up in `catalog_items` by aio_source_key
4. If none match → 404 (item doesn't exist)

**Assertions:**
- Lookups resolve correctly (IMDB id → catalog_item → media_item_id)
- Saves are written to `media_item_id`, not catalog_item_id
- Multi-source items (TMDB + IMDB) map to ONE media_item
- Fallback chain is tried in order; stops at first match
- 404 only when NO id matches

**If logic is wrong:**
- Saves are written to wrong ID → lookups fail
- Same item from different sources creates duplicate saves (data bloat)
- Fallback doesn't work → items "lost" if primary ID missing

---

### FIX-206D-04: Validate blocking interactions with all surfaces

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**When item is blocked, confirm it:**
1. Disappears from Channel → Lists → Browse results
2. Disappears from Channel → Lists → Search results
3. Returns 404 if user tries to view detail (`GET /Discover/Detail`)
4. Returns 403 if user tries to save (`POST /Discover/AddToLibrary`)
5. Still appears in Channel → Saved (for users who saved it)
6. Shows "Blocked" badge in Saved folder
7. User can unsave it from Saved folder
8. Unsave works even if blocked

**When item is unblocked, confirm it:**
1. Reappears in Channel → Lists → Browse
2. Reappears in Channel → Lists → Search
3. Returns 200 for detail view
4. Returns 200 for save (if user wants to save again)
5. Saved items no longer show "Blocked" badge
6. Playback works (cached URL is valid or re-resolved)

**Assertions:**
- Blocking is applied consistently across all surfaces
- Unblocking completely reverses the state
- User can always unsave blocked items
- No orphaned state (partially blocked)

**If logic is wrong:**
- Item blocked in Browse but still saveable (inconsistent)
- Unblock doesn't restore playback (broken state)
- User forced to unsave to get rid of "Blocked" badge (poor UX)

---

## Phase E — Edge Cases & Concurrency

### FIX-206E-01: Concurrent save/unsave by multiple users

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**Scenario:** User A and User B both save item X simultaneously (race condition).

**Trace logic:**
1. User A: `AddSave(userA, itemX)` starts, inserts row, then updates `media_items.saved = true`
2. User B: `AddSave(userB, itemX)` starts, inserts row, then updates `media_items.saved = true`
3. Either order should result in `media_items.saved = true` (idempotent)

**Assertions:**
- No duplicate rows in `user_item_saves` (unique constraint on (user_id, media_item_id))
- `media_items.saved` is correct regardless of order
- Both saves persist

**If logic is wrong:**
- Duplicate save rows exist (constraint violated)
- Race condition causes `media_items.saved` to be wrong

---

### FIX-206D-02: Unsave while item is blocked

**File:** Code analysis (no changes)
**Estimated effort:** S
**What:**

**Scenario:** Item is saved. Admin blocks it. User clicks "Unsave" on the blocked item.

**Trace logic:**
1. Blocked item still appears in Saved folder (by design)
2. User clicks Unsave
3. `RemoveSave(userId, itemId)` deletes the row
4. Item disappears from Saved folder
5. If no other users have saved it: `media_items.saved = false`

**Assertions:**
- Unsave works even for blocked items
- Block status doesn't prevent unsave
- Saves are independent of block status

---

### FIX-206D-03: Block while users are viewing Saved folder

**File:** Code analysis (no changes)
**Estimated effort:** S
**What:**

**Scenario:** User A is browsing Channel → Saved. User B (admin) blocks an item that User A just opened.

**Expected behavior:**
- User A's list is now stale (but this is fine — next refresh shows updated state)
- If User A clicks "Unsave" before refreshing, unsave still works
- If User A clicks "View Details" and item is blocked, show "Blocked" label

**Assertions:**
- No crash or data loss from race
- Next refresh shows correct state
- Unsave is always possible

---

### FIX-206I-02: Extended code review checklist

**User Actions:**
| Check | Expected | Status |
|---|---|---|
| `/Discover/Browse` is un-gated (non-admin) | YES | Verify no `AdminGuard` |
| `/Discover/Search` is un-gated | YES | Verify no `AdminGuard` |
| `/Discover/Detail` is un-gated | YES | Verify no `AdminGuard` |
| `/Discover/AddToLibrary` is un-gated | YES | Verify no `AdminGuard` |
| `/InfiniteDrive/User/Saves` endpoint exists | YES | Confirm location |
| `/InfiniteDrive/User/Saves/Remove` endpoint exists | YES | Confirm location |
| Parental filter is applied to Browse | YES | Confirm query-level filter |
| Parental filter is applied to Search | YES | Confirm query-level filter |
| Parental filter is applied to Detail | YES | Confirm before response |
| Parental filter is applied to AddToLibrary | YES | Confirm before DB write |

**Admin Actions:**
| Check | Expected | Status |
|---|---|---|
| `/Admin/Block` exists and is admin-only | YES | Verify auth guard |
| `/Admin/Unblock` exists and is admin-only | YES | Verify auth guard |
| `/Admin/Block` deletes .strm file | YES | Trace file I/O |
| `/Admin/Unblock` restores .strm or re-syncs | YES | Trace restoration |
| Block does NOT cascade-delete saves | YES | Confirm no FK constraint |
| Unblock allows re-saving | YES | Confirm flag cleared |

**Data Integrity:**
| Check | Expected | Status |
|---|---|---|
| `AddSave()` sets `media_items.saved = true` | YES | Global flag |
| `RemoveSave()` checks remaining saves before clearing flag | YES | Idempotent |
| `user_item_saves` has unique constraint (user_id, media_item_id) | YES | Prevents dups |
| `user_item_saves` has foreign key to media_items | YES | Referential integrity |
| `media_items.saved` is Boolean, not per-user | YES | Global state |
| Channel "Saved" filters on current user_id | YES | Per-user view |
| Channel "Saved" includes blocked items with badge | YES | UX clarity |

**Marvin Cleanup:**
| Check | Expected | Status |
|---|---|---|
| Orphaned saves cleanup (no Emby user) | YES | Deletes row + syncs flag |
| Orphaned flag cleanup (no saves) | YES | Sets flag to false |
| Deleted item cleanup | YES | Deletes save rows |
| All three cleanup passes run in parallel | YES | `Task.WhenAll()` |
| Marvin logs all cleanup actions | YES | Trace log statements |

**Dead Code & Stale References:**
| Check | Expected | Status |
|---|---|---|
| `UserPinRepository` deleted or renamed | YES | No `IPinRepository` refs |
| `UserItemPin` model deleted or renamed | YES | No `PinSource` refs |
| Old `/User/Pins` endpoint deleted or 308 shim | YES | Confirm one or other |
| `[Obsolete(...DoctorTask...)]` updated to MarvinTask | YES | Grep for "DoctorTask" = 0 |
| "Run Doctor Now" button deleted from HTML | YES | Grep HTML for button = 0 |
| Doctor card deleted from configurationpage.html | YES | No `#tab-content-doctor` |
| User tab buttons hidden or deleted (Sprint 205) | YES | No Discover/Picks/Lists buttons |
| User tab bodies removed (Sprint 205) | YES | Grep HTML for bodies = 0 |

---

### FIX-206I-03: Manual end-to-end test — user workflow

1. **Non-admin user A logs in.**
2. Open Channel → Lists → Top Movies → Save "Batman Begins"
   - Confirm: row in `user_item_saves` for User A
   - Confirm: `media_items.saved = true`
3. Open Channel → Saved → See "Batman Begins"
4. Non-admin user B logs in (different user).
5. Open Channel → Saved → Does NOT see "Batman Begins" (A's saves, not B's)
6. User B opens Channel → Lists → Top Movies → Save "Batman Begins"
   - Confirm: row in `user_item_saves` for User B
   - Confirm: `media_items.saved = true` (already true, now two users saved)
7. User A unsaves "Batman Begins"
   - Confirm: row deleted from `user_item_saves` for User A
   - Confirm: `media_items.saved` still true (User B has it saved)
8. User A's Saved folder → "Batman Begins" gone
9. User B's Saved folder → "Batman Begins" still there
10. User B unsaves
    - Confirm: `media_items.saved = false` (no more saves)

---

## Phase F — Parallelization & Performance

### FIX-206F-01: Validate parallelization opportunities

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**Identify operations that can run in parallel:**
1. When user saves item:
   - Insert `user_item_saves` row (DB write)
   - Update `media_items.saved` flag (DB write)
   - Return response to user
   - These 3 operations can be parallelized with `await Task.WhenAll()` or similar

2. When admin blocks item:
   - Delete .strm file from disk (I/O)
   - Delete empty parent directories (I/O)
   - Update `media_items` blocked flag (DB write)
   - Trigger Emby library scan (API call)
   - All 4 can run in parallel

3. Marvin cleanup:
   - Orphaned saves cleanup (DB query + delete)
   - Orphaned flags cleanup (DB query + update)
   - Deleted items cleanup (DB query + delete)
   - All 3 can run in parallel

**Trace the code:**
- Check if `SaveAsync()` awaits both DB operations sequentially or in parallel
- Check if block operation uses `WhenAll()` for file delete + flag update
- Check if Marvin cleanup runs all three cleanup passes in parallel

**Assertions:**
- Multi-step operations use `Task.WhenAll()` where possible
- No unnecessary sequential awaits
- Marvin cleanup runs all three passes concurrently

**If logic is wrong:**
- User blocking is slow (sequential file + DB ops)
- Marvin cleanup takes 3x longer than necessary (sequential cleanup passes)

---

### FIX-206F-02: Validate blocking doesn't block (concurrency)

**File:** Code analysis (no changes)
**Estimated effort:** S
**What:**

**Scenario:** User A is saving an item while admin B is blocking it simultaneously.

**Expected behavior:**
- One of these happens:
  - A's save succeeds, then item is blocked (save exists but item is blocked)
  - Item is blocked first, then A's save is rejected with 403 (correct rejection)
- No deadlock or database corruption
- No orphaned state

**Trace the logic:**
- Is there a lock when updating `media_items.blocked` and `media_items.saved`?
- Or do they use optimistic concurrency (version number)?
- Or are they stateless (just queries)?

**Assertions:**
- No deadlock possible
- Either save succeeds or save is correctly rejected
- Database is consistent after race

---

## Phase G — Dead Code & Stale References

### FIX-206G-01: Scan for dead code references

**File:** Code analysis (no changes)
**Estimated effort:** L
**What:**

**Dead code from pre-Sprint-202 (pins system):**
- `UserPinRepository` — should be deleted or renamed to `UserSaveRepository`
- `IPinRepository` — should be deleted or renamed to `ISaveRepository`
- `UserItemPin` model — should be deleted or renamed to `UserItemSave`
- `PinSource` enum — should be deleted or renamed to `SaveSource`
- Old endpoint: `GET /InfiniteDrive/User/Pins` — should be deleted or return 308 redirect
- Old endpoint: `POST /InfiniteDrive/User/Pins/Remove` — should be deleted or return 308 redirect

**Dead code from pre-Sprint-202 (Doctor task):**
- `[Obsolete("Use DoctorTask instead")]` attributes — should reference `MarvinTask` instead
- `DoctorTask` class references in comments — should say `MarvinTask`
- "Run Doctor Now" button in HTML — should be deleted
- Doctor card UI in `configurationpage.html` — should be deleted

**Scan for:**
```
grep -r "UserPinRepository\|IPinRepository\|UserItemPin\|PinSource" --include="*.cs" .
grep -r "DoctorTask\|DeepCleanTask\|InfiniteDriveDeepClean" --include="*.cs" .
grep -r "Run Doctor\|Doctor Now\|Doctor task" Configuration/ --include="*.html" --include="*.js"
```

**Assertions:**
- 0 matches for pin-related dead code (outside obsolete shims)
- 0 matches for Doctor-related code (outside comments noting the Sprint 147 deletion)
- All stale [Obsolete] attributes reference `MarvinTask`, not `DoctorTask`

**If logic is wrong:**
- Old pin code still references — confusing for future devs
- Doctor references still exist — imply feature exists when it doesn't
- Dead endpoints still respond — client confusion

---

### FIX-206G-02: Validate no orphaned UI elements

**File:** Code analysis (no changes)
**Estimated effort:** S
**What:**

**After Sprints 203 + 205, confirm:**
- User tabs (Discover, My Picks, My Lists) are completely removed from HTML
- Their JS handlers are removed
- No hidden elements remain in DOM
- No dead CSS rules for `.es-tab-content-discover`, etc.

**Scan for:**
```
grep -i "es-tab-content-discover\|es-tab-content-mypicks\|es-tab-content-mylists" Configuration/
grep -i "data-tab=\"discover\"\|data-tab=\"mypicks\"\|data-tab=\"mylists\"" Configuration/
grep -i "\.es-discover\|\.es-mypicks\|\.es-mylists" Configuration/ --include="*.css"
```

**Assertions:**
- 0 matches for hidden user tabs in HTML
- 0 matches for user tab CSS rules
- No dead JS handlers for user tabs

---

## Phase H — Action Outcome Validation

### FIX-206H-01: Validate all actions produce expected outcomes

**File:** Code analysis (no changes)
**Estimated effort:** M
**What:**

**Create a matrix: Action → Expected Outcome → Actual Code Path**

| Action | Expected Outcome | Validation |
|--------|------------------|------------|
| User saves item | `user_item_saves` row written; `media_items.saved = true` | Trace `AddSave()` |
| User unsaves item | Row deleted; `media_items.saved = false` if count==0 | Trace `RemoveSave()` |
| Admin blocks item | .strm deleted; item 404 in detail; stays in Saved folder | Trace block endpoint |
| Admin unblocks item | .strm restored; item 200 in detail; unsaved items can re-save | Trace unblock endpoint |
| User browses Lists | Channel returns paginated items; parental filter applied | Trace `GetDiscoverCatalogAsync()` |
| User searches Lists | Search returns filtered items; parental filter applied | Trace `SearchDiscoverCatalogAsync()` |
| User views Saved | Channel returns only current user's saves | Trace `GetSaves(userId)` |
| Marvin runs | Orphaned saves cleaned up; flags synced; deleted items purged | Trace MarvinTask phases |

**For each row, trace the code to confirm:**
1. The action calls the right endpoint
2. The endpoint executes the right business logic
3. The business logic writes the right data
4. Side effects (file deletion, library scan, etc.) occur
5. Response is correct (200/201/403/404)

**Assertions:**
- All 8 actions have complete traceable paths
- No action silently fails (success response but no DB write)
- No action succeeds when it should fail (e.g., unsave of non-existent save)

**If logic is wrong:**
- Action returns 200 but nothing was saved (silent failure)
- Action returns error but DB was partially updated (inconsistent state)
- Action says "done" but side effects didn't happen

---

## Phase I — Build & Verification

### FIX-206I-01: Comprehensive code review checklist (see table above)

All code checks documented in FIX-206I-02 table.

---

### FIX-206I-02: Extended code review checklist (see table above)

All checks from User Actions, Admin Actions, Data Integrity, Marvin Cleanup, and Dead Code sections.

---

### FIX-206I-03: Manual end-to-end test — user workflow

1. **Non-admin user A logs in.**
2. Open Channel → Lists → Top Movies → Save "Batman Begins"
   - Confirm: row in `user_item_saves` for User A
   - Confirm: `media_items.saved = true`
3. Open Channel → Saved → See "Batman Begins"
4. Non-admin user B logs in (different user).
5. Open Channel → Saved → Does NOT see "Batman Begins" (A's saves, not B's)
6. User B opens Channel → Lists → Top Movies → Save "Batman Begins"
   - Confirm: row in `user_item_saves` for User B
   - Confirm: `media_items.saved = true` (already true, now two users saved)
7. User A unsaves "Batman Begins"
   - Confirm: row deleted from `user_item_saves` for User A
   - Confirm: `media_items.saved` still true (User B has it saved)
8. User A's Saved folder → "Batman Begins" gone
9. User B's Saved folder → "Batman Begins" still there
10. User B unsaves
    - Confirm: `media_items.saved = false` (no more saves)

---

### FIX-206I-04: Manual end-to-end test — admin block/unblock

1. **Admin logs in.**
2. User A has "Batman Begins" saved. User B has it saved.
3. Admin opens Content tab → Search for "Batman Begins" → Click Block (nudity)
   - Confirm: .strm file deleted from disk
   - Confirm: "Batman Begins" no longer appears in Channel → Lists → Browse
4. User A opens Channel → Saved
   - Confirm: "Batman Begins" still visible
   - Confirm: "Blocked" badge shown
   - Confirm: "Unsave" button is clickable
5. User A clicks "Unsave"
   - Confirm: removed from User A's Saved folder
   - Confirm: row deleted from `user_item_saves`
6. User B opens Channel → Saved
   - Confirm: "Batman Begins" still visible (User B's save)
   - Confirm: "Blocked" badge shown
7. Admin opens Content tab → Search for "Batman Begins" → Click Unblock
   - Confirm: .strm file re-created (or re-sync triggered)
   - Confirm: "Batman Begins" reappears in Channel → Lists
8. User B's Saved folder → "Batman Begins" still there, but no "Blocked" badge now

---

### FIX-206I-05: Manual Marvin cleanup test

1. **Corrupt the database:** Insert orphaned row into `user_item_saves` for a user that doesn't exist.
   ```sql
   INSERT INTO user_item_saves (user_id, media_item_id, saved_at)
   VALUES ('nonexistent-user', 123, datetime('now'));
   ```
2. Query `media_items.saved` for that item — confirm true
3. Run Marvin task (click "Summon Marvin" in admin panel)
4. After Marvin completes:
   - Confirm: orphaned row is deleted
   - Confirm: `media_items.saved` is correct (false if no remaining saves)
   - Confirm: log message about orphaned save cleanup

---

### FIX-206I-06: Grep validation — dead code scan

```bash
# Should all return 0 matches (outside Sprint 202 rename from pins to saves)
grep -r "UserPinRepository" --include="*.cs" . | grep -v "UserSaveRepository"
grep -r "IPinRepository" --include="*.cs" . | grep -v "ISaveRepository"
grep -r "UserItemPin" --include="*.cs" . | grep -v "UserItemSave"

# Should return 0 matches (Doctor references deleted)
grep -ri "DoctorTask\b" --include="*.cs" .
grep -ri "DeepCleanTask\b" --include="*.cs" .
grep -ri "InfiniteDriveDeepClean" .

# Should return 0 matches (user tabs deleted in Sprint 205)
grep -i "es-tab-content-discover\|es-tab-content-mypicks\|es-tab-content-mylists" Configuration/
grep -i "data-tab=\"discover\"\|data-tab=\"mypicks\"\|data-tab=\"mylists\"" Configuration/
```

---

### FIX-206I-07: Action outcome validation matrix

Verify each action from FIX-206H-01 produces the correct outcome:
- Code path is correct (right endpoint, right business logic)
- Expected outcome occurs (row written, flag updated, file deleted, etc.)
- Response status is correct (200/201/403/404)
- Side effects happen (library scan, file deletion, log entries, etc.)
- No silent failures (success without DB write)
- No orphaned state (partial updates)

---

## Rollback Plan

- This is validation only — no code changes, no rollback needed.
- If logic bugs are found, report them; fixes go to a future sprint.

---

## Completion Criteria

**Phase A — Save/Unsave Workflows:**
- [ ] User save workflow traced end-to-end (FIX-206A-01)
- [ ] User unsave workflow traced end-to-end (FIX-206A-02)

**Phase B — Admin Block/Unblock Workflows:**
- [ ] Admin block workflow traced end-to-end (FIX-206B-01)
- [ ] Admin unblock workflow traced end-to-end (FIX-206B-02)

**Phase C — Marvin Cleanup:**
- [ ] Orphaned saves cleanup validated (FIX-206C-01)
- [ ] Orphaned flags cleanup validated (FIX-206C-02)
- [ ] Deleted item cleanup validated (FIX-206C-03)

**Phase D — User & Admin Actions:**
- [ ] All 7 user channel actions traced (FIX-206D-01)
- [ ] All 5 admin content actions traced (FIX-206D-02)
- [ ] Lookup/reference hierarchy validated (FIX-206D-03)
- [ ] Blocking interactions validated across all surfaces (FIX-206D-04)

**Phase E — Edge Cases & Concurrency:**
- [ ] Concurrent save/unsave race condition verified (FIX-206E-01)
- [ ] Unsave-while-blocked scenario verified (FIX-206E-02)
- [ ] Concurrent block/view race condition verified (FIX-206E-03)

**Phase F — Parallelization & Performance:**
- [ ] Parallelization opportunities identified (FIX-206F-01)
- [ ] Blocking doesn't create deadlock or corruption (FIX-206F-02)

**Phase G — Dead Code & Stale References:**
- [ ] Dead pin/Doctor code removed (FIX-206G-01)
- [ ] No orphaned UI elements remain (FIX-206G-02)

**Phase H — Action Outcome Validation:**
- [ ] All 8 critical actions produce expected outcomes (FIX-206H-01)

**Phase I — Build & Verification:**
- [ ] Comprehensive code review checklist completed (FIX-206I-01/02)
- [ ] User workflow manual test passed (FIX-206I-03)
- [ ] Admin block/unblock manual test passed (FIX-206I-04)
- [ ] Marvin cleanup manual test passed (FIX-206I-05)
- [ ] Dead code grep validation passed (FIX-206I-06)
- [ ] Action outcome matrix validated (FIX-206I-07)

**Overall:**
- [ ] All logic is correct OR bugs documented with proposed fixes
- [ ] No silent failures, orphaned state, or data loss identified
- [ ] All user and admin actions match their documented intent

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | Where is the block/unblock endpoint after Sprint 203? (`POST /InfiniteDrive/Admin/Block`?) | Dev | Open |
| 2 | Does blocking code delete .strm files, or is that separate? | Dev | Open |
| 3 | Does `AddSave()` or `RemoveSave()` handle setting `media_items.saved`, or is it caller's responsibility? | Dev | Open |
| 4 | Unique constraint on `user_item_saves` (user_id, media_item_id) — confirmed in schema? | Dev | Open |

---

## Notes

**Files created:** 0
**Files modified:** 0
**Files analyzed:** ~25+ (all services, tasks, models, and repositories involved in save/block/marvin logic)

**Validation scope:**
- 9 phases covering user actions, admin actions, data workflows, edge cases, parallelization, dead code, and outcome validation
- 13 detailed FIX tasks tracing code paths end-to-end
- 10+ grep checklist items validating dead code removal
- 3 manual end-to-end tests (user, admin, Marvin)
- Action outcome matrix with 8 critical workflows

**Risk:** LOW — validation only, no production code changes.
Mitigated by:
1. Logic bugs are reported, not fixed in this sprint.
2. Manual tests use throw-away data (can be reset).
3. Marvin cleanup test uses a separate corrupted DB state.
4. All code path tracing is read-only (no instrumentation, no changes).

**Expected outcome:**
- High confidence that Sprints 202–205 achieve their intended behavior
- OR a comprehensive, prioritized list of logic bugs to fix in a future sprint
- Assurance that no silent failures, orphaned state, data loss, or unintended side effects occur
- Confirmation that all user and admin actions match their documented intent
