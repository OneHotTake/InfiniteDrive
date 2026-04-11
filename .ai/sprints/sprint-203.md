# Sprint 203 — Admin Page Restructure + Catalogs→Sources Rename

**Version:** v0.43 | **Status:** Ready | **Risk:** MEDIUM | **Depends:** Sprint 202
**Owner:** Frontend | **Target:** v0.43 | **PR:** TBD

---

## Overview

`Configuration/configurationpage.html` currently has 8 tabs (`Setup`, `Discover`, `My Picks`, `Health`, `Improbability Drive`, `Blocked Items`, `Content Mgmt`, `Settings`) with no clear ownership model. `Settings` alone has 7 accordions hiding duplicated source configuration. "Catalogs" means "a remote source I subscribe to" in admin surfaces and "a row of content to browse" in user surfaces — the same word for two different concepts aimed at two different audiences.

**Problem statement:** Admin page is a mixed-metaphor god-surface. Operators can't find where to configure sources without diving through accordion stacks. Source configuration is duplicated across the Catalog Sync accordion and the Setup wizard Step 3. The word "Catalog" is used in admin contexts where "Source" is the correct term.

**Why now:** Sprint 202 cleaned the vocabulary at the data layer (pins→saves). Sprint 203 cleans the vocabulary at the UI layer (catalogs→sources) and restructures the admin page into a predictable 5-tab layout before Sprint 204 adds the channel (which makes the user tabs vestigial).

**High-level approach:** Replace 8 tabs with 5 (`Setup · Overview · Settings · Content · Marvin`). Merge Health → Overview, Improbability → Marvin, Blocked Items + Content Mgmt → Content. Dismantle the 7 Settings accordions into 5 flat cards. Run a vocabulary pass replacing user-visible "Catalog"/"Catalogs" with "Source"/"Sources" in all admin strings. Hide (not delete) the three user tabs until Sprint 205 — the channel must ship first.

### What the Research Found

- Current tab IDs in the HTML: `setup`, `discover`, `mypicks`, `mylists` (hidden?), `health`, `improbability`, `blocked`, `content-mgmt`, `settings`.
- `showTab(view, name)` in JS (~line 72) uses a hardcoded list of known tab names for DOM switching. Must be updated to the 5 new names.
- The Health tab body contains: system health card (AIOStreams status), source health table (last sync, items, errors), and an API budget display. All move to Overview.
- The Improbability tab body contains: Summon Marvin button, Refresh Worker status, Deep Clean status, enrichment summary, cooldown badge, background task runner. All move to Marvin tab.
- The Settings tab's 7 accordions (identified from HTML structure): Provider/AIOStreams, Catalog & Sources (sync schedule, sub-source picker), Library Paths, Playback & Cache, Security (PluginSecret), System Status, Maintenance (Recovery/Danger Zone).
- "System Status" accordion contents overlap with what moves to Overview. "Maintenance" becomes "Danger Zone" card in Settings.
- Blocked Items tab and Content Mgmt tab both merge into new Content tab.
- The three user tabs (`discover`, `mypicks`, `mylists`) must be hidden (display:none on the tab buttons) but their bodies remain in HTML until Sprint 205. The admin-gating JS block at lines 746-752 already conditionally hides these tabs for non-admins — after Sprint 203 they are hidden for everyone.
- `refreshSourcesTab(view)` populates the source health table. After restructure it populates the Overview tab's source health table instead.
- Vocabulary scope: `grep -i "catalog" Configuration/configurationpage.html` returns matches in user-visible labels, headings, accordion titles, hint text. Internal IDs like `cfg-enable-aio`, `data-es-load-catalogs`, CSS class names are left unchanged.

### Breaking Changes

- Tab IDs change: any external deeplink into a specific tab (e.g. `?tab=health`) will land on the wrong tab. Not a published API — acceptable.
- The 3 user tabs are hidden but their bodies stay in the DOM until Sprint 205; no user-visible content is deleted in this sprint.

### Non-Goals

- ❌ Delete user tab bodies — deferred to Sprint 205 (depends on channel shipping in Sprint 204).
- ❌ `InfiniteDriveChannel` IChannel implementation — Sprint 204.
- ❌ Add new Overview analytics beyond what's already rendered in Health tab.
- ❌ Re-design any individual settings card beyond moving it to its correct tab.
- ❌ Mobile/responsive layout improvements.

---

## Phase A — Tab Bar Restructure

### FIX-203A-01: Replace tab bar with 5 tabs

**File:** `Configuration/configurationpage.html` (tab bar ~lines 208–218)
**Estimated effort:** S
**What:**

Replace the current tab bar markup with 5 tab buttons:

```html
<div class="tabs">
  <button data-tab="setup">Setup</button>
  <button data-tab="overview">Overview</button>
  <button data-tab="settings">Settings</button>
  <button data-tab="content">Content</button>
  <button data-tab="marvin">Marvin</button>
</div>
```

Hide the three user tab buttons (do not delete the bodies):
```html
<button data-tab="discover" style="display:none">Discover</button>
<button data-tab="mypicks" style="display:none">My Picks</button>
<button data-tab="mylists" style="display:none">My Lists</button>
```

> ⚠️ **Watch out:** Confirm the exact button markup pattern (class names, `data-tab` vs `href` vs `id`) before editing. Match the existing pattern — do not introduce a new pattern.

---

### FIX-203A-02: Update `showTab()` in JS

**File:** `Configuration/configurationpage.js` (~line 72)
**Estimated effort:** S
**What:**

Update the hardcoded tab-name list (or switch logic) in `showTab(view, name)` to recognise the 5 new tab names: `setup`, `overview`, `settings`, `content`, `marvin`. Keep the old names in the list but map them to their new homes if any external code still references them:
- `health` → redirect to `overview`
- `improbability` → redirect to `marvin`
- `blocked` → redirect to `content`
- `content-mgmt` → redirect to `content`

> ⚠️ **Watch out:** Check whether `showTab` is called with old tab names anywhere else in the JS file before deleting the old names from the list.

---

## Phase B — Tab Body Migration

### FIX-203B-01: Create Overview tab body

**File:** `Configuration/configurationpage.html`
**Estimated effort:** M
**What:**

Insert a new `<div id="tab-overview" class="es-tab-content">` section. Populate it by moving (cut-and-paste) content from:

1. **Health tab** (~lines 594–628): system health card, source health table, API budget display → move verbatim into Overview.
2. **Item state counts** from the deleted Doctor card (Phase A of Sprint 202): Catalogued/Present/Resolved/Retired/Saved/Orphaned. These counts should be represented as read-only stat chips in Overview.
3. Wrap "Advanced Debug Tools" (Resolution Cache stats, Client Intelligence, Item Inspector, Raw AIOStreams display) in a `<details>` toggle inside Overview so they're accessible but not prominent.

After moving the Health tab content, delete the Health tab body entirely (its content is now in Overview).

---

### FIX-203B-02: Create Marvin tab body

**File:** `Configuration/configurationpage.html`
**Estimated effort:** S
**What:**

Insert a new `<div id="tab-marvin" class="es-tab-content">` section. Move the entire Improbability tab body (~lines 676–742) verbatim into it. Delete the old Improbability tab body.

Update any heading inside the body that says "Improbability Drive" to say "Marvin".

---

### FIX-203B-03: Create Content tab body

**File:** `Configuration/configurationpage.html`
**Estimated effort:** S
**What:**

Insert a new `<div id="tab-content" class="es-tab-content">` section. Move into it:

1. **Blocked Items tab body** (~line 1677): the unblock table.
2. **Content Mgmt tab body** (~line 1694): force-sync button, recently-added audit rows, homescreen row curation.

Delete the old Blocked Items and Content Mgmt tab bodies after moving.

---

### FIX-203B-04: Rebuild Settings tab — dismantle accordions

**File:** `Configuration/configurationpage.html` (Settings tab ~lines 745–1624)
**Estimated effort:** L
**What:**

The 7 current accordions become 5 flat cards inside the Settings tab. No accordion markup (`es-accordion` class, expand/collapse wiring). Each card is a visually-grouped `<fieldset>` or `<section>` with a card header.

Card layout:

1. **Sources card** — merge contents of: AIOStreams provider URL + backup toggle, Catalog & Sources accordion (RSS feeds, sub-source picker, sync schedule, anime path). This is the one place source configuration is editable.
2. **Playback & Cache card** — proxy mode, cache TTLs, prefetch settings. Move from the Playback accordion.
3. **Library Paths card** — strm root, anime path, metadata prefs. Move from Library Paths accordion.
4. **Security card** — PluginSecret display + rotate button, playback auth toggle. Move from Stream Signing Secret accordion.
5. **Danger Zone card** — recovery, reset actions. Move from Maintenance accordion.

Move "System Status" accordion contents to Overview (already done in FIX-203B-01); delete the accordion from Settings.

After migration, delete all `es-accordion` markup from the Settings tab.

> ⚠️ **Watch out:** Each accordion likely has JS wiring (click handlers to expand/collapse). After removing the accordion markup, audit the JS for accordion-related dead code and remove it to avoid JS errors on page load.

---

## Phase C — Vocabulary Pass

### FIX-203C-01: Replace "Catalog"/"Catalogs" with "Source"/"Sources" in admin strings

**Files:** `Configuration/configurationpage.html`, `Configuration/configurationpage.js`
**Estimated effort:** M
**What:**

Scope: user-visible strings only. Do **not** rename:
- HTML `id` attributes (e.g. `id="cfg-enable-aio"`)
- `data-*` attributes used by JS (e.g. `data-es-load-catalogs`)
- CSS class names (e.g. `.es-sources-table`)
- Internal JS variable names

Do rename:
- `<label>` text content: `"Catalog Sync"` → `"Source Sync"`, `"Catalogs"` → `"Sources"`, etc.
- `<h2>` / `<h3>` headings inside tab bodies
- Placeholder text in `<input>` elements
- Button text
- Help text / hint strings
- Toast/notification strings in JS that say "catalog" in an admin context

Run a final `grep -i "catalog" Configuration/configurationpage.html` and review each remaining hit to confirm it's either an internal ID/class (acceptable) or a user-visible string that was missed.

---

### FIX-203C-02: Update `refreshSourcesTab` to target Overview

**File:** `Configuration/configurationpage.js`
**Estimated effort:** S
**What:**

`refreshSourcesTab(view)` currently populates DOM elements that were inside the Health tab. After the Health tab content moves to Overview, update the DOM selectors in `refreshSourcesTab` to target the Overview tab's elements.

Confirm `refreshDashboard` wiring is also updated if it calls `refreshSourcesTab`.

---

### FIX-203C-03: Remove admin-gating user-tab block (or update it)

**File:** `Configuration/configurationpage.js` (~lines 746–752)
**Estimated effort:** S
**What:**

The current admin-gating block hides the user tabs (`discover`, `mypicks`, `mylists`) for non-admins. After Sprint 203, these tabs are hidden for everyone (style="display:none" on the tab buttons from FIX-203A-01). The admin-gating block is now redundant — remove it to avoid confusion. Since the whole config page is admin-only, no non-admin reaches this code.

> ⚠️ **Watch out:** Confirm the admin gate is truly only hiding user tabs and is not also performing any other admin check needed for the remaining tabs. Read the block carefully before deleting.

---

## Phase D — REPO_MAP Update

### FIX-203D-01: Update REPO_MAP.md

**File:** `.ai/REPO_MAP.md`
**Estimated effort:** XS
**What:**

Update the `Configuration/configurationpage.html` Structure section to reflect the new 5-tab layout:
- **Tab bar**: Setup, Overview, Settings, Content, Marvin (Discover/My Picks/My Lists hidden, pending Sprint 205 deletion)
- Remove Health, Improbability, Blocked Items, Content Mgmt entries
- Update Settings description: "5 flat cards (Sources, Playback & Cache, Library Paths, Security, Danger Zone) — no accordions"

---

## Phase E — Build & Verification

### FIX-203E-01: Build

```
dotnet build -c Release
```
Expected: 0 errors, 0 net-new warnings.

---

### FIX-203E-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `grep -c "es-accordion" Configuration/configurationpage.html` | 0 | All accordions removed from Settings |
| `grep -i "catalog" Configuration/configurationpage.html \| grep -v "id=\|data-\|class="` | 0 | Vocabulary pass complete (user-visible only) |
| `grep -i "improbability" Configuration/configurationpage.html` | 0 (outside comments) | Tab renamed to Marvin |

---

### FIX-203E-03: Manual test — 5 tabs

1. `./emby-reset.sh`
2. Open config page. Confirm exactly 5 visible tabs: **Setup, Overview, Settings, Content, Marvin**.
3. Confirm Discover/My Picks/My Lists tab buttons are absent or invisible.

---

### FIX-203E-04: Manual test — Overview read-only

1. Click **Overview**.
2. Confirm: sources table renders, resolution coverage renders, enrichment counts render.
3. Confirm: no editable input fields visible in Overview.

---

### FIX-203E-05: Manual test — Settings saves correctly

1. Click **Settings**.
2. Confirm 5 cards visible: Sources, Playback & Cache, Library Paths, Security, Danger Zone.
3. Change the provider URL. Click Save.
4. Reload page. Confirm the new URL is present.

---

### FIX-203E-06: Manual test — Sources vocabulary

1. Grep the live rendered HTML (view-source in browser) for "Catalog" in user-visible positions.
2. Expected: 0 occurrences of "Catalog"/"Catalogs" in headings, labels, buttons, hints.
3. "Source"/"Sources" is the vocabulary throughout admin surfaces.

---

### FIX-203E-07: Manual test — Content tab

1. Click **Content**.
2. Confirm blocked items table loads.
3. Click Unblock on a blocked item. Confirm it is removed from the list.
4. Confirm force-sync button is present.

---

### FIX-203E-08: Manual test — Marvin tab

1. Click **Marvin**.
2. Click "Summon Marvin". Confirm task starts (spinner visible in Emby Scheduled Tasks).

---

## Rollback Plan

- `git revert` the sprint commit. No schema changes in this sprint — rollback is purely a code revert.
- If a partial edit broke the HTML structure, use `git checkout -- Configuration/configurationpage.html` to restore the file before re-attempting.

---

## Completion Criteria

- [ ] Config page has exactly 5 visible tabs: Setup, Overview, Settings, Content, Marvin
- [ ] Discover/My Picks/My Lists tab buttons hidden (not deleted)
- [ ] Health tab body moved to Overview; Health tab deleted
- [ ] Improbability tab body moved to Marvin; Improbability tab deleted
- [ ] Blocked Items + Content Mgmt bodies merged into Content tab; originals deleted
- [ ] Settings tab has 5 flat cards, zero accordion markup
- [ ] Source configuration editable only in Settings → Sources card
- [ ] "Catalog"/"Catalogs" replaced with "Source"/"Sources" in all user-visible admin strings
- [ ] `refreshSourcesTab` targets Overview tab DOM elements
- [ ] Admin-gating block for user tabs removed
- [ ] `dotnet build -c Release` → 0 errors
- [ ] Grep: 0 `es-accordion` in Settings, 0 user-visible "catalog" strings
- [ ] `.ai/REPO_MAP.md` updated to reflect 5-tab structure

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | Exact line ranges for Health, Improbability, Blocked Items, Content Mgmt tab bodies — must be confirmed by reading HTML before editing to avoid off-by-one cuts. | Dev | Open |
| 2 | Does the Settings accordion JS use a single shared collapse/expand handler or per-accordion inline handlers? Determines how much JS dead code to remove. | Dev | Open |
| 3 | Are there any URLs in marketing/docs that deeplink to specific tabs (e.g. `?tab=health`)? If so, add redirects. | Dev | Open |

---

## Notes

**Files created:** 0
**Files modified:** 3 (`Configuration/configurationpage.html`, `Configuration/configurationpage.js`, `.ai/REPO_MAP.md`)
**Files deleted:** 0

**Risk:** MEDIUM — large HTML restructure with many cut-and-paste moves; easy to accidentally lose content or break JS wiring. Mitigated by:
1. Incremental approach: one tab migration per FIX task, build-check after each.
2. Grep checklist confirms no content was silently dropped.
3. Full manual test of each tab before commit.
