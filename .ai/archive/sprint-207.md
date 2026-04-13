# Sprint 207 — Per-User Saves + InfiniteDriveChannel

**Version:** v1.0 | **Status:** COMPLETE | **Risk:** HIGH | **Depends:** Sprint 205
**Owner:** Fullstack | **Target:** v0.41 | **PR:** TBD

---

## Overview

**Problem statement:** The codebase doesn't compile (14 errors from Sprint 204's un-gating using `StatusService.RequireAuthenticated` instead of `AdminGuard.RequireAuthenticated`). Saves are global — when any user saves an item, it's saved for everyone. The InfiniteDriveChannel was planned in Sprint 204 but never committed. Users have no native Emby channel surface and no way to unsave items. Admins cannot block items via API.

**Why now:** Sprint 205 cleaned up dead user tabs on the assumption that the channel would replace them. The channel doesn't exist. Every user-facing content surface is broken or missing.

**High-level approach:** Fix the build first. Then implement per-user saves via a `user_item_saves` junction table, rewrite `SavedService` to accept userId, wire AddToLibrary as the save action, add unsave endpoint, create InfiniteDriveChannel, and add Marvin save cleanup. Breaking schema change — clean DB required.

### What the Research Found

**Two independent investigations conducted (Claude + Opencode). Key findings consolidated:**

1. **Build broken since Sprint 204.** `StatusService.RequireAuthenticated()` called 6 times but the method lives on `AdminGuard`, not `StatusService`. Additional errors: `DiscoverCatalogEntry.RatingLabel` property missing, `long?` to `string` conversion failure, nullability mismatches. See `FINDINGS_CLAUDE_SPRINT_206.md` and `FINDINGS_OPENCODE_SPRINT_206.md`.

2. **Save system is global.** `media_items.saved` is a boolean column. `saved_by` stores "user"/"admin" (not user IDs). `SavedService.SaveItemAsync(itemId)` has no userId parameter. `GetItemsBySavedAsync(bool)` returns ALL saved items regardless of who saved them.

3. **No unsave endpoint.** Users cannot remove saved items. No REST endpoint exists.

4. **No block endpoint.** `SavedService.BlockItemAsync()` exists (dead code, zero callers). Admins can only unblock via `POST /Admin/UnblockItems`. Blocked items still appear in Browse/Search.

5. **InfiniteDriveChannel never committed.** File appears in git status as untracked but doesn't exist on disk. 0 commits in history. REPO_MAP.md references it as if it exists (stale).

6. **Parental filter exists but is never invoked.** `ApplyParentalFilter()` at `DiscoverService.cs:1122` is defined but never called from Browse/Search.

7. **Dead CSS.** 23 `.es-discover-*` CSS rules remain in `configurationpage.html` (lines 72-94). Empty `<div id="es-discover-onboarding">` at line 1382.

8. **Marvin has no save cleanup.** Three phases (validation, enrichment, token renewal) — none touch saves or database orphans.

9. **User ID is accessible.** `AdminGuard.RequireAuthenticated(_authCtx, Request)` returns null for authenticated users. `TryGetCurrentUserId()` returns Emby user GUID. Pattern established in `home_section_tracking` table.

10. **Key consumers of `media_items.saved` flag:** `SyncTask`, `RemovalService`, `RemovalPipeline`, `YourFilesConflictResolver` — all use it to decide grace period vs deletion. The denormalized boolean must remain.

### Breaking Changes

- **Schema V26:** New `user_item_saves` table. Remove `saved_by`, `save_reason`, `saved_season` columns from `media_items`. Keep `saved` (boolean) and `saved_at`. Clean DB required (`./emby-reset.sh`).
- **No migration blocks.** Breaking change — no backward compatibility.
- **`SavedService` method signatures change.** `SaveItemAsync(itemId)` → `SaveItemAsync(itemId, userId)`. `UnsaveItemAsync(itemId)` → `UnsaveItemAsync(itemId, userId)`. No external callers exist (verified by grep).
- **`MediaItem` model changes.** Remove `SavedBy`, `SaveReason`, `SavedSeason` properties. Remove `MarkSaved()` method.
- **New HTTP endpoints:** `POST /InfiniteDrive/Saved/Unsave`, channel routes via `IChannel`.

### Non-Goals

- ❌ Block endpoint implementation (deferred — blocking via Marvin enrichment failures works)
- ❌ Parental rating filtering in Browse/Search (deferred — `ApplyParentalFilter` exists, just needs invoking)
- ❌ Home screen rails / home section integration (separate plugin responsibility)
- ❌ Cinemeta/AIOStreams integration in channel (channel shows existing catalog data only)
- ❌ Watched-episode auto-save per user (future sprint)
- ❌ Dead CSS cleanup (trivial, can be done anytime)

---

## Phase A — Build Fix (P0 Prerequisite)

### FIX-207A-01: Fix `StatusService.RequireAuthenticated` → `AdminGuard.RequireAuthenticated`

**File:** `Services/DiscoverService.cs` (modify, 6 locations)
**Estimated effort:** S
**What:**

Replace all 6 occurrences of `StatusService.RequireAuthenticated(_authCtx, Request)` with `AdminGuard.RequireAuthenticated(_authCtx, Request)`. Lines: 306, 339, 639, 668, 937, 1039.

Also fix the paired nullability errors (6 occurrences at lines 307, 340, 640, 669, 938, 1040) — cast the deny check result or adjust the return type.

### FIX-207A-02: Fix `DiscoverCatalogEntry.RatingLabel` reference

**File:** `Services/DiscoverService.cs:1133` (modify)
**Estimated effort:** S
**What:**

`ApplyParentalFilter` references `item.RatingLabel` but `DiscoverCatalogEntry` has no such property. Check what rating column exists on `discover_catalog` (likely `imdb_rating` as numeric, or no rating column at all). Either add the property to `DiscoverCatalogEntry` or use an existing field. The `ReadDiscoverCatalogEntry` method in DatabaseManager shows what columns are available.

### FIX-207A-03: Fix `long?` to `string` conversion

**File:** `Services/DiscoverService.cs:1203` (modify)
**Estimated effort:** XS
**What:**

`GetUserMaxParentalRating()` returns `long?` but the method signature or caller expects `string`. Fix the type mismatch — likely return `int` (matching Emby's `MaxParentalRating` as int).

### FIX-207A-04: Verify build passes

**File:** N/A
**Estimated effort:** XS
**What:**

`dotnet build -c Release` — 0 errors, 0 warnings.

> ⚠️ **Watch out:** The `TreatWarningsAsErrors` is true for Release config. Any new warnings introduced by fixes must also be resolved.

---

## Phase B — Database Schema

### FIX-207B-01: Add `user_item_saves` table to Schema.cs

**File:** `Data/Schema.cs` (modify)
**Estimated effort:** S
**What:**

Add new `TableDefinition` after `home_section_tracking`, before `stream_cache`. Bump `CurrentSchemaVersion` from 23 to 26.

```sql
CREATE TABLE user_item_saves (
    id            TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    user_id       TEXT NOT NULL,
    media_item_id TEXT NOT NULL,
    save_reason   TEXT CHECK (save_reason IN ('explicit','watched_episode','admin_override')),
    saved_season  INTEGER,
    saved_at      TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (user_id, media_item_id)
);
CREATE INDEX idx_user_saves_user ON user_item_saves(user_id);
CREATE INDEX idx_user_saves_item ON user_item_saves(media_item_id);
```

No migration block in DatabaseManager.cs — clean install only.

### FIX-207B-02: Remove global save columns from `media_items` in Schema.cs

**File:** `Data/Schema.cs` (modify)
**Estimated effort:** S
**What:**

Remove from the `media_items` CREATE TABLE:
- `saved_by TEXT`
- `save_reason TEXT CHECK (...)`
- `saved_season INTEGER`

Keep:
- `saved INTEGER NOT NULL DEFAULT 0` (denormalized "any user saved this" flag)
- `saved_at TEXT` (timestamp of most recent save, for admin views)

Remove the corresponding index on `saved` if it was only used for save_reason queries (keep the general `idx_media_items_saved` index).

### FIX-207B-03: Add DatabaseManager query methods for `user_item_saves`

**File:** `Data/DatabaseManager.cs` (modify)
**Estimated effort:** L
**What:**

Add these methods after `GetItemsBySavedAsync` (around line 4525):

1. **`GetSavedItemsByUserAsync(string userId, CancellationToken ct)`** — Returns `List<MediaItem>`. JOINs `user_item_saves` to `media_items` filtered by `user_id`.

2. **`UpsertUserSaveAsync(string userId, string mediaItemId, string? saveReason, int? savedSeason, CancellationToken ct)`** — INSERT OR IGNORE into `user_item_saves` (idempotent).

3. **`DeleteUserSaveAsync(string userId, string mediaItemId, CancellationToken ct)`** — DELETE from `user_item_saves` WHERE matching user_id and media_item_id.

4. **`HasUserSaveAsync(string userId, string mediaItemId, CancellationToken ct)`** — Returns bool. SELECT EXISTS check.

5. **`SyncGlobalSavedFlagAsync(string mediaItemId, CancellationToken ct)`** — Atomic UPDATE on `media_items` that sets `saved = EXISTS(SELECT 1 FROM user_item_saves WHERE media_item_id = @Id)`. Also updates `saved_at` to MAX of user saves.

6. **`GetOrphanedUserSavesAsync(CancellationToken ct)`** — Returns orphaned save rows where media_item_id no longer exists in media_items. For Marvin.

7. **`DeleteUserSaveByIdAsync(string saveId, CancellationToken ct)`** — DELETE by save row ID. For Marvin.

### FIX-207B-04: Update `UpsertMediaItemAsync` and `ReadMediaItem`

**File:** `Data/DatabaseManager.cs` (modify)
**Estimated effort:** S
**What:**

Remove references to `saved_by`, `save_reason`, `saved_season` from:
- The UPSERT SQL statement
- The `ReadMediaItem` row reader
- The column bind section

---

## Phase C — Service Layer

### FIX-207C-01: Update `MediaItem` model

**File:** `Models/MediaItem.cs` (modify)
**Estimated effort:** S
**What:**

Remove properties: `SavedBy`, `SaveReason`, `SavedSeason`. Remove `MarkSaved()` method. Keep `Saved` (bool), `SavedAt` (DateTimeOffset?).

### FIX-207C-02: Create `UserItemSave` model

**File:** `Models/UserItemSave.cs` (create)
**Estimated effort:** XS
**What:**

New model following `home_section_tracking` pattern:
```csharp
public class UserItemSave
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string MediaItemId { get; set; } = "";
    public string? SaveReason { get; set; }
    public int? SavedSeason { get; set; }
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### FIX-207C-03: Rewrite `SavedService` for per-user saves

**File:** `Services/SavedService.cs` (modify)
**Estimated effort:** M
**What:**

Rewrite public methods to accept `userId`:

- **`SaveItemAsync(string itemId, string userId, CancellationToken ct)`**
  1. Fetch MediaItem by itemId
  2. If null, log warning and return
  3. `_db.UpsertUserSaveAsync(userId, itemId, "explicit", null, ct)`
  4. `_db.SyncGlobalSavedFlagAsync(itemId, ct)`
  5. Log pipeline event

- **`UnsaveItemAsync(string itemId, string userId, CancellationToken ct)`**
  1. Fetch MediaItem by itemId
  2. If null, log warning and return
  3. `_db.DeleteUserSaveAsync(userId, itemId, ct)`
  4. `_db.SyncGlobalSavedFlagAsync(itemId, ct)`
  5. Log pipeline event

- **`BlockItemAsync(string itemId, CancellationToken ct)`** — unchanged (global)
- **`UnblockItemAsync(string itemId, CancellationToken ct)`** — unchanged (global)

Add helpers:
- `IsItemSavedByUserAsync(itemId, userId, ct)` → `_db.HasUserSaveAsync(userId, itemId, ct)`
- `GetUserSavedItemsAsync(userId, ct)` → `_db.GetSavedItemsByUserAsync(userId, ct)`

Remove the `LogPipelineEvent` helper's dependency on `SavedBy`/`SaveReason` fields.

> ⚠️ **Watch out:** No external callers of `SaveItemAsync`/`UnsaveItemAsync` exist (verified by grep). Signature change is safe.

### FIX-207C-04: Wire `DiscoverService.AddToLibrary` to per-user save

**File:** `Services/DiscoverService.cs:665` (modify)
**Estimated effort:** M
**What:**

After the existing .strm write and catalog update succeeds (around line 775), add per-user save:

1. The method already has `callerUserId` from `TryGetCurrentUserId()` (line 729)
2. Resolve IMDB ID → `media_item_id`: look up `media_items` where `primary_id_type = 'imdb'` and `primary_id = req.ImdbId`. If not found, the item will be created by SyncTask when it indexes the .strm file — save the IMDB ID and resolve later, or create the media_item now with status 'known'.
3. Call `_savedService.SaveItemAsync(mediaItemId, callerUserId, ct)` (inject SavedService or instantiate)
4. Remove the old global save logic (`Saved = true`, `SavedBy = "user"`, etc.) if any remains

> ⚠️ **Watch out:** The discover_catalog and media_items are separate tables. AddToLibrary creates a discover_catalog entry AND a .strm file. The media_item may not exist yet. Simplest approach: query media_items by IMDB ID; if missing, create it with status='known'; then save per-user.

### FIX-207C-05: Add unsave endpoint

**File:** `Services/DiscoverService.cs` (modify — add DTO and handler)
**Estimated effort:** M
**What:**

Add request/response DTOs and handler following existing patterns:

```csharp
[Route("/InfiniteDrive/Discover/RemoveFromLibrary", "POST",
    Summary = "Remove item from current user's saved library")]
public class DiscoverRemoveFromLibraryRequest : IReturn<DiscoverRemoveFromLibraryResponse>
{
    [ApiMember(Name = "ImdbId", ...)]
    public string ImdbId { get; set; } = "";
}

public class DiscoverRemoveFromLibraryResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
}
```

Handler:
1. `AdminGuard.RequireAuthenticated(_authCtx, Request)` — must be authenticated
2. Get `callerUserId` from auth context
3. Resolve IMDB ID → media_item_id
4. Call `_savedService.UnsaveItemAsync(mediaItemId, callerUserId, ct)`
5. Return success

---

## Phase D — InfiniteDriveChannel

### FIX-207D-01: Create `Services/InfiniteDriveChannel.cs`

**File:** `Services/InfiniteDriveChannel.cs` (create)
**Estimated effort:** L
**What:**

Implement `MediaBrowser.Controller.Channels.IChannel`. Reference Sprint 204 spec (`.ai/sprints/sprint-204.md`) and `DISCOVERY_CHANNEL_FIX_SUMMARY.md`.

**Channel metadata:**
```csharp
public string Name => "InfiniteDrive";
public string Description => "Browse your lists and saved items.";
public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
```

**Folder hierarchy:**
```
InfiniteDrive (root)
├── Lists       (folder)
│   └── <user's own catalogs only; admin sees all sources + user catalogs>
└── Saved       (folder)
    └── <flat list of user_item_saves for calling user>
```

**Routing:**
- Root (null/empty Id) → return two `ChannelFolderItem`: Lists, Saved
- Id == "lists" → return user's catalogs. Non-admin: `user_catalogs WHERE owner_user_id = userId`. Admin: all enabled `sources` + their `user_catalogs`
- Id == "saved" → return `SavedService.GetUserSavedItemsAsync(query.UserId)`. Show blocked items with badge.
- Id matches a list slug → return items in that list via `GetDiscoverCatalogAsync`

**Key patterns:**
- Use reflection for `query.Id` (see `DISCOVERY_CHANNEL_FIX_SUMMARY.md`)
- `ChannelMediaType` must be `ChannelMediaType.Video` (enum value, not string)
- `GetChannelFeatures()` returns minimal `ChannelFeatures` (don't claim unsupported capabilities)
- Handle `DynamicImageResponse` blocker: use explicit interface implementation or throw `NotSupportedException` for `GetChannelImage()` if the type can't resolve

**Existing code to reuse:**
- `DatabaseManager.GetDiscoverCatalogAsync` — list items
- `DatabaseManager.GetSavedItemsByUserAsync` — saved items (new from Phase B)
- `AdminGuard.RequireAuthenticated` — auth check

> ⚠️ **Watch out:** IChannel is auto-discovered by Emby — no Plugin.cs registration needed.

> ⚠️ **Watch out:** The `query.UserId` property provides the current Emby user ID — use this for per-user filtering.

---

## Phase E — Marvin Cleanup

### FIX-207E-01: Add Phase 4 save maintenance to MarvinTask

**File:** `Tasks/MarvinTask.cs` (modify)
**Estimated effort:** M
**What:**

Add Phase 4 after Token Renewal:

```csharp
// Phase 4: Save maintenance
progress?.Report(0.85);
await SaveMaintenancePassAsync(cancellationToken);
```

`SaveMaintenancePassAsync`:
1. Get orphaned saves: `_db.GetOrphanedUserSavesAsync(ct)` — saves where media_item no longer exists
2. Delete each orphan: `_db.DeleteUserSaveByIdAsync(id, ct)`
3. Re-sync global flags: scan `media_items` where `saved=1`, check if any user saves exist. If not, `SyncGlobalSavedFlagAsync`
4. Log counts

---

## Phase F — Build & Verification

### FIX-207F-01: Build

`dotnet build -c Release` — 0 errors, 0 warnings.

### FIX-207F-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `StatusService.RequireAuthenticated` | 0 | All replaced with `AdminGuard.RequireAuthenticated` |
| `AdminGuard.RequireAuthenticated` | ≥6 | Correct calls in DiscoverService |
| `user_item_saves` | ≥5 | Schema.cs, DatabaseManager queries, SavedService |
| `GetSavedItemsByUserAsync` | 2 | DatabaseManager + channel |
| `SyncGlobalSavedFlagAsync` | 3 | DatabaseManager + SavedService save/unsave + Marvin |
| `InfiniteDriveChannel` | 1 | Channel class declaration |
| `SaveMaintenancePassAsync` | 2 | Definition + call in MarvinTask |
| `saved_by` in Schema.cs | 0 | Column removed |
| `save_reason` in Schema.cs `media_items` | 0 | Moved to user_item_saves |
| `MarkSaved` in Models/ | 0 | Method removed |

### FIX-207F-03: Manual test — build + deploy

1. `dotnet build -c Release` → 0 errors
2. `./emby-reset.sh` → clean DB with V26 schema
3. Confirm Emby starts without errors

### FIX-207F-04: Manual test — per-user save

1. Log in as non-admin User A
2. `GET /InfiniteDrive/Discover/Browse` → get an item's IMDB ID
3. `POST /InfiniteDrive/Discover/AddToLibrary` with that item → expect `{Ok: true}`
4. Query SQLite: `SELECT * FROM user_item_saves WHERE user_id = '<userA_id>'` → expect 1 row
5. Query SQLite: `SELECT saved FROM media_items WHERE id = '<item_id>'` → expect 1
6. Log in as User B
7. `GET /InfiniteDrive/Discover/Browse` → same item should NOT show "In Library" for User B
8. `POST /InfiniteDrive/Discover/AddToLibrary` same item as User B → expect `{Ok: true}`
9. Query SQLite: `SELECT COUNT(*) FROM user_item_saves WHERE media_item_id = '<item_id>'` → expect 2
10. User A: `POST /InfiniteDrive/Discover/RemoveFromLibrary` → expect `{Ok: true}`
11. User B's save should still exist (count = 1)
12. `media_items.saved` should still be 1
13. User B: `POST /InfiniteDrive/Discover/RemoveFromLibrary`
14. `media_items.saved` should be 0

### FIX-207F-05: Manual test — channel visible

1. Log in as non-admin user
2. Navigate to Emby Channels
3. Confirm "InfiniteDrive" channel appears
4. Open channel → confirm "Lists" and "Saved" folders
5. Open "Saved" → confirm items from FIX-207F-04 appear
6. Confirm saved items show poster, title, year

---

## Rollback Plan

- `git revert` the sprint commit. No data to preserve (clean DB required).
- The `user_item_saves` table is additive — its absence doesn't break anything.
- The build fixes are the highest-risk change. If they introduce regressions, the immediate mitigation is to re-add `AdminGuard.RequireAdmin()` to the four Discover endpoints.

---

## Completion Criteria

**Build:**
- [ ] `dotnet build -c Release` → 0 errors, 0 warnings
- [ ] All 6 `StatusService.RequireAuthenticated` calls fixed
- [ ] `DiscoverCatalogEntry.RatingLabel` error resolved
- [ ] `long?` to `string` conversion error resolved

**Database:**
- [ ] `user_item_saves` table created in Schema.cs
- [ ] `saved_by`, `save_reason`, `saved_season` removed from `media_items` in Schema.cs
- [ ] `CurrentSchemaVersion` bumped to 26
- [ ] 7 new DatabaseManager query methods added
- [ ] `UpsertMediaItemAsync` and `ReadMediaItem` updated (removed columns)

**Service Layer:**
- [ ] `MediaItem.cs` — removed `SavedBy`, `SaveReason`, `SavedSeason`, `MarkSaved()`
- [ ] `UserItemSave.cs` model created
- [ ] `SavedService.cs` — Save/Unsave accept userId, use user_item_saves
- [ ] `DiscoverService.AddToLibrary` — writes per-user save
- [ ] `POST /InfiniteDrive/Discover/RemoveFromLibrary` endpoint works

**Channel:**
- [ ] `InfiniteDriveChannel.cs` created, implements `IChannel`
- [ ] Channel visible in Emby for non-admin users
- [ ] Lists folder: shows user's catalogs (admin sees all sources)
- [ ] Saved folder: shows current user's saves

**Marvin:**
- [ ] Phase 4 save maintenance added
- [ ] Orphaned saves deleted on Marvin run
- [ ] Global saved flags re-synced on Marvin run

**End-to-end:**
- [ ] Per-user save test passes (FIX-207F-04)
- [ ] Channel test passes (FIX-207F-05)

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | AddToLibrary uses IMDB ID, user_item_saves uses media_item_id. How to resolve IMDB → media_item_id at save time? Query `media_items WHERE primary_id_type='imdb'` or create if missing? | Dev | Open |
| 2 | Does `IChannel.DynamicImageResponse` blocker still exist in current Emby DLLs? If so, use explicit interface implementation to defer. | Dev | Open |
| 3 | Should `DiscoverCatalogEntry` get a `RatingLabel` property, or should `ApplyParentalFilter` use a different column? Need to check what rating data discover_catalog has. | Dev | Open |
| 4 | `query.UserId` in IChannel — is this the Emby user GUID (same format as `TryGetCurrentUserId()`)? Confirm in SDK. | Dev | Open |

---

## Notes

**Files created:** 2 (`Services/InfiniteDriveChannel.cs`, `Models/UserItemSave.cs`)
**Files modified:** 5 (`Data/Schema.cs`, `Data/DatabaseManager.cs`, `Services/SavedService.cs`, `Services/DiscoverService.cs`, `Tasks/MarvinTask.cs`, `Models/MediaItem.cs`)

**Risk:** HIGH — this sprint fixes a broken build, introduces a breaking schema change, rewrites core save logic, and creates the primary user-facing surface (channel).

Mitigated by:
1. Clean DB required — no migration edge cases
2. Build fix is mechanical (class name swap + type fixes)
3. `SavedService` has zero external callers — signature change is safe
4. Channel is additive — its absence doesn't break existing functionality
5. Manual test plan covers critical user journeys end-to-end

**Research sources:**
- `FINDINGS_CLAUDE_SPRINT_206.md` — Claude investigation
- `FINDINGS_OPENCODE_SPRINT_206.md` — Opencode investigation
- `.ai/sprints/sprint-204.md` — Original channel spec
- `.ai/sprints/sprint-206.md` — Validation plan (assumptions that didn't match codebase)
