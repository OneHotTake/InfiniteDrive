# Sprint 213 — Apple-like Simplicity: Settings & Status Overhaul

**Version:** v1.0 | **Status:** Planning | **Risk:** HIGH | **Depends:** Sprint 212
**Owner:** Fullstack | **Target:** v0.43 | **PR:** TBD

---

## Overview

### Problem Statement

The current Settings and Status pages are clumsy, confusing, and far from Apple-like simplicity:

1. **Wizard is over-engineered** — Multi-step wizard with confusing navigation, not needed for initial setup
2. **Settings are mixed metaphors** — "Advanced Settings" contains basic settings, "System Health" shows phantom data
3. **No clear separation of concerns** — Source configuration, server settings, health monitoring, and repair all mixed together
4. **Terrible labels** — "Sources" page button labeled "Edit" but actually opens AIOStreams configure URL
5. **No real health data** — System Health shows empty/incorrect values
6. **Phantom code** — Dead CSS, unused sections, confusing terminology

### Why Now

Sprint 212 fixed critical bugs. Now we need to make the UX actually usable for end users. The plugin is technically functional but the UI is a barrier to adoption.

### High-Level Approach

1. **Eliminate the wizard** — Replace with simple, focused pages
2. **Create clear page structure:**
   - External Sources (AIOStreams, Cinemeta, backup)
   - Server Settings (paths, libraries, metadata)
   - System Health (real health data, not phantom)
   - Repair (Summon Marvin - simple, clear)
   - Advanced Settings (truly advanced, minimal)
3. **Fix labels and terminology** — Clear, concise, Apple-style
4. **Show real data** — System Health must show actual, useful information
5. **Remove phantom code** — Dead CSS, unused sections

---

## What the Research Found

### Current Page Structure

**Wizard (3 steps):**
1. Providers (AIOStreams URL, backup, metadata URL)
2. Your Setup (paths, library names, anime toggle)
3. Catalogs (source selection)

**Settings tabs:**
- Overview (System Overview, Active Sources)
- Settings (Multiple cards: Sources, Sync Schedule, Playback & Cache, Parental Controls, Security)
- Content (Blocked Items, Broken .strm files)
- Advanced (Raw JSON, System Health, Nuclear Options)

### Issues Identified

1. **Wizard vs Settings duplication** — Both configure the same things
2. **"Overview" tab is confusing** — Shows "System Overview" but no real system health data
3. **"Active Sources" table** — Shows empty/incorrect data (API mismatch)
4. **"System Health" in Advanced tab** — Should be more prominent, shows phantom data
5. **"Summon Marvin" button** — Hidden in Advanced tab, should be "Repair" page
6. **"Sources" card in Settings** — Duplicate of "Active Sources" in Overview
7. **"Nuclear Options"** — Scary name, should be "Reset Plugin" or similar
8. **Button labels** — "Edit" on Streams page is misleading
9. **"System Overview" title** — Should be "System Health" or "Dashboard"
10. **"DON'T PANIC"** — Funny but not Apple-like, should be professional

---

## Breaking Changes

- **Wizard removal** — Users who rely on wizard will need to use new Settings pages
- **Tab structure change** — Tabs reorganized, bookmarks may break
- **URL structure** — Page URLs may change

---

## Non-Goals

- **Redesign Discover page** — That's a separate concern
- **Add new features** — Focus on simplifying existing features
- **Change backend API** — Only frontend changes unless necessary
- **Support custom themes** — Use Emby's default styling

---

## Phase A: Page Structure (Foundation)

### FIX-213-A1: Eliminate Wizard, Create Simple Setup Flow
**File:** `Configuration/configurationpage.html`
**Effort:** 3 hours

**What:**
1. Remove wizard HTML (steps, navigation, progress)
2. Create simple "First Run" page with:
   - AIOStreams Manifest URL (single field)
   - "Connect" button
   - Auto-proceeds to Settings on success
3. Hide First Run page after first successful connection

**Why:** Wizard is over-engineered. Simple one-field setup is all that's needed for initial configuration.

---

### FIX-213-A2: Reorganize Tabs
**File:** `Configuration/configurationpage.html`
**Effort:** 2 hours

**What:**
1. Rename/Reorganize tabs:
   - `sources` → "External Sources" (AIOStreams, Cinemeta, backup)
   - `settings` → "Server Settings" (paths, libraries, metadata)
   - `health` → "System Health" (real health data)
   - `repair` → "Repair" (Summon Marvin)
   - `advanced` → "Advanced" (truly advanced only)
2. Update tab switching logic in JavaScript

**Why:** Clear separation of concerns. Each tab has a single, focused purpose.

---

## Phase B: Content Simplification

### FIX-213-B1: External Sources Page
**File:** `Configuration/configurationpage.html`, `configurationpage.js`
**Effort:** 2 hours

**What:**
1. "External Sources" page with:
   - AIOStreams Manifest URL (with "Test Connection" and "Open Config" buttons)
   - Backup AIOStreams (toggle + URL)
   - AIOMetadata URL (optional)
   - Cinemeta Catalog (toggle)
   - Source selection (catalogs from manifest)
2. Clear labels, simple layout
3. Remove confusing "Sources" card from Settings

**Why:** All external source configuration in one place.

---

### FIX-213-B2: Server Settings Page
**File:** `Configuration/configurationpage.html`, `configurationpage.js`
**Effort:** 2 hours

**What:**
1. "Server Settings" page with:
   - Library paths (movies, shows, anime)
   - Library names
   - Metadata language, country, image language
   - Subtitle download languages
   - Emby base URL
2. Remove: Sync Schedule (move to Advanced), Parental Controls (move to own tab/page)

**Why:** Core server settings in one place.

---

### FIX-213-B3: System Health Page (NEW)
**File:** `Configuration/configurationpage.html`, `configurationpage.js`
**Effort:** 4 hours

**What:**
1. "System Health" page with real data:
   - Plugin version, status (configured/not configured)
   - AIOStreams connection status (reachable/not reachable, latency)
   - Library status (items, .strm files, last sync)
   - RefreshTask status (last run, active step, items processed)
   - MarvinTask status (last run, items fixed)
   - Warnings (missing TMDB key, localhost URL, etc.)
2. Remove phantom data, ensure all values are real

**Why:** Users need to see actual system health, not empty or incorrect values.

---

### FIX-213-B4: Repair Page (NEW)
**File:** `Configuration/configurationpage.html`, `configurationpage.js`
**Effort:** 1 hour

**What:**
1. "Repair" page with:
   - "Summon Marvin" button (green, prominent)
   - Status display (last run, items fixed)
   - Simple description: "Fix metadata, broken .strm files, and enrichment issues"

**Why:** Clear, focused repair function. No confusion about what it does.

---

### FIX-213-B5: Parental Controls Page (NEW)
**File:** `Configuration/configurationpage.html`, `configurationpage.js`
**Effort:** 1 hour

**What:**
1. "Parental Controls" page with:
   - TMDB API Key
   - Block Unrated toggle
   - Behavior matrix (existing)
   - Filtering status
2. Move content filtering warning here (already done in Sprint 212)

**Why:** Parental controls deserve their own page, not buried in Settings.

---

### FIX-213-B6: Advanced Settings Page
**File:** `Configuration/configurationpage.html`, `configurationpage.js`
**Effort:** 2 hours

**What:**
1. "Advanced" page with truly advanced settings only:
   - Sync Schedule (interval, hour, cap)
   - Playback & Cache (cache lifetime, resolve timeout, proxy mode)
   - Raw JSON viewer
   - "Reset Plugin" (rename from "Nuclear Options", less scary)

**Why:** Only advanced settings here. Clear distinction from basic settings.

---

### FIX-213-B7: Content Page Cleanup
**File:** `Configuration/configurationpage.html`, `configurationpage.js`
**Effort:** 1 hour

**What:**
1. "Content" page cleanup:
   - Blocked Items (existing)
   - Broken .strm files (existing)
   - Clear labels, simple layout
2. Remove any dead/unused sections

**Why:** Content management should be simple and clear.

---

## Phase C: UI Refinement

### FIX-213-C1: Fix Button Labels
**File:** `Configuration/configurationpage.html`
**Effort:** 0.5 hours

**What:**
1. "Edit AIOStreams Manifest" → "Open AIOStreams Config"
2. "Summon Marvin" → "Run Repair" (keep button text as "Summon Marvin" for personality, but page title is "Repair")
3. "Nuclear Options" → "Reset Plugin"
4. Ensure all button labels are clear and concise

**Why:** Clear labels reduce confusion.

---

### FIX-213-C2: Remove "DON'T PANIC" and Humor
**File:** `Configuration/configurationpage.html`
**Effort:** 0.5 hours

**What:**
1. Replace "DON'T PANIC" with professional text
2. Remove other humorous elements (keep subtle personality where appropriate)
3. Maintain friendly tone but be professional

**Why:** Apple-like professionalism without being dry.

---

### FIX-213-C3: Clean Up CSS
**File:** `Configuration/configurationpage.html`
**Effort:** 1 hour

**What:**
1. Remove dead CSS rules
2. Remove unused styles
3. Consolidate similar styles
4. Ensure consistent spacing and sizing

**Why:** Smaller CSS, easier maintenance, consistent look.

---

### FIX-213-C4: Fix System Overview Title
**File:** `Configuration/configurationpage.html`, `configurationpage.js`
**Effort:** 0.5 hours

**What:**
1. Change "System Overview" to "Dashboard"
2. Update JavaScript references
3. Ensure consistency

**Why:** "Dashboard" is clearer and more standard.

---

## Phase D: Wiring & Integration

### FIX-213-D1: Update Tab Navigation
**File:** `Configuration/configurationpage.js`
**Effort:** 1 hour

**What:**
1. Update `showTab()` function to handle new tabs
2. Update tab switching logic
3. Ensure proper default tab selection

**Why:** New tab structure needs new navigation logic.

---

### FIX-213-D2: Update Data Loading
**File:** `Configuration/configurationpage.js`
**Effort:** 2 hours

**What:**
1. Update `populateSettings()` for new page structure
2. Ensure all settings load/save correctly
3. Test save functionality for each page

**Why:** Settings must save correctly across all pages.

---

### FIX-213-D3: Update First Run Flow
**File:** `Configuration/configurationpage.js`
**Effort:** 2 hours

**What:**
1. Implement new First Run flow (single field + Connect button)
2. On success, save config and redirect to Settings
3. Set `IsFirstRunComplete = true`

**Why:** New setup flow needs new logic.

---

### FIX-213-D4: Remove Wizard Code
**File:** `Configuration/configurationpage.js`, `PluginConfiguration.cs`
**Effort:** 2 hours

**What:**
1. Remove wizard-related JavaScript functions
2. Remove wizard-related HTML elements (if any remain)
3. Clean up unused wizard variables
4. Consider removing `IsFirstRunComplete` if not needed for new flow

**Why:** Wizard is gone, remove dead code.

---

## Build & Verification

### BUILD-213-1: Build Check
**Command:** `dotnet build -c Release`
**Acceptance:** No errors, no warnings

---

### VERIFY-213-1: Tab Navigation
**Steps:**
1. Open configuration page
2. Click each tab
3. Verify correct content loads

**Acceptance:** All tabs load correctly with appropriate content

---

### VERIFY-213-2: Settings Save
**Steps:**
1. Modify settings on each page
2. Click "Save Settings"
3. Refresh page
4. Verify settings persist

**Acceptance:** All settings save and persist correctly

---

### VERIFY-213-3: First Run Flow
**Steps:**
1. Delete config/db (clean slate)
2. Open configuration page
3. Enter AIOStreams URL
4. Click "Connect"
5. Verify redirected to Settings

**Acceptance:** First run flow works correctly

---

### VERIFY-213-4: System Health Data
**Steps:**
1. Open System Health page
2. Verify all data points show real values
3. Verify no empty/incorrect values

**Acceptance:** All health data is accurate

---

### VERIFY-213-5: Repair Function
**Steps:**
1. Open Repair page
2. Click "Summon Marvin"
3. Verify task runs
4. Verify status updates

**Acceptance:** Repair function works correctly

---

## Completion Criteria

- [ ] Wizard removed and replaced with simple First Run flow
- [ ] All tabs reorganized with clear purposes
- [ ] External Sources page contains all source configuration
- [ ] Server Settings page contains core server settings
- [ ] System Health page shows real, accurate data
- [ ] Repair page contains Summon Marvin function
- [ ] Parental Controls page contains all parental control settings
- [ ] Advanced Settings page contains only truly advanced settings
- [ ] All button labels are clear and concise
- [ ] "DON'T PANIC" and other humor removed/replaced
- [ ] Dead CSS removed
- [ ] "System Overview" renamed to "Dashboard"
- [ ] All settings save and persist correctly
- [ ] First Run flow works correctly
- [ ] Build succeeds with no errors or warnings
- [ ] All verification steps pass

---

## Open Questions / Blockers

| Question | Status | Next Action |
|----------|--------|-------------|
| Should we keep "Nuclear Options" name or change to "Reset Plugin"? | Open | Decide on naming |
| Should System Health be the default tab or Settings? | Open | Decide on default tab |
| Should we remove `IsFirstRunComplete` or keep it for new flow? | Open | Determine if needed |
| What specific health data points are most important? | Open | Consult user |

---

## Notes

### Files Changed
- `Configuration/configurationpage.html` — Major restructuring
- `Configuration/configurationpage.js` — Remove wizard, update navigation
- `PluginConfiguration.cs` — Possible cleanup of wizard-related properties

### Risk Assessment
**Risk: HIGH** — Major UI restructuring could introduce bugs

**Mitigations:**
- Incremental testing after each phase
- Keep backend API unchanged
- Test all save/load functionality
- Test First Run flow end-to-end

### Performance Considerations
- Removing wizard reduces page load time
- Fewer DOM elements = faster rendering
- CSS cleanup reduces file size

### User Impact
- **Positive:** Simpler, clearer UI
- **Positive:** Faster setup (one field vs multi-step wizard)
- **Positive:** Easier to find settings
- **Negative:** Users familiar with wizard may need to learn new flow
- **Negative:** Tab bookmarks may break (acceptable for v0.43)
