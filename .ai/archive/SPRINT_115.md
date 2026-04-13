# Sprint 115 — Removal Pipeline (v3.3)

**Version:** v3.3 | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 114

---

## Overview

Sprint 115 implements removal pipeline that cleans up items that are no longer in the manifest and have no enabled sources. This respects Coalition rule and user overrides, and implements grace period for safe removal.

**Key Components:**
- RemovalService - Manages item removal with grace period
- RemovalPipeline - Phases of removal (grace period → deletion)
- RemovalTask - Scheduled task for periodic cleanup
- RemovalController - Admin API for manual removal

---

## Phase 115A — RemovalService

### FIX-115A-01: Create RemovalService

**File:** `Services/RemovalService.cs`

```csharp
public class RemovalService
{
    private readonly IDatabaseManager _db;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    // Grace period configuration
    private readonly TimeSpan _gracePeriod = TimeSpan.FromDays(7);

    public async Task<RemovalResult> MarkForRemovalAsync(
        string itemId,
        CancellationToken ct = default)
    {
        // Check if item exists
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null)
        {
            return RemovalResult.Failure("Item not found");
        }

        // Check if item is saved (boolean column, NOT status enum)
        if (item.Saved)
        {
            _logger.Info("Item {ItemId} is Saved, cannot remove", itemId);
            return RemovalResult.Failure("Item is saved by user");
        }

        // Check if item is blocked (boolean column, NOT status enum)
        if (item.Blocked)
        {
            _logger.Info("Item {ItemId} is Blocked, cannot remove", itemId);
            return RemovalResult.Failure("Item is blocked by user");
        }

        // Check coalition rule: does item have enabled source?
        // CRITICAL: This MUST be a single JOIN query
        var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(itemId, ct);

        if (hasEnabledSource)
        {
            _logger.Info("Item {ItemId} has enabled source, cannot remove", itemId);
            return RemovalResult.Failure("Item has enabled source");
        }

        // Start grace period (do NOT set status = Deleted)
        // Per v3.3 spec §10.3: Removal pipeline handles grace period
        item.GraceStartedAt = DateTimeOffset.UtcNow;
        await _db.UpdateMediaItemAsync(item, ct);

        _logger.Info("Item {ItemId} started grace period", itemId);

        return RemovalResult.Success($"Item grace period started: {item.Title}");
    }

    public async Task<RemovalResult> RemoveItemAsync(
        string itemId,
        CancellationToken ct = default)
    {
        // Check if item exists
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null)
        {
            return RemovalResult.Failure("Item not found");
        }

        // Check grace period: is it safe to remove?
        if (!await IsGracePeriodExpiredAsync(item, ct))
        {
            _logger.Warn("Item {ItemId} grace period not expired, cannot remove yet", itemId);
            return RemovalResult.Failure($"Grace period not expired until {item.GraceStartedAt?.Add(_gracePeriod)}");
        }

        // Check coalition rule: does item have enabled source?
        // CRITICAL: This MUST be a single JOIN query
        var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(itemId, ct);

        if (hasEnabledSource || item.Saved || item.Blocked)
        {
            // Item should not be removed, cancel grace period
            item.GraceStartedAt = null;
            await _db.UpdateMediaItemAsync(item, ct);
            _logger.Info("Item {ItemId} removal cancelled (coalition rule), grace cleared", itemId);
            return RemovalResult.Success($"Removal cancelled: {item.Title}");
        }

        // Remove .strm file
        await RemoveStrmFileAsync(item, ct);

        // Remove from Emby library
        await RemoveFromEmbyAsync(item, ct);

        // Update status to Deleted (NOT Removed - that status doesn't exist)
        item.Status = ItemStatus.Deleted;
        await _db.UpdateMediaItemAsync(item, ct);

        _logger.Info("Item {ItemId} removed from library", itemId);

        return RemovalResult.Success($"Item removed: {item.Title}");
    }

    private async Task<bool> IsGracePeriodExpiredAsync(MediaItem item, CancellationToken ct)
    {
        if (!item.GraceStartedAt.HasValue)
        {
            // No grace period started, item can be removed
            return true;
        }

        var graceEnd = item.GraceStartedAt.Value.Add(_gracePeriod);
        var isExpired = DateTimeOffset.UtcNow > graceEnd;

        _logger.Debug("Item {ItemId} grace period: started={Started}, ends={Ends}, expired={IsExpired}",
            item.Id, item.GraceStartedAt, graceEnd, isExpired);

        return isExpired;
    }

    private async Task RemoveStrmFileAsync(MediaItem item, CancellationToken ct)
    {
        var strmPath = GetStrmPath(item);
        if (File.Exists(strmPath))
        {
            File.Delete(strmPath);
            _logger.Debug("Deleted .strm file: {Path}", strmPath);
        }
    }

    private async Task RemoveFromEmbyAsync(MediaItem item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.EmbyItemId))
        {
            _logger.Warn("Item {ItemId} has no EmbyItemId", item.Id);
            return;
        }

        var embyItemId = Guid.Parse(item.EmbyItemId);
        var baseItem = _libraryManager.GetItemById(embyItemId);

        if (baseItem == null)
        {
            _logger.Warn("Emby item not found: {EmbyItemId}", embyItemId);
            return;
        }

        // Emby confirmation gate: Verify item is safe to remove
        // This prevents removing items that may be in use
        if (baseItem.IsPlayed)
        {
            _logger.Debug("Skipping Emby removal for played item {EmbyItemId} (user has watched)", embyItemId);
            return;
        }

        await _libraryManager.DeleteItemAsync(baseItem, ct);
        _logger.Debug("Removed from Emby: {EmbyItemId}", embyItemId);
    }

    private string GetStrmPath(MediaItem item)
    {
        // CRITICAL: Resolve to three separate library paths
        // Per v3.3 spec §4.1: Three separate Emby libraries
        var mediaType = item.MediaType ?? "movie";

        // Base library path from config
        var baseLibraryPath = _config.LibraryPath;

        // Resolve subdirectory based on media type
        var subDir = mediaType switch
        {
            "movie" => "movies",
            "series" => "series",
            // For anime, check if primary ID is AniList/AniDB
            _ when IsAnimeMediaId(item) => "anime",
            _ => "movies"
        };

        return Path.Combine(baseLibraryPath, subDir, $"{item.Id}.strm");
    }

    private bool IsAnimeMediaId(MediaItem item)
    {
        // Check if primary ID type is anime-specific
        return item.PrimaryIdType?.ToLower() switch
        {
            "anilist" => true,
            "anidb" => true,
            "kitsu" => true,
            _ => false
        };
    }
}

public record RemovalResult(
    bool Success,
    string Message
)
{
    public static RemovalResult Success(string message) => new(true, message);
    public static RemovalResult Failure(string message) => new(false, message);
}
```

**Acceptance Criteria:**
- [ ] MarkForRemovalAsync starts grace period (GraceStartedAt = now)
- [ ] MarkForRemovalAsync respects Saved/Blocked/EnabledSource
- [ ] RemoveItemAsync checks grace period expiration
- [ ] RemoveItemAsync respects Coalition rule (double-check)
- [ ] RemoveItemAsync deletes .strm file
- [ ] RemoveItemAsync removes from Emby (with confirmation gate)
- [ ] RemoveItemAsync sets status = Deleted (NOT Removed)
- [ ] GetStrmPath resolves three separate paths (movies/, series/, anime/)
- [ ] All IDs are string TEXT UUIDs
- [ ] Uses Emby ILogger

---

## Phase 115B — RemovalPipeline

### FIX-115B-01: Create RemovalPipeline

**File:** `Services/RemovalPipeline.cs`

```csharp
public class RemovalPipeline
{
    private readonly RemovalService _service;
    private readonly IDatabaseManager _db;
    private readonly ILogger _logger;

    // Grace period configuration
    private readonly TimeSpan _gracePeriod = TimeSpan.FromDays(7);

    public async Task<RemovalPipelineResult> ProcessExpiredGraceItemsAsync(CancellationToken ct = default)
    {
        // Step 1: Get all items with active grace period
        var graceItems = await _db.GetItemsByGraceStartedAsync(ct);
        _logger.Info("Found {Count} items in grace period", graceItems.Count);

        var results = new List<RemovalResult>();
        var removedCount = 0;
        var cancelledCount = 0;
        var extendedCount = 0;

        // Step 2: Process each grace period item
        foreach (var item in graceItems)
        {
            var result = await ProcessGraceItemAsync(item, ct);

            if (result.Message.Contains("removed"))
                removedCount++;
            else if (result.Message.Contains("cancelled"))
                cancelledCount++;
            else if (result.Message.Contains("extended"))
                extendedCount++;

            results.Add(result);
        }

        return new RemovalPipelineResult
        {
            TotalProcessed = graceItems.Count,
            RemovedCount = removedCount,
            CancelledCount = cancelledCount,
            ExtendedCount = extendedCount,
            SuccessCount = results.Count(r => r.Success),
            FailureCount = results.Count(r => !r.Success),
            Results = results
        };
    }

    private async Task<RemovalResult> ProcessGraceItemAsync(MediaItem item, CancellationToken ct)
    {
        // Check grace period expiration
        var graceStarted = item.GraceStartedAt ?? DateTimeOffset.MinValue;
        var graceEnd = graceStarted.Add(_gracePeriod);

        if (DateTimeOffset.UtcNow <= graceEnd)
        {
            // Grace period not expired, keep waiting
            return RemovalResult.Success($"Grace period active until {graceEnd}");
        }

        // Grace period expired, check coalition rule
        // CRITICAL: This MUST be a single JOIN query
        const string coalitionSql = @"
            SELECT EXISTS(
                SELECT 1 FROM source_memberships sm
                JOIN sources s ON sm.source_id = s.id
                WHERE sm.media_item_id = @MediaItemId AND s.enabled = 1
            )";
        var hasEnabledSource = await _db.ExecuteScalarAsync<bool>(
            coalitionSql, new { MediaItemId = item.Id }, ct);

        // Check saved/blocked boolean columns (NOT status enum)
        if (hasEnabledSource || item.Saved || item.Blocked)
        {
            // Item should not be removed, cancel grace period
            item.GraceStartedAt = null;
            await _db.UpdateMediaItemAsync(item, ct);

            var reason = item.Saved ? "Saved" :
                          item.Blocked ? "Blocked" : "has enabled source";

            _logger.Info("Item {ItemId} removal cancelled ({Reason}), grace cleared", item.Id, reason);
            return RemovalResult.Success($"Removal cancelled ({Reason}): {item.Title}");
        }

        // Safe to remove
        return await _service.RemoveItemAsync(item.Id, ct);
    }
}

public record RemovalPipelineResult(
    int TotalProcessed,
    int RemovedCount,
    int CancelledCount,
    int ExtendedCount,
    int SuccessCount,
    int FailureCount,
    List<RemovalResult> Results
);
```

**Acceptance Criteria:**
- [ ] Gets all items with active grace period
- [ ] Checks grace period expiration
- [ ] Coalition rule check uses single JOIN query (not multiple queries)
- [ ] Reverts items that should stay (cancel grace period)
- [ ] Removes items that should go
- [ ] Reports summary with breakdown
- [ ] All IDs are string TEXT UUIDs
- [ ] Uses Emby ILogger

**Coalition Rule SQL (Single JOIN):**

The coalition rule check MUST be a single JOIN query:

```sql
SELECT EXISTS(
    SELECT 1 FROM source_memberships sm
    JOIN sources s ON sm.source_id = s.id
    WHERE sm.media_item_id = @MediaItemId AND s.enabled = 1
)
```

This ensures atomic consistency and prevents race conditions.

---

## Phase 115C — RemovalTask

### FIX-115C-01: Create RemovalTask

**File:** `Tasks/RemovalTask.cs`

```csharp
public class RemovalTask : IScheduledTask
{
    private readonly RemovalPipeline _pipeline;
    private readonly ILogger _logger;

    public string Name => "EmbyStreams Removal Cleanup";
    public string Key => "embystreams_removal";
    public string Description => "Processes expired grace period items for removal";
    public string Category => "EmbyStreams";

    public async Task ExecuteAsync(CancellationToken ct, IProgress<double> progress)
    {
        await Plugin.SyncLock.WaitAsync(ct);
        try
        {
            progress?.Report(0);

            _logger.Info("Starting removal pipeline...");

            // Process expired grace period items
            var result = await _pipeline.ProcessExpiredGraceItemsAsync(ct);
            progress?.Report(100);

            _logger.Info(
                "Removal pipeline complete: {Total} processed, {Removed} removed, {Cancelled} cancelled, {Extended} still active",
                result.TotalProcessed, result.RemovedCount, result.CancelledCount, result.ExtendedCount);
        }
        finally
        {
            Plugin.SyncLock.Release();
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Processes removal pipeline (grace period items)
- [ ] Uses SyncLock
- [ ] Reports progress
- [ ] Logs summary
- [ ] Uses Emby ILogger

---

## Phase 115D — RemovalController

### FIX-115D-01: Create RemovalController

**File:** `Controllers/RemovalController.cs`

```csharp
[Route("embystreams/removal")]
public class RemovalController
{
    private readonly RemovalService _service;
    private readonly RemovalPipeline _pipeline;

    [Route("mark")]
    public async Task<RemovalResult> Post(MarkForRemovalRequest request)
    {
        return await _service.MarkForRemovalAsync(request.ItemId, Request.AbortToken);
    }

    [Route("remove")]
    public async Task<RemovalResult> Post(RemoveRequest request)
    {
        return await _service.RemoveItemAsync(request.ItemId, Request.AbortToken);
    }

    [Route("process")]
    public async Task<RemovalPipelineResult> Post(ProcessRemovalRequest request)
    {
        return await _pipeline.ProcessExpiredGraceItemsAsync(Request.AbortToken);
    }

    [Route("list")]
    public async Task<RemovalListResponse> Get(RemovalListRequest request)
    {
        var graceItems = await _db.GetItemsByGraceStartedAsync(Request.AbortToken);
        return new RemovalListResponse { Items = graceItems };
    }
}

public record MarkForRemovalRequest(string ItemId);
public record RemoveRequest(string ItemId);
public record ProcessRemovalRequest;
public record RemovalListRequest;
public record RemovalListResponse(List<MediaItem> Items);
```

**Acceptance Criteria:**
- [ ] POST /mark starts grace period
- [ ] POST /remove removes item
- [ ] POST /process processes all expired grace items
- [ ] GET /list lists grace period items
- [ ] All ItemId parameters are string TEXT UUIDs
- [ ] Uses Emby ILogger

---

## Sprint 115 Dependencies

- **Previous Sprint:** 114 (Your Files Detection)
- **Blocked By:** Sprint 114
- **Blocks:** Sprint 116 (Collection Management)

---

## Sprint 115 Completion Criteria

- [ ] RemovalService marks items for grace period
- [ ] RemovalService checks grace period expiration
- [ ] RemovalService respects Coalition rule
- [ ] RemovalPipeline uses single JOIN for coalition rule
- [ ] RemovalTask runs periodic cleanup
- [ ] RemovalController exposes API
- [ ] GetStrmPath resolves three separate paths (movies/, series/, anime/)
- [ ] All IDs are string TEXT UUIDs
- [ ] Build succeeds
- [ ] E2E: Items removed correctly after grace period

---

## Sprint 115 Notes

**Grace Period Logic (v3.3 Spec §10):**
- Default grace period: 7 days
- MarkForRemovalAsync sets GraceStartedAt = now
- RemoveItemAsync checks if grace period expired
- RemovalPipeline processes all items with active grace period
- Coalition rule checked before removal (double-check)
- Removed items: status = Deleted (NOT Removed - that status doesn't exist)

**Three Separate Library Paths:**

Per v3.3 spec §4.1, EmbyStreams creates and manages THREE separate Emby libraries:
1. `/embystreams/library/movies/` → TMDB/IMDB movies
2. `/embystreams/library/series/` → TMDB/IMDB series
3. `/embystreams/library/anime/` → AniList/AniDB content

GetStrmPath must resolve to the correct subdirectory based on media type and ID type:
- Movies/series with IMDB/TMDB/TVDB IDs → movies/ or series/
- AniList/AniDB/Kitsu IDs → anime/

**Coalition Rule Implementation (v3.3 Spec §6.2):**
- CRITICAL: Coalition rule check MUST be a single JOIN query
- Do NOT use multiple queries (e.g., check source_memberships separately)
- Single query pattern:

```sql
SELECT EXISTS(
    SELECT 1 FROM source_memberships sm
    JOIN sources s ON sm.source_id = s.id
    WHERE sm.media_item_id = @MediaItemId AND s.enabled = 1
)
```

- This ensures atomic consistency and prevents race conditions
- Check boolean columns (saved, blocked), NOT status enum

**Coalition Rule Overrides:**
- Saved items: Never remove (user override)
- Blocked items: Never remove (user override)
- Has enabled source: Never remove (coalition)
- No enabled source + not saved/blocked + grace expired: Safe to remove

**Emby Confirmation Gate:**
- Before removing from Emby library, verify item is safe to remove
- If item has been played (IsPlayed), skip Emby removal
- This prevents removing items that may be in user's watch history
- The .strm file is still deleted, but library entry remains
- This protects user data and prevents accidental loss of watch progress

**Task Scheduling:**
- Run every 1 hour
- Uses SyncLock to avoid conflicts with sync
- Processes all items with active grace period (GraceStartedAt != null)
- Grace period items with enabled source/saved/blocked: grace cancelled
- Grace period items without enabled source: removed after 7 days

**Database Methods Required:**
- `GetItemsByGraceStartedAsync(CancellationToken ct)` - returns items where grace_started_at IS NOT NULL
- `ItemHasEnabledSourceAsync(string itemId, CancellationToken ct)` - returns bool using single JOIN query
- `UpdateMediaItemAsync(MediaItem item, CancellationToken ct)` - updates item including GraceStartedAt

**Boolean Columns vs Status Enum:**
- Check `saved` boolean column, NOT ItemStatus.Saved
- Check `blocked` boolean column, NOT ItemStatus.Blocked
- Set `status = ItemStatus.Deleted` on removal
- GraceStartedAt is separate timestamp for grace period tracking
