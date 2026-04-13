# Sprint 202 — Dead Cruft Cleanup + Marvin Rename + Pins→Saves

**Version:** v0.42 | **Status:** Ready | **Risk:** MEDIUM | **Depends:** Sprint 201
**Owner:** Fullstack | **Target:** v0.42 | **PR:** TBD

---

## Overview

The codebase contains three categories of honest lies: references to `DoctorTask` (deleted in Sprint 147), the `DeepCleanTask` / `InfiniteDriveDeepClean` key whose UI name should be "Marvin" everywhere, and a `user_item_pins` schema whose table/column names contradict the "Saved" metaphor approved for user-facing surfaces.

**Problem statement:** Code references a class that doesn't exist (`DoctorTask`). The task users see as "Summon Marvin" is registered under `InfiniteDriveDeepClean`, creating a split identity. The `user_item_pins` table, `pin_source` column, `IPinRepository`, `UserItemPin`, and every JS `pin` variable tell a story nobody reads anymore.

**Why now:** Sprint 201 closed the wizard work. Before Sprint 203 restructures the admin page, the schema and vocabulary need to be settled so the new UI can use the correct names throughout without a mid-sprint pivot.

**High-level approach:** Rename `DeepCleanTask` → `MarvinTask` (class, file, key, display name). Update the one JS call site that looks up the task by key. Delete the dead "Run Doctor Now" button. Update stale `[Obsolete]` attributes on the three tasks that blamed `DoctorTask`. Rename `user_item_pins` → `user_item_saves` via a migration step, rename the repository interface and implementation, rename `UserItemPin` → `UserItemSave`, and chase down every call site. Add obsolete-redirect shims for the two affected REST endpoints.

### What the Research Found

- `DoctorTask` class does not exist anywhere in the `InfiniteDrive` codebase — confirmed via grep. The Sprint 147 deletion is referenced in `Services/TriggerService.cs:105`.
- `DeepCleanTask` (`Tasks/DeepCleanTask.cs`) is the live implementation. Its `Key` constant is `"InfiniteDriveDeepClean"`. The "Summon Marvin" JS call in `configurationpage.js` (~line 2828) filters `ScheduledTasks` by `Key === 'InfiniteDriveDeepClean'` to find the right task to run.
- Three tasks carry stale `[Obsolete("Use DoctorTask instead (Sprint 66)")]`: `LibraryReadoptionTask`, `EpisodeExpandTask`, `FileResurrectionTask`. They still run — the attribute is informational only.
- `CollectionSyncTask.cs:23` has an inline comment `// Runs after DoctorTask`.
- `user_item_pins` table exists in `Data/Schema.cs`. `Repositories/Interfaces/IPinRepository.cs` and `Repositories/UserPinRepository.cs` are the read/write layer. `Models/UserItemPin.cs` is the model. Call sites exist in `Services/DiscoverService.cs` (AddToLibrary) and `Tasks/RefreshTask.cs` (auto-save-on-playback).
- SQLite 3.25+ supports `ALTER TABLE ... RENAME COLUMN` — confirmed compatible with the version shipped in Emby beta.
- Emby's `ScheduledTasks/Running/{id}` lookup is by the task's `Key` property, not display name. Renaming `TaskKey` constant requires updating the JS lookup.
- On upgrade, Emby drops the old task registration and creates the new one under the new key. Any admin-configured custom schedule on `InfiniteDriveDeepClean` is lost; the 18h default re-applies. This is a one-time breaking change documented below.

### Breaking Changes

- **Scheduled task key change:** `InfiniteDriveDeepClean` → `InfiniteDriveMarvin`. Admins who set a custom schedule for the Deep Clean task will have it reset to the 18h default on first plugin load after upgrade.
- **Schema migration:** `user_item_pins` table renamed to `user_item_saves`; `pin_source` column renamed to `save_source`; `pinned_at` column renamed to `saved_at` (if present). `Schema.CurrentSchemaVersion` bumped. Migration is applied once in `DatabaseManager` upgrade path; existing rows are preserved.
- **REST endpoint routes:** `/InfiniteDrive/User/Pins` → `/InfiniteDrive/User/Saves`; `/InfiniteDrive/User/Pins/Remove` → `/InfiniteDrive/User/Saves/Remove`. Old routes kept as `[Obsolete]` shims returning `308 Permanent Redirect` for one release.

### Non-Goals

- ❌ Admin page restructure (Sprint 203).
- ❌ `InfiniteDriveChannel` IChannel implementation (Sprint 204).
- ❌ User tab removal from config page (Sprint 205).
- ❌ RSS feed ingestion or user list subscription UX — deferred.
- ❌ Touching any code not directly implicated by the Marvin rename or pins→saves rename.

---

## Phase A — Marvin Rename (Task + JS + UI)

### FIX-202A-01: Rename `DeepCleanTask` → `MarvinTask`

**File:** `Tasks/DeepCleanTask.cs` → rename to `Tasks/MarvinTask.cs` (rename file, rename class)
**Estimated effort:** S
**What:**

1. Rename the file: `Tasks/DeepCleanTask.cs` → `Tasks/MarvinTask.cs`.
2. Change `class DeepCleanTask` → `class MarvinTask`.
3. Change `private const string TaskName = "InfiniteDrive Deep Clean"` → `"InfiniteDrive Marvin"`.
4. Change `private const string TaskKey = "InfiniteDriveDeepClean"` → `"InfiniteDriveMarvin"`.
5. Update all internal log messages that say `"DeepCleanTask"` → `"MarvinTask"`.
6. Update the `ILogger<DeepCleanTask>` type parameter → `ILogger<MarvinTask>`.
7. Update `EmbyLoggerAdapter<DeepCleanTask>` → `EmbyLoggerAdapter<MarvinTask>`.
8. The `last_deepclean_run_time` metadata key persisted to the DB can stay as-is — it's an opaque string key, not user-visible.

> ⚠️ **Watch out:** The `_runningGate` semaphore is `static` — fine after rename since there's only one class. No other `.cs` file references `DeepCleanTask` by name (confirmed by grep hits are only the file itself and the stale task comments updated in Phase B).

---

### FIX-202A-02: Update JS task-key lookup

**File:** `Configuration/configurationpage.js` (~line 2828)
**Estimated effort:** S
**What:**

In the `summonMarvin()` function, find the line that filters `ScheduledTasks` by key:

```js
// Before
tasks.find(t => t.Key === 'InfiniteDriveDeepClean')

// After
tasks.find(t => t.Key === 'InfiniteDriveMarvin')
```

Confirm no other occurrence of `InfiniteDriveDeepClean` in the JS file.

---

### FIX-202A-03: Delete "Run Doctor Now" button and Doctor card

**File:** `Configuration/configurationpage.html` (~lines 751–801)
**Estimated effort:** S
**What:**

Delete the entire Doctor reconciliation card block (the card that contains the "▶ Run Doctor Now" button). This button POSTs to a trigger case (`doctor`) that was removed in Sprint 147 — clicking it has been a no-op since then.

Also confirm that the item-state counts (Catalogued/Present/Resolved/Retired/Pinned/Orphaned) in this card are noted for migration to the Overview tab in Sprint 203, then delete them here.

> ⚠️ **Watch out:** Deleting only the card; do not touch surrounding HTML outside the card boundary. Identify the exact open/close `<div>` pair by line number before editing.

---

### FIX-202A-04: Update `TriggerService.cs` comment

**File:** `Services/TriggerService.cs:105`
**Estimated effort:** XS
**What:**

Update the comment that references "Doctor task removed in Sprint 147" to also reference MarvinTask:

```cs
// Doctor task removed in Sprint 147 — replaced by MarvinTask + RefreshTask.
```

---

## Phase B — Stale Obsolete Attributes

### FIX-202B-01: Update `LibraryReadoptionTask` obsolete attribute

**File:** `Tasks/LibraryReadoptionTask.cs:39`
**Estimated effort:** XS
**What:**

Replace:
```cs
[Obsolete("Use DoctorTask instead (Sprint 66)")]
```
With:
```cs
[Obsolete("Superseded by MarvinTask (Sprint 202). Retained as scheduled safety net.")]
```

The task still runs (Sprint 152 note: it's the safety net for infrequent-scan environments). Do not delete the class.

---

### FIX-202B-02: Update `EpisodeExpandTask` obsolete attribute

**File:** `Tasks/EpisodeExpandTask.cs:42`
**Estimated effort:** XS
**What:**

Replace:
```cs
[Obsolete("Use DoctorTask instead (Sprint 66)")]
```
With:
```cs
[Obsolete("Superseded by SeriesPreExpansionService (Sprint 66). Retained as fallback.")]
```

---

### FIX-202B-03: Update `FileResurrectionTask` obsolete attribute

**File:** `Tasks/FileResurrectionTask.cs:34`
**Estimated effort:** XS
**What:**

Replace:
```cs
[Obsolete("Use DoctorTask instead (Sprint 66)")]
```
With:
```cs
[Obsolete("Superseded by MarvinTask (Sprint 202). Retained as scheduled safety net.")]
```

---

### FIX-202B-04: Update `CollectionSyncTask` comment

**File:** `Tasks/CollectionSyncTask.cs:23`
**Estimated effort:** XS
**What:**

Replace the comment `// Runs after DoctorTask` with `// Runs after MarvinTask`.

---

## Phase C — Pins → Saves Schema + Model Rename

### FIX-202C-01: Rename `UserItemPin` model → `UserItemSave`

**File:** `Models/UserItemPin.cs` → rename to `Models/UserItemSave.cs`
**Estimated effort:** S
**What:**

1. Rename file to `UserItemSave.cs`.
2. Rename class `UserItemPin` → `UserItemSave`.
3. Rename enum `PinSource` → `SaveSource` (all enum values stay the same: `Discover`, `Playback`, etc.).
4. Any property named `PinSource` on the model → `SaveSource`.

---

### FIX-202C-02: Schema migration — rename table and columns

**File:** `Data/Schema.cs`
**Estimated effort:** S
**What:**

1. In the `CREATE TABLE` SQL for `user_item_pins`, rename the table definition to `user_item_saves`.
2. Rename column `pin_source` → `save_source`.
3. Rename column `pinned_at` → `saved_at` (if present; skip if column doesn't exist).
4. Bump `Schema.CurrentSchemaVersion` by 1.

---

### FIX-202C-03: Database migration step

**File:** `Data/DatabaseManager.cs`
**Estimated effort:** M
**What:**

In the schema upgrade path (wherever the version bump is handled), add a migration step for the new schema version:

```sql
ALTER TABLE user_item_pins RENAME TO user_item_saves;
ALTER TABLE user_item_saves RENAME COLUMN pin_source TO save_source;
-- only if column exists:
ALTER TABLE user_item_saves RENAME COLUMN pinned_at TO saved_at;
```

Also update every SQL string in `DatabaseManager` that references `user_item_pins`, `pin_source`, or `pinned_at` to use the new names.

> ⚠️ **Watch out:** `ALTER TABLE ... RENAME COLUMN` requires SQLite 3.25+. Emby ships a compatible version, but confirm the exact SQLite version in `../emby-beta/` if there's any doubt. The migration must be idempotent — wrap in a `try/catch` or check whether the old table still exists before attempting the rename.

---

### FIX-202C-04: Rename `IPinRepository` → `ISaveRepository`

**File:** `Repositories/Interfaces/IPinRepository.cs` → rename to `Repositories/Interfaces/ISaveRepository.cs`
**Estimated effort:** S
**What:**

1. Rename file.
2. Rename interface `IPinRepository` → `ISaveRepository`.
3. Rename methods: `GetPins()` → `GetSaves()`, `AddPin()` → `AddSave()`, `RemovePin()` → `RemoveSave()`.
4. Update any method parameter types that used `UserItemPin` → `UserItemSave`.

---

### FIX-202C-05: Rename `UserPinRepository` → `UserSaveRepository`

**File:** `Repositories/UserPinRepository.cs` → rename to `Repositories/UserSaveRepository.cs`
**Estimated effort:** S
**What:**

1. Rename file.
2. Rename class `UserPinRepository` → `UserSaveRepository`.
3. Implement `ISaveRepository` (not `IPinRepository`).
4. Update all method names and SQL strings to match Phase C-03 schema rename.
5. Update DI registration in `Plugin.cs` (wherever `UserPinRepository` is registered).

---

### FIX-202C-06: Update all call sites

**Files:** `Services/DiscoverService.cs`, `Tasks/RefreshTask.cs`, `Plugin.cs`, any other file referencing `IPinRepository` / `UserItemPin` / `PinSource`
**Estimated effort:** M
**What:**

For each call site:
- Replace `IPinRepository` / `UserPinRepository` → `ISaveRepository` / `UserSaveRepository`.
- Replace `UserItemPin` → `UserItemSave`.
- Replace `PinSource` → `SaveSource`.
- Replace `AddPin` / `GetPins` / `RemovePin` → `AddSave` / `GetSaves` / `RemoveSave`.
- Replace variable names `pin` / `pins` → `save` / `saves` where they appear in user-facing contexts (method bodies, log messages, JS strings).

---

### FIX-202C-07: Update REST endpoint routes + add redirect shims

**File:** `Services/UserService.cs` (or wherever `/InfiniteDrive/User/Pins` is declared)
**Estimated effort:** S
**What:**

1. Rename the primary endpoint routes:
   - `/InfiniteDrive/User/Pins` → `/InfiniteDrive/User/Saves`
   - `/InfiniteDrive/User/Pins/Remove` → `/InfiniteDrive/User/Saves/Remove`
2. Add obsolete shim methods at the old routes that return `308 Permanent Redirect` to the new routes.
3. Mark shim methods `[Obsolete("Redirect shim — remove in Sprint 205")]`.

---

### FIX-202C-08: Update JS "My Picks" references

**File:** `Configuration/configurationpage.js`
**Estimated effort:** S
**What:**

- Replace all user-visible strings `"My Picks"` → `"Saved"`.
- Replace JS variable names `pin` / `pins` / `pinned` → `save` / `saves` / `saved` where they appear in user-facing contexts (rendered text, aria labels, data attributes).
- Update any `fetch('/InfiniteDrive/User/Pins...')` calls to use the new `/Saves` routes.

> ⚠️ **Watch out:** Internal JS identifiers (local variables, function names) that are not user-visible can be renamed for consistency but don't need to be for this sprint — prioritise the routes and rendered strings.

---

## Phase D — Doc Comment Cleanup

### FIX-202D-01: Clean up "Doctor phase" / "Doctor dashboard" comments

**Files:** `Models/ItemState.cs`, `Models/CatalogItem.cs`, `Data/DatabaseManager.cs`, `Plugin.cs`
**Estimated effort:** S
**What:**

Grep each file for `Doctor` (case-insensitive). For each hit:
- If the comment explains something still true (e.g. "this phase runs orphan cleanup") — replace `Doctor` with `Marvin`.
- If the comment only existed to explain `DoctorTask` integration that is now gone — delete the comment line.
- Do not modify any code logic; doc comments only.

---

## Phase E — Build & Verification

### FIX-202E-01: Build

```
dotnet build -c Release
```
Expected: 0 errors, 0 net-new warnings.

---

### FIX-202E-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `grep -ri "DoctorTask" --include="*.cs" .` | 0 matches | Class deleted Sprint 147 |
| `grep -ri "DeepCleanTask" --include="*.cs" .` | 0 matches | Renamed to MarvinTask |
| `grep -ri "InfiniteDriveDeepClean" .` | 0 matches | Key renamed to InfiniteDriveMarvin |
| `grep -ri "data-es-task=\"doctor\"" Configuration/` | 0 matches | Button deleted |
| `grep -ri "user_item_pins" --include="*.cs" .` | 0 matches | Table renamed |
| `grep -ri "PinSource\|IPinRepository" --include="*.cs" .` | 0 matches (outside shim) | Renamed |
| `grep -i "My Picks" Configuration/` | 0 matches | Vocabulary change |

---

### FIX-202E-03: Manual test — Marvin task key

1. Run `./emby-reset.sh`.
2. Open Emby Admin → Scheduled Tasks.
3. Confirm task is listed as **"InfiniteDrive Marvin"** (not "InfiniteDrive Deep Clean").
4. Click "Summon Marvin" on the Improbability tab.
5. Confirm the task starts (spinner visible) and completes without error in the Emby log.

---

### FIX-202E-04: Manual test — Doctor button gone

1. Open config page → Marvin tab (or wherever the Doctor card was).
2. Confirm no "Run Doctor Now" button is visible.

---

### FIX-202E-05: Manual test — Saves schema migration

1. On a test install with existing `user_item_pins` rows, run `./emby-reset.sh`.
2. Open the SQLite DB: `sqlite3 ~/emby-dev-data/data/infinitedrive.db`.
3. Run `.tables` — confirm `user_item_saves` exists, `user_item_pins` does not.
4. Run `SELECT * FROM user_item_saves LIMIT 5;` — confirm rows migrated with correct column names (`save_source`, `saved_at`).

---

### FIX-202E-06: Manual test — redirect shim

1. `curl -v http://localhost:8096/InfiniteDrive/User/Pins` — confirm `308` response with `Location: /InfiniteDrive/User/Saves`.
2. `curl -v http://localhost:8096/InfiniteDrive/User/Saves` — confirm `200` response with saves JSON.

---

## Rollback Plan

- `git revert` the sprint commit (single commit per CLAUDE.md sprint ritual).
- The schema migration (`ALTER TABLE user_item_pins RENAME TO user_item_saves`) is **not automatically reversible** via git revert. If rollback is needed after the migration has run, manually execute: `ALTER TABLE user_item_saves RENAME TO user_item_pins; ALTER TABLE user_item_pins RENAME COLUMN save_source TO pin_source; ALTER TABLE user_item_pins RENAME COLUMN saved_at TO pinned_at;` in the SQLite DB, then revert the code.
- Scheduled task key reset: Emby will re-register the old key on rollback. No data loss; the 18h default schedule re-applies.

---

## Completion Criteria

- [ ] `Tasks/MarvinTask.cs` exists; `Tasks/DeepCleanTask.cs` deleted
- [ ] Task `Key` = `"InfiniteDriveMarvin"`, display name = `"InfiniteDrive Marvin"`
- [ ] JS `summonMarvin()` uses `'InfiniteDriveMarvin'`
- [ ] "Run Doctor Now" button deleted from HTML
- [ ] `LibraryReadoptionTask`, `EpisodeExpandTask`, `FileResurrectionTask` obsolete attributes updated
- [ ] `CollectionSyncTask` comment updated to reference MarvinTask
- [ ] `TriggerService.cs` comment updated
- [ ] `Models/UserItemSave.cs` exists; `Models/UserItemPin.cs` deleted
- [ ] `enum SaveSource` replaces `enum PinSource`
- [ ] `ISaveRepository` / `UserSaveRepository` replace `IPinRepository` / `UserPinRepository`
- [ ] `user_item_saves` table in schema; migration step written
- [ ] All `DatabaseManager` SQL strings updated
- [ ] `/InfiniteDrive/User/Saves` routes primary; old `/Pins` routes return 308
- [ ] JS "My Picks" strings → "Saved"; JS `/Pins` fetch calls → `/Saves`
- [ ] "Doctor phase" / "Doctor dashboard" doc comments cleaned up
- [ ] `dotnet build -c Release` → 0 errors
- [ ] Grep checklist: all patterns return 0 matches (outside shim)
- [ ] Scheduled task visible as "InfiniteDrive Marvin" in Emby UI
- [ ] SQLite confirms `user_item_saves` table with migrated rows

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | Does `user_item_pins` have a `pinned_at` column or only `pin_source`? Check `Data/Schema.cs` before writing migration. | Dev | Open |
| 2 | Are there any other REST clients (home section JS, mobile app) that call `/InfiniteDrive/User/Pins` directly? If so the 308 shim must stay past Sprint 205. | Dev | Open |
| 3 | `Plugin.cs:62` — what is the "Doctor dashboard" comment referencing? Verify whether the line is in a live code path before deleting. | Dev | Open |

---

## Notes

**Files created:** 2 (`Tasks/MarvinTask.cs`, `Models/UserItemSave.cs`, `Repositories/UserSaveRepository.cs`, `Repositories/Interfaces/ISaveRepository.cs`)
**Files renamed:** 4 (`DeepCleanTask.cs`, `UserItemPin.cs`, `UserPinRepository.cs`, `IPinRepository.cs`)
**Files modified:** ~10 (`configurationpage.js`, `configurationpage.html`, `TriggerService.cs`, `CollectionSyncTask.cs`, `LibraryReadoptionTask.cs`, `EpisodeExpandTask.cs`, `FileResurrectionTask.cs`, `DatabaseManager.cs`, `Schema.cs`, `DiscoverService.cs`, `RefreshTask.cs`, `Plugin.cs`, `UserService.cs`)
**Files deleted:** 0 (renamed only)

**Risk:** MEDIUM — schema migration is irreversible without manual SQL; task key rename causes one-time schedule reset for any admin who customised the Deep Clean schedule. Both are documented breaking changes.
Mitigated by:
1. Migration is additive-by-rename; no rows are deleted.
2. 308 redirect shim preserves API compatibility for one release.
3. Full `./emby-reset.sh` test before commit.
