# Sprint 117 — Admin UI (v3.3 Configuration Page)

**Version:** v3.3 | **Status:** Partial | **Risk:** LOW | **Depends:** Sprint 116

---

## Overview

Sprint 117 implements Admin UI for v3.3 configuration. The configuration page allows users to manage sources, collections, saved/blocked items, handle superseded conflicts, and trigger manual actions.

**Key Components:**
- Config Page HTML - Admin UI layout
- Config Page JavaScript - UI logic and API calls
- Config Page CSS - Styling
- Config Controller - API endpoints for UI

---

## Phase 117A — Config Page HTML

### FIX-117A-01: Create Config Page Layout

**File:** `Configuration/configurationpage.html`

```html
<!DOCTYPE html>
<html>
<head>
    <title>EmbyStreams Configuration</title>
    <link rel="stylesheet" href="configurationpage.css">
</head>
<body>
    <div id="embystreams-config">
        <header>
            <h1>EmbyStreams v3.3</h1>
            <div id="status-badge">Loading...</div>
        </header>

        <div id="install-notice" class="install-notice hidden">
            <div class="install-notice-content">
                <h3>Installation Complete</h3>
                <p>EmbyStreams v3.3 has been installed and configured with THREE separate libraries:</p>
                <ul>
                    <li><strong>Movies Library:</strong> <code>/embystreams/library/movies/</code> (TMDB/IMDB)</li>
                    <li><strong>Series Library:</strong> <code>/embystreams/library/series/</code> (TMDB/IMDB)</li>
                    <li><strong>Anime Library:</strong> <code>/embystreams/library/anime/</code> (AniList/AniDB)</li>
                </ul>
                <p><strong>All three libraries are hidden from navigation panel</strong> for existing users.</p>
                <p class="note">You can unhide libraries in Emby user settings if desired.</p>
                <button onclick="dismissInstallNotice()">Dismiss</button>
            </div>
        </div>

        <nav class="tabs">
            <button class="tab active" data-tab="sources">Sources</button>
            <button class="tab" data-tab="collections">Collections</button>
            <button class="tab" data-tab="saved">Saved</button>
            <button class="tab" data-tab="needs-review">Needs Review</button>
            <button class="tab" data-tab="blocked">Blocked</button>
            <button class="tab" data-tab="actions">Actions</button>
            <button class="tab" data-tab="logs">Logs</button>
        </nav>

        <main>
            <section id="sources-tab" class="tab-content active">
                <h2>Sources</h2>
                <div id="sources-list"></div>
                <button id="add-source">Add Source</button>
            </section>

            <section id="collections-tab" class="tab-content">
                <h2>Collections</h2>
                <div id="collections-list"></div>
            </section>

            <section id="saved-tab" class="tab-content">
                <h2>Saved Items</h2>
                <div id="saved-list"></div>
            </section>

            <section id="needs-review-tab" class="tab-content">
                <h2>Needs Review</h2>
                <p class="section-note">
                    These items have a <strong>Your Files conflict</strong>: they were explicitly saved by you,
                    but also match local files you've added to your library.
                    Please choose how to resolve each conflict.
                </p>
                <div id="needs-review-list"></div>
            </section>

            <section id="blocked-tab" class="tab-content">
                <h2>Blocked Items</h2>
                <div id="blocked-list"></div>
            </section>

            <section id="actions-tab" class="tab-content">
                <h2>Actions</h2>
                <div class="actions-grid">
                    <button id="sync-now" class="action-btn">Sync Now</button>
                    <button id="your-files-now" class="action-btn">Your Files Reconcile</button>
                    <button id="cleanup-now" class="action-btn">Cleanup Removed</button>
                    <button id="collections-now" class="action-btn">Sync Collections</button>
                    <button id="purge-cache" class="action-btn danger">Purge Cache</button>
                    <button id="reset-db" class="action-btn danger">Reset Database</button>
                </div>
            </section>

            <section id="logs-tab" class="tab-content">
                <h2>Logs</h2>
                <div id="logs-container">
                    <select id="log-filter">
                        <option value="all">All</option>
                        <option value="info">Info</option>
                        <option value="warning">Warning</option>
                        <option value="error">Error</option>
                    </select>
                    <div id="logs-list"></div>
                </div>
            </section>
        </main>

        <footer>
            <span id="version">Loading version...</span>
            <span id="last-sync">Last sync: Never</span>
        </footer>
    </div>

    <script src="configurationpage.js"></script>
</body>
</html>
```

**Acceptance Criteria:**
- [ ] Tabbed navigation
- [ ] Sources tab
- [ ] Collections tab
- [ ] Saved tab
- [ ] Needs Review tab (for superseded_conflict items)
- [ ] Blocked tab
- [ ] Actions tab
- [ ] Logs tab
- [ ] Status badge
- [ ] Install notice (THREE libraries)
- [ ] Install note about hidden navigation

---

## Phase 117B — Config Page JavaScript

### FIX-117B-00: Add Item Inspector for Series Season Display

**File:** `Configuration/item-inspector.js`

```javascript
// Item inspector with series season save display
function renderItemInspector(itemId) {
    fetch(`/embystreams/items/${itemId}`)
        .then(response => response.json())
        .then(item => {
            const inspector = document.getElementById('item-inspector');

            let seasonInfo = '';
            if (item.mediaType === 'series' && item.savedSeason) {
                seasonInfo = `
                    <div class="inspector-section">
                        <h4>Saved Season</h4>
                        <div class="season-badge">Season ${item.savedSeason}</div>
                        <p class="season-note">
                            All episodes in Season ${item.savedSeason} were saved automatically
                            because you watched an episode from this season.
                        </p>
                    </div>
                `;
            }

            let supersededInfo = '';
            if (item.superseded) {
                const conflictText = item.supersededConflict
                    ? '<span class="conflict-badge">Saved + Your Files Conflict</span>'
                    : '<span class="superseded-badge">Superseded</span>';

                supersededInfo = `
                    <div class="inspector-section superseded-section">
                        <h4>Superseded Status</h4>
                        ${conflictText}
                        <p class="superseded-note">
                            ${item.supersededConflict
                                ? 'This item was explicitly saved, but also matches a local file you added. Please review.'
                                : 'This item matches a local file you added to your library. The stream file is hidden.'}
                        </p>
                        ${item.supersededAt ? `<p class="superseded-date">Superseded: ${new Date(item.supersededAt).toLocaleString()}</p>` : ''}
                    </div>
                `;
            }

            inspector.innerHTML = `
                <div class="inspector-header">
                    <h2>${item.title}</h2>
                    <span class="year-badge">${item.year}</span>
                </div>
                <div class="inspector-body">
                    <div class="inspector-section">
                        <h4>Status</h4>
                        <span class="status-badge ${item.status.toLowerCase()}">${item.status}</span>
                    </div>
                    ${item.saveReason ? `
                        <div class="inspector-section">
                            <h4>Save Reason</h4>
                            <span class="save-reason-badge">${item.saveReason}</span>
                        </div>
                    ` : ''}
                    ${seasonInfo}
                    ${supersededInfo}
                </div>
            `;
        });
}
```

**Acceptance Criteria:**
- [ ] Shows saved season number for series items
- [ ] Explains auto-save reason
- [ ] Hidden for non-series items
- [ ] Hidden if no saved season
- [ ] Shows superseded status with conflict information
- [ ] Shows superseded date if available

### FIX-117B-01: Initialize Config Page

**File:** `Configuration/configurationpage.js`

```javascript
document.addEventListener('DOMContentLoaded', () => {
    initTabs();
    loadStatus();
    loadSources();
    loadCollections();
    loadSavedItems();
    loadNeedsReviewItems();
    loadBlockedItems();
    initActions();
});

function initTabs() {
    document.querySelectorAll('.tab').forEach(tab => {
        tab.addEventListener('click', () => {
            // Remove active class from all tabs
            document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));

            // Add active class to clicked tab
            tab.classList.add('active');
            const tabName = tab.dataset.tab;
            document.getElementById(`${tabName}-tab`).classList.add('active');
        });
    });
}

async function loadStatus() {
    const response = await fetch('/embystreams/status');
    const status = await response.json();

    updateStatusBadge(status);
    updateVersion(status);
    updateLastSync(status);
}

function updateStatusBadge(status) {
    const badge = document.getElementById('status-badge');
    // CRITICAL: Use pluginStatus, NOT manifestStatus (v20 concept)
    badge.textContent = status.pluginStatus || status.status || 'Unknown';
    badge.className = `status-badge ${(status.pluginStatus || status.status || 'unknown').toLowerCase()}`;
}

function updateVersion(status) {
    document.getElementById('version').textContent = `v${status.version}`;
}

function updateLastSync(status) {
    const lastSync = status.lastSyncAt
        ? new Date(status.lastSyncAt).toLocaleString()
        : 'Never';
    document.getElementById('last-sync').textContent = `Last sync: ${lastSync}`;
}

async function checkInstallNotice() {
    const response = await fetch('/embystreams/status');
    const status = await response.json();

    if (status.installNoticePending) {
        document.getElementById('install-notice').classList.remove('hidden');
    }
}

function dismissInstallNotice() {
    document.getElementById('install-notice').classList.add('hidden');
    fetch('/embystreams/status/dismiss-install-notice', { method: 'POST' });
}

// Call checkInstallNotice on page load
checkInstallNotice();
```

**Acceptance Criteria:**
- [ ] Tab switching works
- [ ] Status badge loads
- [ ] Status badge uses pluginStatus (NOT manifestStatus)
- [ ] Version loads
- [ ] Last sync time loads
- [ ] Install notice check on page load
- [ ] Install notice dismissible

### FIX-117B-02: Implement Sources Tab

**File:** `Configuration/configurationpage.js`

```javascript
async function loadSources() {
    const response = await fetch('/embystreams/sources');
    const sources = await response.json();

    const container = document.getElementById('sources-list');
    container.innerHTML = sources.map(source => `
        <div class="source-card" data-source-id="${source.id}">
            <h3>${source.name}</h3>
            <div class="source-info">
                <span>Items: ${source.itemCount || 0}</span>
                <span>Last Sync: ${source.lastSyncedAt ? new Date(source.lastSyncedAt).toLocaleString() : 'Never'}</span>
            </div>
            <div class="source-actions">
                <label>
                    <input type="checkbox" ${source.enabled ? 'checked' : ''}
                           onchange="toggleSource('${source.id}', this.checked)">
                    Enabled
                </label>
                <label>
                    <input type="checkbox" ${source.showAsCollection ? 'checked' : ''}
                           onchange="toggleShowAsCollection('${source.id}', this.checked)">
                    Show as Collection
                </label>
                <button onclick="syncSource('${source.id}')">Sync</button>
                <button onclick="deleteSource('${source.id}')" class="danger">Delete</button>
            </div>
        </div>
    `).join('');
}

async function toggleSource(sourceId, enabled) {
    const action = enabled ? 'enable' : 'disable';
    const response = await fetch(`/embystreams/sources/${sourceId}/${action}`, { method: 'POST' });
    if (response.ok) {
        showToast(`Source ${action}d`);
    } else {
        showToast(`Failed to ${action} source`, 'error');
    }
}

async function toggleShowAsCollection(sourceId, show) {
    const response = await fetch(`/embystreams/sources/${sourceId}/show-as-collection`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ show })
    });
    if (response.ok) {
        showToast('Collection setting updated');
    } else {
        showToast('Failed to update collection setting', 'error');
    }
}

async function deleteSource(sourceId) {
    if (!confirm('Are you sure you want to delete this source?')) return;

    const response = await fetch(`/embystreams/sources/${sourceId}`, { method: 'DELETE' });
    if (response.ok) {
        showToast('Source deleted');
        loadSources();
    } else {
        showToast('Failed to delete source', 'error');
    }
}
```

**Acceptance Criteria:**
- [ ] Lists all sources
- [ ] Toggles enabled/disabled
- [ ] Toggles Show as Collection
- [ ] Syncs source
- [ ] Deletes source
- [ ] All source IDs are string TEXT UUIDs

### FIX-117B-03: Implement Collections Tab

**File:** `Configuration/configurationpage.js`

```javascript
async function loadCollections() {
    const response = await fetch('/embystreams/collections');
    const collections = await response.json();

    const container = document.getElementById('collections-list');
    container.innerHTML = collections.map(collection => `
        <div class="collection-card">
            <h3>${collection.collectionName || collection.name}</h3>
            <div class="collection-info">
                <span>Source: ${collection.name}</span>
                <span>Last Synced: ${collection.lastSyncedAt ? new Date(collection.lastSyncedAt).toLocaleString() : 'Never'}</span>
            </div>
            <div class="collection-actions">
                <button onclick="syncCollection('${collection.sourceId}')">Sync Now</button>
                <button onclick="viewCollection('${collection.embyCollectionId}')">View in Emby</button>
            </div>
        </div>
    `).join('');
}

async function syncCollection(sourceId) {
    showToast('Syncing collection...');
    const response = await fetch(`/embystreams/collections/${sourceId}/sync`, { method: 'POST' });
    if (response.ok) {
        showToast('Collection synced');
        loadCollections();
    } else {
        showToast('Failed to sync collection', 'error');
    }
}

function viewCollection(collectionId) {
    // Open Emby UI for this collection
    window.open(`#!/collection?id=${collectionId}`, '_blank');
}
```

**Acceptance Criteria:**
- [ ] Lists all collections
- [ ] Syncs collection
- [ ] Views collection in Emby
- [ ] All source IDs are string TEXT UUIDs

### FIX-117B-04: Implement Saved/Blocked/Needs Review Tabs

**File:** `Configuration/configurationpage.js`

```javascript
async function loadSavedItems() {
    const response = await fetch('/embystreams/saved/list');
    const data = await response.json();

    const container = document.getElementById('saved-list');
    container.innerHTML = data.Saved.map(item => `
        <div class="item-card">
            <div class="item-info">
                <h4>${item.title} (${item.year})</h4>
                <span class="save-reason">${item.saveReason || 'Manual'}</span>
                ${item.savedSeason ? `<span class="season-badge-mini">Season ${item.savedSeason}</span>` : ''}
            </div>
            <div class="item-actions">
                <button onclick="showItemInspector('${item.id}')">Details</button>
                <button onclick="unsaveItem('${item.id}')">Unsave</button>
                <button onclick="blockItem('${item.id}')" class="danger">Block</button>
            </div>
        </div>
    `).join('');
}

async function loadNeedsReviewItems() {
    const response = await fetch('/embystreams/items/needs-review');
    const items = await response.json();

    const container = document.getElementById('needs-review-list');

    if (items.length === 0) {
        container.innerHTML = '<p class="empty-note">No items need review.</p>';
        return;
    }

    container.innerHTML = items.map(item => `
        <div class="item-card needs-review">
            <div class="item-info">
                <h4>${item.title} (${item.year})</h4>
                <span class="save-reason">Saved: ${item.saveReason || 'Manual'}</span>
                <span class="conflict-badge">Your Files Conflict</span>
                ${item.savedSeason ? `<span class="season-badge-mini">Season ${item.savedSeason}</span>` : ''}
            </div>
            <div class="item-actions">
                <button onclick="keepSavedItem('${item.id}')">Keep Saved</button>
                <button onclick="acceptYourFiles('${item.id}')" class="danger">Accept Your Files</button>
            </div>
        </div>
    `).join('');
}

async function keepSavedItem(itemId) {
    const response = await fetch('/embystreams/items/keep-saved', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ itemId })
    });
    if (response.ok) {
        showToast('Item kept as saved');
        loadNeedsReviewItems();
        loadSavedItems();
    } else {
        showToast('Failed to keep saved item', 'error');
    }
}

async function acceptYourFiles(itemId) {
    if (!confirm('This will delete the stream file and remove the item from Emby. Continue?')) return;

    const response = await fetch('/embystreams/items/accept-your-files', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ itemId })
    });
    if (response.ok) {
        showToast('Your Files match accepted');
        loadNeedsReviewItems();
        loadSavedItems();
    } else {
        showToast('Failed to accept Your Files match', 'error');
    }
}

async function loadBlockedItems() {
    const response = await fetch('/embystreams/saved/list');
    const data = await response.json();

    const container = document.getElementById('blocked-list');
    container.innerHTML = data.Blocked.map(item => `
        <div class="item-card blocked">
            <div class="item-info">
                <h4>${item.title} (${item.year})</h4>
            </div>
            <div class="item-actions">
                <button onclick="unblockItem('${item.id}')">Unblock</button>
                <button onclick="saveItem('${item.id}')">Save</button>
            </div>
        </div>
    `).join('');
}

async function saveItem(itemId) {
    const response = await fetch('/embystreams/saved/save', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ itemId })
    });
    if (response.ok) {
        showToast('Item saved');
        loadSavedItems();
        loadBlockedItems();
    } else {
        showToast('Failed to save item', 'error');
    }
}

async function unsaveItem(itemId) {
    const response = await fetch('/embystreams/saved/unsave', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ itemId })
    });
    if (response.ok) {
        showToast('Item unsaved');
        loadSavedItems();
        loadBlockedItems();
    } else {
        showToast('Failed to unsave item', 'error');
    }
}

async function blockItem(itemId) {
    const response = await fetch('/embystreams/saved/block', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ itemId })
    });
    if (response.ok) {
        showToast('Item blocked');
        loadSavedItems();
        loadBlockedItems();
    } else {
        showToast('Failed to block item', 'error');
    }
}

async function unblockItem(itemId) {
    const response = await fetch('/embystreams/saved/unblock', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ itemId })
    });
    if (response.ok) {
        showToast('Item unblocked');
        loadSavedItems();
        loadBlockedItems();
    } else {
        showToast('Failed to unblock item', 'error');
    }
}

function showItemInspector(itemId) {
    // Open item inspector modal
    renderItemInspector(itemId);
    document.getElementById('item-inspector-modal').classList.remove('hidden');
}
```

**Acceptance Criteria:**
- [ ] Lists saved items
- [ ] Lists blocked items
- [ ] Lists needs-review items (superseded_conflict = true)
- [ ] Save/Unsave works
- [ ] Block/Unblock works
- [ ] Keep Saved clears superseded_conflict flag
- [ ] Accept Your Files supersedes item
- [ ] Item inspector shows season info
- [ ] All item IDs are string TEXT UUIDs

### FIX-117B-05: Implement Actions Tab

**File:** `Configuration/configurationpage.js`

```javascript
function initActions() {
    document.getElementById('sync-now').addEventListener('click', async () => {
        showToast('Syncing...');
        const response = await fetch('/embystreams/actions/sync', { method: 'POST' });
        handleActionResponse(response);
    });

    document.getElementById('your-files-now').addEventListener('click', async () => {
        showToast('Reconciling Your Files...');
        const response = await fetch('/embystreams/actions/yourfiles', { method: 'POST' });
        handleActionResponse(response);
    });

    document.getElementById('cleanup-now').addEventListener('click', async () => {
        showToast('Cleaning up...');
        const response = await fetch('/embystreams/actions/cleanup', { method: 'POST' });
        handleActionResponse(response);
    });

    document.getElementById('collections-now').addEventListener('click', async () => {
        showToast('Syncing collections...');
        const response = await fetch('/embystreams/actions/collections', { method: 'POST' });
        handleActionResponse(response);
    });

    document.getElementById('purge-cache').addEventListener('click', async () => {
        if (!confirm('Are you sure you want to purge all cached stream URLs?')) return;

        showToast('Purging cache...');
        const response = await fetch('/embystreams/actions/purge-cache', { method: 'POST' });
        handleActionResponse(response);
    });

    document.getElementById('reset-db').addEventListener('click', async () => {
        // CRITICAL: Double confirmation with "type RESET" gate
        if (!confirm('Are you sure you want to reset the database?')) return;

        const confirmText = prompt('Type "RESET" to confirm database reset. This will delete all data!');
        if (confirmText !== 'RESET') {
            showToast('Database reset cancelled - you did not type "RESET"');
            return;
        }

        showToast('Resetting database...');
        const response = await fetch('/embystreams/actions/reset', { method: 'POST' });
        handleActionResponse(response);
    });
}

async function handleActionResponse(response) {
    if (response.ok) {
        const result = await response.json();
        showToast(result.message);
        loadStatus();
    } else {
        const error = await response.json();
        showToast(error.message, 'error');
    }
}

function showToast(message, type = 'success') {
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.textContent = message;
    document.body.appendChild(toast);

    setTimeout(() => {
        toast.remove();
    }, 3000);
}
```

**Acceptance Criteria:**
- [ ] Sync Now works
- [ ] Your Files Reconcile works
- [ ] Cleanup Removed works
- [ ] Sync Collections works
- [ ] Purge Cache works (with confirmation)
- [ ] Reset Database works (with double confirmation: dialog + type "RESET")
- [ ] Toast notifications work

---

## Phase 117C — Config Page CSS

### FIX-117C-01: Create Config Page Styles

**File:** `Configuration/configurationpage.css`

```css
#embystreams-config {
    max-width: 1200px;
    margin: 0 auto;
    padding: 20px;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
}

header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 20px;
}

#status-badge {
    padding: 8px 16px;
    border-radius: 4px;
    font-weight: bold;
}

.status-badge.ok { background: #4caf50; color: white; }
.status-badge.stale { background: #ff9800; color: white; }
.status-badge.error { background: #f44336; color: white; }
.status-badge.unknown { background: #9e9e9e; color: white; }

nav.tabs {
    display: flex;
    gap: 10px;
    margin-bottom: 20px;
}

.tab {
    padding: 10px 20px;
    border: none;
    background: #e0e0e0;
    cursor: pointer;
    border-radius: 4px;
}

.tab.active {
    background: #2196f3;
    color: white;
}

.tab-content {
    display: none;
}

.tab-content.active {
    display: block;
}

.install-notice {
    background: #e3f2fd;
    border: 1px solid #2196f3;
    border-radius: 8px;
    padding: 20px;
    margin-bottom: 20px;
}

.install-notice.hidden {
    display: none;
}

.install-notice-content h3 {
    margin-top: 0;
    color: #1976d2;
}

.install-notice-content ul {
    margin: 15px 0;
    padding-left: 20px;
}

.install-notice-content li {
    margin-bottom: 8px;
}

.install-notice-content .note {
    color: #666;
    font-style: italic;
    margin-top: 15px;
}

.install-notice-content button {
    background: #2196f3;
    color: white;
    border: none;
    padding: 10px 20px;
    border-radius: 4px;
    cursor: pointer;
    margin-top: 15px;
}

.install-notice-content button:hover {
    background: #1976d2;
}

.source-card, .collection-card, .item-card {
    background: white;
    border: 1px solid #e0e0e0;
    border-radius: 8px;
    padding: 15px;
    margin-bottom: 10px;
}

.source-info, .collection-info, .item-info {
    margin-bottom: 10px;
}

.source-actions, .collection-actions, .item-actions {
    display: flex;
    gap: 10px;
}

.section-note {
    background: #fff3cd;
    border: 1px solid #ffc107;
    border-radius: 4px;
    padding: 10px;
    margin-bottom: 15px;
    font-size: 14px;
}

.empty-note {
    color: #999;
    font-style: italic;
    padding: 20px;
    text-align: center;
}

.item-card.blocked {
    border-color: #f44336;
}

.item-card.needs-review {
    border-color: #ff9800;
    border-width: 2px;
}

.season-badge-mini {
    background: #2196f3;
    color: white;
    padding: 2px 8px;
    border-radius: 3px;
    font-size: 11px;
    margin-left: 8px;
}

.save-reason {
    background: #4caf50;
    color: white;
    padding: 2px 8px;
    border-radius: 3px;
    font-size: 11px;
}

.conflict-badge {
    display: inline-block;
    background: #ff9800;
    color: white;
    padding: 4px 8px;
    border-radius: 4px;
    font-size: 12px;
    font-weight: bold;
    margin-left: 10px;
}

.superseded-section {
    background: #ffebee;
    border: 1px solid #e91e63;
    border-radius: 4px;
    margin-top: 10px;
}

.superseded-badge {
    display: inline-block;
    background: #e91e63;
    color: white;
    padding: 4px 8px;
    border-radius: 4px;
    font-size: 12px;
    font-weight: bold;
}

.superseded-note {
    margin: 10px 0 0 0;
    color: #666;
    font-size: 13px;
}

.superseded-date {
    color: #999;
    font-size: 12px;
    margin-top: 5px;
}

.actions-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
    gap: 10px;
}

.action-btn {
    padding: 15px;
    border: none;
    border-radius: 4px;
    background: #2196f3;
    color: white;
    cursor: pointer;
    font-size: 14px;
}

.action-btn:hover {
    background: #1976d2;
}

.action-btn.danger {
    background: #f44336;
}

.action-btn.danger:hover {
    background: #d32f2f;
}

.toast {
    position: fixed;
    bottom: 20px;
    right: 20px;
    padding: 15px 20px;
    border-radius: 4px;
    color: white;
    z-index: 1000;
}

.toast.success { background: #4caf50; }
.toast.error { background: #f44336; }

/* Item Inspector Styles */
.inspector-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 20px;
}

.inspector-header h2 {
    margin: 0;
    font-size: 24px;
}

.year-badge {
    background: #666;
    color: white;
    padding: 4px 12px;
    border-radius: 4px;
    font-size: 14px;
}

.inspector-body {
    display: flex;
    flex-direction: column;
    gap: 20px;
}

.inspector-section {
    background: white;
    border: 1px solid #e0e0e0;
    border-radius: 8px;
    padding: 15px;
}

.inspector-section h4 {
    margin-top: 0;
    margin-bottom: 10px;
    font-size: 14px;
    color: #666;
    text-transform: uppercase;
}

.season-badge {
    display: inline-block;
    background: #2196f3;
    color: white;
    padding: 6px 16px;
    border-radius: 4px;
    font-weight: bold;
    font-size: 16px;
}

.season-note {
    margin: 10px 0 0 0;
    color: #666;
    font-size: 14px;
    font-style: italic;
}

footer {
    margin-top: 20px;
    padding-top: 20px;
    border-top: 1px solid #e0e0e0;
    display: flex;
    justify-content: space-between;
    color: #757575;
}
```

**Acceptance Criteria:**
- [ ] Responsive layout
- [ ] Status badge colors (ok, stale, error, unknown)
- [ ] Tabbed navigation styling
- [ ] Card styling
- [ ] Action button styling
- [ ] Toast notifications
- [ ] Superseded section styling
- [ ] Needs Review badge styling
- [ ] Item inspector styling
- [ ] Season badge styling

---

## Sprint 117 Dependencies

- **Previous Sprint:** 116 (Collection Management)
- **Blocked By:** Sprint 116
- **Blocks:** Sprint 118 (Home Screen Rails)

---

## Sprint 117 Completion Criteria

- [ ] Config page HTML structure
- [ ] All tabs implemented (Sources, Collections, Saved, Needs Review, Blocked, Actions, Logs)
- [ ] Status badge uses pluginStatus (NOT manifestStatus)
- [ ] All API calls work
- [ ] Toast notifications work
- [ ] Confirmations work for dangerous actions
- [ ] Reset Database uses double confirmation (dialog + type "RESET")
- [ ] Install notice shows THREE separate libraries
- [ ] Install note about hidden navigation
- [ ] Needs Review tab for superseded_conflict items
- [ ] Item Inspector with season save display
- [ ] CSS styling complete
- [ ] Build succeeds
- [ ] E2E: Config page works in browser

---

## Sprint 117 Notes

**Tabs:**
- Sources: Manage enabled/disabled sources, ShowAsCollection
- Collections: View and sync collections
- Saved: List and manage saved items
- Needs Review: Handle superseded_conflict items (Your Files conflicts)
- Blocked: List and manage blocked items
- Actions: Trigger manual sync, cleanup, etc.
- Logs: View pipeline logs

**Status Badge (CRITICAL):**
- Use `status.pluginStatus` or `status.status` (overall plugin status)
- Do NOT use `status.manifestStatus` (v20 concept, removed in v3.3)

**Library Visibility on Install (v3.3 Spec §15):**
- On first install, EmbyStreams creates THREE separate libraries:
  1. `/embystreams/library/movies/` → TMDB/IMDB movies
  2. `/embystreams/library/series/` → TMDB/IMDB series
  3. `/embystreams/library/anime/` → AniList/AniDB content
- All THREE libraries are automatically hidden from navigation panel
- Install notice shown to all users on first visit to config page
- Install notice explains THREE separate library structure
- User can unhide libraries via Emby user settings if desired
- Dismiss button hides notice permanently (stored in config)

**Needs Review Tab (v3.3 Spec §11.2):**
- Displays items with `superseded_conflict = true`
- These are Saved items that also match Your Files (superseded = true)
- Two resolution options:
  - "Keep Saved" → clears superseded_conflict flag, keeps Saved status
  - "Accept Your Files" → deletes .strm, removes from Emby, keeps superseded=true
- Admin review required before automatic cleanup
- Item inspector shows both saved and superseded status

**Series Season Save Display (v3.3 Spec §9.2):**
- Item inspector shows saved season for series items
- Displays: "Saved Season X" badge
- Explains: "All episodes in Season X were saved automatically because you watched an episode from this season"
- Only shown for series with `savedSeason != null`

**Dangerous Actions:**
- Purge Cache: Confirm once
- Reset Database: Double confirm (dialog + type "RESET")
  - First confirmation: "Are you sure?"
  - Second confirmation: Prompt user to type "RESET"
  - Only proceed if user types exactly "RESET"
- This prevents accidental database resets

**Toast Notifications:**
- Success: Green
- Error: Red
- Auto-dismiss after 3 seconds
- Fixed position at bottom-right

**Item Inspector (v3.3 Spec §12):**
- Modal popup when user clicks "Details" button
- Shows item metadata: title, year, status, save reason
- Shows saved season info for series
- Shows superseded status and conflict information
- Shows superseded date if available
