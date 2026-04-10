# Sprint 150 — Spec Drift & User Experience Fixes

**Status:** Ready for Implementation
**Priority:** HIGH — Missing features from Sprint 148 spec
**Estimated Effort:** 2-3 days
**Dependencies:** Sprint 149 (hotfix) must be complete

---

## Overview

Sprint 148 spec promised user-facing features that never got wired up:
- Blocked items admin tab with Unblock action
- My Picks user tab (pinned items with "I'm done" action)
- Separate User Discover vs Admin Content Management
- Per-user "In My Library" status
- Parental rating ceiling enforcement

This sprint closes that gap.

---

## Task H-4: Per-User "In My Library" Status

**File:** Services/DiscoverService.cs:844-883

**Problem:** InLibrary = entry.IsInUserLibrary checks global catalog presence, not whether THIS user has pinned it.

**Fix:**

1. Add userId parameter to DiscoverBrowse/Search/Detail requests
2. Update query to join user_item_pins:

```sql
SELECT
    dc.*,
    CASE WHEN uip.id IS NOT NULL THEN 1 ELSE 0 END AS in_my_library
FROM discover_catalog dc
LEFT JOIN user_item_pins uip
    ON uip.catalog_item_id = dc.id
    AND uip.emby_user_id = @userId
WHERE ...
```

3. Return InMyLibrary = (row.GetInt32("in_my_library") == 1)

**Validation:** User A pins item, User B sees InMyLibrary = false for same item.

**Priority:** P1 | **Effort:** 1-2 hours

---

## Task H-6: Parental Rating Ceiling Enforcement

**File:** Services/DiscoverService.cs + Data/DatabaseManager.cs

**Multi-part fix:**

### Part 1: Schema (add to next migration)
```sql
ALTER TABLE discover_catalog ADD COLUMN parental_rating INTEGER;
```

### Part 2: Populate ratings during catalog sync
Update CatalogDiscoverTask to fetch parental rating from AIOStreams and store it.

### Part 3: Per-user ceiling
Option A: Add to PluginConfiguration (global ceiling for all users)
Option B: Read from Emby's native user.Policy.MaxParentalRating

### Part 4: Filter in queries
```sql
WHERE (parental_rating IS NULL OR parental_rating <= @maxRating)
```

**Validation:** Set user ceiling to PG-13, verify R-rated items hidden from Discover.

**Priority:** P1 | **Effort:** 4-6 hours

---

## Task MISSING-1: Blocked Items Admin Tab

**Files:** Configuration/configurationpage.html, Configuration/configurationpage.js, Api/AdminController.cs (or similar)

**Problem:** Blocked count shows in status, but no way to view/manage blocked items.

### Part 1: Add tab to admin UI

In configurationpage.html, add after existing tabs:

```html
<div data-role="page" id="embystreams-blocked-page">
    <div data-role="content">
        <div class="readOnlyContent">
            <h2>Blocked Items</h2>
            <p>Items that failed enrichment after 3 attempts. Still playable but won't retry.</p>

            <div id="blocked-items-list"></div>

            <button id="unblock-selected-btn" class="raised button-submit">
                Unblock Selected
            </button>
        </div>
    </div>
</div>
```

### Part 2: Add endpoint to fetch blocked items

```csharp
// Api/AdminController.cs or Services/StatusService.cs
public async Task<object> Get(GetBlockedItemsRequest req)
{
    AdminGuard.RequireAdmin(_authCtx, Request);

    var items = await _db.GetBlockedItemsAsync(CancellationToken.None);

    return new GetBlockedItemsResponse
    {
        Items = items.Select(i => new BlockedItemDto
        {
            Id = i.Id,
            Title = i.Title,
            Year = i.Year,
            BlockedAt = i.BlockedAt,
            BlockedBy = i.BlockedBy,
            ImdbId = i.ImdbId
        }).ToList()
    };
}
```

### Part 3: Add unblock endpoint

```csharp
public async Task<object> Post(UnblockItemsRequest req)
{
    AdminGuard.RequireAdmin(_authCtx, Request);

    foreach (var itemId in req.ItemIds)
    {
        await _db.UnblockItemAsync(itemId, CancellationToken.None);
    }

    return new { Success = true, Count = req.ItemIds.Count };
}
```

### Part 4: Add DatabaseManager methods

```csharp
// Already exists from Sprint 149: GetBlockedItemsAsync

public async Task UnblockItemAsync(string itemId, CancellationToken ct)
{
    await _dbWriteGate.WaitAsync(ct);
    try
    {
        await using var db = await OpenConnectionAsync(ct);
        await using var cmd = new SQLiteCommand(@"
            UPDATE catalog_items
            SET blocked_at = NULL,
                blocked_by = NULL,
                nfo_status = 'NeedsEnrich',
                retry_count = 0,
                next_retry_at = NULL
            WHERE id = @id", db);

        cmd.Parameters.AddWithValue("@id", itemId);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("[DatabaseManager] Unblocked item {Id}", itemId);
    }
    finally
    {
        _dbWriteGate.Release();
    }
}
```

### Part 5: Wire up JS

```javascript
// configurationpage.js
async function loadBlockedItems() {
    const response = await ApiClient.fetch('/EmbyStreams/Admin/BlockedItems');
    const data = await response.json();

    const html = data.Items.map(item => `
        <div class="listItem">
            <input type="checkbox" class="blocked-item-checkbox" data-id="${item.Id}">
            <span>${item.Title} (${item.Year})</span>
            <span class="secondary">${item.BlockedAt} - ${item.BlockedBy}</span>
        </div>
    `).join('');

    document.getElementById('blocked-items-list').innerHTML = html;
}

document.getElementById('unblock-selected-btn').addEventListener('click', async () => {
    const checkboxes = document.querySelectorAll('.blocked-item-checkbox:checked');
    const itemIds = Array.from(checkboxes).map(cb => cb.dataset.id);

    await ApiClient.fetch('/EmbyStreams/Admin/UnblockItems', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ItemIds: itemIds })
    });

    await loadBlockedItems();
});
```

**Priority:** P1 | **Effort:** 3-4 hours

---

## Task MISSING-2: My Picks User Tab

**Files:** Configuration/configurationpage.html, Configuration/configurationpage.js

**Problem:** User can pin items via playback or Discover, but no UI to view/manage them.

### Part 1: Add tab

```html
<div data-role="page" id="embystreams-mypicks-page">
    <div data-role="content">
        <h2>My Picks</h2>
        <p>Items you've added from Discover or played recently. Remove items you're done with.</p>

        <div class="detailSection">
            <div class="detailSectionHeader">
                <h3>Recently Played</h3>
            </div>
            <div id="playback-pins-list"></div>
        </div>

        <div class="detailSection">
            <div class="detailSectionHeader">
                <h3>Added from Discover</h3>
            </div>
            <div id="discover-pins-list"></div>
        </div>

        <button id="remove-selected-pins-btn" class="raised button-submit">
            I'm Done With These
        </button>
    </div>
</div>
```

### Part 2: Add endpoint

```csharp
public async Task<object> Get(GetUserPinsRequest req)
{
    var userId = GetCurrentUserId(); // From Request context

    var pins = await _pinRepo.GetUserPinsAsync(userId, CancellationToken.None);

    return new GetUserPinsResponse
    {
        PlaybackPins = pins.Where(p => p.PinSource == "playback").ToList(),
        DiscoverPins = pins.Where(p => p.PinSource == "discover").ToList()
    };
}

public async Task<object> Post(RemovePinsRequest req)
{
    var userId = GetCurrentUserId();

    foreach (var catalogItemId in req.CatalogItemIds)
    {
        await _pinRepo.RemovePinAsync(userId, catalogItemId, CancellationToken.None);
    }

    return new { Success = true, Count = req.CatalogItemIds.Count };
}
```

### Part 3: Wire up JS

```javascript
async function loadMyPicks() {
    const response = await ApiClient.fetch('/EmbyStreams/User/MyPins');
    const data = await response.json();

    renderPinsList('playback-pins-list', data.PlaybackPins);
    renderPinsList('discover-pins-list', data.DiscoverPins);
}

function renderPinsList(elementId, pins) {
    const html = pins.map(pin => `
        <div class="listItem">
            <input type="checkbox" class="pin-checkbox" data-catalog-id="${pin.CatalogItemId}">
            <span>${pin.Title} (${pin.Year})</span>
            <span class="secondary">Pinned ${formatDate(pin.PinnedAt)}</span>
        </div>
    `).join('');

    document.getElementById(elementId).innerHTML = html;
}

document.getElementById('remove-selected-pins-btn').addEventListener('click', async () => {
    const checkboxes = document.querySelectorAll('.pin-checkbox:checked');
    const catalogItemIds = Array.from(checkboxes).map(cb => cb.dataset.catalogId);

    await ApiClient.fetch('/EmbyStreams/User/RemovePins', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ CatalogItemIds: catalogItemIds })
    });

    await loadMyPicks();
});
```

**Priority:** P1 | **Effort:** 3-4 hours

---

## Task MISSING-3: Separate User Discover from Admin Content Management

**Problem:** Current "Discover" tab is single-purpose. Spec calls for:
- User-facing Discover (browse, search, add to library)
- Admin Content Management (force refresh, view orphans, manage sync sources)

**Fix:**

### Option A: Two separate pages
- Keep existing Discover tab for users
- Add new "Content Management" admin-only tab

### Option B: Role-based tab rendering
- Single "Discover" tab that shows different content based on user role
- Non-admins see browse/search only
- Admins see additional management tools

**Recommendation:** Option A (cleaner separation)

**Priority:** P2 | **Effort:** 2-3 hours

---

## Task H-5: Add Composite Index on user_item_pins

**File:** Data/DatabaseManager.cs (schema migration)

**Problem:** My Picks query needs efficient lookup by (user_id, pin_source, pinned_at DESC)

**Fix:**

```csharp
await using var cmd = new SQLiteCommand(@"
    CREATE INDEX IF NOT EXISTS idx_user_item_pins_user_source_pinned
    ON user_item_pins(emby_user_id, pin_source, pinned_at DESC);", db);
await cmd.ExecuteNonQueryAsync(ct);
```

**Priority:** P2 | **Effort:** 5 minutes

---

## Task M-6: Health Panel Threshold Logic

**File:** Services/StatusService.cs:300-409

**Problem:** Panel shows raw timestamps, no computed health status.

**Fix:**

```csharp
// In StatusService.Get() method
var refreshLastRun = DateTime.Parse(await _db.GetMetadataAsync("last_refresh_run_time"));
var refreshAge = DateTime.UtcNow - refreshLastRun;
var refreshInterval = TimeSpan.FromMinutes(6);

response.RefreshHealth = refreshAge > refreshInterval * 3 ? "red"
                       : refreshAge > refreshInterval * 2 ? "yellow"
                       : "green";

var deepCleanLastRun = DateTime.Parse(await _db.GetMetadataAsync("last_deepclean_run_time"));
var deepCleanAge = DateTime.UtcNow - deepCleanLastRun;
var deepCleanInterval = TimeSpan.FromHours(18);

response.DeepCleanHealth = deepCleanAge > deepCleanInterval * 3 ? "red"
                          : deepCleanAge > deepCleanInterval * 2 ? "yellow"
                          : "green";
```

**Update JS to render colored dots:**

```javascript
function renderHealthDot(health) {
    const colors = { green: '🟢', yellow: '🟡', red: '🔴' };
    return colors[health] || '⚪';
}

document.getElementById('refresh-health').textContent = renderHealthDot(data.RefreshHealth);
```

**Priority:** P2 | **Effort:** 30 minutes

---

## Testing Checklist

```
[ ] H-4: User A pins item, User B sees InMyLibrary=false
[ ] H-6: Set parental ceiling, verify R-rated items hidden
[ ] MISSING-1: View blocked items, unblock, verify shows in NeedsEnrich queue
[ ] MISSING-2: View My Picks, remove pin, verify item stays in Emby but removed from pins table
[ ] MISSING-3: Non-admin user cannot see Content Management tab
[ ] H-5: Verify EXPLAIN QUERY PLAN uses new index for My Picks query
[ ] M-6: Stop RefreshTask for >18 min, verify health dot turns red
```

---

## Commit Message

```
feat(sprint-150): spec drift fixes - missing UI tabs and per-user features

USER FEATURES:
- Add My Picks tab with playback/discover pin management (MISSING-2)
- Per-user "In My Library" status in Discover (H-4)
- Parental rating ceiling enforcement (H-6)

ADMIN FEATURES:
- Add Blocked Items tab with Unblock action (MISSING-1)
- Separate User Discover from Admin Content Management (MISSING-3)
- Health panel now shows colored status dots (M-6)

PERFORMANCE:
- Add composite index on user_item_pins(user_id, pin_source, pinned_at) (H-5)

Closes spec drift from Sprint 148. All promised features now wired up.
```

---

## Files Modified/Added

- Configuration/configurationpage.html — Add Blocked/MyPicks tabs
- Configuration/configurationpage.js — Add tab logic, API calls
- Services/DiscoverService.cs — Per-user InMyLibrary, parental filter
- Services/StatusService.cs — Health threshold computation
- Api/AdminController.cs — Blocked items endpoints
- Api/UserController.cs — My Picks endpoints
- Data/DatabaseManager.cs — UnblockItemAsync, schema migration
- Data/Repositories/UserPinRepository.cs — GetUserPinsAsync enhancement

**Total changes:** ~400 lines added, ~50 modified
