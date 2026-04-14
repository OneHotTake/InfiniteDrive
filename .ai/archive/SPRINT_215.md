---

# Sprint 215 — Settings Redesign UI (v2.3)

**Status:** Draft | **Risk:** MED | **Depends:** Sprint 214 | **Target:** v0.55

## Why
Replace the current 7-tab admin UI (wizard + scattered settings) with the flat, calm, first-party-feeling layout defined in the v2.3 spec. Sprint 214 delivered all backend prerequisites. This sprint is the complete replacement of `configurationpage.html` and `configurationpage.js`.

## Non-Goals
- No C# changes of any kind
- No new API endpoints (all were added in Sprint 214)
- No changes to `discoverpage.html` or `discoverpage.js`
- No changes to task scheduling, playback, or stream resolution

---

## Tasks

### FIX-215-01: Replace tab structure in `configurationpage.html`
**Files:** `Configuration/configurationpage.html` (modify)
**Effort:** L
**What:** Replace the existing tab bar and all tab content `div`s with the new 7-tab structure. The new tab IDs must be:

- `data-es-tab="providers"` → `id="es-tab-content-providers"`
- `data-es-tab="libraries"` → `id="es-tab-content-libraries"`
- `data-es-tab="sources"` → `id="es-tab-content-sources"`
- `data-es-tab="security"` → `id="es-tab-content-security"`
- `data-es-tab="parental"` → `id="es-tab-content-parental"`
- `data-es-tab="health"` → `id="es-tab-content-health"`
- `data-es-tab="repair"` → `id="es-tab-content-repair"`

Default active tab: `providers`.

Add status pills to the tab bar (right-aligned via flexbox with `margin-left:auto` on a wrapper span):

```
<span id="es-pill-aio" class="es-status-pill">AIO ✅</span>
<span id="es-pill-backup" class="es-status-pill es-pill-hidden">Backup ✅</span>
```

Add CSS class:

```
.es-status-pill { font-size:.72em; padding:.2em .55em; border-radius:99px;
  background:rgba(128,128,128,0.15); margin-left:.4em; }
.es-pill-hidden { display:none; }
.es-pill-ok  { background:rgba(40,167,69,0.2);  color:#28a745; }
.es-pill-err { background:rgba(220,53,69,0.2);  color:#dc3545; }
```

Add floating save button (hidden by default, shown on libraries/security/parental tabs):

```html
<div id="es-float-save" style="display:none;position:fixed;bottom:1.5em;right:1.5em;z-index:100">
  <button type="button" is="emby-button" class="raised button-submit" id="es-float-save-btn">
    Save Settings
  </button>
  <span id="es-float-save-confirm" style="display:none;margin-left:.75em;font-size:.85em;color:#28a745">
    ✓ Saved
  </span>
</div>
```

**Gotcha:** Delete all `es-tab-content-setup`, `es-tab-content-overview`, `es-tab-content-settings`, `es-tab-content-content`, `es-tab-content-marvin`, `es-tab-content-advanced` divs entirely after their contents are migrated.

---

### FIX-215-02: Build Tab 1 — Providers
**Files:** `Configuration/configurationpage.html` (modify)
**Effort:** L
**What:** Inside `es-tab-content-providers`, build four provider cards plus the Getting Started checklist card and RSS card. Layout top to bottom:

1. **Getting Started card** (`id="es-card-getting-started"`) — three steps, computed by JS on load. See spec §Tab 1.

2. **AIOStreams Primary card** (`es-card es-card-accent`) — contains:
   - URL input `id="prov-aio-url"` (type=`url`, `is="emby-input"`)
   - `id="prov-aio-parse-result"` hint span
   - "Don't have one?" link to `https://duckkota.gitlab.io/stremio-tools/quickstart/`
   - `[ Refresh ]` button `data-es-action="prov-aio-refresh"`
   - `[ Edit ]` button `data-es-action="prov-aio-edit"` (hidden until URL is set)
   - Status line `id="prov-aio-status"` — "Last refreshed: X  •  N sources"

3. **AIOStreams Backup card** — contains:
   - URL input `id="prov-backup-url"` (type=`url`, `is="emby-input"`)
   - Helper text: "Leave blank to disable backup."
   - `[ Refresh ]` button `data-es-action="prov-backup-refresh"`
   - `[ Edit ]` button `data-es-action="prov-backup-edit"` (hidden until URL is set)
   - Status line `id="prov-backup-status"`

4. **AIOMetadata card** — contains:
   - Checkbox `id="prov-meta-enabled"` `is="emby-checkbox"` label "Enable AIOMetadata"
   - URL input `id="prov-meta-url"` (hidden when checkbox unchecked) via wrapper `id="prov-meta-fields"`
   - `[ Refresh ]` and `[ Edit ]` buttons, same pattern

5. **Cinemeta card** — checkbox only: `id="prov-cinemeta-enabled"`. No URL.

6. **System RSS Feeds card** — textarea `id="prov-rss-urls"` (`is="emby-textarea"`), text input `id="prov-rss-name"`, inline `[ Save Feeds ]` button `data-es-action="prov-rss-save"`.

Add per-tab Refresh button in top-right header of this tab: `data-es-action="prov-refresh-all"`.

**Gotcha:** The Edit buttons are wired to `data.ManifestConfigureUrl` / `data.BackupManifestConfigureUrl` from `GET /InfiniteDrive/Status`, not hardcoded URLs. JS sets `href` dynamically. Show username interstitial popover (see FIX-215-06).

---

### FIX-215-03: Build Tab 2 — Libraries & Servers
**Files:** `Configuration/configurationpage.html` (modify)
**Effort:** M
**What:** Inside `es-tab-content-libraries`, three cards:

1. **Library Locations card** — inputs:
   - `id="lib-base-path"` Base sync path
   - Derived subfolder display `id="lib-derived-paths"` (read-only text, updated live by JS)
   - `id="lib-name-movies"` Movies library name
   - `id="lib-name-series"` Series library name
   - `id="lib-name-anime"` Anime library name
   - Static note: "Anime is always created as a separate library."

2. **Emby Server card** — preserve the existing localhost warning banner exactly (`id="es-base-url-warning"`, `id="es-base-url-suggested"`). Input: `id="lib-emby-url"`.

3. **Metadata Preferences card** — default state shows summary line `id="lib-meta-summary"` and `[ Override → ]` button `data-es-action="lib-meta-override"`. Override section `id="lib-meta-override-fields"` (hidden by default) contains the three existing dropdowns: `id="lib-meta-lang"`, `id="lib-meta-country"`, `id="lib-meta-img-lang"`. Checkbox: `id="lib-write-nfo"` "Write .nfo metadata files".

Float save applies to this tab. Show `es-float-save` when `libraries` tab is active.

---

### FIX-215-04: Build Tab 3 — Sources
**Files:** `Configuration/configurationpage.html` (modify)
**Effort:** M
**What:** Inside `es-tab-content-sources`, migrate the existing catalog table from the wizard/settings tab. **Remove the order columns (▲ ▼) entirely.** New column set: `☑ SOURCE | TYPE | PROGRESS | LIMIT`.

Keep existing element IDs for the table body and catalog panel so existing `loadCatalogs()` / `renderCatalogPanel()` JS functions work without rewriting: `id="es-catalog-panel-src"` (new prefix `src` replacing `cfg`/`wiz`), `id="es-catalog-progress-src"`.

Top-right buttons:
- `[ Refresh Sources ]` `data-es-action="src-refresh"`
- `[ Sync All Now ]` `data-es-load-catalogs="src"` (reuses existing pattern)

Bottom of table: last sync timestamp `id="src-last-sync"`, `[ Select All ]` `data-es-select="src" data-es-select-val="true"`, `[ Select None ]` `data-es-select-val="false"`.

Add Custom Source sub-card below the table: `id="src-custom-name"`, `id="src-custom-url"`, `[ Validate Source ]` `data-es-action="src-custom-validate"`, `[ Add to Library ]` `data-es-action="src-custom-add"` (disabled until validation passes).

**Gotcha:** Do not add `data-es-cat-move` buttons. They existed for the defunct channel feature and are removed per spec. The `moveCatalogRow()` JS function can remain but will never be called.

---

### FIX-215-05: Build Tab 4 — Security
**Files:** `Configuration/configurationpage.html` (modify)
**Effort:** S
**What:** Inside `es-tab-content-security`, one card only:

```html
<div class="es-card">
  <div class="es-card-title">Playback Security</div>
  <p class="es-card-text">
    InfiniteDrive signs all stream URLs using a unique secret key.
    This happens automatically — you never need to manage it.
  </p>
  <div class="es-btn-row">
    <button type="button" is="emby-button" class="raised button-submit"
            data-es-action="sec-rotate">Rotate Secret</button>
  </div>
  <div id="sec-rotate-progress" style="display:none;margin-top:.75em">
    <div class="es-track"><div class="es-fill" id="sec-rotate-bar" style="width:0%"></div></div>
    <div id="sec-rotate-msg" style="font-size:.82em;opacity:.7;margin-top:.35em"></div>
  </div>
  <p class="es-hint" style="margin-top:.75em">
    Last rotated: <span id="sec-rotated-ago">never</span>
  </p>
</div>
```

Storage note below the card (existing `<p>` text, preserve verbatim).

**No** Signature Validity card. **No** auto-rotation dropdown. **No** reveal/regenerate split. Float save is NOT shown on this tab — the Rotate button is self-contained.

---

### FIX-215-06: Build Tab 5 — Parental Controls
**Files:** `Configuration/configurationpage.html` (modify)
**Effort:** M
**What:** Inside `es-tab-content-parental`, two cards:

**Card 1 — Content Filtering:** Preserve the existing info banner, TMDb key input (`id="cfg-tmdb-api-key"` — keep this ID), Test Key button, Behavior Matrix table, Block Unrated checkbox (`id="cfg-block-unrated"` — keep this ID), and Filtering Status div (`id="es-filter-status"` — keep this ID) **exactly as they exist today**. Do not change IDs, classes, or structure of these elements.

**Card 2 — Blocked Content:** Replace the existing raw IMDb ID text input block with:

```html
<div class="inputContainer">
  <label class="inputLabel">Search titles in your InfiniteDrive library</label>
  <div class="es-src-row">
    <input id="par-block-search" type="text" is="emby-input"
           placeholder="Search titles..." />
    <button type="button" is="emby-button" class="raised button"
            data-es-action="par-block-search">Search</button>
  </div>
  <p class="es-hint">Only content managed by InfiniteDrive appears here.
     Physical media files added separately are unaffected.</p>
</div>
<div id="par-search-results" style="display:none;margin:.75em 0"></div>
<div style="margin-top:1.25em">
  <label class="inputLabel">Currently Blocked</label>
  <div id="es-blocked-items-list"></div>
  <div style="display:flex;gap:.75em;flex-wrap:wrap;margin-top:.6em">
    <button id="es-unblock-selected-btn" type="button" is="emby-button"
            class="raised button">Unblock Selected</button>
    <button id="es-unblock-all-btn" type="button" is="emby-button"
            class="raised button">Unblock All</button>
  </div>
</div>
```

**Keep IDs** `es-blocked-items-list`, `es-unblock-selected-btn`, `es-unblock-all-btn` — existing JS uses these.

Float save applies to this tab (for TMDb key and Block Unrated toggle).

---

### FIX-215-07: Build Tab 6 — System Health
**Files:** `Configuration/configurationpage.html` (modify)
**Effort:** M
**What:** Inside `es-tab-content-health`, migrate the existing health dashboard from the `overview`/`settings` tab. Keep the existing dashboard rendering IDs intact (`es-sources-body`, `es-sync-body`, debug card `data-es-dashboard-debug="true"` pattern) so `refreshDashboard()` / `renderDashboard()` work without rewriting.

Add top-right Refresh button: `data-es-action="refresh-dashboard"` (same action as existing — no change needed in JS).

Add provider status cards grid above the existing stats:

```html
<div id="health-provider-grid"
     style="display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:.75em;margin-bottom:1.25em">
  <div class="es-card" id="health-card-aio">
    <div class="es-card-title" style="font-size:.85em">AIOStreams (Primary)</div>
    <div id="health-aio-status">—</div>
    <div id="health-aio-detail" class="es-hint"></div>
  </div>
  <div class="es-card" id="health-card-backup" style="display:none">
    <div class="es-card-title" style="font-size:.85em">AIOStreams Backup</div>
    <div id="health-backup-status">—</div>
    <div id="health-backup-detail" class="es-hint"></div>
  </div>
  <div class="es-card" id="health-card-meta" style="display:none">
    <div class="es-card-title" style="font-size:.85em">AIOMetadata</div>
    <div id="health-meta-status">—</div>
  </div>
  <div class="es-card" id="health-card-cinemeta">
    <div class="es-card-title" style="font-size:.85em">Cinemeta</div>
    <div id="health-cinemeta-status">—</div>
  </div>
</div>
```

Background tasks table: migrate existing task rows from the Advanced tab. Keep `data-es-task` attributes intact.

Advanced Debug Tools section: migrate from Advanced tab verbatim. Collapsed by default (`id="es-debug-inner"` `style="display:none"`), toggle via existing `data-es-action="toggle-debug"`.

**Gotcha:** The existing `renderDashboard()` function writes to IDs inside the old `es-tab-content-settings` div. After this sprint those IDs live in `es-tab-content-health`. The IDs themselves do not change — only which tab div contains them. `renderDashboard()` uses `q(view, id)` which searches the whole view, so no JS changes are needed.

---

### FIX-215-08: Build Tab 7 — Repair
**Files:** `Configuration/configurationpage.html` (modify)
**Effort:** M
**What:** Inside `es-tab-content-repair`, migrate from existing `es-tab-content-marvin` and `es-tab-content-content`. Layout:

1. **Marvin card** — migrate existing DON'T PANIC / Summon Marvin UI verbatim. Keep `id="es-summon-marvin-btn"`, `id="es-marvin-status"`, `id="es-refresh-worker-status"`, `id="es-needs-enrichment-count"`, `id="es-blocked-count"` — existing `loadImprobabilityStatus()` writes to these.

2. **Content Management card** — migrate existing Source Sync / Version Slots / Pre-Warm Cache buttons verbatim. Keep `data-es-task` values identical.

3. **Danger Zone card** — migrate verbatim from current Settings tab. Keep `data-es-action` values: `reset-settings-confirm`, `purge-catalog-confirm`, `nuclear-step1`, `nuclear-step2`, `nuclear-execute`, `nuclear-cancel`. Keep all IDs for the confirmation UI steps.

---

### FIX-215-09: Update `showTab()` in `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** S
**What:** Replace the existing `tabMap` and tab ID arrays with the new structure.

```javascript
function showTab(view, name) {
    var tabs = ['providers','libraries','sources','security','parental','health','repair'];

    tabs.forEach(function(t) {
        var c = q(view, 'es-tab-content-' + t);
        if (c) c.classList.toggle('active', t === name);
    });
    var btns = view.querySelectorAll('[data-es-tab]');
    for (var i = 0; i < btns.length; i++) {
        btns[i].classList.toggle('active', btns[i].getAttribute('data-es-tab') === name);
    }

    // Float save visibility
    var floatSave = view.querySelector('#es-float-save');
    if (floatSave) {
        floatSave.style.display =
            (name === 'libraries' || name === 'parental') ? 'block' : 'none';
    }

    // Tab-specific load actions
    if (name === 'providers')  { loadProvidersTab(view); }
    if (name === 'libraries')  { populateLibrariesTab(view, _loadedConfig); }
    if (name === 'sources')    { loadCatalogs(view, 'src'); }
    if (name === 'health')     { refreshDashboard(view);
                                  if (!_dashInterval) _dashInterval = setInterval(function() { refreshDashboard(view); }, 10000); }
    if (name === 'repair')     { loadImprobabilityStatus(view); }
    if (name === 'parental')   { loadBlockedItems(view); }
    if (name !== 'health') {
        if (_dashInterval) { clearInterval(_dashInterval); _dashInterval = null; }
    }
}
```

Default tab on load: `showTab(view, 'providers')`.

**Gotcha:** Remove all references to old tab names (`setup`, `overview`, `settings`, `content`, `marvin`, `advanced`) from `showTab()` and from `loadConfig()`.

---

### FIX-215-10: Update `loadConfig()` and add `populateProvidersTab()` in `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** M
**What:**

**In `loadConfig()`:** After fetching config, call `ApiClient.getServerConfiguration()` to auto-fill metadata fields if not yet overridden:

```javascript
ApiClient.getServerConfiguration().then(function(serverCfg) {
    if (!cfg.MetadataLanguage)
        cfg.MetadataLanguage = serverCfg.PreferredMetadataLanguage || 'en';
    if (!cfg.MetadataCertificationCountry)
        cfg.MetadataCertificationCountry = serverCfg.MetadataCountryCode || 'US';
    if (!cfg.MetadataImageLanguage)
        cfg.MetadataImageLanguage = serverCfg.PreferredMetadataLanguage || 'en';
    _loadedConfig = cfg;
    showTab(view, 'providers');
    loading.hide();
}).catch(function() {
    showTab(view, 'providers');
    loading.hide();
});
```

**Add `loadProvidersTab(view)`:** Reconstructs the display manifest URL from stored components and updates provider card fields:

```javascript
function loadProvidersTab(view) {
    if (!_loadedConfig) return;
    var cfg = _loadedConfig;

    // Reconstruct manifest URL for display
    var aioUrl = cfg.AioStreamsUrl || '';
    var uuid   = cfg.AioStreamsUuid  || '';
    var token  = cfg.AioStreamsToken || '';
    var displayUrl = (aioUrl && uuid && token)
        ? aioUrl + '/stremio/' + uuid + '/' + token + '/manifest.json'
        : (aioUrl || '');

    var urlEl = view.querySelector('#prov-aio-url');
    if (urlEl && !urlEl._userEditing) urlEl.value = displayUrl;

    var backupEl = view.querySelector('#prov-backup-url');
    if (backupEl && !backupEl._userEditing)
        backupEl.value = cfg.SecondaryManifestUrl || '';

    var metaEl = view.querySelector('#prov-meta-url');
    if (metaEl) metaEl.value = cfg.AioMetadataBaseUrl || '';

    var metaChk = view.querySelector('#prov-meta-enabled');
    if (metaChk) metaChk.checked = !!(cfg.AioMetadataBaseUrl);

    var cineChk = view.querySelector('#prov-cinemeta-enabled');
    if (cineChk) cineChk.checked =
        cfg.EnableCinemetaCatalog != null ? cfg.EnableCinemetaCatalog : true;

    var metaFields = view.querySelector('#prov-meta-fields');
    if (metaFields) metaFields.style.display =
        (cfg.AioMetadataBaseUrl) ? 'block' : 'none';

    // Edit buttons — set href from Status response
    esFetch('/InfiniteDrive/Status').then(function(r) { return r.json(); })
        .then(function(data) {
            var editBtn = view.querySelector('#prov-aio-edit-btn');
            if (editBtn) {
                if (data.ManifestConfigureUrl) {
                    editBtn.style.display = '';
                    editBtn._configureUrl  = data.ManifestConfigureUrl;
                } else {
                    editBtn.style.display = 'none';
                }
            }
            // Status pills
            updateStatusPills(view, data);
            // Getting Started card
            evaluateGettingStarted(view, _loadedConfig);
        }).catch(function() {});
}
```

---

### FIX-215-11: Add `populateLibrariesTab()`, update `saveSettings()`, add `saveLibrariesTab()` in `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** M
**What:**

**Add `populateLibrariesTab(view, cfg)`:**

```javascript
function populateLibrariesTab(view, cfg) {
    if (!cfg) return;
    function set(id, v) { var el = view.querySelector('#'+id); if (el) el.value = v || ''; }
    var base = cfg.BaseSyncPath || '/media/infinitedrive';
    set('lib-base-path', base);
    updateDerivedPaths(view, base);
    set('lib-name-movies', cfg.LibraryNameMovies || 'Streamed Movies');
    set('lib-name-series', cfg.LibraryNameSeries || 'Streamed Series');
    set('lib-name-anime',  cfg.LibraryNameAnime  || 'Streamed Anime');
    set('lib-emby-url',    cfg.EmbyBaseUrl || window.location.origin);

    // Metadata summary vs override
    var lang    = cfg.MetadataLanguage || 'en';
    var country = cfg.MetadataCertificationCountry || 'US';
    var imgLang = cfg.MetadataImageLanguage || 'en';
    var summaryEl = view.querySelector('#lib-meta-summary');
    var langNames = { en:'English', fr:'French', de:'German', es:'Spanish',
                      it:'Italian', ja:'Japanese', pt:'Portuguese',
                      ru:'Russian', zh:'Chinese', ko:'Korean', pl:'Polish', nl:'Dutch' };
    var countryNames = { US:'United States', GB:'United Kingdom', CA:'Canada',
                         AU:'Australia', FR:'France', DE:'Germany', JP:'Japan', KR:'South Korea' };
    if (summaryEl) summaryEl.textContent =
        'Language: ' + (langNames[lang] || lang) +
        '  •  Country: ' + (countryNames[country] || country) +
        '  •  Artwork: ' + (langNames[imgLang] || imgLang);

    // Populate dropdowns (even if hidden)
    set('lib-meta-lang',    lang);
    set('lib-meta-country', country);
    set('lib-meta-img-lang', imgLang);

    var nfoEl = view.querySelector('#lib-write-nfo');
    if (nfoEl) nfoEl.checked = !!cfg.WriteNfoFiles;
}
```

**Add `saveLibrariesTab(view)`** — called by the float save button when on the `libraries` tab:

```javascript
function saveLibrariesTab(view) {
    if (!_loadedConfig) return;
    var cfg = JSON.parse(JSON.stringify(_loadedConfig));
    var base = (view.querySelector('#lib-base-path') || {}).value || '/media/infinitedrive';
    cfg.BaseSyncPath        = base;
    cfg.SyncPathMovies      = base + '/movies';
    cfg.SyncPathShows       = base + '/shows';
    cfg.SyncPathAnime       = base + '/anime';
    cfg.LibraryNameMovies   = (view.querySelector('#lib-name-movies') || {}).value || 'Streamed Movies';
    cfg.LibraryNameSeries   = (view.querySelector('#lib-name-series') || {}).value || 'Streamed Series';
    cfg.LibraryNameAnime    = (view.querySelector('#lib-name-anime')  || {}).value || 'Streamed Anime';
    cfg.EmbyBaseUrl         = (view.querySelector('#lib-emby-url')    || {}).value || window.location.origin;
    cfg.MetadataLanguage             = (view.querySelector('#lib-meta-lang')    || {}).value || 'en';
    cfg.MetadataCertificationCountry = (view.querySelector('#lib-meta-country') || {}).value || 'US';
    cfg.MetadataImageLanguage        = (view.querySelector('#lib-meta-img-lang')|| {}).value || 'en';
    cfg.WriteNfoFiles = !!(view.querySelector('#lib-write-nfo') || {}).checked;
    ApiClient.updatePluginConfiguration(pluginId, cfg)
        .then(function() {
            _loadedConfig = cfg;
            _unsavedChanges = false;
            showFloatSaveConfirm(view);
            esFetch('/InfiniteDrive/Setup/ProvisionLibraries', {method:'POST'}).catch(function(){});
        })
        .catch(function(err) { Dashboard.alert('Save failed: ' + (err && err.message || err)); });
}
```

---

### FIX-215-12: Add security rotation wiring in `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** S
**What:** Add `rotateSecret(view)` function and wire it to `data-es-action="sec-rotate"` in `dispatchAction()`.

```javascript
function rotateSecret(view) {
    if (!confirm('Rotate signing secret?\n\nThis rebuilds all .strm files with a new key.\nYour library remains fully playable throughout.')) return;
    var bar = view.querySelector('#sec-rotate-bar');
    var msg = view.querySelector('#sec-rotate-msg');
    var prog = view.querySelector('#sec-rotate-progress');
    if (prog) prog.style.display = 'block';
    if (msg) msg.textContent = 'Starting rotation…';

    esFetch('/InfiniteDrive/Setup/RotateApiKey', {method:'POST'})
        .then(function(r) { return r.json(); })
        .then(function(data) {
            if (!data.Success) throw new Error(data.Error || 'Rotation failed');
            // Poll progress
            var poll = setInterval(function() {
                esFetch('/InfiniteDrive/Setup/RotationStatus')
                    .then(function(r) { return r.json(); })
                    .then(function(s) {
                        var pct = s.FilesTotal > 0
                            ? Math.round((s.FilesWritten / s.FilesTotal) * 100) : 0;
                        if (bar) bar.style.width = pct + '%';
                        if (msg) msg.textContent = 'Rebuilding stream files… ' +
                            s.FilesWritten + ' of ' + s.FilesTotal;
                        if (!s.IsRotating) {
                            clearInterval(poll);
                            if (bar) bar.style.width = '100%';
                            if (msg) msg.textContent = '✅ Done. All stream files updated.';
                            // Update "last rotated" display
                            var agoEl = view.querySelector('#sec-rotated-ago');
                            if (agoEl) agoEl.textContent = 'just now';
                            setTimeout(function() {
                                if (prog) prog.style.display = 'none';
                            }, 4000);
                        }
                    }).catch(function() { clearInterval(poll); });
            }, 1500);
        })
        .catch(function(err) {
            if (msg) msg.textContent = '❌ Rotation failed. Existing stream files are unchanged. ' +
                (err && err.message || '');
        });
}
```

In `dispatchAction()`, add: `case 'sec-rotate': rotateSecret(view); break;`

---

### FIX-215-13: Add block search wiring in `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** M
**What:** Add `searchBlockItems(view)` and `blockItemById(view, internalId, title)`. Wire to `data-es-action="par-block-search"`.

```javascript
function searchBlockItems(view) {
    var input = view.querySelector('#par-block-search');
    var resultsEl = view.querySelector('#par-search-results');
    if (!input || !resultsEl) return;
    var query = (input.value || '').trim();
    if (!query) return;

    resultsEl.style.display = 'block';
    resultsEl.innerHTML = '<p style="opacity:.5;font-size:.85em">Searching…</p>';

    esFetch('/InfiniteDrive/Admin/SearchItems?q=' + encodeURIComponent(query) + '&limit=5')
        .then(function(r) { return r.json(); })
        .then(function(data) {
            if (!data.Items || !data.Items.length) {
                resultsEl.innerHTML = '<p class="es-hint">No results found in your InfiniteDrive library.</p>';
                return;
            }
            var html = '<div style="border:1px solid rgba(128,128,128,0.2);border-radius:6px;overflow:hidden">';
            data.Items.forEach(function(item) {
                var icon = item.MediaType === 'movie' ? '🎬' : (item.MediaType === 'anime' ? '🎌' : '📺');
                var extId = item.DisplayExternalId
                    ? '<span style="opacity:.45;font-size:.8em;margin-left:.5em">'
                      + esc(item.DisplayExternalIdType) + ':' + esc(item.DisplayExternalId) + '</span>'
                    : '';
                html += '<div style="display:flex;align-items:center;justify-content:space-between;'
                      + 'padding:.55em .75em;border-bottom:1px solid rgba(128,128,128,0.1)">'
                      + '<span>' + icon + '  <strong>' + esc(item.Title) + '</strong>'
                      + (item.Year ? ' (' + item.Year + ')' : '')
                      + extId + '</span>'
                      + '<button type="button" is="emby-button" class="raised button"'
                      + ' style="font-size:.8em;padding:.25em .7em"'
                      + ' data-es-action="par-block-item"'
                      + ' data-item-id="' + esc(item.Id) + '"'
                      + ' data-item-title="' + esc(item.Title) + '">'
                      + 'Block</button>'
                      + '</div>';
            });
            html += '</div>';
            resultsEl.innerHTML = html;
        })
        .catch(function(err) {
            resultsEl.innerHTML = '<p style="color:#dc3545;font-size:.85em">Search failed: ' + esc(err.message) + '</p>';
        });
}

function blockItemById(view, internalId, title) {
    if (!confirm('Block "' + title + '"?\n\nThis removes the item\'s stream files immediately.')) return;

    esFetch('/InfiniteDrive/Admin/BlockItems', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ItemIds: [internalId] })
    })
    .then(function(r) { return r.json(); })
    .then(function(data) {
        if (data.Success) {
            // Update the search result row inline — no full reload
            var btn = view.querySelector('[data-item-id="' + internalId + '"]');
            if (btn) {
                var row = btn.parentElement;
                if (row) row.innerHTML = row.innerHTML.replace(
                    /<button[^>]*data-item-id="[^"]*"[^>]*>Block<\/button>/,
                    '<span style="color:#28a745;font-size:.8em">✅ Blocked</span>'
                );
            }
            // Refresh the blocked items list
            loadBlockedItems(view);
        } else {
            Dashboard.alert('Block failed: ' + (data.Error || 'unknown error'));
        }
    })
    .catch(function() {
        Dashboard.alert('Block request failed. Check server logs.');
    });
}
```

In `dispatchAction()`, add:

```javascript
case 'par-block-search':
    searchBlockItems(view);
    break;
case 'par-block-item':
    blockItemById(view,
        el.getAttribute('data-item-id'),
        el.getAttribute('data-item-title') || 'this item');
    break;
```

Also wire the search input to trigger on Enter key in `initView()`:

```javascript
var parSearch = view.querySelector('#par-block-search');
if (parSearch) {
    parSearch.addEventListener('keydown', function(e) {
        if (e.key === 'Enter') { e.preventDefault(); searchBlockItems(view); }
    });
}
```

---

### FIX-215-14: Add Getting Started card logic and status pill updates in `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** S
**What:** Add `evaluateGettingStarted(view, cfg)` and `updateStatusPills(view, data)`.

```javascript
function evaluateGettingStarted(view, cfg) {
    var card = view.querySelector('#es-card-getting-started');
    if (!card) return;

    var step1 = !!(cfg.AioStreamsUrl);
    var step2 = !!(cfg.BaseSyncPath && cfg.EmbyBaseUrl);
    var step3 = !!(cfg.AioStreamsCatalogIds || cfg.EnableAioStreamsCatalog);

    if (step1 && step2 && step3) {
        card.innerHTML =
            '<div style="display:flex;align-items:center;justify-content:space-between">'
          + '<span>✅ Setup complete — InfiniteDrive is configured and syncing.</span>'
          + '<a class="es-link-btn" href="#" data-es-action="show-guide">Review Guide</a>'
          + '</div>';
        return;
    }

    function stepHtml(num, label, done, tabTarget) {
        if (done) {
            return '<div style="padding:.35em 0">✅ ' + num + '. ' + label + '</div>';
        }
        return '<div style="padding:.35em 0">'
             + num + '. ' + label + '  '
             + '<a class="es-link-btn" href="#" data-es-tab="' + tabTarget + '">'
             + '→ Go to ' + tabTarget.charAt(0).toUpperCase() + tabTarget.slice(1) + ' tab'
             + '</a></div>';
    }

    card.innerHTML =
        '<div class="es-card-title">Getting Started</div>'
      + stepHtml(1, 'Connect AIOStreams Manifest', step1, 'providers')
      + stepHtml(2, 'Configure Libraries &amp; Servers', step2, 'libraries')
      + stepHtml(3, 'Choose Sources', step3, 'sources')
      + '<p class="es-hint" style="margin-top:.75em">'
      + 'Once complete, your library will begin populating automatically.</p>';
}

function updateStatusPills(view, data) {
    var pillAio    = view.querySelector('#es-pill-aio');
    var pillBackup = view.querySelector('#es-pill-backup');
    if (!pillAio) return;

    var aioOnline = !!(data && data.AioStreamsOnline);
    pillAio.textContent = 'AIO ' + (aioOnline ? '✅' : '🔴');
    pillAio.className = 'es-status-pill ' + (aioOnline ? 'es-pill-ok' : 'es-pill-err');

    if (data && data.BackupConfigured) {
        if (pillBackup) {
            pillBackup.style.display = '';
            var backupOnline = !!(data.BackupAioStreamsOnline);
            pillBackup.textContent = 'Backup ' + (backupOnline ? '✅' : '🔴');
            pillBackup.className = 'es-status-pill ' + (backupOnline ? 'es-pill-ok' : 'es-pill-err');
        }
    } else {
        if (pillBackup) pillBackup.style.display = 'none';
    }
}
```

---

### FIX-215-15: Add float save wiring, dirty-state tracking, and username interstitial in `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** S
**What:**

**Float save button:**

```javascript
function initFloatSave(view) {
    var btn = view.querySelector('#es-float-save-btn');
    if (!btn || btn._esBound) return;
    btn._esBound = true;
    btn.addEventListener('click', function() {
        var activeTab = view.querySelector('[data-es-tab].active');
        var tab = activeTab ? activeTab.getAttribute('data-es-tab') : '';
        if (tab === 'libraries') saveLibrariesTab(view);
        if (tab === 'parental')  saveParentalTab(view);
    });
}

function markDirty(view) {
    _unsavedChanges = true;
    var btn = view.querySelector('#es-float-save-btn');
    if (btn && btn.textContent.indexOf('•') === -1) btn.textContent = 'Save Settings •';
}

function showFloatSaveConfirm(view) {
    _unsavedChanges = false;
    var btn  = view.querySelector('#es-float-save-btn');
    var conf = view.querySelector('#es-float-save-confirm');
    if (btn)  btn.textContent = 'Save Settings';
    if (conf) {
        conf.style.display = 'inline';
        setTimeout(function() { conf.style.display = 'none'; }, 3000);
    }
}
```

Call `initFloatSave(view)` from `initView(view)`.

Wire `markDirty(view)` to `change` events on all inputs in the libraries and parental tabs by adding to `initView()`:

```javascript
['lib-base-path','lib-name-movies','lib-name-series','lib-name-anime',
 'lib-emby-url','lib-meta-lang','lib-meta-country','lib-meta-img-lang',
 'lib-write-nfo','cfg-tmdb-api-key','cfg-block-unrated'].forEach(function(id) {
    var el = view.querySelector('#' + id);
    if (el) el.addEventListener('change', function() { markDirty(view); });
});
```

**Username interstitial for Edit buttons:**

```javascript
function openProviderEdit(view, configureUrl) {
    if (!configureUrl) return;
    ApiClient.getCurrentUser().then(function(user) {
        var username = (user && user.Name) ? user.Name : '';
        var msg = 'Opening AIOStreams configuration.\n'
                + (username ? 'Sign in as: ' + username + '\n' : '')
                + '\n' + configureUrl;
        if (confirm(msg + '\n\nOpen in new tab?')) {
            window.open(configureUrl, '_blank', 'noopener');
        }
    }).catch(function() {
        window.open(configureUrl, '_blank', 'noopener');
    });
}
```

In `dispatchAction()` add:

```javascript
case 'prov-aio-edit': {
    var editBtn = view.querySelector('#prov-aio-edit-btn');
    if (editBtn && editBtn._configureUrl) openProviderEdit(view, editBtn._configureUrl);
    break;
}
case 'prov-backup-edit': {
    var bkBtn = view.querySelector('#prov-backup-edit-btn');
    if (bkBtn && bkBtn._configureUrl) openProviderEdit(view, bkBtn._configureUrl);
    break;
}
```

---

### FIX-215-16: Add `saveParentalTab()` and wire `sec-rotated-ago` on load in `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** S
**What:**

```javascript
function saveParentalTab(view) {
    if (!_loadedConfig) return;
    var cfg = JSON.parse(JSON.stringify(_loadedConfig));
    cfg.TmdbApiKey              = (view.querySelector('#cfg-tmdb-api-key') || {}).value || '';
    cfg.BlockUnratedForRestricted = !!(view.querySelector('#cfg-block-unrated') || {}).checked;
    ApiClient.updatePluginConfiguration(pluginId, cfg)
        .then(function() {
            _loadedConfig = cfg;
            _unsavedChanges = false;
            showFloatSaveConfirm(view);
            updateFilterStatus(view, cfg.TmdbApiKey);
        })
        .catch(function(err) { Dashboard.alert('Save failed: ' + (err && err.message || err)); });
}
```

Wire "Last rotated" display in `loadProvidersTab()` or at end of `loadConfig()`:

```javascript
var agoEl = view.querySelector('#sec-rotated-ago');
if (agoEl) {
    var rotatedAt = _loadedConfig && _loadedConfig.PluginSecretRotatedAt;
    agoEl.textContent = rotatedAt
        ? fmtRelative(new Date(rotatedAt * 1000))
        : 'never';
}
```

---

### FIX-215-17: Remove dead wizard code from `configurationpage.js`
**Files:** `Configuration/configurationpage.js` (modify)
**Effort:** S
**What:** Remove or stub the following functions that are no longer called by any tab. Do not delete functions that are still referenced by the repair/health tabs.

Functions to remove:
- `initWizardTab()`
- `populateWizard()`
- `showWizardStep()`
- `wizNext()`
- `wizBack()`
- `finishWizard()`
- `testWizardConnection()`
- `animateSyncProgress()`

Functions to keep (still used by Repair/Health):
- `loadImprobabilityStatus()` — used by Repair tab
- `summonMarvin()` — used by Repair tab
- `refreshDashboard()` / `renderDashboard()` — used by Health tab
- `loadBlockedItems()` / `initBlockedTab()` — used by Parental tab
- `loadCatalogs()` / `renderCatalogPanel()` — used by Sources tab
- `runTask()` — used by Repair tab
- `resetWizardConfirm()` → rename to `resetSettingsConfirm()` (already exists as alias)
- `purgeCatalogConfirm()`, `nuclearStep1()` etc. — used by Danger Zone
- `fmtRelative()` — used everywhere
- `parseManifestUrl()` / `bindManifestField()` — used by Providers tab
- `updateFilterStatus()` — used by Parental tab

**Gotcha:** Before deleting each function, `grep` its name across the file to confirm zero remaining callers. The wizard functions are safe to delete — the wizard HTML is gone. Do not delete anything with a remaining `data-es-action` or `data-es-wiz-` reference, even if it looks dead.

---

## Verification

- [ ] `dotnet build -c Release` (0 errors, 0 warnings)
- [ ] `./emby-reset.sh` succeeds, plugin configuration page loads
- [ ] Providers tab loads as default, Getting Started card evaluates correctly
- [ ] AIOStreams Primary URL field reconstructs from `AioStreamsUrl` + `AioStreamsUuid` + `AioStreamsToken` on load
- [ ] Backup URL field: populate → save → reload confirms `SecondaryManifestUrl` saved; clear → save confirms it cleared
- [ ] Edit button shows username interstitial before opening tab
- [ ] Libraries tab: Metadata fields hidden by default, populated from Emby server config, Override expands dropdowns
- [ ] Libraries tab: Save updates all lib/path fields, float save button shows "✓ Saved" for 3s
- [ ] Sources tab: table renders, no ▲ ▼ buttons present, checkbox and limit auto-save
- [ ] Security tab: Rotate Secret confirms, polls `/InfiniteDrive/Setup/RotationStatus`, shows progress bar, shows "Last rotated: just now" on completion
- [ ] Parental tab: Search calls `/InfiniteDrive/Admin/SearchItems`, results show internal-UUID-based Block buttons, blocking shows ✅ Blocked inline, blocked list refreshes
- [ ] Parental tab: TMDb key and Block Unrated save via float save button
- [ ] Health tab: provider grid cards render, existing debug tools present and collapsed
- [ ] Repair tab: Summon Marvin works, Danger Zone confirmation dialogs work
- [ ] Status pills in tab bar update from `/InfiniteDrive/Status` response
- [ ] No console errors on any tab switch
- [ ] `grep -i "es-tab-content-setup\|es-tab-content-marvin\|es-tab-content-advanced\|es-tab-content-overview" Configuration/configurationpage.html` returns zero hits

## Completion

- [ ] All tasks done
- [ ] BACKLOG.md updated (Sprint 215 complete, v0.55)
- [ ] REPO_MAP.md updated (`configurationpage.html` and `configurationpage.js` entries reflect new tab structure)
- [ ] SESSION_SUMMARY.md updated
- [ ] `git commit -m "feat: sprint 215 — settings redesign UI v2.3"`
