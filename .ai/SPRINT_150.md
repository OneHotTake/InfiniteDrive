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

**Fulfills:** MAINTENANCE.md sections on user pin management, blocked item recovery, per-user library status

---

## Task H-4: Per-User "In My Library" Status

**File:** Services/DiscoverService.cs:844-883

**Problem:** InLibrary = entry.IsInUserLibrary checks global catalog presence, not whether THIS user has pinned it.

**MAINTENANCE.md Reference:** Sprint 148 requirement for per-user pin status

**Fix:**

1. Get current user ID from request context:
```csharp
private string GetCurrentUserId()
{
    var user = _authCtx.GetAuthorizationInfo(Request).User;
    return user?.Id.ToString("N") ?? throw new UnauthorizedAccessException("No user context");
}
```

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

**Validation:** User A pins item, User B sees InMyLibrary=false for same item.

**Priority:** P1 | **Effort:** 1-2 hours

---

## Task H-6: Parental Rating Ceiling Enforcement

**File:** Services/DiscoverService.cs + Data/DatabaseManager.cs

**MAINTENANCE.md Reference:** Security rule #3 (all filtering must be server-side)

**Strategy:** Prefer Emby's native `user.Policy.MaxParentalRating` for per-user behavior. Fall back to plugin config for global override if needed.

### Part 1: Schema (add to next migration)
```sql
ALTER TABLE discover_catalog ADD COLUMN parental_rating INTEGER;
-- Map: G=0, PG=2, PG-13=3, R=4, NC-17=5, TV-MA=6, etc.
```

### Part 2: Rating mapping utility
```csharp
private static int MapRatingToNumeric(string rating)
{
    return rating?.ToUpperInvariant() switch
    {
        "G" => 0,
        "PG" => 2,
        "PG-13" => 3,
        "R" => 4,
        "NC-17" => 5,
        "TV-14" => 3,
        "TV-MA" => 6,
        _ => 99 // Unknown = max restriction
    };
}
```

### Part 3: Populate ratings during catalog sync
Update CatalogDiscoverTask to fetch parental rating from AIOStreams and store numeric value.

### Part 4: Get user's ceiling
```csharp
private int GetUserParentalCeiling()
{
    var user = _authCtx.GetAuthorizationInfo(Request).User;

    if (!user.Policy.IsParentalScheduleAllowed(DateTime.UtcNow))
        return -1; // Parental controls active, block everything

    if (user.Policy.MaxParentalRating.HasValue)
        return user.Policy.MaxParentalRating.Value;

    // Fall back to plugin config if Emby setting not configured
    return Plugin.Instance.Configuration.DefaultParentalCeiling ?? 99;
}
```

### Part 5: Filter in queries
```sql
WHERE (parental_rating IS NULL OR parental_rating <= @maxRating)
```

**Validation:**
- Set user ceiling to PG-13 (3), verify R-rated (4) items hidden from Discover
- Verify admin users with no ceiling see all items

**Priority:** P1 | **Effort:** 4-6 hours

---

## Task MISSING-1: Blocked Items Admin Tab

**Files:** Configuration/configurationpage.html, Configuration/configurationpage.js, Services/StatusService.cs (or new AdminController.cs)

**MAINTENANCE.md Reference:** Sprint 146/148 admin UI requirement, blocked item recovery workflow

**Problem:** Blocked count shows in status, but no way to view/manage blocked items.

### Part 1: Add tab to admin UI

In configurationpage.html, add after existing tabs:

```html
<div data-role="page" id="embystreams-blocked-page">
    <div data-role="content">
        <div class="readOnlyContent">
            <h2>Blocked Items</h2>
            <p>Items that failed enrichment after 3 attempts. Still playable but won't retry automatically.</p>

            <div id="blocked-items-list" class="itemsContainer vertical-wrap"></div>

            <div class="buttons">
                <button id="unblock-selected-btn" class="raised button-submit">
                    Unblock Selected
                </button>
                <button id="refresh-blocked-btn" class="raised">
                    Refresh All Blocked
                </button>
            </div>
        </div>
    </div>
</div>
```

### Part 2: Add endpoints

```csharp
// Services/AdminService.cs (or extend StatusService)
public class GetBlockedItemsRequest : IReturn<GetBlockedItemsResponse> { }

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
            ImdbId = i.ImdbId,
            RetryCount = i.RetryCount
        }).ToList()
    };
}

public class UnblockItemsRequest : IReturn<UnblockItemsResponse>
{
    public List<string> ItemIds { get; set; }
}

public async Task<object> Post(UnblockItemsRequest req)
{
    AdminGuard.RequireAdmin(_authCtx, Request);

    foreach (var itemId in req.ItemIds)
    {
        await _db.UnblockItemAsync(itemId, CancellationToken.None);
    }

    // Log to refresh_run_log
    await _db.InsertRunLogAsync("Admin", "unblock", CancellationToken.None,
        notes: $"Unblocked {req.ItemIds.Count} items");

    return new UnblockItemsResponse { Success = true, Count = req.ItemIds.Count };
}
```

### Part 3: DatabaseManager methods

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
                next_retry_at = NULL,
                updated_at = @now
            WHERE id = @id", db);

        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("[DatabaseManager] Unblocked item {Id}", itemId);
    }
    finally
    {
        _dbWriteGate.Release();
    }
}
```

### Part 4: Wire up JS

```javascript
// configurationpage.js
async function loadBlockedItems() {
    try {
        const response = await ApiClient.fetch('/EmbyStreams/Admin/BlockedItems');
        const data = await response.json();

        if (!data.Items || data.Items.length === 0) {
            document.getElementById('blocked-items-list').innerHTML =
                '<p class="secondary">No blocked items.</p>';
            return;
        }

        const html = data.Items.map(item => `
            <div class="listItem">
                <label>
                    <input type="checkbox" class="blocked-item-checkbox" data-id="${item.Id}">
                    <span>${item.Title} (${item.Year})</span>
                </label>
                <div class="secondary">
                    ${item.ImdbId} • Blocked ${formatDate(item.BlockedAt)} • ${item.BlockedBy}
                </div>
            </div>
        `).join('');

        document.getElementById('blocked-items-list').innerHTML = html;
    } catch (err) {
        console.error('Failed to load blocked items:', err);
        Dashboard.alert('Failed to load blocked items. Check server logs.');
    }
}

document.getElementById('unblock-selected-btn').addEventListener('click', async () => {
    const checkboxes = document.querySelectorAll('.blocked-item-checkbox:checked');
    if (checkboxes.length === 0) {
        Dashboard.alert('No items selected');
        return;
    }

    const itemIds = Array.from(checkboxes).map(cb => cb.dataset.id);

    try {
        await ApiClient.fetch('/EmbyStreams/Admin/UnblockItems', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ItemIds: itemIds })
        });

        Dashboard.alert(`${itemIds.length} items unblocked and queued for enrichment.`);
        await loadBlockedItems();
    } catch (err) {
        console.error('Unblock failed:', err);
        Dashboard.alert('Unblock failed. Check server logs.');
    }
});

document.getElementById('refresh-blocked-btn').addEventListener('click', async () => {
    // Force all blocked items back to NeedsEnrich
    const response = await ApiClient.fetch('/EmbyStreams/Admin/BlockedItems');
    const data = await response.json();
    const allIds = data.Items.map(i => i.Id);

    if (allIds.length === 0) return;

    await ApiClient.fetch('/EmbyStreams/Admin/UnblockItems', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ItemIds: allIds })
    });

    Dashboard.alert(`All ${allIds.length} blocked items queued for re-enrichment.`);
    await loadBlockedItems();
});
```

**Priority:** P1 | **Effort:** 3-4 hours

---

## Task MISSING-2: My Picks User Tab

**Files:** Configuration/configurationpage.html, Configuration/configurationpage.js, Services/UserService.cs (new)

**MAINTENANCE.md Reference:** Sprint 148 user pin management, per-user library curation

**Problem:** User can pin items via playback or Discover, but no UI to view/manage them.

### Part 1: Add tab (user-facing, no admin guard needed)

```html
<div data-role="page" id="embystreams-mypicks-page">
    <div data-role="content">
        <h2>My Picks</h2>
        <p>Items you've added from Discover or played recently. Remove items you're done with.</p>

        <div class="detailSection">
            <div class="detailSectionHeader">
                <h3>Recently Played</h3>
            </div>
            <div id="playback-pins-list" class="itemsContainer vertical-wrap"></div>
        </div>

        <div class="detailSection">
            <div class="detailSectionHeader">
                <h3>Added from Discover</h3>
            </div>
            <div id="discover-pins-list" class="itemsContainer vertical-wrap"></div>
        </div>

        <button id="remove-selected-pins-btn" class="raised button-submit">
            I'm Done With These
        </button>
    </div>
</div>
```

### Part 2: Add user endpoint (no admin guard - user can only see their own pins)

```csharp
// Services/UserService.cs
public class GetUserPinsRequest : IReturn<GetUserPinsResponse> { }

public async Task<object> Get(GetUserPinsRequest req)
{
    var userId = GetCurrentUserId(); // From IAuthorizationContext

    var pins = await _pinRepo.GetUserPinsAsync(userId, CancellationToken.None);

    // Join with catalog_items to get full metadata
    var items = await _db.GetCatalogItemsByIdsAsync(
        pins.Select(p => p.CatalogItemId).ToList(),
        CancellationToken.None);

    var playbackPins = pins.Where(p => p.PinSource == "playback")
        .Join(items, p => p.CatalogItemId, i => i.Id, (p, i) => new UserPinDto
        {
            CatalogItemId = i.Id,
            Title = i.Title,
            Year = i.Year,
            ImdbId = i.ImdbId,
            PinnedAt = p.PinnedAt,
            PinSource = p.PinSource
        }).ToList();

    var discoverPins = pins.Where(p => p.PinSource == "discover")
        .Join(items, p => p.CatalogItemId, i => i.Id, (p, i) => new UserPinDto
        {
            CatalogItemId = i.Id,
            Title = i.Title,
            Year = i.Year,
            ImdbId = i.ImdbId,
            PinnedAt = p.PinnedAt,
            PinSource = p.PinSource
        }).ToList();

    return new GetUserPinsResponse
    {
        PlaybackPins = playbackPins,
        DiscoverPins = discoverPins
    };
}

public class RemovePinsRequest : IReturn<RemovePinsResponse>
{
    public List<string> CatalogItemIds { get; set; }
}

public async Task<object> Post(RemovePinsRequest req)
{
    var userId = GetCurrentUserId();

    foreach (var catalogItemId in req.CatalogItemIds)
    {
        await _pinRepo.RemovePinAsync(userId, catalogItemId, CancellationToken.None);
    }

    return new RemovePinsResponse { Success = true, Count = req.CatalogItemIds.Count };
}
```

### Part 3: Wire up JS

```javascript
async function loadMyPicks() {
    try {
        const response = await ApiClient.fetch('/EmbyStreams/User/MyPins');
        const data = await response.json();

        renderPinsList('playback-pins-list', data.PlaybackPins);
        renderPinsList('discover-pins-list', data.DiscoverPins);
    } catch (err) {
        console.error('Failed to load My Picks:', err);
    }
}

function renderPinsList(elementId, pins) {
    if (!pins || pins.length === 0) {
        document.getElementById(elementId).innerHTML = '<p class="secondary">No items yet.</p>';
        return;
    }

    const html = pins.map(pin => `
        <div class="listItem">
            <label>
                <input type="checkbox" class="pin-checkbox" data-catalog-id="${pin.CatalogItemId}">
                <span>${pin.Title} (${pin.Year})</span>
            </label>
            <div class="secondary">
                ${pin.ImdbId} • Pinned ${formatDate(pin.PinnedAt)}
            </div>
        </div>
    `).join('');

    document.getElementById(elementId).innerHTML = html;
}

document.getElementById('remove-selected-pins-btn').addEventListener('click', async () => {
    const checkboxes = document.querySelectorAll('.pin-checkbox:checked');
    if (checkboxes.length === 0) {
        Dashboard.alert('No items selected');
        return;
    }

    const catalogItemIds = Array.from(checkboxes).map(cb => cb.dataset.catalogId);

    try {
        await ApiClient.fetch('/EmbyStreams/User/RemovePins', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ CatalogItemIds: catalogItemIds })
        });

        Dashboard.alert(`Removed ${catalogItemIds.length} items from your picks.`);
        await loadMyPicks();
    } catch (err) {
        console.error('Remove failed:', err);
        Dashboard.alert('Failed to remove pins. Check server logs.');
    }
});
```

**Priority:** P1 | **Effort:** 3-4 hours

---

## Task MISSING-3: Separate User Discover from Admin Content Management

**Files:** Configuration/configurationpage.html

**Problem:** Current "Discover" tab is single-purpose. Spec calls for role-based separation.

**Strategy:** Two separate tabs (Option A from spec — cleaner security boundary)

### Part 1: User-facing Discover (existing, no changes needed to functionality)

Keep current Browse/Search/Detail/AddToLibrary functionality as-is.

### Part 2: New Admin Content Management tab

```html
<div data-role="page" id="embystreams-admin-content-page">
    <div data-role="content">
        <div class="readOnlyContent">
            <h2>Content Management</h2>

            <div class="detailSection">
                <h3>Catalog Sync</h3>
                <button id="force-catalog-refresh-btn" class="raised">
                    Force Catalog Refresh Now
                </button>
                <p class="fieldDescription">
                    Manually trigger catalog sync (normally runs every 6 hours).
                </p>
            </div>

            <div class="detailSection">
                <h3>Orphan Files</h3>
                <div id="orphan-files-list"></div>
                <button id="cleanup-orphans-btn" class="raised button-submit">
                    Clean Up Orphans
                </button>
            </div>

            <div class="detailSection">
                <h3>Sync Sources</h3>
                <div id="sync-sources-list"></div>
            </div>
        </div>
    </div>
</div>
```

### Part 3: Use Emby's role checks for tab visibility

```javascript
// configurationpage.js - on page load
const currentUser = await ApiClient.getCurrentUser();
if (currentUser.Policy.IsAdministrator) {
    // Show admin-only tabs
    document.getElementById('embystreams-admin-content-page').style.display = '';
    document.getElementById('embystreams-blocked-page').style.display = '';
} else {
    // Hide admin tabs for regular users
    document.getElementById('embystreams-admin-content-page').style.display = 'none';
    document.getElementById('embystreams-blocked-page').style.display = 'none';
}
```

**Priority:** P2 | **Effort:** 2-3 hours

---

## Task H-5: Add Composite Index on user_item_pins

**File:** Data/DatabaseManager.cs (schema migration)

**MAINTENANCE.md Reference:** Performance rule #10 (indexed queries for hot paths)

**Problem:** My Picks query needs efficient lookup by (user_id, pin_source, pinned_at DESC)

**Fix:**

```csharp
// In EnsureSchemaAsync or next schema version bump
await using var cmd = new SQLiteCommand(@"
    CREATE INDEX IF NOT EXISTS idx_user_item_pins_user_source_pinned
    ON user_item_pins(emby_user_id, pin_source, pinned_at DESC);", db);
await cmd.ExecuteNonQueryAsync(ct);

_logger.LogInformation("[DatabaseManager] Created composite index on user_item_pins");
```

**Validation:** Run `EXPLAIN QUERY PLAN SELECT * FROM user_item_pins WHERE emby_user_id = ? AND pin_source = ? ORDER BY pinned_at DESC` and verify it uses the index.

**Priority:** P2 | **Effort:** 5 minutes

---

## Task M-6: Health Panel Threshold Logic

**File:** Services/StatusService.cs:300-409

**MAINTENANCE.md Reference:** Health panel 2×/3× interval thresholds for yellow/red status

**Problem:** Panel shows raw timestamps, no computed health status.

**Fix:**

```csharp
// In StatusService.Get() method
var refreshLastRunStr = await _db.GetMetadataAsync("last_refresh_run_time", ct);
if (!string.IsNullOrEmpty(refreshLastRunStr))
{
    var refreshLastRun = DateTime.Parse(refreshLastRunStr);
    var refreshAge = DateTime.UtcNow - refreshLastRun;
    var refreshInterval = TimeSpan.FromMinutes(6);

    response.RefreshHealth = refreshAge > refreshInterval * 3 ? "red"
                           : refreshAge > refreshInterval * 2 ? "yellow"
                           : "green";
}

var deepCleanLastRunStr = await _db.GetMetadataAsync("last_deepclean_run_time", ct);
if (!string.IsNullOrEmpty(deepCleanLastRunStr))
{
    var deepCleanLastRun = DateTime.Parse(deepCleanLastRunStr);
    var deepCleanAge = DateTime.UtcNow - deepCleanLastRun;
    var deepCleanInterval = TimeSpan.FromHours(18);

    response.DeepCleanHealth = deepCleanAge > deepCleanInterval * 3 ? "red"
                              : deepCleanAge > deepCleanInterval * 2 ? "yellow"
                              : "green";
}
```

**Update JS to render colored dots:**

```javascript
function renderHealthDot(health) {
    const colors = { green: '🟢', yellow: '🟡', red: '🔴' };
    return colors[health] || '⚪';
}

// In loadImprobabilityStatus()
document.getElementById('refresh-health-dot').textContent = renderHealthDot(data.RefreshHealth);
document.getElementById('deepclean-health-dot').textContent = renderHealthDot(data.DeepCleanHealth);
```

**Priority:** P2 | **Effort:** 30 minutes

---

## Testing Checklist

```
[ ] Run full Refresh + DeepClean cycle after changes (no regression)
[ ] Verify inline Enrich still works for no-ID items
[ ] Verify token renewal still functions
[ ] H-4: User A pins item, User B sees InMyLibrary=false
[ ] H-6: Set user ceiling to PG-13, verify R-rated items hidden
[ ] H-6: Admin with no ceiling sees all items
[ ] MISSING-1: View blocked items, unblock, verify shows in NeedsEnrich queue
[ ] MISSING-1: "Refresh All Blocked" unblocks all items at once
[ ] MISSING-2: View My Picks, remove pin, verify item stays in Emby but removed from pins table
[ ] MISSING-2: Playback auto-pin still creates pins with pin_source='playback'
[ ] MISSING-3: Non-admin user cannot see Content Management or Blocked tabs
[ ] H-5: Verify EXPLAIN QUERY PLAN uses new index for My Picks query
[ ] M-6: Stop RefreshTask for >18 min, verify health dot turns red
[ ] M-6: Resume RefreshTask, verify dot returns to green
```

---

## Commit Message

```
feat(sprint-150): spec drift fixes - missing UI tabs and per-user features

USER FEATURES:
- Add My Picks tab with playback/discover pin management (MISSING-2)
- Per-user "In My Library" status in Discover (H-4)
- Parental rating ceiling enforcement using Emby's native policy (H-6)

ADMIN FEATURES:
- Add Blocked Items tab with Unblock + Refresh All actions (MISSING-1)
- Separate User Discover from Admin Content Management (MISSING-3)
- Health panel now shows colored status dots with 2x/3x thresholds (M-6)

PERFORMANCE:
- Add composite index on user_item_pins(user_id, pin_source, pinned_at) (H-5)

Closes spec drift from Sprint 148. All promised features now wired up.

Fulfills: MAINTENANCE.md user pin management, blocked recovery, parental controls
```

---

## Files Modified/Added

- Configuration/configurationpage.html — Add Blocked/MyPicks/ContentMgmt tabs
- Configuration/configurationpage.js — Add tab logic, API calls, role-based visibility
- Services/DiscoverService.cs — Per-user InMyLibrary, parental filter
- Services/StatusService.cs — Health threshold computation
- Services/AdminService.cs — NEW: Blocked items endpoints
- Services/UserService.cs — NEW: My Picks endpoints
- Data/DatabaseManager.cs — UnblockItemAsync, GetCatalogItemsByIdsAsync, schema migration
- Data/Repositories/UserPinRepository.cs — GetUserPinsAsync enhancement

**Total changes:** ~500 lines added, ~60 modified
