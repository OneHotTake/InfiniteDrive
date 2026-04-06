# Sprint 119 — API Endpoints (v3.3)

**Version:** v3.3 | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 118

---

## Overview

Sprint 119 implements comprehensive API endpoints for v3.3. These endpoints support the Admin UI, home screen rails, and external integrations.

**Key Components:**
- StatusController - Plugin status
- SourcesController - Source management
- CollectionsController - Collection management
- ItemsController - Item queries
- ActionsController - Manual actions
- LogsController - Log retrieval

---

## Phase 119A — StatusController

### FIX-119A-01: Create StatusController

**File:** `Controllers/StatusController.cs`

```csharp
[Route("embystreams/status")]
public class StatusController
{
    public StatusResponse Get()
    {
        return new StatusResponse
        {
            Version = Plugin.Instance.PluginVersion,
            SchemaVersion = Schema.CurrentSchemaVersion,
            // CRITICAL: Removed ManifestStatus and ManifestFetchedAt (v20 concepts)
            LastSyncAt = GetLastSyncTime(),
            DatabasePath = GetDatabasePath(),
            PluginStatus = "ok"
        };
    }
}

public record StatusResponse(
    string Version,
    int SchemaVersion,
    DateTimeOffset? LastSyncAt,
    string DatabasePath,
    string PluginStatus
);
```

**Acceptance Criteria:**
- [ ] Returns version
- [ ] Returns schema version
- [ ] Returns last sync time
- [ ] Returns database path
- [ ] Returns plugin status
- [ ] Does NOT include ManifestStatus (v20 concept removed)
- [ ] Does NOT include ManifestFetchedAt (v20 concept removed)

---

## Phase 119B — SourcesController

### FIX-119B-01: Create SourcesController

**File:** `Controllers/SourcesController.cs`

```csharp
[Route("embystreams/sources")]
public class SourcesController
{
    private readonly SourcesService _service;

    [Route("")]
    public async Task<List<Source>> Get(CancellationToken ct)
    {
        return await _service.GetSourcesAsync(ct);
    }

    [Route("")]
    public async Task<Source> Post(CreateSourceRequest request, CancellationToken ct)
    {
        // CRITICAL: Restrict POST to Trakt/MdbList only
        // AIO and BuiltIn sources are managed internally
        if (request.Type == SourceType.Aio || request.Type == SourceType.BuiltIn)
        {
            throw new BadRequestException(
                $"Cannot create {request.Type} sources. Only Trakt and MdbList sources can be manually added.");
        }

        var source = new Source
        {
            Name = request.Name,
            Url = request.Url,
            Type = request.Type,
            Enabled = true,
            ShowAsCollection = false
        };

        return await _service.CreateSourceAsync(source, ct);
    }

    [Route("{id}")]
    public async Task<Source> Get(string id, CancellationToken ct)
    {
        return await _service.GetSourceAsync(id, ct);
    }

    [Route("{id}")]
    public async Task Delete(string id, CancellationToken ct)
    {
        await _service.DeleteSourceAsync(id, ct);
    }

    [Route("{id}/enable")]
    public async Task Post(string id, CancellationToken ct)
    {
        await _service.EnableSourceAsync(id, ct);
    }

    [Route("{id}/disable")]
    public async Task Post(string id, CancellationToken ct)
    {
        await _service.DisableSourceAsync(id, ct);
    }

    [Route("{id}/show-as-collection")]
    public async Task Post(string id, ShowAsCollectionRequest request, CancellationToken ct)
    {
        await _service.ToggleShowAsCollectionAsync(id, request.Show, ct);
    }
}

public record CreateSourceRequest(string Name, string Url, SourceType Type);
public record ShowAsCollectionRequest(bool Show);
```

**Acceptance Criteria:**
- [ ] GET / lists sources
- [ ] POST / creates source (Trakt/MdbList only, not AIO/BuiltIn)
- [ ] GET /{id} gets source
- [ ] DELETE /{id} deletes source
- [ ] POST /{id}/enable enables source
- [ ] POST /{id}/disable disables source
- [ ] POST /{id}/show-as-collection toggles collection flag
- [ ] All source IDs are string TEXT UUIDs

---

## Phase 119C — CollectionsController

### FIX-119C-01: Create CollectionsController

**File:** `Controllers/CollectionsController.cs`

```csharp
[Route("embystreams/collections")]
public class CollectionsController
{
    private readonly CollectionsService _service;

    [Route("")]
    public async Task<List<Collection>> Get(CancellationToken ct)
    {
        return await _service.GetCollectionsAsync(ct);
    }

    [Route("{sourceId}/sync")]
    public async Task<CollectionSyncResult> Post(string sourceId, CancellationToken ct)
    {
        return await _service.SyncCollectionAsync(sourceId, ct);
    }
}
```

**Acceptance Criteria:**
- [ ] GET / lists collections
- [ ] POST /{sourceId}/sync syncs collection
- [ ] All source IDs are string TEXT UUIDs

---

## Phase 119D — ItemsController

### FIX-119D-01: Create ItemsController

**File:** `Controllers/ItemsController.cs`

```csharp
[Route("embystreams/items")]
public class ItemsController
{
    private readonly IDatabaseManager _db;

    [Route("")]
    public async Task<ItemListResponse> Get(ItemListRequest request, CancellationToken ct)
    {
        var items = await _db.GetItemsAsync(
            request.Status,
            request.OrderBy,
            request.OrderDirection,
            request.Limit,
            request.Offset,
            ct);

        var total = await _db.GetItemCountAsync(request.Status, ct);

        return new ItemListResponse
        {
            Items = items,
            Total = total,
            Limit = request.Limit,
            Offset = request.Offset
        };
    }

    [Route("{id}")]
    public async Task<MediaItem?> Get(string id, CancellationToken ct)
    {
        // CRITICAL: MediaItem.Id is TEXT UUID, not int
        return await _db.GetMediaItemAsync(id, ct);
    }

    [Route("search")]
    public async Task<List<MediaItem>> Post(SearchRequest request, CancellationToken ct)
    {
        return await _db.SearchItemsAsync(request.Query, ct);
    }
}

public record ItemListRequest(
    ItemStatus? Status = null,
    string OrderBy = "title",
    string OrderDirection = "asc",
    int Limit = 50,
    int Offset = 0
);

public record ItemListResponse(
    List<MediaItem> Items,
    int Total,
    int Limit,
    int Offset
);

public record SearchRequest(string Query);
```

**Acceptance Criteria:**
- [ ] GET / lists items with pagination
- [ ] GET /{id} gets item
- [ ] POST /search searches items
- [ ] MediaItem.Id is string TEXT UUID (not int)
- [ ] Pagination limit/offset respected

---

## Phase 119E — ActionsController

### FIX-119E-01: Create ActionsController

**File:** `Controllers/ActionsController.cs`

```csharp
[Route("embystreams/actions")]
public class ActionsController
{
    private readonly SyncTask _syncTask;
    private readonly YourFilesTask _yourFilesTask;
    private readonly RemovalTask _removalTask;
    private readonly CollectionTask _collectionTask;
    private readonly StreamCache _cache;

    [Route("sync")]
    public async Task<ActionResult> Post(CancellationToken ct)
    {
        await _syncTask.ExecuteAsync(ct, null);
        return ActionResult.Success("Sync complete");
    }

    [Route("yourfiles")]
    public async Task<ActionResult> Post(CancellationToken ct)
    {
        await _yourFilesTask.ExecuteAsync(ct, null);
        return ActionResult.Success("Your Files reconciliation complete");
    }

    [Route("cleanup")]
    public async Task<ActionResult> Post(CancellationToken ct)
    {
        await _removalTask.ExecuteAsync(ct, null);
        return ActionResult.Success("Cleanup complete");
    }

    [Route("collections")]
    public async Task<ActionResult> Post(CancellationToken ct)
    {
        await _collectionTask.ExecuteAsync(ct, null);
        return ActionResult.Success("Collections synced");
    }

    [Route("purge-cache")]
    public async Task<ActionResult> Post(CancellationToken ct)
    {
        await _cache.PurgeExpiredAsync(ct);
        return ActionResult.Success("Cache purged");
    }

    [Route("reset")]
    [AdminGuard.RequireAdmin]
    public async Task<ActionResult> Post(CancellationToken ct)
    {
        // CRITICAL: Removed MigrationService (v20 concept, doesn't exist in v3.3)
        // Per v3.3 spec §17: No migration, fresh wipe only
        // Database reset is handled by DatabaseInitializer re-running

        // This endpoint should be disabled or replaced with proper v3.3 reset logic
        throw new NotImplementedException(
            "Database reset not available in v3.3. Use Danger Zone in Admin UI instead.");
    }
}
```

**Acceptance Criteria:**
- [ ] POST /sync triggers sync
- [ ] POST /yourfiles triggers Your Files reconciliation
- [ ] POST /cleanup triggers removal cleanup
- [ ] POST /collections triggers collection sync
- [ ] POST /purge-cache purges cache
- [ ] POST /reset is disabled (v3.3: no migration, fresh wipe only)
- [ ] Admin guard on reset endpoint
- [ ] Removed MigrationService reference (v20 concept)

---

## Phase 119F — LogsController

### FIX-119F-01: Create LogsController

**File:** `Controllers/LogsController.cs`

```csharp
[Route("embystreams/logs")]
public class LogsController
{
    private readonly IDatabaseManager _db;

    [Route("pipeline")]
    public async Task<List<PipelineLogEntry>> Get(PipelineLogsRequest request, CancellationToken ct)
    {
        return await _db.GetPipelineLogsAsync(
            request.PrimaryId,
            request.PrimaryIdType,
            request.MediaType,
            request.Trigger,
            request.Limit,
            ct);
    }

    [Route("resolution")]
    public async Task<List<ResolutionLogEntry>> Get(ResolutionLogsRequest request, CancellationToken ct)
    {
        return await _db.GetResolutionLogsAsync(
            request.PrimaryId,
            request.PrimaryIdType,
            request.MediaType,
            request.Limit,
            ct);
    }

    [Route("recent")]
    public async Task<List<RecentLogEntry>> Get(RecentLogsRequest request, CancellationToken ct)
    {
        return await _db.GetRecentLogsAsync(
            request.Level,
            request.Limit,
            ct);
    }
}

public record PipelineLogsRequest(
    string? PrimaryId = null,
    string? PrimaryIdType = null,
    string? MediaType = null,
    PipelineTrigger? Trigger = null,
    int Limit = 100);

public record ResolutionLogsRequest(
    string? PrimaryId = null,
    string? PrimaryIdType = null,
    string? MediaType = null,
    int Limit = 100);

public record RecentLogsRequest(string? Level = null, int Limit = 100);
```

**Acceptance Criteria:**
- [ ] GET /pipeline returns pipeline logs
- [ ] GET /resolution returns resolution logs
- [ ] GET /recent returns recent logs
- [ ] PipelineLogsRequest uses string? PrimaryId, string? PrimaryIdType, string? MediaType
- [ ] ResolutionLogsRequest uses string? PrimaryId, string? PrimaryIdType, string? MediaType
- [ ] No int? ItemId parameter exists anywhere in LogsController
- [ ] GetPipelineLogsAsync called with (primaryId, primaryIdType, mediaType, trigger, limit)
- [ ] GetResolutionLogsAsync called with (primaryId, primaryIdType, mediaType, limit)
- [ ] Uses correct column names from v3.3 spec:
  - item_pipeline_log: (primary_id, primary_id_type, media_type, phase, trigger, success, details, timestamp)
  - stream_resolution_log: (primary_id, primary_id_type, media_type, media_id, stream_count, selected_stream, duration_ms, timestamp)

---

## Sprint 119 Dependencies

- **Previous Sprint:** 118 (Home Screen Rails)
- **Blocked By:** Sprint 118
- **Blocks:** Sprint 120 (Logging)

---

## Sprint 119 Completion Criteria

- [ ] StatusController returns status (v3.3: no manifest fields)
- [ ] SourcesController manages sources (Trakt/MdbList only)
- [ ] CollectionsController manages collections
- [ ] ItemsController queries items
- [ ] ActionsController triggers actions
- [ ] LogsController returns logs
- [ ] All endpoints documented
- [ ] Build succeeds
- [ ] E2E: All endpoints work

---

## Sprint 119 Notes

**API Response Format:**
- JSON for all endpoints
- ISO 8601 for dates
- HTTP status codes: 200 (success), 400 (bad request), 404 (not found), 500 (error)

**ID Types (v3.3 Breaking Change):**
- Source.Id: **string** TEXT UUID (sources table uses TEXT UUID primary key)
- MediaItem.Id: **TEXT UUID** (media_items table uses TEXT UUID primary key)
- Collection.SourceId: **string** (foreign key to sources)
- EmbyItemId: **string** TEXT GUID (converted to Guid when accessing Emby API)
- All API endpoints use string IDs for MediaItem/Source

**Admin Guard:**
- Actions requiring admin privileges use AdminGuard.RequireAdmin
- Returns 403 for non-admin users

**Source Creation Restrictions (v3.3):**
- AIO sources: Managed internally (manifest URL), cannot be manually created
- BuiltIn sources: Managed internally, cannot be manually created
- Trakt/MdbList sources: Can be manually created via Admin UI
- This prevents duplicate/invalid source configurations

**Removed v20 Concepts:**
- ManifestStatus field removed from StatusResponse
- ManifestFetchedAt field removed from StatusResponse
- MigrationService removed from ActionsController (v3.3: no migration path)
- Reset database endpoint disabled (use fresh wipe initialization instead)

**Pagination:**
- Items list uses limit/offset
- Default limit: 50
- Maximum limit: 200

**Log Schema (v3.3 Spec §13):**
Both log tables are keyed on composite TEXT identifiers, not integer FKs:

item_pipeline_log columns:
  primary_id TEXT, primary_id_type TEXT, media_type TEXT,
  phase TEXT, trigger TEXT, success INTEGER, details TEXT, timestamp TEXT

stream_resolution_log columns:
  primary_id TEXT, primary_id_type TEXT, media_type TEXT,
  media_id TEXT, stream_count INTEGER, selected_stream TEXT,
  duration_ms INTEGER, timestamp TEXT

Query parameters for both log endpoints use string? PrimaryId, string? PrimaryIdType,
string? MediaType — never int? ItemId. There is no integer item_id column on either
log table.
