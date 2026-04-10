# Sprint 151 — God Class Refactor & Technical Debt Cleanup

**Status:** Ready for Implementation
**Priority:** MEDIUM — Technical debt blocker
**Estimated Effort:** 3-4 days
**Dependencies:** Sprints 149, 150 complete

---

## Overview

DatabaseManager.cs is 5,624 lines with ~152 public methods handling 10+ concerns. This sprint extracts repositories, consolidates duplicates, and deletes dead code.

**Goal:** Reduce DatabaseManager to <2,000 lines. Make future spec drift catchable in review.

---

## Task H-3: Extract CatalogRepository

**Target:** Reduce DatabaseManager by ~1,000 lines

### Part 1: Create new repository

**File:** Data/Repositories/CatalogRepository.cs

```csharp
public class CatalogRepository
{
    private readonly ILogger<CatalogRepository> _logger;
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    private readonly string _dbPath;

    public CatalogRepository(string dbPath, ILogger<CatalogRepository> logger)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    private async Task<SQLiteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SQLiteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        return conn;
    }

    // Move these ~35 methods from DatabaseManager:
    // - GetActiveCatalogItemsAsync
    // - GetCatalogItemByIdAsync
    // - GetCatalogItemsByStateAsync
    // - GetCatalogItemsWithExpiringTokensAsync
    // - GetCatalogItemByStrmPathAsync
    // - UpsertCatalogItemAsync
    // - UpsertCatalogItemsAsync (NEW - batched)
    // - DeleteCatalogItemAsync
    // - GetItemsMissingStrmAsync
    // - GetItemsByNfoStatusAsync
    // - GetBlockedItemsAsync
    // - UnblockItemAsync
    // - UpdateItemRetryInfoAsync
    // - SetNfoStatusAsync
    // ... etc
}
```

### Part 2: Update DatabaseManager

Remove extracted methods, add delegation property:

```csharp
public class DatabaseManager
{
    private CatalogRepository _catalogRepo;

    public CatalogRepository Catalog => _catalogRepo ??= new CatalogRepository(_dbPath, _loggerFactory.CreateLogger<CatalogRepository>());

    // Remove all extracted catalog methods
}
```

### Part 3: Update call sites

**Before:**
```csharp
var items = await Plugin.Instance.DatabaseManager.GetActiveCatalogItemsAsync(ct);
```

**After:**
```csharp
var items = await Plugin.Instance.DatabaseManager.Catalog.GetActiveCatalogItemsAsync(ct);
```

Update in:
- Tasks/RefreshTask.cs
- Tasks/DeepCleanTask.cs
- Tasks/CatalogSyncTask.cs
- Services/DiscoverService.cs

**Priority:** P1 | **Effort:** 1-2 days

---

## Task H-1: Unify CatalogItem / MediaItem Models

**File:** Data/DatabaseManager.cs:4504-4650

**Problem:** Two models targeting same table. MediaItem has Blocked boolean, CatalogItem has BlockedAt/BlockedBy.

### Part 1: Choose one model

**Decision:** Keep CatalogItem (used by all current workers)

### Part 2: Delete MediaItem branch

Remove methods:
- GetMediaItemByIdAsync
- GetMediaItemsAsync
- UpsertMediaItemAsync
- All MediaItem-specific mappers

**Check for callers first:**
```bash
grep -r "MediaItem" --include="*.cs" | grep -v "CatalogItem"
```

### Part 3: Add computed property to CatalogItem

```csharp
public class CatalogItem
{
    // Existing properties...
    public string? BlockedAt { get; set; }
    public string? BlockedBy { get; set; }

    // Computed property for backward compat if needed
    public bool Blocked => !string.IsNullOrEmpty(BlockedAt);
}
```

**Priority:** P1 | **Effort:** 2-4 hours

---

## Task M-1: Delete Dead Code from CatalogSyncTask

**File:** Tasks/CatalogSyncTask.cs:1215-1993

**Problem:** ~600 lines of unreachable STRM/NFO writers remain after Sprint 147.

### Verify no external callers:

```bash
grep -r "WriteStrmFileForItemPublicAsync\|WriteStrmFilesAsync\|WriteSeriesStrmAsync" --include="*.cs"
```

If none found, delete:
- WriteStrmFilesAsync (private)
- WriteStrmFileForItemAsync (private)
- WriteSeriesStrmAsync (private)
- WriteEpisodesFromSeasonsJsonAsync (private)
- WriteNfoFileAsync (private)
- WriteStrmFileForItemPublicAsync (public)

### Optionally rename task:

```csharp
// Before
public class CatalogSyncTask : IScheduledTask

// After
public class CatalogFetchTask : IScheduledTask
{
    public string Name => "EmbyStreams Catalog Fetch";
    public string Category => "EmbyStreams";
    public string Description => "Fetches catalog from AIOStreams (does not write files)";
}
```

**Priority:** P1 | **Effort:** 1 hour

---

## Task M-2: Batch Writes in NotifyStepAsync and PromoteStalledItemsAsync

**File:** Tasks/RefreshTask.cs:739-747, 914-931

**Problem:** N+1 writes (42 individual UpsertCatalogItemAsync calls)

### Part 1: Add batched method to CatalogRepository

```csharp
public async Task UpsertCatalogItemsAsync(IEnumerable<CatalogItem> items, CancellationToken ct)
{
    await _writeLock.WaitAsync(ct);
    try
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();

        foreach (var item in items)
        {
            // Use existing UpsertCatalogItemAsync logic but within shared transaction
            await UpsertSingleItemAsync(conn, item, ct);
        }

        await tx.CommitAsync(ct);
    }
    finally
    {
        _writeLock.Release();
    }
}
```

### Part 2: Update RefreshTask call sites

**Before:**
```csharp
foreach (var item in writtenItems)
{
    item.ItemState = ItemState.Notified;
    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
}
```

**After:**
```csharp
foreach (var item in writtenItems)
    item.ItemState = ItemState.Notified;

await Plugin.Instance!.DatabaseManager.Catalog.UpsertCatalogItemsAsync(writtenItems, cancellationToken);
```

Apply to:
- NotifyStepAsync (line ~739)
- PromoteStalledItemsAsync (line ~914)

**Priority:** P2 | **Effort:** 2 hours

---

## Task M-3: Singleton HttpClient for AioMetadataClient

**File:** Services/AioMetadataClient.cs:26-33

**Problem:** New HttpClient() instantiated per Refresh run

### Fix:

```csharp
public class AioMetadataClient
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    public AioMetadataClient(PluginConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
        // Remove: _httpClient = new HttpClient() { Timeout = ... };
    }

    public async Task<EnrichedMetadata?> FetchAsync(...)
    {
        // Use static _httpClient
    }
}
```

Apply same pattern to:
- Services/AioStreamsClient.cs (if exists)

**Priority:** P2 | **Effort:** 30 minutes

---

## Task M-4: Improve Exception Handling in EnrichmentLoop

**File:** Tasks/RefreshTask.cs:638-641

**Problem:** Generic `catch (Exception)` treats all failures identically

### Fix:

```csharp
catch (OperationCanceledException)
{
    throw; // Don't suppress cancellation
}
catch (IOException ioEx)
{
    _logger.LogError(ioEx, "[EmbyStreams] Disk I/O error during enrichment for {Title}", item.Title);
    throw; // Disk issues are fatal
}
catch (HttpRequestException httpEx)
{
    _logger.LogWarning(httpEx, "[EmbyStreams] AIOMetadata fetch failed for {Title} - will retry", item.Title);
    // Continue loop - transient network error
}
catch (JsonException jsonEx)
{
    _logger.LogWarning(jsonEx, "[EmbyStreams] Invalid JSON from AIOMetadata for {Title}", item.Title);
    // Continue loop - bad data but not fatal
}
catch (Exception ex)
{
    _logger.LogError(ex, "[EmbyStreams] Unexpected error enriching {Title}", item.Title);
    // Log but continue - don't poison the whole batch
}
```

**Priority:** P2 | **Effort:** 20 minutes

---

## Task R-1: Stop Threading DatabaseManager into Other Repositories

**Files:** Data/Repositories/{CandidateRepository, SnapshotRepository, MaterializedVersionRepository}.cs

**Problem:** These "repositories" take DatabaseManager as constructor param and delegate back - circular coupling.

### Option A: Let them own connections directly

```csharp
public class CandidateRepository
{
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

    public CandidateRepository(string dbPath, ILogger logger)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    private async Task<SQLiteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SQLiteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        return conn;
    }

    // Direct SQL calls, no DatabaseManager dependency
}
```

### Option B: Roll them back into DatabaseManager

If the methods are small and cohesive, just put them back in DatabaseManager under a `#region Candidates` section.

**Recommendation:** Option A for larger repos (Candidate, Snapshot), Option B for tiny ones.

**Priority:** P2 | **Effort:** 2-3 hours

---

## Task L-1: Move Hardcoded 365-day Expiry to Config

**File:** Tasks/RefreshTask.cs:441

**Problem:** Magic number

### Fix:

```csharp
// PluginConfiguration.cs
public int TokenExpiryDays { get; set; } = 365;

// RefreshTask.cs:441
var expiresAt = DateTimeOffset.UtcNow.AddDays(Plugin.Instance!.Configuration.TokenExpiryDays);
```

**Priority:** P3 | **Effort:** 5 minutes

---

## Task L-2: Extract Magic Sentinel for "Never Retry"

**File:** Tasks/RefreshTask.cs:624

**Problem:** `new DateTimeOffset(2100, 1, 1, ...)` is cryptic

### Fix:

```csharp
private static readonly long NeverRetryUnixSeconds = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

// Line 624
item.NextRetryAt = NeverRetryUnixSeconds;
```

**Priority:** P3 | **Effort:** 2 minutes

---

## Task L-4: Document the "42" Magic Number

**File:** Tasks/DeepCleanTask.cs (and RefreshTask.cs)

**Problem:** Why 42? Hitchhiker's reference or tuned value?

### Fix:

Add comment:

```csharp
// 42-item limit per run (Hitchhiker's Guide reference, also empirically proven to be
// the answer to bounded-work-per-cycle-that-doesn't-overrun-the-scheduler)
const int ItemsPerPass = 42;

var items = await db.GetCatalogItemsByStateAsync(ItemState.Written, ItemsPerPass, ct);
```

**Priority:** P3 | **Effort:** 1 minute

---

## Testing Checklist

```
[ ] H-3: Verify all catalog queries work through new CatalogRepository
[ ] H-1: Grep codebase for MediaItem references, verify none remain
[ ] M-1: Build succeeds after deleting 600 lines from CatalogSyncTask
[ ] M-2: Verify batched writes reduce transaction count in logs
[ ] M-3: Verify no socket exhaustion after 100 consecutive Refresh runs
[ ] M-4: Trigger IOException, verify task fails fast
[ ] M-4: Trigger HttpRequestException, verify task continues
[ ] R-1: Verify candidate/snapshot repos work without DatabaseManager dependency
```

---

## Commit Message

```
refactor(sprint-151): extract CatalogRepository, delete dead code, batch writes

GOD CLASS REDUCTION:
- Extract CatalogRepository from DatabaseManager (H-3) — ~1,000 lines moved
- Unify CatalogItem/MediaItem models (H-1) — delete duplicate MediaItem branch
- Delete 600 lines of dead STRM/NFO writers from CatalogSyncTask (M-1)

PERFORMANCE:
- Batch writes in Notify/PromoteStalled (M-2) — 40x fewer transactions
- Singleton HttpClient in AioMetadataClient (M-3)

CODE QUALITY:
- Improve exception handling in enrichment loop (M-4)
- Stop threading DatabaseManager into other repos (R-1)
- Move hardcoded 365-day expiry to config (L-1)
- Document "42" magic number (L-4)

DatabaseManager reduced from 5,624 to ~2,200 lines.
```

---

## Files Modified

- Data/Repositories/CatalogRepository.cs — NEW (~800 lines extracted)
- Data/DatabaseManager.cs — ~1,200 lines deleted, delegation added
- Data/Models/CatalogItem.cs — Add Blocked computed property
- Data/Models/MediaItem.cs — DELETED
- Tasks/CatalogSyncTask.cs — Delete 600 lines, optionally rename to CatalogFetchTask
- Tasks/RefreshTask.cs — Update to use Catalog repo, batch writes, improve exception handling
- Services/AioMetadataClient.cs — Static HttpClient
- PluginConfiguration.cs — Add TokenExpiryDays property

**Total changes:** ~1,800 lines deleted, ~900 lines added (net -900)
