# Sprint 153 — UI Wiring Fixes

**Version:** v4.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 150

---

## Overview

Three buttons and one data panel added in Sprint 150 are silently broken: they render in the UI but have no JavaScript handlers or data loaders. Additionally the "Summon Marvin" button on the Improbability Drive tab — which predates Sprint 150 — was never wired to trigger any task after DoctorTask was removed. All four fixes are JavaScript/HTML only. No C# changes.

### Why This Exists

Sprint 150 added the Content Mgmt tab and My Picks/Blocked tabs. The "Force Catalog Sync" button and "Sync Sources" list in Content Mgmt were scaffolded in HTML but never wired in JS. The "Summon Marvin" button lost its purpose when DoctorTask was retired and was never redirected to the new two-worker system.

**Audit note:** Several audit findings were invalidated by the actual source:
- MetadataFallbackTask already has `DelayMs = 500` between Cinemeta calls — no fix needed
- `ApiCallDelayMs` in RehydrationService sits between iterations (not wrapping disk writes) — no fix needed
- Dead services (UnauthenticatedStreamService, StreamProxyService, etc.) are already gone
- ProxyMode, AioStreamsStreamIdPrefixes, SignatureValidityDays are all live and consumed

---

## Phase 153A — Improbability Drive

### FIX-153A-01: Wire summonMarvin to trigger DeepCleanTask

**File:** `Configuration/configurationpage.js` (modify — `summonMarvin` function, line ~2740)

**What:**
1. Replace the current `setTimeout`-only body with the same pattern as `triggerRefreshNow()`:
```javascript
function summonMarvin(view) {
    var btn = q(view, 'es-summon-marvin-btn');
    var statusEl = q(view, 'es-marvin-status');
    if (!btn || !statusEl) return;

    btn.textContent = 'Marvin is grumbling…';
    btn.disabled = true;
    statusEl.textContent = 'Starting deep clean…';

    ApiClient.getJSON(ApiClient.getUrl('ScheduledTasks')).then(function(tasks) {
        var task = (tasks || []).find(function(t) { return t.Key === 'EmbyStreamsDeepClean'; });
        if (!task) {
            btn.textContent = 'Summon Marvin';
            btn.disabled = false;
            statusEl.textContent = 'Task not found.';
            return;
        }
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('ScheduledTasks/Running/' + task.Id) });
        statusEl.textContent = 'Deep clean running…';

        // Safety reset after 5 minutes
        setTimeout(function() {
            btn.textContent = 'Summon Marvin';
            btn.disabled = false;
            statusEl.textContent = '';
            loadImprobabilityStatus(view);
        }, 300000);
    }).catch(function() {
        btn.textContent = 'Summon Marvin';
        btn.disabled = false;
        statusEl.textContent = 'Failed to start.';
    });
}
```

**Reference:** `triggerRefreshNow()` in configurationpage.js line ~2668 — identical pattern, use it as the template.
**Task key:** `"EmbyStreamsDeepClean"` — confirmed in `Tasks/DeepCleanTask.cs` line 30.

**Depends on:** Nothing

---

## Phase 153B — Content Mgmt Tab

### FIX-153B-01: Wire Force Catalog Sync button

**File:** `Configuration/configurationpage.html` (modify — line 1569)

**What:**
1. Add `data-es-task="catalog_sync"` attribute to the button:
```html
<!-- BEFORE -->
<button id="es-force-catalog-sync-btn" class="es-btn">Force Catalog Sync Now</button>

<!-- AFTER -->
<button id="es-force-catalog-sync-btn" class="es-btn" data-es-task="catalog_sync">Force Catalog Sync Now</button>
```

The existing click delegation in configurationpage.js (line ~2106) reads `data-es-task` and calls `runTask(view, task, el)`. `runTask` already handles `catalog_sync` — it calls `esFetch('/EmbyStreams/Trigger?task=catalog_sync')` and activates `startCatalogPoll(view, 'cfg')`. No JS change required.

**Depends on:** Nothing

### FIX-153B-02: Populate Sync Sources list

**File:** `Configuration/configurationpage.js` (modify)

**What:**
1. Add `loadContentMgmtSources(view)` function:
```javascript
function loadContentMgmtSources(view) {
    var el = view.querySelector('#es-sync-sources-list');
    if (!el) return;

    esFetch('/EmbyStreams/Status')
        .then(function(r) { return r.json(); })
        .then(function(data) {
            if (!data.CatalogSources || !data.CatalogSources.length) {
                el.innerHTML = '<span style="opacity:.5">No sources configured yet.</span>';
                return;
            }
            el.innerHTML = data.CatalogSources.map(function(src) {
                var ok = src.LastReachableAt ? '🟢' : '🔴';
                var lastSync = src.LastSyncAt ? fmtRelative(new Date(src.LastSyncAt)) : 'never';
                return '<div style="padding:.3em 0">' + ok + ' <strong>' + esc(src.SourceKey || '?') + '</strong>'
                    + ' &mdash; ' + (src.ItemCount || 0) + ' items, synced ' + lastSync + '</div>';
            }).join('');
        })
        .catch(function() {
            el.innerHTML = '<span style="color:#dc3545">Failed to load. Check server logs.</span>';
        });
}
```

2. Wire to `showTab()` — in the existing if-chain (around line 270):
```javascript
if (name === 'content-mgmt') { loadContentMgmtSources(view); }
```

**Note:** `fmtRelative` and `esc` are already defined. `data.CatalogSources` is already present in the `/EmbyStreams/Status` response. The existing `refreshSourcesTab()` is NOT reused — it targets `es-sources-body` (a `<tbody>` in the Health tab) and renders table rows, which is wrong for the `<div>` target here.

**Depends on:** Nothing

### FIX-153B-03: Add Rehydration trigger button

**File:** `Configuration/configurationpage.html` (modify — Content Mgmt tab, after Catalog Sync section)

**What:**
1. Add a new section to the Content Mgmt card before the Sync Sources section:
```html
<div style="margin-bottom:1.5em">
  <h3 style="margin-bottom:.5em">Version Slots</h3>
  <p class="es-hint">Apply pending slot changes (add/remove quality tiers) across the full catalog.</p>
  <button class="es-btn" data-es-task="embystreams_rehydration">Run Pending Rehydration</button>
</div>
```

`data-es-task="embystreams_rehydration"` is handled by the existing `runTask()` delegation. No JS change required.

**Task key:** `"embystreams_rehydration"` — confirmed in `Tasks/RehydrationTask.cs` line 30.

**Depends on:** Nothing

---

## Phase 153C — Build Verification

### FIX-153C-01: Smoke test

**What:**
1. `dotnet build -c Release` — 0 errors (HTML/JS changes do not affect the build, but verify nothing else was touched)
2. Open config page in browser
3. **Summon Marvin:** On Improbability Drive tab, click "Summon Marvin" → button disables, status shows "Deep clean running…", verify DeepCleanTask appears as running in Emby Scheduled Tasks list
4. **Force Catalog Sync:** On Content Mgmt tab, click "Force Catalog Sync Now" → button shows ⏳, catalog progress panel activates (same behaviour as `data-es-task="catalog_sync"` button elsewhere)
5. **Sync Sources:** On Content Mgmt tab, verify sources list renders (not "Loading…")
6. **Rehydration:** On Content Mgmt tab, click "Run Pending Rehydration" → runTask fires, button resets after response

**Depends on:** FIX-153A-01, FIX-153B-01, FIX-153B-02, FIX-153B-03
