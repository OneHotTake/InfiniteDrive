# Sprint 116 — Collection Management (v3.3 BoxSet API)

**Version:** v3.3 | **Status:** Complete ✓ | **Risk:** LOW | **Depends:** Sprint 115

---

## Overview

Sprint 116 implements collection management using Emby ICollectionManager API. Sources with `ShowAsCollection = true` are automatically synced to Emby BoxSets.

**Key Components:**
- BoxSetRepository - Persist BoxSet metadata
- BoxSetService - Manage BoxSets via Emby ICollectionManager API
- CollectionSyncService - Sync sources to BoxSets
- CollectionTask - Scheduled sync task

---

## Phase 116A — BoxSetRepository

### FIX-116A-01: Create BoxSetRepository

**File:** `Repositories/BoxSetRepository.cs`

```csharp
public class BoxSetRepository : IBoxSetRepository
{
    private readonly IDatabaseManager _db;

    public async Task<List<Collection>> GetAllCollectionsAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM collections
            ORDER BY name";

        return await _db.QueryAsync<Collection>(sql, ct);
    }

    public async Task<Collection?> GetCollectionBySourceIdAsync(
        string sourceId,
        CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM collections
            WHERE source_id = @SourceId";

        return await _db.QueryFirstOrDefaultAsync<Collection>(
            sql, new { SourceId = sourceId }, ct);
    }

    public async Task UpsertCollectionAsync(Collection collection, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO collections (source_id, name, emby_collection_id, collection_name, last_synced_at)
            VALUES (@SourceId, @Name, @EmbyCollectionId, @CollectionName, @LastSyncedAt)
            ON CONFLICT(source_id) DO UPDATE SET
                name = excluded.name,
                emby_collection_id = excluded.emby_collection_id,
                collection_name = excluded.collection_name,
                last_synced_at = excluded.last_synced_at";

        await _db.ExecuteAsync(sql, collection, ct);
    }

    public async Task DeleteCollectionAsync(string sourceId, CancellationToken ct = default)
    {
        const string sql = @"
            DELETE FROM collections
            WHERE source_id = @SourceId";

        await _db.ExecuteAsync(sql, new { SourceId = sourceId }, ct);
    }
}
```

**Acceptance Criteria:**
- [ ] Gets all collections
- [ ] Gets collection by source ID
- [ ] Upserts collection (insert or update)
- [ ] Deletes collection
- [ ] All source IDs are string TEXT UUIDs
- [ ] Uses emby_collection_id (NOT emby_boxset_id)
- [ ] Does NOT include item_count column (not in spec)

---

## Phase 116B — BoxSetService

### FIX-116B-01: Create BoxSetService

**File:** `Services/BoxSetService.cs`

```csharp
public class BoxSetService
{
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger _logger;

    public async Task<BoxSet?> FindOrCreateBoxSetAsync(
        string name,
        CancellationToken ct = default)
    {
        // Query for existing BoxSet using ICollectionManager
        var existing = await _collectionManager.GetCollectionsAsync(
            new CollectionQuery
            {
                IncludeItemTypes = new[] { BaseItemType.BoxSet },
                Name = name,
                Recursive = false
            }).FirstOrDefault(ct) as BoxSet;

        if (existing != null)
        {
            _logger.Debug("Found existing BoxSet: {BoxSetId}", existing.Id);
            return existing;
        }

        // Create new BoxSet using ICollectionManager
        _logger.Info("Creating new BoxSet: {Name}", name);
        return await CreateBoxSetAsync(name, ct);
    }

    public async Task<BoxSet> CreateBoxSetAsync(
        string name,
        CancellationToken ct = default)
    {
        // Emby SDK: Create collection via ICollectionManager
        // CRITICAL: IsLocked must be FALSE to allow AddToCollection/RemoveFromCollection
        var boxSet = await _collectionManager.CreateCollectionAsync(
            new CollectionCreationOptions
            {
                Name = name,
                IsLocked = false, // MUST be false to allow collection modifications
                ItemIdList = Array.Empty<Guid>()
            },
            ct);

        _logger.Info("Created BoxSet: {BoxSetId} - {Name}", boxSet.Id, name);

        return boxSet;
    }

    public async Task AddItemToBoxSetAsync(
        Guid boxSetId,
        Guid itemId,
        CancellationToken ct = default)
    {
        var boxSet = await _collectionManager.GetCollectionsAsync(
            new CollectionQuery
            {
                IncludeItemTypes = new[] { BaseItemType.BoxSet },
                CollectionIds = new[] { boxSetId },
                Recursive = false
            }).FirstOrDefault(ct) as BoxSet;

        if (boxSet == null)
        {
            _logger.Warn("BoxSet not found: {BoxSetId}", boxSetId);
            return;
        }

        // Add to collection using ICollectionManager
        await _collectionManager.AddToCollectionAsync(boxSetId, itemId, ct);
        _logger.Debug("Added item {ItemId} to BoxSet {BoxSetId}", itemId, boxSetId);
    }

    public async Task RemoveItemFromBoxSetAsync(
        Guid boxSetId,
        Guid itemId,
        CancellationToken ct = default)
    {
        var boxSet = await _collectionManager.GetCollectionsAsync(
            new CollectionQuery
            {
                IncludeItemTypes = new[] { BaseItemType.BoxSet },
                CollectionIds = new[] { boxSetId },
                Recursive = false
            }).FirstOrDefault(ct) as BoxSet;

        if (boxSet == null)
        {
            _logger.Warn("BoxSet not found: {BoxSetId}", boxSetId);
            return;
        }

        // Remove from collection using ICollectionManager
        // CRITICAL: RemoveFromCollectionAsync requires BoxSet cast, not BaseItem
        await _collectionManager.RemoveFromCollectionAsync(boxSet, itemId, ct);
        _logger.Debug("Removed item {ItemId} from BoxSet {BoxSetId}", itemId, boxSetId);
    }

    public async Task EmptyBoxSetAsync(
        Guid boxSetId,
        CancellationToken ct = default)
    {
        var boxSet = await _collectionManager.GetCollectionsAsync(
            new CollectionQuery
            {
                IncludeItemTypes = new[] { BaseItemType.BoxSet },
                CollectionIds = new[] { boxSetId },
                Recursive = false
            }).FirstOrDefault(ct) as BoxSet;

        if (boxSet == null)
        {
            _logger.Warn("BoxSet not found: {BoxSetId}", boxSetId);
            return;
        }

        // Get current members
        var currentMembers = await _collectionManager.GetCollectionsAsync(
            new CollectionQuery
            {
                IncludeItemTypes = new[] { BaseItemType.BoxSet },
                CollectionIds = new[] { boxSetId },
                Recursive = true
            }).Select(i => i.InternalId).ToListAsync(ct);

        if (currentMembers.Count > 0)
        {
            // Remove all members using ICollectionManager
            await _collectionManager.RemoveFromCollectionAsync(boxSet, currentMembers, ct);
            _logger.Info("Emptied BoxSet {BoxSetId}: removed {Count} items", boxSetId, currentMembers.Count);
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Finds existing BoxSet by name via ICollectionManager
- [ ] Creates new BoxSet via ICollectionManager
- [ ] IsLocked = false (allows AddToCollection/RemoveFromCollection)
- [ ] Adds item to BoxSet via ICollectionManager
- [ ] Removes item from BoxSet via ICollectionManager with BoxSet cast
- [ ] Empties BoxSet (removes all members)
- [ ] All IDs are Guid (Emby Guids)
- [ ] Uses Emby ILogger

---

## Phase 116C — CollectionSyncService

### FIX-116C-01: Create CollectionSyncService

**File:** `Services/CollectionSyncService.cs`

```csharp
public class CollectionSyncService
{
    private readonly BoxSetRepository _repo;
    private readonly BoxSetService _service;
    private readonly ILogger _logger;

    public async Task<CollectionSyncResult> SyncCollectionsAsync(CancellationToken ct = default)
    {
        // Get sources with ShowAsCollection = true
        var collectionSources = await _db.GetSourcesWithShowAsCollectionAsync(ct);
        _logger.Info("Found {Count} sources with ShowAsCollection", collectionSources.Count);

        var results = new List<CollectionResult>();

        foreach (var source in collectionSources)
        {
            var result = await SyncSourceCollectionAsync(source, ct);
            results.Add(result);
        }

        // Prune orphaned collections (source no longer has ShowAsCollection)
        // CRITICAL: Empty BoxSet, do NOT delete it
        await EmptyOrphanedCollectionsAsync(collectionSources, ct);

        return new CollectionSyncResult
        {
            TotalProcessed = results.Count,
            SuccessCount = results.Count(r => r.Success),
            Results = results
        };
    }

    private async Task<CollectionResult> SyncSourceCollectionAsync(
        Source source,
        CancellationToken ct)
    {
        try
        {
            // Find or create BoxSet
            var boxSet = await _service.FindOrCreateBoxSetAsync(source.Name, ct);
            if (boxSet == null)
            {
                return CollectionResult.Failure(source.Name, "Failed to create/find BoxSet");
            }

            // Get items in this source
            var items = await _db.GetItemsBySourceAsync(source.Id, ct);

            // Sync items to BoxSet
            var syncedCount = 0;
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.EmbyItemId))
                {
                    var embyItemId = Guid.Parse(item.EmbyItemId);
                    await _service.AddItemToBoxSetAsync(boxSet.Id, embyItemId, ct);
                    syncedCount++;
                }
            }

            // Update collection metadata
            var collection = new Collection
            {
                SourceId = source.Id,
                Name = source.Name,
                EmbyCollectionId = boxSet.Id.ToString(),
                CollectionName = source.Name, // Override name from source
                LastSyncedAt = DateTimeOffset.UtcNow
            };

            await _repo.UpsertCollectionAsync(collection, ct);

            _logger.Info("Synced collection '{Name}': {Count} items", source.Name, syncedCount);

            return CollectionResult.Success(source.Name, syncedCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to sync collection for source {SourceName}", source.Name);
            return CollectionResult.Failure(source.Name, ex.Message);
        }
    }

    private async Task EmptyOrphanedCollectionsAsync(
        List<Source> activeSources,
        CancellationToken ct)
    {
        var activeSourceIds = activeSources.Select(s => s.Id).ToHashSet();
        var allCollections = await _repo.GetAllCollectionsAsync(ct);

        foreach (var collection in allCollections)
        {
            if (!activeSourceIds.Contains(collection.SourceId))
            {
                _logger.Info("Emptying orphaned collection: {Name}", collection.Name);

                // CRITICAL: Empty BoxSet, do NOT delete it
                // This preserves the BoxSet structure for manual user edits
                var boxSetId = Guid.Parse(collection.EmbyCollectionId);
                await _service.EmptyBoxSetAsync(boxSetId, ct);

                // Delete collection metadata (not the BoxSet itself)
                await _repo.DeleteCollectionAsync(collection.SourceId, ct);
            }
        }
    }
}

public record CollectionResult(
    string SourceName,
    bool Success,
    int ItemCount,
    string? Message = null
)
{
    public static CollectionResult Success(string sourceName, int count) =>
        new(sourceName, true, count);

    public static CollectionResult Failure(string sourceName, string message) =>
        new(sourceName, false, 0, message);
}

public record CollectionSyncResult(
    int TotalProcessed,
    int SuccessCount,
    List<CollectionResult> Results
);
```

**Acceptance Criteria:**
- [ ] Syncs all ShowAsCollection sources
- [ ] Creates BoxSets as needed via ICollectionManager
- [ ] Adds items to BoxSets via ICollectionManager
- [ ] Empties orphaned BoxSets (does NOT delete them)
- [ ] Updates collection metadata
- [ ] All source IDs are string TEXT UUIDs
- [ ] Uses Emby ILogger

---

## Phase 116D — CollectionTask

### FIX-116D-01: Create CollectionTask

**File:** `Tasks/CollectionTask.cs`

```csharp
public class CollectionTask : IScheduledTask
{
    private readonly CollectionSyncService _service;
    private readonly ILogger _logger;

    public string Name => "EmbyStreams Collection Sync";
    public string Key => "embystreams_collections";
    public string Description => "Syncs sources with ShowAsCollection to Emby BoxSets";
    public string Category => "EmbyStreams";

    public async Task ExecuteAsync(CancellationToken ct, IProgress<double> progress)
    {
        await Plugin.SyncLock.WaitAsync(ct);
        try
        {
            progress?.Report(0);

            _logger.Info("Starting collection sync...");

            var result = await _service.SyncCollectionsAsync(ct);
            progress?.Report(100);

            _logger.Info(
                "Collection sync complete: {Success}/{Total} collections synced",
                result.SuccessCount, result.TotalProcessed);
        }
        finally
        {
            Plugin.SyncLock.Release();
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Syncs collections
- [ ] Uses SyncLock
- [ ] Reports progress
- [ ] Logs summary
- [ ] Uses Emby ILogger

---

## Sprint 116 Dependencies

- **Previous Sprint:** 115 (Removal Pipeline)
- **Blocked By:** Sprint 115
- **Blocks:** Sprint 117 (Admin UI)

---

## Sprint 116 Completion Criteria

- [ ] BoxSetRepository persists BoxSet metadata
- [ ] BoxSetRepository uses emby_collection_id (not emby_boxset_id)
- [ ] BoxSetRepository does NOT use item_count column
- [ ] BoxSetService manages BoxSets via Emby ICollectionManager API
- [ ] BoxSetService.IsLocked = false (allows collection modifications)
- [ ] BoxSetService.RemoveFromCollectionAsync uses BoxSet cast
- [ ] CollectionSyncService syncs sources to BoxSets
- [ ] CollectionSyncService empties orphaned BoxSets (does NOT delete)
- [ ] CollectionTask runs periodic sync
- [ ] All IDs are string TEXT UUIDs (Source) or Guid (Emby)
- [ ] Build succeeds
- [ ] E2E: BoxSets created and synced correctly

---

## Sprint 116 Notes

**BoxSet Management:**
- ShowAsCollection = true → create BoxSet
- BoxSet name = Source.CollectionName (from source, can be overridden)
- BoxSet items = items in source with EmbyItemId
- BoxSet.IsLocked = false (CRITICAL: allows AddToCollection/RemoveFromCollection)

**Emby ICollectionManager API (CRITICAL):**

BoxSetService MUST use `ICollectionManager` interface, NOT `ILibraryManager`:

```csharp
// Create collection
await _collectionManager.CreateCollectionAsync(
    new CollectionCreationOptions
    {
        Name = name,
        IsLocked = false, // MUST be false
        ItemIdList = Array.Empty<Guid>()
    },
    ct);

// Add item to collection
await _collectionManager.AddToCollectionAsync(boxSetId, itemId, ct);

// Remove item from collection
await _collectionManager.RemoveFromCollectionAsync(boxSet, itemId, ct);

// Remove multiple items from collection
await _collectionManager.RemoveFromCollectionAsync(boxSet, itemIds, ct);
```

**BoxSet Cast Pattern (CRITICAL):**

RemoveFromCollectionAsync requires BoxSet cast, not BaseItem:

```csharp
// WRONG:
await _collectionManager.RemoveFromCollectionAsync(baseItem, itemId, ct);

// CORRECT:
await _collectionManager.RemoveFromCollectionAsync(boxSet, itemId, ct);
// where boxSet is retrieved as BoxSet from GetCollectionsAsync()
```

This is a common error source when refactoring from older SDK versions.

**Database Schema (collections table):**

```sql
CREATE TABLE collections (
    id TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    source_id TEXT NOT NULL,
    name TEXT NOT NULL,
    emby_collection_id TEXT,  -- NOT emby_boxset_id
    collection_name TEXT,  -- Override name from source
    last_synced_at TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);
```

- Uses `emby_collection_id` (NOT `emby_boxset_id`)
- Does NOT include `item_count` column (not in spec)
- Has `collection_name` for override name from source
- All IDs are TEXT UUIDs

**Orphan Pruning (CRITICAL):**

When a source no longer has ShowAsCollection = true:
- Empty the BoxSet (remove all members)
- Do NOT delete the BoxSet itself
- This preserves the BoxSet structure for manual user edits
- Delete only the collection metadata record

This is different from Sprint 113's SavedBoxSetService which creates a "Saved" BoxSet for user-saved items.

**Dependency Injection Notes:**
- BoxSetService depends on ICollectionManager
- CollectionSyncService depends on BoxSetRepository and BoxSetService
- CollectionTask depends on CollectionSyncService
- All dependencies should be constructor-injected via DI container

**Task Scheduling:**
- Run every 1 hour
- Uses SyncLock to avoid conflicts with sync
- Syncs all ShowAsCollection sources
