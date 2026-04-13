# Sprint 144 — RefreshTask: Notify + Verify

**Version:** v4.0 | **Status:** Plan | **Risk:** HIGH | **Depends:** Sprint 143

---

## Overview

Complete the RefreshTask pipeline with Steps 4 (Notify) and 5 (Verify). Step 4 tells Emby about new .strm files via surgical per-item notification. Step 5 confirms Emby has absorbed the items and handles token renewal for expiring items.

### Why This Exists

Writing .strm and .nfo files to disk is not enough — Emby must be told to scan them. The current approach uses `QueueLibraryScan()` which scans the entire media directory. The new Notify pattern uses a 4-step surgical notification:
1. POST `/Library/Media/Updated` with the item path
2. Poll for BaseItem (6 x 500ms = 3s max)
3. Fallback: non-recursive parent folder refresh
4. Full metadata refresh on confirmed BaseItem

---

## Phase 144A — Step 4: Notify

### FIX-144A-01: Implement surgical notification pattern

**File:** `Tasks/RefreshTask.cs` (modify)

**What:**
1. Query all items with `ItemState = Written` (bounded at 42):
```sql
SELECT * FROM catalog_items WHERE item_state = @written AND removed_at IS NULL LIMIT 42;
```
2. For each Written item:
   a. **Step 4a:** Report path to Emby via ILibraryManager:
   ```csharp
   _libraryManager.ReportItemAdded(itemPath);
   ```
   Or use `ValidatePath` / scoped `ValidateMediaLibrary`. Exact SDK method TBD — verify against Emby SDK at `../emby.SDK-beta/`.

   b. **Step 4b:** Poll for BaseItem (up to 6 attempts x 500ms):
   ```csharp
   for (int attempt = 0; attempt < 6; attempt++)
   {
       var query = new InternalItemsQuery { Path = item.StrmPath };
       var results = _libraryManager.GetItemList(query);
       if (results.Count > 0) break;
       await Task.Delay(500, cancellationToken);
   }
   ```

   c. **Step 4c:** If BaseItem not found after polling, trigger non-recursive parent folder refresh as fallback

   d. **Step 4d:** If BaseItem confirmed, trigger metadata refresh on the item

3. Transition item to `ItemState = Notified`
4. Log each notification outcome
5. If the surgical approach is unavailable in the SDK, fall back to `QueueLibraryScan()` (proven approach from DoctorTask)

**SDK verification needed:** Before implementing, confirm the exact ILibraryManager methods available:
- Does `ReportItemAdded(string path)` exist?
- Is there a way to trigger a single-directory scan?
- What is the correct way to poll for a BaseItem by file path?

**Fallback:** If surgical approach unavailable, use `QueueLibraryScan()`. This is what DoctorTask currently uses and it works.

**Depends on:** Sprint 143 (Written state, RefreshTask skeleton)

---

## Phase 144B — Step 5: Verify

### FIX-144B-01: Implement Verify step

**File:** `Tasks/RefreshTask.cs` (modify)

**What:**
1. Query all items with `ItemState = Notified` (bounded at 42):
```sql
SELECT * FROM catalog_items WHERE item_state = @notified AND removed_at IS NULL LIMIT 42;
```
2. For each Notified item:
   a. Check if Emby has a BaseItem for this path:
   ```csharp
   var query = new InternalItemsQuery { Path = item.StrmPath, IncludeItemTypes = new[] { "Movie", "Series", "Episode" } };
   var results = _libraryManager.GetItemList(query);
   ```
   b. If BaseItem found: transition to `ItemState = Ready` — item is fully live
   c. If BaseItem not found: leave as `ItemState = Notified` — the 24-hour stalled-item promotion in FIX-144C-01 handles this. No per-item failure counter needed; the stalled check is the sole escalation mechanism.

3. Token renewal for items with `strm_token_expires_at` within 90 days (sharing the 42-item budget):
```sql
SELECT * FROM catalog_items
WHERE (
    strm_token_expires_at < (unixepoch('now') + 7776000)
    OR strm_token_expires_at IS NULL
)
AND removed_at IS NULL
ORDER BY strm_token_expires_at ASC
LIMIT :remaining_budget;
```
   For each: rewrite .strm with fresh token (same logic as DoctorTask Phase 6), update `strm_token_expires_at`

4. Update run log: step="verify", items_affected=count

**Depends on:** FIX-144A-01

---

## Phase 144C — Lifecycle Transitions

### FIX-144C-01: Implement stalled-item promotion

**File:** `Tasks/RefreshTask.cs` (modify)

**What:**
1. At the end of each Verify step, check for Notified items stalled > 24 hours
2. Promote these to `ItemState = NeedsEnrich` with `nfo_status = 'NeedsEnrich'`
3. This catches items where Emby's scanner failed to pick up the .strm file
4. Deep Clean will handle the enrichment in Sprint 145

**Depends on:** FIX-144B-01

---

## Phase 144D — Build Verification

### FIX-144D-01: Build + integration test

**What:**
1. `dotnet build -c Release` — 0 errors, 0 new warnings
2. `./emby-reset.sh` — server starts, plugin loads
3. Trigger RefreshTask manually
4. Verify full pipeline: Collect -> Write -> Hint -> Notify -> Verify
5. Verify Written items transition to Notified after Notify step
6. Verify Notified items transition to Ready after Verify step confirms BaseItem
7. Verify token renewal works for items within 90-day window
8. Verify 42-item bound works: add 50 items, verify only 42 processed per run
9. Verify stalled-item promotion: set a Notified item's updated_at to 25h ago, verify it becomes NeedsEnrich

**Depends on:** FIX-144C-01

---

## Sprint 144 Dependencies

- **Previous Sprint:** 143 (RefreshTask Collect + Write + Hint)
- **Blocked By:** Sprint 143
- **Blocks:** Sprint 145 (DeepCleanTask)

---

## Sprint 144 Completion Criteria

- [ ] Step 4 Notify: per-item notification via ILibraryManager
- [ ] 4-step notification pattern: report -> poll BaseItem -> folder fallback -> metadata refresh
- [ ] Written items transition to Notified
- [ ] 42-item bound per Notify run
- [ ] Step 5 Verify: confirms BaseItem exists for Notified items
- [ ] Notified items transition to Ready on confirmation
- [ ] Token renewal for items expiring within 90 days (sharing 42-item budget)
- [ ] Stalled Notified items (>24h) promoted to NeedsEnrich
- [ ] Full pipeline works end-to-end: Queued -> Written -> Notified -> Ready
- [ ] Bounded poll failure: items where Notify poll fails stay Written, picked up by next cycle
- [ ] Build succeeds with 0 errors, 0 new warnings

---

## Sprint 144 Notes

**Files modified:** 1 (`Tasks/RefreshTask.cs`)

**Risk assessment:** HIGH. The Notify step depends on Emby's ILibraryManager API, which has limited documentation. The exact method for single-file notification may not exist in the SDK. Fallback to `QueueLibraryScan()` is acceptable but less surgical.

**Design decisions:**
- 42-item bound applies to both Notify and Verify steps (42 each per 6-minute run)
- Token renewal shares the Verify budget. On a healthy library, most of the 42 slots go to status promotion; token renewal picks up the slack.
- The 3-attempt Verify threshold before NeedsEnrich is ~18 minutes (3 runs x 6 min), which is fast enough to catch scan failures without false positives.
- **Large sync draining:** When Write produces 500 items in one pass, Notify processes 42 per 6-minute cycle. The remaining 458 Written items stay in Written state and drain over subsequent cycles (~72 minutes for 500 items). This is expected behavior — the 42-item bound prevents resource spikes. No backpressure mechanism needed.

**Bounded poll failure behavior:**
- If Notify fails to confirm a BaseItem for an item (poll returns empty), the item still transitions to Notified (notification was attempted). This is intentional — `Notified` means "we told Emby about this item" not "Emby confirmed it exists." The Verify step is the confirmation gate.
- Items that remain Notified >24h are promoted to NeedsEnrich by the stalled-item check (FIX-144C-01). This is the sole escalation mechanism — no per-item failure counter or 3-attempt threshold.
- Items that remain Written after Notify (because of the 42-item bound) are picked up by the next cycle's Notify step. Large syncs drain over multiple 6-minute cycles by design.

---
