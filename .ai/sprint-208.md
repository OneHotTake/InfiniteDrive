# Sprint 208 — Admin Block Action + Dead Code Cleanup

**Version:** v1.0 | **Status:** Under Review — not yet approved | **Risk:** MEDIUM | **Depends:** Sprint 207
**Owner:** Fullstack | **Target:** v0.48 | **PR:** TBD

---

## Overview

**Problem statement:** Nine findings from Sprint 206 research remain unresolved. The block system is incomplete — `BlockItemAsync`/`UnblockItemAsync` exist in `SavedService` but have zero callers. There is no REST endpoint for blocking. Blocking only sets a flag; it doesn't delete .strm/.nfo files or trigger Emby library scans. Blocked items still appear in Discover Browse/Search. The Content tab UI has a misleading hint claiming auto-blocking exists. 22 dead CSS rules and an empty `<div>` remain from the removed Discover surface.

**Why now:** Sprint 207 completed per-user saves and the channel. Sprint 209 (parental filtering) must ship before public release. Sprint 208 closes the admin block gap first — blocked items must actually disappear from all surfaces, not just get a flag set.

**High-level approach:** Add a `POST /InfiniteDrive/Admin/BlockItems` endpoint to AdminService. Make the block action aggressive: delete .strm/.nfo, clear user saves, force Emby scan. Filter blocked items from Discover Browse/Search. Fix the misleading UI hint. Clean up dead CSS. Wire existing `BlockItemAsync`/`UnblockItemAsync` through the new endpoint.

### What the Research Found

1. **`SavedService.BlockItemAsync`** (line 87) — sets `Blocked=true`, `BlockedAt=now`, upserts. Does NOT delete .strm/.nfo. Does NOT clear user saves. Zero callers confirmed by grep.

2. **`AdminService`** — has `GET /Admin/BlockedItems` and `POST /Admin/UnblockItems`. No `POST /Admin/BlockItems` endpoint exists.

3. **Discover Browse/Search** — no filter for blocked items. `ApplyParentalFilter` at line 1219 is separate (parental ratings — Sprint 209).

4. **Content tab** (line 1396-1424) — has "Blocked Items" card with "Unblock Selected" / "Unblock All" buttons. No "Block" button. Hint text at line 1400 says: *"Items blocked after failing enrichment 3 times. Still playable but won't be retried automatically."* — both claims are wrong.

5. **Dead CSS** — 23 `.es-discover-*` rules at lines 72-94. Empty `<div id="es-discover-onboarding">` at line 1382. These are from the removed Discover tab UI (Sprint 205 removed user tabs but kept the CSS).

6. **`ManifestFilter.IsBlockedAsync`** (line 46) — already checks `item?.Blocked == true` during sync. Blocked items are correctly excluded from new sync writes. But existing .strm files on disk are not cleaned up.

7. **RemovalService** (line 62) — checks `item.Blocked` and skips removal. This is correct — blocked items are protected from automatic removal.

8. **YourFilesConflictResolver** — checks `item.Blocked` and returns `KeepBlocked`. Correct — blocked items are never superseded.

9. **Race condition:** Admin blocks an item while a user simultaneously saves it. No transaction wrapping around the block+save operations. SQLite WAL mode provides read consistency but the application-level race (block sets flag, then save writes user_item_saves) could result in a blocked item having active user saves.

### Breaking Changes

- **None.** All changes are additive or corrective. No schema changes.

### Non-Goals

- ❌ Parental rating filtering (Sprint 209)
- ❌ Auto-blocking on enrichment failure (no such feature planned)
- ❌ Block from Discover card UI (admin-only action from Content tab)
- ❌ InfiniteDriveChannel block badge (deferred — channel shows library items, Emby handles visibility)

---

## Phase A — Block Endpoint

### FIX-208A-01: Add `POST /InfiniteDrive/Admin/BlockItems` endpoint

**File:** `Services/AdminService.cs` (modify)
**Estimated effort:** M
**What:**

Add request/response DTOs and handler following existing pattern:

```csharp
[Route("/InfiniteDrive/Admin/BlockItems", "POST",
    Summary = "Blocks items: deletes .strm/.nfo, clears user saves, triggers Emby scan")]
public class BlockItemsRequest : IReturn<BlockItemsResponse>
{
    public List<string> ItemIds { get; set; } = new();
}

public class BlockItemsResponse
{
    public bool Success { get; set; }
    public int Count { get; set; }
    public List<string> Errors { get; set; } = new();
}
```

Handler:
1. `AdminGuard.RequireAdmin` check
2. Validate ItemIds not empty
3. For each item:
   a. Call `SavedService.BlockItemAsync(itemId, ct)` — sets flag
   b. Delete .strm file from disk (if exists)
   c. Delete .nfo file from disk (if exists)
   d. Delete all user saves: `_db.DeleteAllUserSavesForItemAsync(itemId, ct)` (new method)
   e. Sync global saved flag: `_db.SyncGlobalSavedFlagAsync(itemId, ct)`
4. Trigger Emby library scan (fire-and-forget)
5. Return success count + any errors

### FIX-208A-02: Add `DeleteAllUserSavesForItemAsync` to DatabaseManager

**File:** `Data/DatabaseManager.cs` (modify)
**Estimated effort:** XS
**What:**

```csharp
/// <summary>
/// Deletes all user save records for a given media item.
/// Used by admin block action to clear all per-user saves.
/// </summary>
public async Task DeleteAllUserSavesForItemAsync(string mediaItemId, CancellationToken ct)
{
    using var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
    await conn.OpenAsync(ct);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM user_item_saves WHERE media_item_id = @id";
    cmd.Parameters.AddWithValue("@id", mediaItemId);
    await cmd.ExecuteNonQueryAsync(ct);
}
```

### FIX-208A-03: Inject `SavedService` and `StrmWriterService` into `AdminService`

**File:** `Services/AdminService.cs` (modify)
**Estimated effort:** S
**What:**

AdminService currently only has `ILogManager` and `IAuthorizationContext` dependencies. Add `SavedService` (for block logic) and `StrmWriterService` (for path resolution to find .strm/.nfo files on disk).

Alternatively, resolve via `Plugin.Instance` singleton pattern (check what other services do).

### FIX-208A-04: Wire the Emby library scan trigger

**File:** `Services/AdminService.cs` (modify)
**Estimated effort:** S
**What:**

After blocking and deleting files, trigger an Emby library scan so the removed items disappear from user libraries. Use existing pattern from `CatalogSyncTask` — call `ILibraryManager.ValidateMediaLibrary()` or the Emby REST API `/Library/Refresh`.

> ⚠️ **Watch out:** Check how `CatalogSyncTask` triggers the library scan — it likely uses `Plugin.Instance.LibraryManager` or similar. Follow the same pattern.

---

## Phase B — Aggressive Block Action

### FIX-208B-01: Delete .strm/.nfo on block

**File:** `Services/AdminService.cs` (modify — within BlockItems handler)
**Estimated effort:** S
**What:**

For each blocked item, resolve the file paths and delete:

```csharp
// Resolve .strm path from media_item
var strmPath = item.StrmPath;
if (!string.IsNullOrEmpty(strmPath) && File.Exists(strmPath))
{
    File.Delete(strmPath);
    _logger.LogInformation("[AdminService] Deleted .strm: {Path}", strmPath);
}

// Resolve .nfo path (same directory, different extension)
if (!string.IsNullOrEmpty(strmPath))
{
    var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
    if (File.Exists(nfoPath))
    {
        File.Delete(nfoPath);
        _logger.LogInformation("[AdminService] Deleted .nfo: {Path}", nfoPath);
    }
}
```

### FIX-208B-02: Clear user saves and re-sync global flag

**File:** `Services/AdminService.cs` (modify — within BlockItems handler)
**Estimated effort:** XS
**What:**

After deleting files:
1. `_db.DeleteAllUserSavesForItemAsync(itemId, ct)` — remove all per-user saves
2. `_db.SyncGlobalSavedFlagAsync(itemId, ct)` — recompute `media_items.saved` (will become 0)

This prevents the race condition where a blocked item still has active user saves.

---

## Phase C — Filter Blocked Items from Discover

### FIX-208C-01: Exclude blocked items from Browse/Search results

**File:** `Services/DiscoverService.cs` (modify)
**Estimated effort:** S
**What:**

In `Get(DiscoverBrowseRequest)` and `Get(DiscoverSearchRequest)`, after fetching results from the database, filter out items that are blocked.

Two approaches (pick one):
- **A) SQL filter:** Add `AND blocked_at IS NULL` to the discover_catalog queries (if blocked data is joined)
- **B) Application filter:** After fetching, filter the list in C# before returning

Check what data `DiscoverBrowseRequest` returns — if it queries `discover_catalog` (not `media_items`), the blocked status may need a JOIN. If it queries `media_items`, just add a WHERE clause.

> ⚠️ **Watch out:** The discover_catalog and media_items tables are separate. Blocked status is on `media_items.blocked`. A JOIN or subquery may be needed. Check the existing query patterns in DiscoverService.

### FIX-208C-02: Exclude blocked items from Discover Detail

**File:** `Services/DiscoverService.cs` (modify)
**Estimated effort:** XS
**What:**

In `Get(DiscoverDetailRequest)`, if the item is blocked, return 404 or an empty response instead of showing details. Blocked items should not be accessible via direct URL either.

---

## Phase D — UI Fixes

### FIX-208D-01: Fix misleading Blocked Items hint

**File:** `Configuration/configurationpage.html` (modify)
**Estimated effort:** XS
**What:**

Replace line 1400:
```
Old: Items blocked after failing enrichment 3 times. Still playable but won't be retried automatically.
New: Items manually blocked by an administrator. Blocking removes the item from all user libraries.
```

### FIX-208D-02: Add Block button to Content tab

**File:** `Configuration/configurationpage.html` (modify)
**Estimated effort:** S
**What:**

Add a "Block Selected" button alongside the existing "Unblock Selected" and "Unblock All" buttons. The block action should:
1. Show a confirmation dialog (destructive action)
2. Call `POST /InfiniteDrive/Admin/BlockItems` with selected item IDs
3. Refresh the blocked items list on success

Also add the ability to browse/search for items to block. This could be:
- An IMDB ID input field + "Block by IMDB ID" button (simplest)
- Or a search interface that queries discover_catalog (more complex)

> Start with the IMDB ID input approach — admin knows what they want to block.

### FIX-208D-03: Wire Block button in configurationpage.js

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** S
**What:**

- Add click handler for the new Block button
- Confirmation dialog: "Block this item? This will delete its .strm file and remove it from all user libraries."
- Call `POST /InfiniteDrive/Admin/BlockItems` API
- Refresh blocked items list on success
- Show toast notification

### FIX-208D-04: Dead CSS cleanup

**File:** `Configuration/configurationpage.html` (modify)
**Estimated effort:** XS
**What:**

Delete the 23 `.es-discover-*` CSS rules at lines 72-94. These are dead CSS from the removed Discover tab UI (Sprint 205 removed user tabs but left the CSS).

Delete the empty `<div id="es-discover-onboarding"></div>` at line 1382.

### FIX-208D-05: Clean up JS references to removed Discover elements

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** XS
**What:**

Grep for any JS code that references `es-discover-*` element IDs. These are dead code from the removed Discover tab. Delete or comment out any such references.

---

## Phase E — Build & Verification

### FIX-208E-01: Build

`dotnet build -c Release` — 0 errors, 0 net-new warnings.

### FIX-208E-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `BlockItemsRequest` | 2 | DTO + handler |
| `DeleteAllUserSavesForItemAsync` | 2 | DatabaseManager + AdminService |
| `File.Delete` in AdminService | ≥1 | .strm/.nfo deletion |
| `blocked_at IS NULL` or `!item.Blocked` | ≥2 | Browse + Search filter |
| `es-discover-` in HTML | 0 | All dead CSS removed |
| `es-discover-onboarding` | 0 | Empty div removed |
| "Still playable" in HTML | 0 | Misleading hint removed |
| "manually blocked by" in HTML | 1 | Corrected hint |

### FIX-208E-03: Manual test — block action

1. Add an item to library via Discover (creates .strm + user save)
2. Go to Content tab → note the item is in library
3. Enter the item's IMDB ID in the "Block by IMDB ID" field
4. Click Block → confirm dialog
5. Assert: .strm file deleted from disk
6. Assert: .nfo file deleted from disk
7. Assert: `user_item_saves` row removed
8. Assert: `media_items.saved` = 0
9. Assert: item no longer appears in Discover Browse/Search
10. Assert: item appears in Blocked Items list on Content tab

### FIX-208E-04: Manual test — unblock

1. From Content tab Blocked Items, select the blocked item
2. Click "Unblock Selected"
3. Assert: item removed from blocked list
4. Assert: item reappears in Discover Browse (if still in catalog)
5. Assert: next sync can re-create .strm file

---

## Rollback Plan

- `git revert` the sprint commit.
- No schema changes — rollback is clean.
- Block endpoint is additive — its absence doesn't break anything.
- Blocked items in DB remain blocked (flag persists) but .strm files are already deleted — would need manual unblock or re-sync to restore.

---

## Completion Criteria

- [ ] `POST /InfiniteDrive/Admin/BlockItems` endpoint works (admin-only)
- [ ] Block action deletes .strm and .nfo files from disk
- [ ] Block action clears all user saves and re-syncs global flag
- [ ] Block action triggers Emby library scan
- [ ] Blocked items excluded from Discover Browse results
- [ ] Blocked items excluded from Discover Search results
- [ ] Blocked items excluded from Discover Detail
- [ ] Content tab hint text corrected
- [ ] "Block by IMDB ID" UI works with confirmation dialog
- [ ] Dead CSS (23 `.es-discover-*` rules) deleted
- [ ] Empty `<div id="es-discover-onboarding">` deleted
- [ ] Dead JS references cleaned up
- [ ] `dotnet build -c Release` — 0 errors, 0 warnings

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | How does AdminService resolve IMDB ID → media_item_id for blocking? Need to query `media_items` by primary_id where type='imdb'. | Dev | Open |
| 2 | Should block also delete the parent folder if empty after .strm/.nfo removal? (HousekeepingService already handles orphaned folders) | Dev | Open |
| 3 | How to trigger Emby library scan from AdminService? Check existing pattern in CatalogSyncTask. | Dev | Open |
| 4 | Should the Content tab show ALL items for blocking (not just already-blocked ones)? Or just the IMDB ID input? | Dev | Open |

---

## Notes

**Files modified:** 5 (`Services/AdminService.cs`, `Services/DiscoverService.cs`, `Data/DatabaseManager.cs`, `Configuration/configurationpage.html`, `Configuration/configurationpage.js`)
**Files created:** 0

**Risk:** MEDIUM — block action is destructive (deletes files), but limited to admin-only and confirmation-dialog-protected.
Mitigated by:
1. Admin-only endpoint with AdminGuard check
2. Confirmation dialog before destructive action
3. Unblock action reverses the flag (but not the deleted files — next sync re-creates)
4. No schema changes — clean rollback
5. Dead CSS cleanup is zero-risk (visual only, no logic)
