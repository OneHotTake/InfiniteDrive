# Sprint 204 — InfiniteDriveChannel (IChannel) + DiscoverService Un-gating

**Version:** v0.44 | **Status:** Ready | **Risk:** MEDIUM | **Depends:** Sprint 202, Sprint 203
**Owner:** Backend | **Target:** v0.44 | **PR:** TBD

---

## Overview

Every `DiscoverService` browse/search/detail endpoint currently calls `AdminGuard.RequireAdmin()`, so non-admin users see empty Discover tabs they can't use. The original Sprint 54 design had a `DiscoverChannel : IChannel` that was never carried over to InfiniteDrive. The USER_DISCOVER.md un-gating spec (Sprint 148) was also never shipped. Sprint 204 delivers both: a thin `IChannel` adapter that puts "Lists" and "Saved" into Emby's native Channels section, and un-gated browse/search/detail endpoints with per-user parental rating enforcement.

**Problem statement:** Non-admin users have no browse surface for InfiniteDrive content. The config page user tabs (`Discover`, `My Picks`, `My Lists`) are admin-only by accident (AdminGuard) and by location (config page = admin surface). The `ISaveRepository` (née `IPinRepository`) exists in the DB but is never written by a non-admin user.

**Why now:** Sprint 202 renamed pins→saves and Sprint 203 restructured the admin page. The channel can now reference `ISaveRepository` and `SaveSource` without triggering a rename mid-sprint. The admin page is clean enough that hiding the user tabs (Sprint 205) makes sense immediately after the channel ships.

**High-level approach:** Write `Services/InfiniteDriveChannel.cs` implementing `IChannel` with two root folders (`Lists`, `Saved`). The channel is a thin adapter over existing services — no new business logic. Un-gate four `DiscoverService` endpoints, replacing `AdminGuard.RequireAdmin()` with user-identity reads from the auth context. Add server-side parental rating filtering to all browse/search/detail paths. Clean up the dead `PluginPageInfo` stubs in `Plugin.cs`.

### What the Research Found

- The legacy `DiscoverChannel.cs` in `../embyStreams/Services/DiscoverChannel.cs` is the reference implementation from Sprint 54. Key compile fixes are documented in `DISCOVERY_CHANNEL_FIX_SUMMARY.md`.
- `IChannel` is in `MediaBrowser.Controller.Channels`. The channel is auto-discovered by Emby via DI — no manual registration needed beyond implementing the interface.
- `query.Id` routing for root vs sub-folder requires reflection (`query.GetType().GetProperty("Id")?.GetValue(query)`) — Emby's internal `ChannelItemQuery` does not expose `Id` as a public property in the SDK version in use. See `DISCOVERY_CHANNEL_FIX_SUMMARY.md` for the exact reflection pattern.
- `MediaBrowser.Model.Channels.ChannelMediaType.Video` is the correct enum value (not `.ToString()`). Using `.ToString()` causes a runtime serialisation error.
- `DatabaseManager.GetDiscoverCatalogAsync(int limit, int offset, string? mediaType, string? sortBy)` already supports filtering and paging — no changes needed to this method.
- `DatabaseManager.SearchDiscoverCatalogAsync` already supports `mediaType` parameter.
- `CinemetaProvider` in `Services/` already handles per-item metadata fetches from Cinemeta.
- `Plugin.CooldownGate` / `CooldownKind.Cinemeta` (Sprint 155) is the rate-limit gate for Cinemeta rail fetches — must be applied in the channel's `Lists` folder fetch.
- `ISaveRepository.GetSaves(userId)` returns saves for a specific user — the `Saved` folder is per-caller.
- Parental rating filter: Emby user object has a `MaxParentalRating` property. The filter must be applied server-side (SQL `WHERE` clause for catalog queries, in-memory filter for Cinemeta responses). Fail-closed: unknown ratings are treated as restricted.
- `AdminGuard.RequireAdmin()` is in `Services/AdminGuard.cs`. The replacement pattern for user-auth is `_authCtx.GetAuthorizationInfo(Request).UserId` (string, nullable).
- `Plugin.cs:293-332` contains stub `PluginPageInfo` entries for `Wizard`, `ContentManagement`, `MyLibrary` with no `EmbeddedResourcePath`. These cause Emby to attempt resource loads that always 404 — safe to delete.

### Breaking Changes

- Four endpoints lose admin-only gating: `GET /Discover/Browse`, `GET /Discover/Search`, `GET /Discover/Detail`, `POST /Discover/AddToLibrary`. Any tool that relied on these being admin-only (e.g. a custom script) will now accept non-admin tokens.
- `POST /Discover/AddToLibrary` now writes a `user_item_saves` row keyed to the calling user's ID. Admin calls continue to work identically.

### Non-Goals

- ❌ User RSS feed subscription UI (paste Trakt/MDBList URL) — deferred. The channel only surfaces *existing* `user_catalogs` rows.
- ❌ Home section / home row integration — channel is the minimum viable user surface.
- ❌ Delete user tabs from config page — Sprint 205.
- ❌ Rewrite `DiscoverService` business logic — channel is a presentation adapter only.
- ❌ Channel item art / thumbnail fetching beyond what `CinemetaProvider` already provides.

---

## Phase A — InfiniteDriveChannel Service

### FIX-204A-01: Create `Services/InfiniteDriveChannel.cs`

**File:** `Services/InfiniteDriveChannel.cs` (create)
**Estimated effort:** L
**What:**

Implement `MediaBrowser.Controller.Channels.IChannel`. Reference the legacy `../embyStreams/Services/DiscoverChannel.cs` for structure and the compile-fix patterns in `DISCOVERY_CHANNEL_FIX_SUMMARY.md`.

**Channel metadata:**
```cs
public string Name => "InfiniteDrive";
public string Description => "Browse curated lists and your saved items.";
public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
```

**Folder hierarchy:**
```
InfiniteDrive (root)
├── Lists       (folder)
│   ├── Top Movies          [From Cinemeta]
│   ├── Top Series          [From Cinemeta]
│   ├── Top Anime           [From Cinemeta]
│   ├── <each enabled AIOStreams sub-source>   [From AIOStreams: <addon name>]
│   └── <each active user_catalogs row>        [From <OwnerDisplayName>'s Trakt/MDBList]
└── Saved       (folder)
    └── <flat list of user_item_saves for calling user>
```

**Root folder routing:**
- Use reflection to read `query.Id` (see DISCOVERY_CHANNEL_FIX_SUMMARY.md pattern).
- `null` or empty Id → return two `ChannelFolderItem` entries: `Lists` and `Saved`.
- Id == `"lists"` → return list rows (each row is itself a `ChannelFolderItem` or `ChannelItem`).
- Id == `"saved"` → return flat list of `user_item_saves` for `query.UserId`.
- Id matches a list slug (e.g. `"cinemeta-movies"`) → return items in that list.

**Source attribution (hard rule):**
Every list-level item's `Overview` / subtitle must carry its origin:
- Cinemeta defaults: `"From Cinemeta"`
- AIOStreams sub-source: `"From AIOStreams: <addon name>"`
- User catalog: `"From <OwnerDisplayName>'s <service name>"`

**Parental rating filter:**
- Read calling user's `MaxParentalRating` from Emby user object.
- Filter applied to all item results before returning.
- Unknown/unrated items: treat as restricted (fail-closed).

**CooldownGate:**
- Wrap Cinemeta rail fetches in `Plugin.CooldownGate` with `CooldownKind.Cinemeta`.

**Existing code to reuse (do not duplicate):**
- `DatabaseManager.GetDiscoverCatalogAsync` — Lists items
- `DatabaseManager.SearchDiscoverCatalogAsync` — search within Lists
- `ISaveRepository.GetSaves(userId)` — Saved items
- `CinemetaProvider` — per-item metadata

> ⚠️ **Watch out:** The `ChannelMediaType` must be set to `MediaBrowser.Model.Channels.ChannelMediaType.Video` (enum value), not the string `"Video"`. The Sprint 54 doc explicitly calls this out as a compile/runtime trap.

> ⚠️ **Watch out:** `IChannel` requires `GetChannelFeatures()` to return a `ChannelFeatures` object. Return a minimal object — don't claim `SupportsLatestMedia` or `SupportsSearch` unless the implementation is complete.

---

## Phase B — DiscoverService Un-gating

### FIX-204B-01: Un-gate `GET /Discover/Browse`

**File:** `Services/DiscoverService.cs:301`
**Estimated effort:** S
**What:**

Replace `AdminGuard.RequireAdmin()` with:
```cs
var userId = _authCtx.GetAuthorizationInfo(Request).UserId;
if (string.IsNullOrEmpty(userId))
    return Unauthenticated();
```

Add parental rating filter to the browse results before returning:
```cs
results = ApplyParentalFilter(results, userId);
```

`ApplyParentalFilter` reads the user's `MaxParentalRating` from Emby's user manager and filters the result set. See FIX-204B-05 for the shared filter implementation.

---

### FIX-204B-02: Un-gate `GET /Discover/Search`

**File:** `Services/DiscoverService.cs:338`
**Estimated effort:** S
**What:**

Same pattern as FIX-204B-01. Replace `AdminGuard.RequireAdmin()` with user-auth read. Apply parental filter to search results.

---

### FIX-204B-03: Un-gate `GET /Discover/Detail`

**File:** `Services/DiscoverService.cs:644`
**Estimated effort:** S
**What:**

Replace `AdminGuard.RequireAdmin()` with user-auth read. After loading the item, check it passes the caller's parental rating ceiling. If the item's rating exceeds the ceiling, return `404 Not Found` (fail-closed — do not reveal the item exists).

---

### FIX-204B-04: Un-gate `POST /Discover/AddToLibrary`

**File:** `Services/DiscoverService.cs:677`
**Estimated effort:** S
**What:**

Replace `AdminGuard.RequireAdmin()` with user-auth read. Enforce parental filter before accepting the save: if the requested item exceeds the caller's rating ceiling, return `403 Forbidden`.

When the save is accepted, write the `user_item_saves` row with `save_source = SaveSource.Discover` and `user_id = userId` (the calling user's ID, not a hardcoded admin ID).

---

### FIX-204B-05: Shared parental rating filter

**File:** `Services/DiscoverService.cs` (private helper) or `Services/ParentalFilterHelper.cs` (new, if reuse across channel is needed)
**Estimated effort:** M
**What:**

Implement `ApplyParentalFilter(IEnumerable<CatalogItem> items, string userId)`:

1. Load user from Emby user manager by `userId`.
2. Read `user.Policy.MaxParentalRating` (int, nullable — null means unrestricted).
3. Map each item's `rating_label` string (e.g. `"PG-13"`, `"R"`) to an integer using a static lookup table:
   - `G` = 0, `TV-Y` = 0, `TV-G` = 100
   - `PG` = 200, `TV-PG` = 300
   - `PG-13` = 400, `TV-14` = 500
   - `R` = 600, `TV-MA` = 700
   - `NC-17` = 800
   - Unknown / null → treat as `999` (fail-closed)
4. Exclude items where `itemRating > maxRating`.
5. For Detail and AddToLibrary (single-item paths), return 404/403 rather than an empty list.

> ⚠️ **Watch out:** The rating values in `catalog_items` may not be populated for all items. Fail-closed: if `rating_label` is null/empty, treat as restricted (excluded for users with any ceiling).

---

## Phase C — Plugin.cs Cleanup

### FIX-204C-01: Delete dead `PluginPageInfo` stubs

**File:** `Plugin.cs:293-332`
**Estimated effort:** S
**What:**

Delete the stub `PluginPageInfo` entries for `Wizard`, `ContentManagement`, `MyLibrary` that have no `EmbeddedResourcePath`. These cause Emby to log 404 warnings on every page load. Identify the exact lines by reading the `GetPages()` method body before editing.

Keep only the real registrations that have a valid `EmbeddedResourcePath` pointing to an embedded resource.

---

## Phase D — Build & Verification

### FIX-204D-01: Build

```
dotnet build -c Release
```
Expected: 0 errors, 0 net-new warnings.

---

### FIX-204D-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `grep -n "AdminGuard.RequireAdmin" Services/DiscoverService.cs` | 2 lines only | Lines 945, 1046 (diagnostic endpoints stay admin) |
| `grep -rn "InfiniteDriveChannel" --include="*.cs" .` | ≥1 (new file) | Channel registered |
| `grep -n "PluginPageInfo" Plugin.cs` | Only valid entries | Stubs deleted |

---

### FIX-204D-03: Manual test — channel visible

1. `./emby-reset.sh`
2. Log in as a **non-admin** Emby user.
3. Navigate to **Channels** in Emby web UI.
4. Confirm "InfiniteDrive" channel is visible.
5. Open the channel. Confirm two folders: **Lists**, **Saved**.

---

### FIX-204D-04: Manual test — Lists folder attribution

1. Open **Lists** folder.
2. Confirm rows are visible within 2s.
3. Confirm each row shows its origin in subtitle/overview: `From Cinemeta`, `From AIOStreams`, or `From <Owner>'s Trakt`.

---

### FIX-204D-05: Manual test — parental rating enforcement

1. Create a non-admin Emby user with **PG-13** parental ceiling.
2. Log in as that user.
3. Navigate to Channel → Lists.
4. Confirm no R-rated items visible in any list.
5. `curl -u <pg13user>:<pass> 'http://localhost:8096/InfiniteDrive/Discover/Detail?id=<R-rated-id>'` → expect `404`.
6. `curl -u <pg13user>:<pass> -X POST 'http://localhost:8096/InfiniteDrive/Discover/AddToLibrary?id=<R-rated-id>'` → expect `403`.
7. Log in as admin. Same Detail/AddToLibrary URLs return `200`.

---

### FIX-204D-06: Manual test — save end-to-end

1. Log in as non-admin user.
2. In Channel → Lists → Top Movies, click an item and "Save to Library".
3. Navigate to Channel → **Saved**.
4. Confirm the just-saved item appears in Saved.
5. Query SQLite: `SELECT * FROM user_item_saves WHERE user_id = '<id>';` — confirm row exists with `save_source = 'Discover'`.

---

## Rollback Plan

- `git revert` the sprint commit. No schema changes in this sprint — rollback is purely a code revert.
- Un-gating four endpoints is the highest-risk change. If a security issue is discovered post-deploy, the immediate mitigation is to re-add `AdminGuard.RequireAdmin()` to the four endpoints and restart Emby — a 5-minute fix.

---

## Completion Criteria

- [ ] `Services/InfiniteDriveChannel.cs` created, implements `IChannel`
- [ ] Channel registered in Emby (auto-discovered via DI)
- [ ] Channel shows two root folders: Lists, Saved
- [ ] Lists rows carry source attribution in Overview/subtitle field
- [ ] Cinemeta rail fetches gated by `Plugin.CooldownGate`
- [ ] `GET /Discover/Browse` — non-admin users authenticated, parental filter applied
- [ ] `GET /Discover/Search` — non-admin users authenticated, parental filter applied
- [ ] `GET /Discover/Detail` — non-admin users authenticated; R-item returns 404 to PG-13 user
- [ ] `POST /Discover/AddToLibrary` — non-admin users authenticated; R-item returns 403 to PG-13 user; saves write `user_item_saves` row with correct `user_id`
- [ ] `GET /Discover/TestStreamResolution` — still admin-only
- [ ] `GET /Discover/DirectStreamUrl` — still admin-only
- [ ] Dead `PluginPageInfo` stubs deleted from `Plugin.cs`
- [ ] `dotnet build -c Release` → 0 errors
- [ ] Non-admin user can browse Lists, see attribution, save an item, see it in Saved
- [ ] PG-13 user never sees R-rated items on any surface

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | Exact `query.Id` reflection pattern — confirm `DISCOVERY_CHANNEL_FIX_SUMMARY.md` is still applicable to the current Emby SDK version in `../emby-beta/`. | Dev | Open |
| 2 | Does `catalog_items` have a `rating_label` column? If not, what column holds the parental rating string? Check `Data/Schema.cs` before writing the filter. | Dev | Open |
| 3 | What is the `user_catalogs` table schema? Confirm owner attribution column names for the "From <Owner>'s Trakt" row labels. | Dev | Open |
| 4 | Does `ISaveRepository.GetSaves` take a `userId` parameter, or is it session-scoped? Confirm after Sprint 202 renames. | Dev | Open |
| 5 | `CinemetaProvider` — does it have a method that returns a list of "top" items (Top Movies/Series/Anime rails), or only per-item metadata? If not, how does the channel populate the default rails? | Dev | Open |

---

## Notes

**Files created:** 1 (`Services/InfiniteDriveChannel.cs`)
**Files modified:** 2 (`Services/DiscoverService.cs`, `Plugin.cs`)
**Files deleted:** 0

**Risk:** MEDIUM — new IChannel registration could conflict with existing Emby channel discovery if the class name or DI wiring is wrong. Un-gating four endpoints widens the attack surface; parental filter must be correct to avoid content leakage.
Mitigated by:
1. Reference the battle-tested Sprint 54 `DiscoverChannel.cs` implementation.
2. Parental filter is fail-closed (unknown ratings excluded).
3. The two diagnostic endpoints (`TestStreamResolution`, `DirectStreamUrl`) remain admin-only.
4. Full manual test with a PG-13 user before commit.
