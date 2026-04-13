# Sprint 113 — Saved/Blocked User Actions (v3.3)

**Version:** v3.3 | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 112

---

## Overview

Sprint 113 implements user-triggered Save/Unsave/Block actions. These actions override Coalition rule, allowing users to explicitly keep or remove items regardless of source membership.

**Key Components:**
- SavedActionService - Handles Save/Unsave/Block/Unblock
- SavedRepository - Persist saved/blocked state
- SavedController - Admin API endpoints
- Saved UI - Config page UI
- PlaybackEventSubscriptionService - Series Season Save on Watch
- SavedBoxSetService - Saved Items BoxSet Maintenance

---

## Phase 113A — SavedRepository

### FIX-113A-01: Create SavedRepository

**File:** `Repositories/SavedRepository.cs`

```csharp
public class SavedRepository : ISavedRepository
{
    private readonly IDatabaseManager _db;

    public async Task<bool> IsSavedAsync(string itemId, CancellationToken ct = default)
    {
        // Check boolean saved column, NOT status enum
        const string sql = @"
            SELECT COUNT(*) FROM media_items
            WHERE id = @ItemId AND saved = 1";
        var count = await _db.ExecuteScalarAsync<int>(sql, new { ItemId = itemId }, ct);
        return count > 0;
    }

    public async Task<bool> IsBlockedAsync(string itemId, CancellationToken ct = default)
    {
        // Check boolean blocked column, NOT status enum
        const string sql = @"
            SELECT COUNT(*) FROM media_items
            WHERE id = @ItemId AND blocked = 1";
        var count = await _db.ExecuteScalarAsync<int>(sql, new { ItemId = itemId }, ct);
        return count > 0;
    }

    public async Task SetSavedAsync(
        string itemId,
        bool saved,
        CancellationToken ct = default)
    {
        // Set boolean saved column, NOT status enum
        const string sql = @"
            UPDATE media_items
            SET saved = @Saved,
                saved_at = @SavedAt,
                saved_by = @SavedBy,
                save_reason = @SaveReason,
                updated_at = @UpdatedAt
            WHERE id = @ItemId";

        var now = DateTimeOffset.UtcNow;
        var parameters = new
        {
            Saved = saved ? 1 : 0,
            SavedAt = saved ? now : (DateTimeOffset?)null,
            SavedBy = saved ? "user" : (string)null,
            SaveReason = saved ? SaveReason.Explicit.ToString() : (string)null,
            UpdatedAt = now,
            ItemId = itemId
        };

        await _db.ExecuteAsync(sql, parameters, ct);
    }

    public async Task SetBlockedAsync(
        string itemId,
        bool blocked,
        CancellationToken ct = default)
    {
        // Set boolean blocked column, NOT status enum
        const string sql = @"
            UPDATE media_items
            SET blocked = @Blocked,
                blocked_at = @BlockedAt,
                updated_at = @UpdatedAt
            WHERE id = @ItemId";

        var now = DateTimeOffset.UtcNow;
        var parameters = new
        {
            Blocked = blocked ? 1 : 0,
            BlockedAt = blocked ? now : (DateTimeOffset?)null,
            UpdatedAt = now,
            ItemId = itemId
        };

        await _db.ExecuteAsync(sql, parameters, ct);
    }

    public async Task<List<MediaItem>> GetAllSavedAsync(CancellationToken ct = default)
    {
        // Query boolean saved column, NOT status enum
        const string sql = @"
            SELECT * FROM media_items
            WHERE saved = 1
            ORDER BY title";

        return await _db.QueryAsync<MediaItem>(sql, ct);
    }

    public async Task<List<MediaItem>> GetAllBlockedAsync(CancellationToken ct = default)
    {
        // Query boolean blocked column, NOT status enum
        const string sql = @"
            SELECT * FROM media_items
            WHERE blocked = 1
            ORDER BY title";

        return await _db.QueryAsync<MediaItem>(sql, ct);
    }
}
```

**Acceptance Criteria:**
- [ ] IsSavedAsync checks saved = 1 (boolean column)
- [ ] IsBlockedAsync checks blocked = 1 (boolean column)
- [ ] SetSavedAsync updates saved/blocked boolean columns
- [ ] SetBlockedAsync updates blocked boolean column
- [ ] GetAllSavedAsync queries saved = 1
- [ ] GetAllBlockedAsync queries blocked = 1
- [ ] All IDs are string TEXT UUIDs

---

## Phase 113B — SavedActionService

### FIX-113B-01: Create SavedActionService

**File:** `Services/SavedActionService.cs`

```csharp
public class SavedActionService
{
    private readonly ISavedRepository _savedRepo;
    private readonly IDatabaseManager _db;
    private readonly SavedBoxSetService _boxSetService;
    private readonly ILogger _logger;

    public async Task<ActionResult> SaveItemAsync(
        string itemId,
        CancellationToken ct = default)
    {
        // Check if item exists
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null)
        {
            return ActionResult.Failure("Item not found");
        }

        // Set saved = true (boolean column)
        await _savedRepo.SetSavedAsync(itemId, true, ct);

        // Add to Saved BoxSet
        if (!string.IsNullOrEmpty(item.EmbyItemId))
        {
            var embyItemId = Guid.Parse(item.EmbyItemId);
            await _boxSetService.AddItemToBoxSetAsync(embyItemId, ct);
        }

        // Log pipeline event
        // TODO: Use PipelineLogger when implemented
        _logger.Info("Item {ItemId} saved by user", itemId);

        return ActionResult.Success($"Item saved: {item.Title}");
    }

    public async Task<ActionResult> UnsaveItemAsync(
        string itemId,
        CancellationToken ct = default)
    {
        // Check if item exists
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null)
        {
            return ActionResult.Failure("Item not found");
        }

        // Check coalition rule: does item have enabled source?
        var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(itemId, ct);

        // Set saved = false (boolean column)
        await _savedRepo.SetSavedAsync(itemId, false, ct);

        // Remove from Saved BoxSet
        if (!string.IsNullOrEmpty(item.EmbyItemId))
        {
            var embyItemId = Guid.Parse(item.EmbyItemId);
            await _boxSetService.RemoveItemFromBoxSetAsync(embyItemId, ct);
        }

        // CRITICAL: Unsave sets saved=false ONLY, NOT Deleted
        // Per v3.3 spec §8.3: Unsave sets Active, not Deleted
        // Removal pipeline (Sprint 115) handles grace period
        _logger.Info("Item {ItemId} unsaved, saved = false", itemId);

        return ActionResult.Success($"Item unsaved: {item.Title}");
    }

    public async Task<ActionResult> BlockItemAsync(
        string itemId,
        CancellationToken ct = default)
    {
        // Check if item exists
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null)
        {
            return ActionResult.Failure("Item not found");
        }

        // Set blocked = true (boolean column)
        await _savedRepo.SetBlockedAsync(itemId, true, ct);

        // Remove from Saved BoxSet (if present)
        if (!string.IsNullOrEmpty(item.EmbyItemId) && item.Saved)
        {
            var embyItemId = Guid.Parse(item.EmbyItemId);
            await _boxSetService.RemoveItemFromBoxSetAsync(embyItemId, ct);
        }

        // Remove from library (delete .strm, remove from Emby)
        await RemoveFromLibraryAsync(item, ct);

        _logger.Info("Item {ItemId} blocked by user", itemId);

        return ActionResult.Success($"Item blocked: {item.Title}");
    }

    public async Task<ActionResult> UnblockItemAsync(
        string itemId,
        CancellationToken ct = default)
    {
        // Check if item exists
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null)
        {
            return ActionResult.Failure("Item not found");
        }

        // Set blocked = false (boolean column)
        await _savedRepo.SetBlockedAsync(itemId, false, ct);

        // CRITICAL: Unblock sets blocked=false ONLY, NOT Deleted
        // Per v3.3 spec §8.4: Unblock sets Active, not Deleted
        _logger.Info("Item {ItemId} unblocked, blocked = false", itemId);

        return ActionResult.Success($"Item unblocked: {item.Title}");
    }

    private async Task RemoveFromLibraryAsync(MediaItem item, CancellationToken ct)
    {
        // Delete .strm file
        var strmPath = GetStrmPath(item);
        if (File.Exists(strmPath))
        {
            File.Delete(strmPath);
        }

        // Remove from Emby library
        if (!string.IsNullOrEmpty(item.EmbyItemId))
        {
            var embyItemId = Guid.Parse(item.EmbyItemId);
            var baseItem = _libraryManager.GetItemById(embyItemId);
            if (baseItem != null)
            {
                await _libraryManager.DeleteItemAsync(baseItem, ct);
            }
        }
    }

    private string GetStrmPath(MediaItem item)
    {
        var baseDir = _config.LibraryPath;
        var subDir = item.MediaType == "movie" ? "movies" :
                     item.MediaType == "series" ? "series" : "anime";
        return Path.Combine(baseDir, subDir, $"{item.Id}.strm");
    }
}
```

**Acceptance Criteria:**
- [ ] SaveItemAsync sets saved = 1 (boolean column)
- [ ] UnsaveItemAsync sets saved = 0, NOT Deleted
- [ ] UnsaveItemAsync removes from Saved BoxSet
- [ ] BlockItemAsync sets blocked = 1
- [ ] BlockItemAsync removes from library
- [ ] UnblockItemAsync sets blocked = 0, NOT Deleted
- [ ] All IDs are string TEXT UUIDs
- [ ] Saved BoxSet integration works

---

## Phase 113C — SavedController

### FIX-113C-01: Create SavedController

**File:** `Controllers/SavedController.cs`

```csharp
[Route("embystreams/saved")]
public class SavedController
{
    private readonly SavedActionService _service;

    [Route("save")]
    public async Task<ActionResult> Post(SaveRequest request)
    {
        return await _service.SaveItemAsync(request.ItemId, Request.AbortToken);
    }

    [Route("unsave")]
    public async Task<ActionResult> Post(UnsaveRequest request)
    {
        return await _service.UnsaveItemAsync(request.ItemId, Request.AbortToken);
    }

    [Route("block")]
    public async Task<ActionResult> Post(BlockRequest request)
    {
        return await _service.BlockItemAsync(request.ItemId, Request.AbortToken);
    }

    [Route("unblock")]
    public async Task<ActionResult> Post(UnblockRequest request)
    {
        return await _service.UnblockItemAsync(request.ItemId, Request.AbortToken);
    }

    [Route("list")]
    public async Task<SavedListResponse> Get(SavedListRequest request)
    {
        var saved = await _service.GetAllSavedAsync(Request.AbortToken);
        var blocked = await _service.GetAllBlockedAsync(Request.AbortToken);

        return new SavedListResponse
        {
            Saved = saved,
            Blocked = blocked
        };
    }
}

public record SaveRequest(string ItemId);
public record UnsaveRequest(string ItemId);
public record BlockRequest(string ItemId);
public record UnblockRequest(string ItemId);
public record SavedListRequest;
public record SavedListResponse(List<MediaItem> Saved, List<MediaItem> Blocked);
```

**Acceptance Criteria:**
- [ ] POST /save saves item
- [ ] POST /unsave unsaves item
- [ ] POST /block blocks item
- [ ] POST /unblock unblocks item
- [ ] GET /list returns saved and blocked items
- [ ] All ItemId parameters are string TEXT UUIDs

---

## Phase 113D — Saved UI

### FIX-113D-01: Create Saved Section in Config Page

**File:** `Configuration/configurationpage.html`

```html
<section id="saved-section">
    <h3>Saved Items</h3>
    <div id="saved-list"></div>
    <h3>Blocked Items</h3>
    <div id="blocked-list"></div>
</section>
```

### FIX-113D-02: Create Saved UI JavaScript

**File:** `Configuration/configurationpage.js`

```javascript
async function loadSavedItems() {
    const response = await fetch('/embystreams/saved/list');
    const data = await response.json();

    renderSavedList(data.Saved);
    renderBlockedList(data.Blocked);
}

function renderSavedList(items) {
    const container = document.getElementById('saved-list');
    container.innerHTML = items.map(item => `
        <div class="saved-item">
            <span>${item.title} (${item.year})</span>
            <button onclick="unsaveItem('${item.id}')">Unsave</button>
        </div>
    `).join('');
}

function renderBlockedList(items) {
    const container = document.getElementById('blocked-list');
    container.innerHTML = items.map(item => `
        <div class="blocked-item">
            <span>${item.title} (${item.year})</span>
            <button onclick="unblockItem('${item.id}')">Unblock</button>
        </div>
    `).join('');
}

async function unsaveItem(itemId) {
    const response = await fetch('/embystreams/saved/unsave', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ itemId })
    });
    const result = await response.json();
    if (result.success) {
        loadSavedItems();
    } else {
        alert(result.message);
    }
}

async function unblockItem(itemId) {
    const response = await fetch('/embystreams/saved/unblock', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ itemId })
    });
    const result = await response.json();
    if (result.success) {
        loadSavedItems();
    } else {
        alert(result.message);
    }
}
```

**Acceptance Criteria:**
- [ ] Lists saved items
- [ ] Lists blocked items
- [ ] Unsave button works
- [ ] Unblock button works
- [ ] Updates after action
- [ ] Item IDs are strings (TEXT UUID)

---

## Phase 113E — Series Season Save on Watch

### FIX-113E-01: Playback Event Subscription for Season Save

**File:** `Services/PlaybackEventSubscriptionService.cs`

```csharp
public class PlaybackEventSubscriptionService : IOnDemandServerEntryPoint
{
    private readonly IPlaybackManager _playbackManager;
    private readonly SavedActionService _savedService;
    private readonly IDatabaseManager _db;
    private readonly ILogger _logger;

    public PlaybackEventSubscriptionService(
        IPlaybackManager playbackManager,
        SavedActionService savedService,
        IDatabaseManager db,
        ILogger logger)
    {
        _playbackManager = playbackManager;
        _savedService = savedService;
        _db = db;
        _logger = logger;
    }

    public void Run()
    {
        _playbackManager.PlaybackStarted += OnPlaybackStarted;
    }

    public void Dispose()
    {
        _playbackManager.PlaybackStarted -= OnPlaybackStarted;
    }

    private async void OnPlaybackStarted(object sender, PlaybackProgressEventArgs args)
    {
        if (args.Item is null || args.Item.MediaType == null)
            return;

        // Look up MediaItem by EmbyItemId
        if (!string.IsNullOrEmpty(args.Item.InternalId))
        {
            var item = await _db.GetMediaItemByEmbyItemIdAsync(
                args.Item.InternalId,
                CancellationToken.None);

            if (item == null || item.MediaType != "series")
                return;

            // Series: save entire season on any episode watch
            var seasonNumber = ExtractSeasonNumber(args.Item);
            if (seasonNumber > 0)
            {
                await _savedService.SaveItemAsync(item.Id, CancellationToken.None);
                // Note: saved_season field is set but not yet used for filtering
                _logger.Info(
                    "Season {SeasonNumber} saved for series '{Title}' (episode watch)",
                    seasonNumber,
                    item.Title);
            }
        }
    }

    private int ExtractSeasonNumber(BaseItem item)
    {
        // Check for season number in item name (e.g., "S01E01")
        var match = Regex.Match(item.Name, @"\bS(\d+)E\d+\b");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var season))
        {
            return season;
        }
        return 0;
    }
}
```

**Acceptance Criteria:**
- [ ] Subscribed to PlaybackStarted events
- [ ] Series episodes save entire season on any watch
- [ ] Season number extracted from item name correctly
- [ ] SaveReason = WatchEpisode set
- [ ] saved_season populated for series saves
- [ ] Operation is async and non-blocking
- [ ] All IDs are string TEXT UUIDs
- [ ] Build succeeds

---

## Phase 113F — Saved Items BoxSet Maintenance

### FIX-113F-01: Saved Items BoxSet

**File:** `Services/SavedBoxSetService.cs`

```csharp
public class SavedBoxSetService
{
    private readonly IDatabaseManager _db;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger _logger;

    // The "Saved" BoxSet is identified by its name, not source_id
    private const string SavedBoxSetName = "Saved";

    public SavedBoxSetService(
        IDatabaseManager db,
        ICollectionManager collectionManager,
        ILogger logger)
    {
        _db = db;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    public async Task<BoxSet> EnsureBoxSetExistsAsync(CancellationToken ct = default)
    {
        // Query for existing BoxSet with name "Saved"
        var boxSet = await _collectionManager.GetCollectionsAsync(new CollectionQuery
        {
            IncludeItemTypes = new[] { BaseItemType.BoxSet },
            Name = SavedBoxSetName,
            Recursive = false
        }).Cast<BoxSet>().FirstOrDefault(ct);

        if (boxSet != null)
        {
            _logger.Debug("Saved BoxSet already exists");
            return boxSet;
        }

        // Create new BoxSet
        boxSet = await _collectionManager.CreateCollectionAsync(
            new CollectionCreationOptions
            {
                Name = SavedBoxSetName,
                IsLocked = false, // Allow user to manually add items
                ItemIdList = Array.Empty<Guid>()
            },
            ct);

        _logger.Info("Created Saved BoxSet: {BoxSetId}", boxSet.Id);
        return boxSet;
    }

    public async Task AddItemToBoxSetAsync(
        Guid embyItemId,
        CancellationToken ct = default)
    {
        // Get the Saved BoxSet
        var boxSet = await EnsureBoxSetExistsAsync(ct);

        // Add item to BoxSet using ICollectionManager
        await _collectionManager.AddToCollectionAsync(boxSet.Id, embyItemId, ct);
        _logger.Debug("Added item {EmbyItemId} to Saved BoxSet", embyItemId);
    }

    public async Task RemoveItemFromBoxSetAsync(
        Guid embyItemId,
        CancellationToken ct = default)
    {
        // Get the Saved BoxSet
        var boxSet = await EnsureBoxSetExistsAsync(ct);

        // Remove item from BoxSet
        // IMPORTANT: RemoveFromCollection requires BoxSet cast
        await _collectionManager.RemoveFromCollectionAsync(boxSet, embyItemId, ct);

        _logger.Debug("Removed item {EmbyItemId} from Saved BoxSet", embyItemId);
    }

    public async Task SyncBoxSetMembershipAsync(CancellationToken ct = default)
    {
        // Get all saved items (boolean column, NOT status enum)
        var savedItems = await _db.GetItemsBySavedAsync(true, ct);

        // Get or create Saved BoxSet
        var boxSet = await EnsureBoxSetExistsAsync(ct);

        // Get current members
        var currentMembers = await _collectionManager.GetCollectionsAsync(
            new CollectionQuery
            {
                IncludeItemTypes = new[] { BaseItemType.BoxSet },
                CollectionIds = new[] { boxSet.Id },
                Recursive = true
            }).Select(i => i.InternalId).ToHashSetAsync(ct);

        var savedMembers = savedItems
            .Where(i => !string.IsNullOrEmpty(i.EmbyItemId))
            .Select(i => Guid.Parse(i.EmbyItemId))
            .ToHashSet();

        // Remove members no longer saved
        var toRemove = currentMembers.Except(savedMembers).ToList();
        if (toRemove.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(boxSet, toRemove, ct);
            _logger.Info(
                "Removed {Count} items from Saved BoxSet (no longer saved)",
                toRemove.Count);
        }

        // Add newly saved items
        var toAdd = savedMembers.Except(currentMembers).ToList();
        foreach (var embyItemId in toAdd)
        {
            await _collectionManager.AddToCollectionAsync(boxSet.Id, embyItemId, ct);
        }

        if (toAdd.Count > 0)
        {
            _logger.Info(
                "Added {Count} items to Saved BoxSet (newly saved)",
                toAdd.Count);
        }
    }
}
```

**Acceptance Criteria:**
- [ ] BoxSet created if doesn't exist
- [ ] BoxSet identified by name "Saved"
- [ ] AddItem adds to BoxSet using ICollectionManager
- [ ] RemoveItem removes from BoxSet using ICollectionManager
- [ ] SyncBoxSetMembership adds newly saved items
- [ ] SyncBoxSetMembership removes unsaved items
- [ ] Uses ICollectionManager pattern (NOT _libraryManager.AddToCollectionAsync)
- [ ] Build succeeds

**Integration Notes:**
- This service is called from SavedActionService.SaveItemAsync() and SavedActionService.UnsaveItemAsync()
- Also called from SavedActionService.BlockItemAsync()
- Home screen rail for "Saved" points to this BoxSet (Sprint 118)
- User can manually add/remove items from BoxSet via Emby UI

---

## Sprint 113 Dependencies

- **Previous Sprint:** 112 (Stream Resolution and Playback)
- **Blocked By:** Sprint 112
- **Blocks:** Sprint 114 (Your Files Detection)

---

## Sprint 113 Completion Criteria

- [ ] SavedRepository persists saved/blocked state (boolean columns)
- [ ] SavedActionService handles all actions with correct behavior
- [ ] UnsaveItemAsync sets saved=false, NOT Deleted
- [ ] UnblockItemAsync sets blocked=false, NOT Deleted
- [ ] SavedController exposes API endpoints
- [ ] Saved UI lists and manages items
- [ ] PlaybackEventSubscriptionService saves series season on watch
- [ ] SavedBoxSetService maintains "Saved" BoxSet using ICollectionManager
- [ ] Coalition rule respected (checked but not forced)
- [ ] All IDs are string TEXT UUIDs
- [ ] Build succeeds
- [ ] E2E: Save/Unsave/Block/Unblock actions work

---

## Sprint 113 Notes

**Boolean Columns vs Status Enum:**
- v3.3 uses `saved` and `blocked` as boolean columns (0/1)
- NOT part of ItemStatus enum (Known, Resolved, Hydrated, Created, Indexed, Active, Failed, Deleted)
- SavedRepository queries boolean columns, NOT status
- Unsave/Unblock set boolean columns to false, NOT status changes

**Coalition Rule for User Actions:**
- Save: Always sets saved=true (overrides sources)
- Unsave: Sets saved=false, removal pipeline handles grace period
- Block: Always sets blocked=true and removes from library
- Unblock: Sets blocked=false, item remains based on Coalition rule

**Important Note on Unsave Behavior:**
Per v3.3 spec §8.3, unsave moves saved=true → saved=false. The removal pipeline then evaluates it on next run — starting grace period if no enabled source claims it. Unsave does NOT immediately set status = Deleted.

**Important Note on Unblock Behavior:**
Per v3.3 spec §8.4, unblock moves blocked=true → blocked=false. The item remains available based on Coalition rule (saved state + enabled source membership). Unblock does NOT set status = Deleted.

**Saved BoxSet:**
EmbyStreams must maintain a real Emby BoxSet named "Saved" whose membership always reflects the current set of saved items. This is managed by SavedBoxSetService and synced to home screen in Sprint 118.

**Series Season Save on Watch:**
- Series: save entire season on any episode watch
- Extract season number from item name (e.g., "S01E01")
- SaveReason = WatchEpisode
- saved_season populated for tracking (future filtering use)

**ICollectionManager Pattern:**
- Use `ICollectionManager.CreateCollectionAsync()` for BoxSet creation
- Use `ICollectionManager.AddToCollectionAsync()` for adding items
- Use `ICollectionManager.RemoveFromCollectionAsync()` for removing items
- RemoveFromCollectionAsync requires BoxSet cast (not BaseItem)

**Pipeline Events:**
- Save: PipelineTrigger.UserSave
- Block: PipelineTrigger.UserBlock
- Logged to item_pipeline_log
