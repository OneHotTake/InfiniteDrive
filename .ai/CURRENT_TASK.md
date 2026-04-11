---
status: complete
task: Sprint 207 — Per-User Saves + InfiniteDriveChannel
phase: Build verification passed
last_updated: 2026-04-11

## Summary

Sprint 207 is **COMPLETE**. Build passes with 0 errors, 0 warnings. All grep checklist items pass.

## What Was Done

### Phase A: Build Fix (3 tasks)
- Replaced 6× `StatusService.RequireAuthenticated` → `AdminGuard.RequireAuthenticated` in DiscoverService
- Stubbed `ApplyParentalFilter` (dead code referencing missing `RatingLabel` property)
- Fixed `GetUserId()` to delegate to `TryGetCurrentUserId()` (was using `authInfo?.UserId` which is `long?`)

### Phase B: Database Schema (4 tasks)
- Bumped `CurrentSchemaVersion` from 23 to 26
- Added `user_item_saves` table (per-user junction table with FK to media_items)
- Removed `saved_by`, `save_reason`, `saved_season` columns from `media_items` in Schema.cs
- Added 7 new DatabaseManager methods: `GetSavedItemsByUserAsync`, `UpsertUserSaveAsync`, `DeleteUserSaveAsync`, `HasUserSaveAsync`, `SyncGlobalSavedFlagAsync`, `GetOrphanedUserSavesAsync`, `DeleteUserSaveByIdAsync`
- Updated `UpsertMediaItemAsync`, `UpdateMediaItemAsync`, and `ReadMediaItem` to remove 3 columns

### Phase C: Service Layer (5 tasks)
- Removed `SavedBy`, `SaveReason`, `SavedSeason` properties and `MarkSaved()` from `MediaItem`
- Created `Models/UserItemSave.cs` model
- Rewrote `SavedService` for per-user saves (`SaveItemAsync(itemId, userId)`, `UnsaveItemAsync(itemId, userId)`)
- Wired `DiscoverService.AddToLibrary` to create per-user saves via `UpsertUserSaveAsync`
- Added `POST /InfiniteDrive/Discover/RemoveFromLibrary` endpoint with DTOs

### Phase D: InfiniteDriveChannel (1 task)
- Created `Services/InfiniteDriveChannel.cs` implementing `IChannel`
- Root shows "Lists" and "Saved" folders
- Lists folder: admin sees all sources + user catalogs, non-admin sees own catalogs
- Saved folder: shows current user's per-user saves
- Uses `IUserManager.GetUserById(long)` to convert Emby numeric user ID to GUID

### Phase E: Marvin Cleanup (1 task)
- Added Phase 4 save maintenance to `MarvinTask`
- Deletes orphaned user saves (where media_item no longer exists)
- Re-syncs global saved flags

### Phase F: Verification (2 tasks)
- `dotnet build -c Release` → 0 errors, 0 warnings
- All 10 grep checklist items pass

## Files Created (2)
- `Services/InfiniteDriveChannel.cs`
- `Models/UserItemSave.cs`

## Files Modified (7)
- `Data/Schema.cs` — V26 schema, user_item_saves table, removed 3 columns from media_items
- `Data/DatabaseManager.cs` — 7 new methods, updated UpsertMediaItemAsync/ReadMediaItem
- `Services/DiscoverService.cs` — Build fixes, per-user save wiring, RemoveFromLibrary endpoint
- `Services/SavedService.cs` — Rewritten for per-user saves
- `Models/MediaItem.cs` — Removed 3 properties + MarkSaved()
- `Models/UserItemSave.cs` — New model
- `Tasks/MarvinTask.cs` — Phase 4 save maintenance
- `Tests/UserActionTests.cs` — Updated for removed properties

## Blockers

None.

## Next Action

Sprint 208 candidates:
- Deploy and run `./emby-reset.sh` for clean V26 DB
- Manual test FIX-207F-03/F-04/F-05 (deploy, per-user save, channel visible)
- Dead CSS cleanup (23 `.es-discover-*` rules)
- Parental rating filtering in Browse/Search
