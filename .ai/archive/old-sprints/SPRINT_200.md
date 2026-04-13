# Sprint 200 — Wizard & Settings UX Overhaul

**Version:** v1.0 | **Status:** ✅ Implemented | **Risk:** MEDIUM | **Depends:** Sprint 160+
**Owner:** Fullstack | **Target:** v0.41 | **PR:** TBD

---

## Overview

### Problem Statement

The wizard and settings pages have accumulated technical debt across dozens of sprints. Fields have been added piecemeal without UX cohesion: metadata preferences are buried in Step 2 alongside library paths, the AIOStreams catalog loader fires before the URL is saved, backup provider configuration is unclear, AIOMetadata has no contextual explanation, and the first-run experience fails to communicate InfiniteDrive's opinionated design decisions. Users frequently misconfigure because nothing is self-explaining.

### Why Now

The rename to InfiniteDrive (Sprint 161) created a clean break. The plugin is pre-release beta. This is the right moment for a breaking UX overhaul before users accumulate expectations. All the config fields exist in `PluginConfiguration.cs`; only presentation and organization need to change.

### High-Level Approach

Redesign the three-step wizard around a clear mental model:

- **Step 1 — External Providers:** Every external service InfiniteDrive calls. One screen, clearly grouped, required vs optional explicitly labeled. No guessing.
- **Step 2 — Your Setup:** Disk locations, library names, Emby server URL, language/locale. The things that are specific to *this* installation.
- **Step 3 — Your Catalogs:** Live catalog picker loaded from the manifest configured in Step 1. User selects what to sync. Nothing syncs until this is confirmed.

Design philosophy: Apple-esque. Every field has a single-sentence purpose statement. Required fields are obvious. Optional fields are visually subordinate. Warnings appear only when they carry real consequence.

### What the Research Found

- `SecondaryManifestUrl` already exists in `PluginConfiguration.cs:75` — no schema change needed for backup AIOStreams
- No `EnableBackupAioStreams` bool exists — need to add one to gate the secondary URL field
- No `SystemRssFeedUrls` field exists anywhere — need to add to `PluginConfiguration.cs`
- `AioMetadataBaseUrl` exists at line 574 — no model change needed
- `EnableCinemetaDefault` exists at line 472 — maps to the Cinemeta checkbox
- `EmbyBaseUrl` exists at line 133 — maps to the Emby URL field; default is `http://127.0.0.1:8096`
- `IsFirstRunComplete` at line 585 gates whether wizard shows vs completion screen
- Wizard JS: `initWizardTab` (line 139), `finishWizard` (line 2315), `wizNext` (line 461)
- Current Step 1 has: AIOStreams URL + AIOMetadata URL + Test Connection
- Current Step 2 has: Library paths + library names + metadata lang/country + Cinemeta checkbox + catalog picker (currently on Step 2, should move to Step 3)
- Current Step 3 has: Summary + Finish button
- Catalog loading bug (fixed this session): `loadCatalogs('wiz')` was reading wrong field ID — now correct

### Breaking Changes

- Wizard step labels change: "Provider" → "Providers", "Preferences" → "Your Setup", "Sync" → "Catalogs"
- Catalog picker moves from Step 2 to Step 3 — `finishWizard` must still collect selected catalog IDs
- `initWizardTab`, `wizNext`, `finishWizard` all need updates
- **No database changes.** All fields are config-layer only.
- Two new `PluginConfiguration` properties added (backward-compatible, have defaults)

### Non-Goals

- ❌ Per-user RSS feeds — system RSS is admin-only; user-owned feeds are a future sprint (Sprint 201+)
- ❌ Admin content management page changes — out of scope
- ❌ Sources tab redesign on main settings page — minor sync only; full settings page UX is Sprint 201
- ❌ Stream type preferences in wizard — stays in main settings
- ❌ Proxy/cache/advanced settings — wizard is for first-run essentials only

---

## Phase A — Configuration Model

### FIX-200A-01: Add `EnableBackupAioStreams` to `PluginConfiguration.cs`

**File:** `PluginConfiguration.cs` (modify)
**Estimated effort:** S
**What:**

After line 75 (`SecondaryManifestUrl`), add:

```csharp
/// <summary>
/// Whether the user has configured a backup AIOStreams instance.
/// When false, SecondaryManifestUrl is ignored even if populated.
/// </summary>
public bool EnableBackupAioStreams { get; set; } = false;
```

---

### FIX-200A-02: Add `SystemRssFeedUrls` to `PluginConfiguration.cs`

**File:** `PluginConfiguration.cs` (modify)
**Estimated effort:** S
**What:**

After the `AioMetadataBaseUrl` property (line 574), add:

```csharp
/// <summary>
/// Newline-separated list of system-wide RSS feed URLs.
/// Content from these feeds is visible to ALL users — admin-only setting.
/// </summary>
public string SystemRssFeedUrls { get; set; } = string.Empty;
```

> ⚠️ **Watch out:** RSS feed processing is not yet implemented — this sprint only exposes the config field. The field is wired to UI but the value is not consumed by any service yet. That is intentional; plumbing comes in a later sprint.

---

## Phase B — Wizard HTML Overhaul

### FIX-200B-01: Redesign Step 1 — External Providers

**File:** `Configuration/configurationpage.html` (modify)
**Estimated effort:** L
**What:**

Replace the entire `data-es-wizard-content="1"` div with a new layout. The new Step 1 contains four visually distinct sections:

**Section 1 — AIOStreams (required)**
```html
<div class="es-wizard-step-content active" data-es-wizard-content="1">

  <!-- ── Hero intro ── -->
  <div style="margin-bottom:1.4em">
    <h2 style="margin:0 0 .35em;font-size:1.25em;font-weight:700">Connect Your Streaming Provider</h2>
    <p style="margin:0;font-size:.9em;opacity:.65;line-height:1.6">
      InfiniteDrive bridges your AIOStreams instance to Emby.
      Paste your manifest URL below and everything else follows.
    </p>
  </div>

  <!-- ── AIOStreams Primary (required) ── -->
  <div class="es-card es-card-accent">
    <div style="display:flex;align-items:baseline;justify-content:space-between;margin-bottom:.3em">
      <div class="es-card-title" style="margin:0">AIOStreams</div>
      <span style="font-size:.75em;font-weight:600;color:var(--theme-button-background,#00a4dc);
                   background:rgba(0,164,220,.12);padding:.15em .55em;border-radius:99px">REQUIRED</span>
    </div>
    <p class="es-card-text">
      Your AIOStreams manifest URL. This is the only required field.
      AIOStreams supplies your catalog, your stream links, and optional metadata resolvers — all in one URL.
    </p>
    <div class="inputContainer" style="margin-bottom:.5em">
      <label class="inputLabel inputLabelUnfocused" for="wiz-aio-url">Manifest URL</label>
      <input id="wiz-aio-url" type="url" is="emby-input"
             placeholder="https://yourdomain.com/stremio/uuid/token/manifest.json" />
      <div class="es-hint">
        Don't have one?
        <a href="https://duckkota.gitlab.io/stremio-tools/quickstart/" target="_blank" rel="noopener"
           style="color:var(--theme-button-background,#00a4dc)">Create one with Stremio Tools →</a>
      </div>
    </div>
    <div class="es-src-row" style="margin-bottom:.8em">
      <button type="button" is="emby-button" class="raised button-submit"
              data-es-wiz-test="aio" style="min-width:140px">Test Connection</button>
      <span class="es-src-status" id="wiz-aio-status"></span>
    </div>

    <!-- Backup toggle -->
    <div style="border-top:1px solid rgba(128,128,128,.12);padding-top:.85em;margin-top:.2em">
      <label class="checkboxContainer">
        <input type="checkbox" is="emby-checkbox" id="wiz-enable-backup-aio" />
        <span class="checkboxLabel" style="font-size:.9em">
          Enable a backup AIOStreams instance
          <span style="font-weight:400;opacity:.6;font-size:.9em;display:block;margin-top:.1em">
            If your primary is unreachable, InfiniteDrive falls back to this URL.
          </span>
        </span>
      </label>
      <div id="wiz-backup-aio-fields" style="display:none;margin-top:.75em">
        <div class="inputContainer" style="margin-bottom:.5em">
          <label class="inputLabel inputLabelUnfocused" for="wiz-aio-backup-url">Backup Manifest URL</label>
          <input id="wiz-aio-backup-url" type="url" is="emby-input"
                 placeholder="https://backup.yourdomain.com/stremio/uuid/token/manifest.json" />
        </div>
        <div class="es-src-row">
          <button type="button" is="emby-button" class="raised button"
                  data-es-wiz-test="aio-backup">Test Backup</button>
          <span class="es-src-status" id="wiz-aio-backup-status"></span>
        </div>
      </div>
    </div>
  </div>

  <!-- ── AIOMetadata (optional) ── -->
  <div class="es-card">
    <div style="display:flex;align-items:baseline;justify-content:space-between;margin-bottom:.3em">
      <div class="es-card-title" style="margin:0">AIOMetadata</div>
      <span style="font-size:.75em;font-weight:600;opacity:.55;
                   background:rgba(128,128,128,.12);padding:.15em .55em;border-radius:99px">OPTIONAL</span>
    </div>
    <p class="es-card-text">
      <strong>InfiniteDrive uses Emby's built-in metadata engine — including any plugins you have
      installed — as its primary source of truth.</strong>
      AIOMetadata is a supplementary fallback for titles that Emby cannot identify: obscure releases,
      certain anime, and content with non-standard IDs. Most setups don't need this.
    </p>
    <div class="inputContainer">
      <label class="inputLabel inputLabelUnfocused" for="wiz-aio-metadata-url">
        AIOMetadata Base URL
      </label>
      <input id="wiz-aio-metadata-url" type="url" is="emby-input"
             placeholder="https://yourdomain.com/aio-metadata" />
    </div>
  </div>

  <!-- ── Cinemeta (optional) ── -->
  <div class="es-card">
    <div style="display:flex;align-items:baseline;justify-content:space-between;margin-bottom:.3em">
      <div class="es-card-title" style="margin:0">Cinemeta</div>
      <span style="font-size:.75em;font-weight:600;opacity:.55;
                   background:rgba(128,128,128,.12);padding:.15em .55em;border-radius:99px">OPTIONAL</span>
    </div>
    <p class="es-card-text">
      Cinemeta is Stremio's public catalog. When enabled, InfiniteDrive uses it as a last-resort fallback
      to fill in titles that neither Emby nor AIOMetadata can identify. Useful for obscure anime
      and international content.
    </p>
    <label class="checkboxContainer">
      <input type="checkbox" is="emby-checkbox" id="wiz-use-cinemeta" checked />
      <span class="checkboxLabel">Use Cinemeta as a metadata fallback</span>
    </label>
  </div>

  <!-- ── System RSS Feeds (optional) ── -->
  <div class="es-card">
    <div style="display:flex;align-items:baseline;justify-content:space-between;margin-bottom:.3em">
      <div class="es-card-title" style="margin:0">System RSS Feeds</div>
      <span style="font-size:.75em;font-weight:600;opacity:.55;
                   background:rgba(128,128,128,.12);padding:.15em .55em;border-radius:99px">OPTIONAL</span>
    </div>
    <p class="es-card-text">
      RSS feeds added here are available to <strong>every user on this server.</strong>
      Individual users can add their own private feeds in their account settings.
      Leave blank if you don't need server-wide RSS sources.
    </p>
    <div class="es-alert es-alert-warn" style="font-size:.82em;margin-bottom:.75em;padding:.6em .8em">
      ⚠️ Content from system RSS feeds is visible to all users regardless of parental controls.
      Only add feeds appropriate for your entire audience.
    </div>
    <div class="inputContainer">
      <label class="inputLabel inputLabelUnfocused" for="wiz-rss-feeds">
        Feed URLs <span style="opacity:.6">(one per line)</span>
      </label>
      <textarea id="wiz-rss-feeds" is="emby-textarea" rows="3"
                style="font-size:.85em;font-family:monospace;width:100%"
                placeholder="https://example.com/feed.rss"></textarea>
    </div>
  </div>

</div><!-- /step 1 -->
```

**Step indicator label** for step 1 changes from `Provider` → `Providers`.

> ⚠️ **Watch out:** The backup AIOStreams toggle (`wiz-enable-backup-aio`) must show/hide `wiz-backup-aio-fields` via a JS change handler in `initWizardTab`. The test button `data-es-wiz-test="aio-backup"` must be wired up separately in the test dispatcher. If the backup checkbox is unchecked at `finishWizard` time, `SecondaryManifestUrl` must be saved as empty string (not the field value).

---

### FIX-200B-02: Redesign Step 2 — Your Setup

**File:** `Configuration/configurationpage.html` (modify)
**Estimated effort:** M
**What:**

Replace the entire `data-es-wizard-content="2"` div. Remove: Metadata Source card (moved to Step 1 as Cinemeta), AIOStreams Catalogs card (moved to Step 3). Add: Emby Server URL section. Reorganize metadata language/locale below library paths.

```html
<div class="es-wizard-step-content" data-es-wizard-content="2">

  <!-- ── Intro ── -->
  <div style="margin-bottom:1.4em">
    <h2 style="margin:0 0 .35em;font-size:1.25em;font-weight:700">Your Setup</h2>
    <p style="margin:0;font-size:.9em;opacity:.65;line-height:1.6">
      Where InfiniteDrive stores files, what your libraries are named,
      and how to reach your Emby server.
    </p>
  </div>

  <!-- ── Library Locations ── -->
  <div class="es-card">
    <div class="es-card-title">Library Locations</div>
    <p class="es-card-text">
      InfiniteDrive creates lightweight <code>.strm</code> files that Emby reads as a virtual library.
      Choose a base path — movies, shows, and anime folders are created inside it automatically.
    </p>
    <div class="inputContainer" style="margin-bottom:.35em">
      <label class="inputLabel inputLabelUnfocused" for="wiz-base-path">Base path</label>
      <input id="wiz-base-path" type="text" is="emby-input" placeholder="/media/infinitedrive" />
    </div>
    <div id="wiz-derived-paths"
         style="font-size:.82em;opacity:.55;margin:.15em 0 .8em;font-family:monospace;line-height:1.8">
      movies/ &nbsp;→&nbsp; <span id="wiz-derived-movies"></span><br/>
      shows/ &nbsp;&nbsp;→&nbsp; <span id="wiz-derived-shows"></span>
    </div>

    <!-- Library toggles -->
    <div style="display:grid;gap:.6em">
      <!-- Movies (always on) -->
      <div style="display:flex;align-items:center;gap:.75em">
        <input type="checkbox" is="emby-checkbox" id="wiz-enable-movies" checked disabled
               style="flex-shrink:0" />
        <div style="flex:1;min-width:0">
          <label style="font-size:.88em;font-weight:600;display:block;margin-bottom:.2em">Movies</label>
          <input id="wiz-library-name-movies" type="text" is="emby-input"
                 placeholder="Streamed Movies" style="margin:0" />
        </div>
      </div>
      <!-- Series (always on) -->
      <div style="display:flex;align-items:center;gap:.75em">
        <input type="checkbox" is="emby-checkbox" id="wiz-enable-series" checked disabled
               style="flex-shrink:0" />
        <div style="flex:1;min-width:0">
          <label style="font-size:.88em;font-weight:600;display:block;margin-bottom:.2em">Series</label>
          <input id="wiz-library-name-series" type="text" is="emby-input"
                 placeholder="Streamed Series" style="margin:0" />
        </div>
      </div>
      <!-- Anime (optional) -->
      <div style="display:flex;align-items:center;gap:.75em">
        <input type="checkbox" is="emby-checkbox" id="wiz-enable-anime" style="flex-shrink:0" />
        <div style="flex:1;min-width:0">
          <label style="font-size:.88em;font-weight:600;display:block;margin-bottom:.2em">
            Anime
            <span style="font-weight:400;opacity:.55;font-size:.9em">
              — separate library for anime titles
            </span>
          </label>
          <input id="wiz-library-name-anime" type="text" is="emby-input"
                 placeholder="Streamed Anime" style="margin:0" id="wiz-library-name-anime-field" />
        </div>
      </div>
    </div>
  </div>

  <!-- ── Emby Server URL ── -->
  <div class="es-card">
    <div class="es-card-title">Emby Server URL</div>
    <p class="es-card-text">
      InfiniteDrive uses this URL when generating playback links.
      We've pre-filled it from your browser — but if users access Emby from outside your network,
      use a domain name instead of an IP address so links remain valid everywhere.
    </p>
    <div class="inputContainer" style="margin-bottom:.4em">
      <label class="inputLabel inputLabelUnfocused" for="wiz-emby-base-url">Server URL</label>
      <input id="wiz-emby-base-url" type="url" is="emby-input"
             placeholder="https://emby.yourdomain.com" />
    </div>
    <div class="es-hint" id="wiz-emby-url-hint" style="font-size:.82em">
      ℹ️ Inferred from your browser: <strong id="wiz-emby-url-inferred"></strong>.
      Change this only if Emby is accessible at a different public address.
    </div>
  </div>

  <!-- ── Language & Region ── -->
  <div class="es-card">
    <div class="es-card-title">Language &amp; Region</div>
    <p class="es-card-text">
      Used when fetching metadata, artwork, and episode titles.
      Pick the language your library should default to.
    </p>
    <div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:.75em">
      <div class="inputContainer" style="margin:0">
        <label class="inputLabel inputLabelUnfocused" for="wiz-meta-lang">Metadata language</label>
        <select id="wiz-meta-lang" is="emby-select" style="width:100%">
          <option value="en">English</option>
          <option value="fr">French</option>
          <option value="de">German</option>
          <option value="es">Spanish</option>
          <option value="it">Italian</option>
          <option value="pt">Portuguese</option>
          <option value="ja">Japanese</option>
          <option value="ko">Korean</option>
          <option value="zh">Chinese</option>
        </select>
      </div>
      <div class="inputContainer" style="margin:0">
        <label class="inputLabel inputLabelUnfocused" for="wiz-meta-country">Country</label>
        <select id="wiz-meta-country" is="emby-select" style="width:100%">
          <option value="US">United States</option>
          <option value="GB">United Kingdom</option>
          <option value="CA">Canada</option>
          <option value="AU">Australia</option>
          <option value="FR">France</option>
          <option value="DE">Germany</option>
          <option value="JP">Japan</option>
          <option value="KR">South Korea</option>
        </select>
      </div>
      <div class="inputContainer" style="margin:0">
        <label class="inputLabel inputLabelUnfocused" for="wiz-meta-img-lang">Artwork language</label>
        <select id="wiz-meta-img-lang" is="emby-select" style="width:100%">
          <option value="en">English</option>
          <option value="fr">French</option>
          <option value="de">German</option>
          <option value="es">Spanish</option>
          <option value="ja">Japanese</option>
          <option value="ko">Korean</option>
        </select>
      </div>
    </div>
  </div>

</div><!-- /step 2 -->
```

**Step indicator label** for step 2 changes from `Preferences` → `Your Setup`.

> ⚠️ **Watch out:** The Emby URL field (`wiz-emby-base-url`) must be pre-populated in `initWizardTab` with `cfg.EmbyBaseUrl` if set, otherwise with `window.location.origin`. The inferred value display (`wiz-emby-url-inferred`) should always show `window.location.origin` regardless of what's in the input, so the user understands the inference.

---

### FIX-200B-03: Redesign Step 3 — Catalogs

**File:** `Configuration/configurationpage.html` (modify)
**Estimated effort:** M
**What:**

Replace the entire `data-es-wizard-content="3"` div. This step is now purely the catalog picker — the confirmation/finish step moves into the nav button area.

```html
<div class="es-wizard-step-content" data-es-wizard-content="3">

  <!-- ── Intro ── -->
  <div style="margin-bottom:1.4em">
    <h2 style="margin:0 0 .35em;font-size:1.25em;font-weight:700">Choose Your Catalogs</h2>
    <p style="margin:0;font-size:.9em;opacity:.65;line-height:1.6">
      These are the catalogs your AIOStreams manifest provides.
      Select which ones you want synced to Emby. You can change this at any time.
    </p>
  </div>

  <!-- Catalog loader panel -->
  <div class="es-card">
    <div id="wiz-catalog-panel">
      <div style="text-align:center;padding:2em 0;opacity:.5;font-size:.9em">
        Loading catalogs from your manifest…
      </div>
    </div>
    <input type="hidden" id="wiz-catalog-ids" value="" />
  </div>

  <!-- Summary strip (shown after catalogs load) -->
  <div id="wiz-catalog-summary" style="display:none"
       class="es-hint" style="margin-top:.5em;text-align:center">
    <span id="wiz-catalog-summary-text"></span>
  </div>

</div><!-- /step 3 -->
```

The "Save & Start First Sync" button stays in `es-wizard-nav` (bottom bar) and is shown only on step 3. The existing progress/completion screen HTML (currently in step 3) moves to be a sibling of the wizard container, not inside a step.

**Step indicator label** for step 3 changes from `Sync` → `Catalogs`.

> ⚠️ **Watch out:** Catalogs must be loaded automatically when the user navigates to step 3 (not via a button click). Wire this in `wizNext` when advancing to step 3: call `loadCatalogs(view, 'wiz')`. Do not require another button press.

---

## Phase C — Wizard JavaScript Overhaul

### FIX-200C-01: Update `initWizardTab` for new fields

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** S
**What:**

In `initWizardTab(view, cfg)`, add population of new fields and wire backup toggle visibility:

```javascript
// New fields
set('wiz-aio-backup-url',      cfg.SecondaryManifestUrl || '');
set('wiz-rss-feeds',           cfg.SystemRssFeedUrls || '');
set('wiz-emby-base-url',       cfg.EmbyBaseUrl || window.location.origin);
chk('wiz-enable-backup-aio',   cfg.EnableBackupAioStreams || false);

// Show inferred URL hint
var inferredEl = q(view, 'wiz-emby-url-inferred');
if (inferredEl) inferredEl.textContent = window.location.origin;

// Backup AIOStreams toggle visibility
var backupChk = q(view, 'wiz-enable-backup-aio');
var backupFields = q(view, 'wiz-backup-aio-fields');
function syncBackupVisibility() {
    if (backupFields) backupFields.style.display = backupChk && backupChk.checked ? 'block' : 'none';
}
if (backupChk) { backupChk.addEventListener('change', syncBackupVisibility); syncBackupVisibility(); }
```

Also rename existing `set('wiz-aio-url', ...)` and `set('wiz-aio-metadata-url', ...)` calls to remain in place (no change needed — field IDs are preserved).

---

### FIX-200C-02: Update `wizNext` — catalog auto-load on step 3

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** S
**What:**

In `wizNext(view, step)`, when `step === 3` (i.e., navigating to step 3), immediately call `loadCatalogs(view, 'wiz')` so the catalog panel populates without a separate button press:

```javascript
// Inside wizNext, after showWizardStep(view, step):
if (step === 3) {
    loadCatalogs(view, 'wiz');
}
```

Also update step 3 nav buttons: hide "Next" on step 3, show "Save & Start First Sync" button instead (change `data-es-wiz-next` button label dynamically or replace with finish button on step 3).

---

### FIX-200C-03: Update `finishWizard` for new fields

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** S
**What:**

Add new fields to the `config` object collected in `finishWizard`:

```javascript
EnableBackupAioStreams:  esChk(view, 'wiz-enable-backup-aio'),
SecondaryManifestUrl:   esChk(view, 'wiz-enable-backup-aio')
                            ? esVal(view, 'wiz-aio-backup-url')
                            : '',
SystemRssFeedUrls:      esVal(view, 'wiz-rss-feeds'),
EmbyBaseUrl:            esVal(view, 'wiz-emby-base-url') || window.location.origin,
```

Remove the stale `EmbyBaseUrl: window.location.origin` line that currently unconditionally overwrites with the inferred value.

---

### FIX-200C-04: Wire backup AIOStreams test button

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** S
**What:**

Find the test dispatcher (the click handler that routes `data-es-wiz-test` values) and add a case for `"aio-backup"`:

```javascript
// In the existing wiz-test click handler:
if (type === 'aio-backup') {
    testSource(view, 'aio-backup');  // or equivalent inline test logic
}
```

The backup URL test should read from `wiz-aio-backup-url` and write status to `wiz-aio-backup-status`. Mirror the existing `aio` test logic exactly.

---

### FIX-200C-05: Update `updateWizardSummary`

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** S
**What:**

`updateWizardSummary` (line 184) currently reads from old field IDs and writes to summary elements in old step 3. After the step restructure, the summary display is removed from the wizard (it was a repeat of information the user just entered). Remove or no-op this function. If a lightweight summary is desired on completion, it can reference `_loadedConfig` after save.

---

## Phase D — Main Settings Page Sync

### FIX-200D-01: Add `EnableBackupAioStreams` + `SystemRssFeedUrls` to settings page

**File:** `Configuration/configurationpage.html` (modify)
**Estimated effort:** S
**What:**

In the Sources tab (`src-*` fields, around the `SecondaryManifestUrl` / `src-duck-url` area), add:

```html
<label class="checkboxContainer">
  <input type="checkbox" is="emby-checkbox" id="cfg-enable-backup-aio" />
  <span class="checkboxLabel">Enable backup AIOStreams instance</span>
</label>
<div id="cfg-backup-aio-fields">
  <div class="inputContainer">
    <label class="inputLabel inputLabelUnfocused" for="src-duck-url">Backup Manifest URL</label>
    <input id="src-duck-url" type="url" is="emby-input"
           placeholder="https://backup.yourdomain.com/stremio/uuid/token/manifest.json" />
  </div>
</div>
```

In the Advanced/System section, add an RSS Feeds textarea (mirror the wizard pattern, admin-only warning).

Wire `cfg-enable-backup-aio` show/hide of `cfg-backup-aio-fields` in `initSourcesTab` JS.

---

### FIX-200D-02: Sync new fields in `initSourcesTab` and `saveSourcesTab`

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** S
**What:**

In `initSourcesTab`:
```javascript
chk('cfg-enable-backup-aio', cfg.EnableBackupAioStreams || false);
// Toggle visibility on load
var backupChk = q(view, 'cfg-enable-backup-aio');
var backupFields = q(view, 'cfg-backup-aio-fields');
function syncCfgBackup() {
    if (backupFields) backupFields.style.display = backupChk && backupChk.checked ? 'block' : 'none';
}
if (backupChk) { backupChk.addEventListener('change', syncCfgBackup); syncCfgBackup(); }
```

In `saveSourcesTab` / `saveSettings`:
```javascript
EnableBackupAioStreams: esChk(view, 'cfg-enable-backup-aio'),
SecondaryManifestUrl:  esChk(view, 'cfg-enable-backup-aio') ? esVal(view, 'src-duck-url') : '',
SystemRssFeedUrls:     esVal(view, 'cfg-rss-feeds'),
```

---

## Phase E — Build & Verification

### FIX-200E-01: Build

`dotnet build -c Release` — 0 errors, 0 net-new warnings.

---

### FIX-200E-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `EnableBackupAioStreams` | 3+ hits | PluginConfiguration.cs, initSourcesTab, saveSettings |
| `SystemRssFeedUrls` | 3+ hits | PluginConfiguration.cs, initWizardTab, finishWizard |
| `wiz-enable-backup-aio` | 4+ hits | HTML + JS (init, finish, handler) |
| `wiz-rss-feeds` | 3+ hits | HTML + initWizardTab + finishWizard |
| `wiz-emby-base-url` | 3+ hits | HTML + initWizardTab + finishWizard |
| `wiz-backup-aio-fields` | 2+ hits | HTML + JS toggle |
| `es-wizard-step-content` | 3 hits | Exactly 3 steps |

---

### FIX-200E-03: Manual test — Fresh wizard flow

1. Run `./emby-reset.sh` to wipe state
2. Navigate to `http://localhost:8096/web/configurationpage?name=InfiniteDrive`
3. **Step 1:** Confirm layout: AIOStreams card (accent border, REQUIRED badge), AIOMetadata card, Cinemeta card, RSS Feeds card. All OPTIONAL badges visible.
4. Enter a manifest URL. Click "Test Connection". Confirm status appears inline.
5. Check "Enable a backup AIOStreams instance". Confirm backup URL field appears smoothly.
6. Uncheck backup. Confirm backup field hides.
7. Click "Next →"
8. **Step 2:** Confirm layout: Library Locations, Emby Server URL, Language & Region. No metadata source card, no catalog picker.
9. Confirm Emby URL field pre-filled with `window.location.origin`. Confirm hint shows inferred URL.
10. Click "Next →"
11. **Step 3:** Confirm catalogs load automatically (no button required). Confirm catalog checkboxes appear.
12. Select a subset of catalogs. Click "Save & Start First Sync".
13. Assert: config saved, sync triggered, completion screen shown.

---

### FIX-200E-04: Manual test — Wizard re-entry after first run

1. After completing the wizard, navigate back to the config page.
2. Assert: completion screen shown (not wizard steps).
3. Click "Re-run Setup Wizard" (or equivalent reset link).
4. Assert: wizard restarts at Step 1 with all fields pre-populated from saved config.

---

### FIX-200E-05: Manual test — Backup AIOStreams saved correctly

1. Complete wizard with backup AIOStreams enabled and a backup URL entered.
2. Re-open config page → Sources tab.
3. Assert: `cfg-enable-backup-aio` checkbox is checked, backup URL field is visible and populated.
4. Complete wizard WITHOUT enabling backup.
5. Assert: `SecondaryManifestUrl` is empty string in saved config (not stale from previous run).

---

## Rollback Plan

- All changes are HTML/JS/configuration-layer only — no database changes
- `PluginConfiguration.cs` additions are backward-compatible (new properties with defaults)
- To rollback: `git revert` the sprint commits; `./emby-reset.sh` to clear any config saved during testing
- No migration needed in either direction

---

## Completion Criteria

- [x] `EnableBackupAioStreams` and `SystemRssFeedUrls` added to `PluginConfiguration.cs`
- [x] Wizard Step 1 shows: AIOStreams (required, accent card), backup toggle, AIOMetadata, Cinemeta, RSS Feeds
- [x] Wizard Step 2 shows: Library Locations, Emby Server URL (pre-filled from browser), Language & Region
- [x] Wizard Step 3 shows: Catalog picker, loads automatically on step navigation (no button)
- [x] Step labels: "Providers" / "Your Setup" / "Catalogs"
- [x] Backup AIOStreams fields conditionally shown/hidden via checkbox
- [x] Emby URL hint shows inferred `window.location.origin`
- [x] `finishWizard` correctly saves all new fields; backup URL saved as empty string when checkbox unchecked
- [x] Main settings Sources tab synced: backup toggle, RSS feeds textarea
- [x] `dotnet build -c Release` — 0 errors
- [ ] Fresh wizard flow completes end-to-end without errors (manual test required)
- [x] Grep checklist passes

---

## Resolved Design Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **RSS feed notice is inline** — not a modal. It's a definition of how system RSS works ("visible to all users"), not a warning. Modals interrupt flow; inline text educates in context. Apple principle: inform, don't alarm. | Apple UX: inline for informational, modal only for destructive/irreversible actions |
| 2 | **Apple TV–style nav buttons on all wizard steps.** Two buttons: `< Back` and `Finish & Sync` (step 3) / `Next →` (steps 1–2). The wizard is re-entrant — "Start First Sync" is wrong because it implies first-run only. "Finish & Sync" is honest: it saves your choices and syncs, every time. On re-entry, it still means "apply these settings and sync now." | Apple TV onboarding: `< Back` left, primary action right, one tap to commit |
| 3 | **RSS feed plumbing deferred.** `SystemRssFeedUrls` is config-only in Sprint 200; service consumption is a separate sprint. Acceptable. | Backward-compatible; field has defaults |
| 4 | **Anime routing is by genre, not media type.** When `wiz-enable-anime` is checked, items tagged with anime genre (from catalog) route to a dedicated mixed library (`SyncPathAnime`). When unchecked, anime items flow to normal paths — anime movies → `SyncPathMovies`, anime series → `SyncPathShows`. The checkbox controls routing destination; genre controls identification; media type controls folder structure (flat for movies, `Season 01/` for series). | Anime is a genre, not a type. Mixed library handles both movies and series in one place |

---

## Notes

**Files created:** 0
**Files modified:** 3 (`PluginConfiguration.cs`, `Configuration/configurationpage.html`, `Configuration/configurationpage.js`)
**Files deleted:** 0

Build: `dotnet build -c Release` — 0 errors, 1 warning (pre-existing)
Grep checklist: All passed

**Risk:** MEDIUM — wizard is the first-run critical path; a broken wizard blocks all new users from configuring the plugin.
Mitigated by:
1. `./emby-reset.sh` is the canonical test harness for fresh-run scenarios
2. All changes are JS/HTML — no compiled code except two new config properties
3. `IsFirstRunComplete` flag means existing users never see the wizard again unless they explicitly re-run it
