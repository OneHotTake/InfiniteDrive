# Sprint 206 Findings — Claude Research

**Generated:** 2026-04-11
**Sprint:** 206 (Data & Logic Validation)
**Status:** Research Complete

---

## Executive Summary

Sprint 206 validation found that **the codebase doesn't compile** (14 errors) and the per-user save system that Sprints 202-206 assumed **was never implemented**. The InfiniteDriveChannel was planned in Sprint 204 but never committed. Sprint 204's un-gating partially shipped with a wrong method call that broke the build.

---

## P0: Build Broken (14 Errors)

**All errors in `Services/DiscoverService.cs`.**

Root cause: Sprint 204 un-gating calls `StatusService.RequireAuthenticated()` (6 occurrences) but the method lives on `AdminGuard`, not `StatusService`. Correct call: `AdminGuard.RequireAuthenticated(_authCtx, Request)`.

Additional errors:
- `DiscoverCatalogEntry.RatingLabel` property referenced but doesn't exist (line 1133)
- `long?` to `string` type conversion error (line 1203)
- Nullability mismatch paired with each `StatusService.RequireAuthenticated` call (6 occurrences)

**Verification:** `dotnet build -c Release` → 14 errors, 0 warnings.

---

## Architecture Findings

### Save System: Global, Not Per-User

| Component | What Exists | What's Missing |
|-----------|-------------|----------------|
| Save table | `media_items.saved` (boolean column) | `user_item_saves` junction table |
| Save service | `SavedService.SaveItemAsync(itemId)` — global | No userId parameter |
| Save endpoint | `POST /Discover/AddToLibrary` — writes .strm + global flag | No per-user tracking |
| Unsave endpoint | None | No endpoint at all |
| Save query | `GetItemsBySavedAsync(bool)` — returns ALL saved items | No per-user query |

**Current save columns on `media_items`:** `saved` (bool), `saved_at`, `saved_by` ("user"/"admin"), `save_reason`, `saved_season`
**All global. No user ID stored.**

### InfiniteDriveChannel: Never Committed

- Planned in Sprint 204 (`.ai/sprints/sprint-204.md`)
- `Services/InfiniteDriveChannel.cs` appears in git status as untracked but file doesn't exist on disk
- 0 commits in git history for this file
- REPO_MAP.md references it as if it exists (stale documentation)

### Block System: Incomplete

- `AdminService.cs:113` — unblock endpoint exists (`POST /Admin/UnblockItems`)
- **No block endpoint** — admins cannot block items via API
- `SavedService.BlockItemAsync()` exists (lines 80-99) but has **zero callers** — dead code
- Blocking doesn't filter items from Browse/Search (only `discover_catalog.blocked_at IS NULL` filter exists)
- Blocking doesn't delete .strm files

### Marvin Task: Filesystem Only

Three phases, none save-related:
1. Validation — .strm integrity, orphan file cleanup
2. Enrichment — NFO metadata with retry backoff
3. Token renewal — .strm URL refresh

No orphaned save cleanup. No database row cleanup.

---

## Dead Code / Stale References

| Item | Status | Location |
|------|--------|----------|
| Pin repository classes | Clean (0 matches) | Renamed to Save variants |
| Doctor task references | Clean (0 matches in code/HTML) | Fully removed |
| User tab HTML elements | Clean (0 matches) | Removed in Sprint 205 |
| `.es-discover-*` CSS | **23 rules remain** (lines 72-94) | Dead CSS in configurationpage.html |
| `es-discover-onboarding` div | **1 match** (line 1382) | Empty div leftover |
| `SavedService.BlockItemAsync()` | Dead code (0 callers) | Lines 80-99 |
| `SavedService.UnblockItemAsync()` | Dead code (0 callers) | Lines 106-125 |
| `ApplyParentalFilter()` | Defined but never invoked | Line 1122 |

---

## User Context Available

- `AdminGuard.RequireAuthenticated(_authCtx, Request)` — returns deny object if not authenticated
- `_authCtx.GetAuthorizationInfo(Request).User?.Id.ToString("N")` — Emby user GUID
- `DiscoverService.TryGetCurrentUserId()` at line 835 — already extracts user ID
- Pattern established in `home_section_tracking` table for per-user data

---

## Schema State

- `Schema.CurrentSchemaVersion = 23` (constant for clean installs)
- Runtime migrations go up to V25
- Next version: V26
- Migration pattern: `if (version < N)` blocks with `ExecuteInline(conn, sql)`

---

## Key Consumers of `media_items.saved`

These depend on the global saved flag and must keep working:

- `SyncTask` — uses saved flag to decide sync behavior
- `RemovalService` / `RemovalPipeline` — uses saved flag to enter grace period vs delete
- `YourFilesConflictResolver` — uses saved flag for conflict resolution
- `SavedBoxSetService` — queries `GetItemsBySavedAsync(true)` (TODO, not wired)

The denormalized `media_items.saved` boolean should remain as "any user saved this" — used by these consumers. The per-user tracking goes in a new `user_item_saves` table.

---

## Sprint 207 Scope (Design)

Based on user direction:
1. **Save == Add to Library** — one action, not two
2. **Breaking change, no migrations** — clean DB required
3. **Remove** `saved_by`, `save_reason`, `saved_season` from `media_items` (keep `saved` boolean + `saved_at`)
4. **Channel**: Users see only their lists. Admins see all sources + their lists.
5. **Fix build** — prerequisite for everything else

---

**Research Conducted By:** Claude (Opus 4.6)
