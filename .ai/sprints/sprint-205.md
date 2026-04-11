# Sprint 205 — Remove User Tabs from Config Page

**Version:** v0.45 | **Status:** Ready | **Risk:** LOW | **Depends:** Sprint 204
**Owner:** Frontend | **Target:** v0.45 | **PR:** TBD

---

## Overview

Sprint 203 hid the `Discover`, `My Picks`, and `My Lists` tab buttons. Sprint 204 delivered the `InfiniteDriveChannel` giving non-admin users a real browse surface in Emby Channels. Now that the channel is proven and shipping, the dead tab bodies in `configurationpage.html` and their JS handlers can be deleted outright.

**Problem statement:** The config page still contains ~150 lines of HTML for three user-facing tabs (Discover, My Picks, My Lists) that are hidden and whose functionality has moved to the native Emby channel. The corresponding JS handlers for `es-discover-*`, `es-mypicks-*`, `es-mylists-*` are dead code that add noise and maintenance surface.

**Why now:** The channel shipped in Sprint 204 and is verified working. Deleting the dead code is low-risk, low-effort, and keeps the codebase honest.

**High-level approach:** Delete the three hidden tab bodies from HTML. Delete the corresponding JS event handlers. Update `REPO_MAP.md` to note user content has moved to `InfiniteDriveChannel`.

### What the Research Found

- Sprint 203 set `style="display:none"` on the three user tab buttons. Their bodies remained in HTML pending this sprint.
- The three tab body regions are estimated at HTML lines 630–674 (Discover), 1626–1649 (My Picks), 1650–1676 (My Lists) — exact lines must be re-confirmed by reading the file at sprint time (line numbers shift with each sprint's edits).
- JS handlers to delete: filter chip handlers for `es-discover-*`, search wiring for `es-mypicks-*` and `es-mylists-*`, any function exclusively serving these three tabs.
- The `/InfiniteDrive/User/Saves` redirect shim (added in Sprint 202) should also be evaluated for deletion in this sprint if enough time has passed. The shim returns `308 Permanent Redirect` from the old `/Pins` routes — delete if no known clients still use the old routes.

### Breaking Changes

- The Discover/My Picks/My Lists UI tabs are permanently removed from the config page. Users who navigate to `?tab=discover` will hit an unknown tab (no redirect). This is acceptable — users should be using the native Emby channel from Sprint 204.
- The old `/InfiniteDrive/User/Pins` redirect shim may be deleted, permanently breaking any cached client that didn't follow the 308 in Sprint 202. Documented as a known one-release deprecation window.

### Non-Goals

- ❌ Any new channel features — Sprint 204 delivered the channel.
- ❌ Admin page redesign — completed in Sprint 203.
- ❌ RSS feed subscription UX — still deferred.

---

## Phase A — Delete User Tab HTML Bodies

### FIX-205A-01: Delete Discover tab body

**File:** `Configuration/configurationpage.html`
**Estimated effort:** S
**What:**

Locate and delete the `<div id="tab-content-discover">` (or equivalent) block in its entirety. Confirm the block boundary by reading the file — identify the outermost opening `<div>` and its matching closing `</div>` using indentation/id patterns before deleting.

After deletion, run `grep -i "es-tab-content-discover" Configuration/configurationpage.html` — expect 0 matches.

---

### FIX-205A-02: Delete My Picks tab body

**File:** `Configuration/configurationpage.html`
**Estimated effort:** S
**What:**

Locate and delete the `<div id="tab-content-mypicks">` (or equivalent) block. Confirm exact boundaries before deleting.

After deletion: `grep -i "es-tab-content-mypicks" Configuration/configurationpage.html` → 0 matches.

---

### FIX-205A-03: Delete My Lists tab body

**File:** `Configuration/configurationpage.html`
**Estimated effort:** S
**What:**

Locate and delete the `<div id="tab-content-mylists">` (or equivalent) block. Confirm exact boundaries before deleting.

After deletion: `grep -i "es-tab-content-mylists" Configuration/configurationpage.html` → 0 matches.

---

### FIX-205A-04: Delete hidden tab buttons

**File:** `Configuration/configurationpage.html`
**Estimated effort:** XS
**What:**

Delete the three hidden tab button elements added in Sprint 203:
```html
<button data-tab="discover" style="display:none">Discover</button>
<button data-tab="mypicks" style="display:none">My Picks</button>
<button data-tab="mylists" style="display:none">My Lists</button>
```

---

## Phase B — Delete Dead JS Handlers

### FIX-205B-01: Delete Discover JS handlers

**File:** `Configuration/configurationpage.js`
**Estimated effort:** S
**What:**

Delete all event handlers and functions exclusively serving the Discover tab:
- Filter chip click handlers referencing `es-discover-filter-*`
- Search input/submit handlers referencing `es-discover-search-*`
- Any `initDiscoverTab(view, cfg)` function
- Any `refreshDiscoverTab(view)` function
- `fetch('/InfiniteDrive/Discover/Browse...')` calls that were in the Discover tab initialisation path (not in the channel — the channel calls the endpoint server-side)

> ⚠️ **Watch out:** Some Discover-related fetch calls may be shared with admin diagnostic functions (e.g. `TestStreamResolution`). Only delete handlers that are exclusively for the deleted UI tab. Read each function before deleting.

---

### FIX-205B-02: Delete My Picks / My Lists JS handlers

**File:** `Configuration/configurationpage.js`
**Estimated effort:** S
**What:**

Delete all handlers and functions exclusively serving the My Picks and My Lists tabs:
- Pin/save list rendering functions (`renderPins`, `renderSaves`, etc.) that were driving the My Picks tab display
- `es-mypicks-*` and `es-mylists-*` selectors
- Any RSS feed list management functions tied to the My Lists tab UI (note: the underlying RSS feed *feature* may still exist — only delete the UI tab handlers)

---

### FIX-205B-03: Delete `/Pins` redirect shim (conditional)

**File:** `Services/UserService.cs` (or wherever the Sprint 202 shim lives)
**Estimated effort:** XS
**What:**

Evaluate whether the `[Obsolete]` shim methods returning `308` from `/InfiniteDrive/User/Pins` and `/InfiniteDrive/User/Pins/Remove` can be safely deleted.

Criteria for deletion:
- Sprint 202 shipped ≥1 release ago (it did — Sprints 202, 203, 204 all shipped first)
- No known client (home section JS, mobile, external tool) still calls the old routes

If safe to delete: remove both shim methods. If uncertain: leave them and note for Sprint 206 review.

---

## Phase C — REPO_MAP Update

### FIX-205C-01: Update REPO_MAP.md

**File:** `.ai/REPO_MAP.md`
**Estimated effort:** XS
**What:**

Update the `Configuration/configurationpage.html` Structure section:
- Remove `Discover tab`, `My Picks tab`, `My Lists tab` entries entirely
- Add note: "User content surfaces moved to `Services/InfiniteDriveChannel.cs` (Sprint 204)"
- Confirm the 5-tab description from Sprint 203 is still accurate

---

## Phase D — Build & Verification

### FIX-205D-01: Build

```
dotnet build -c Release
```
Expected: 0 errors, 0 net-new warnings.

---

### FIX-205D-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `grep -i "es-tab-content-discover" Configuration/configurationpage.html` | 0 | Tab body deleted |
| `grep -i "es-tab-content-mypicks" Configuration/configurationpage.html` | 0 | Tab body deleted |
| `grep -i "es-tab-content-mylists" Configuration/configurationpage.html` | 0 | Tab body deleted |
| `grep -i "data-tab=\"discover\"" Configuration/configurationpage.html` | 0 | Button deleted |
| `grep -i "data-tab=\"mypicks\"" Configuration/configurationpage.html` | 0 | Button deleted |
| `grep -i "data-tab=\"mylists\"" Configuration/configurationpage.html` | 0 | Button deleted |

---

### FIX-205D-03: Manual test — config page

1. `./emby-reset.sh`
2. Open config page. Confirm exactly 5 visible tabs: **Setup, Overview, Settings, Content, Marvin**.
3. Confirm no Discover/My Picks/My Lists tabs visible (not even hidden elements) in browser DevTools.

---

### FIX-205D-04: Manual test — channel still works

1. Log in as a non-admin Emby user.
2. Navigate to **Channels → InfiniteDrive**.
3. Confirm Lists and Saved folders still work (regression check from Sprint 204).

---

## Rollback Plan

- `git revert` the sprint commit. No schema changes — rollback is purely a code revert.
- If the redirect shim was deleted and a client breaks, restore the shim methods from the Sprint 202 commit.

---

## Completion Criteria

- [ ] `<div id="tab-content-discover">` deleted from HTML
- [ ] `<div id="tab-content-mypicks">` deleted from HTML
- [ ] `<div id="tab-content-mylists">` deleted from HTML
- [ ] Hidden tab buttons for discover/mypicks/mylists deleted from HTML
- [ ] Discover JS handlers deleted from JS
- [ ] My Picks / My Lists JS handlers deleted from JS
- [ ] `/Pins` redirect shim evaluated and deleted (or explicitly deferred with note)
- [ ] `dotnet build -c Release` → 0 errors
- [ ] Grep checklist: all tab-content patterns return 0 matches
- [ ] Config page shows exactly 5 tabs, no user content surfaces
- [ ] Channel (Sprint 204) still works for non-admin users
- [ ] `.ai/REPO_MAP.md` updated

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | Are there any Emby home section plugins or external tools that call `/InfiniteDrive/User/Pins`? If yes, keep the 308 shim. | Dev | Open |
| 2 | Exact HTML line numbers for tab bodies — must be re-confirmed at sprint time after Sprint 203 and 204 edits shift line numbers. | Dev | Open |

---

## Notes

**Files created:** 0
**Files modified:** 3 (`Configuration/configurationpage.html`, `Configuration/configurationpage.js`, `.ai/REPO_MAP.md`)
**Files deleted:** 0 (code only removed from existing files)

**Risk:** LOW — purely a deletion sprint with no new logic. The channel from Sprint 204 provides the replacement functionality; no user feature is removed, only the dead admin-page surface.
Mitigated by:
1. Sprint 204 must be verified working before Sprint 205 executes.
2. Grep checklist confirms no orphaned references remain.
3. Full regression check of the channel after deletions.
