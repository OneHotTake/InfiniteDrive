# Sprint 151 — God Class Refactor & Technical Debt Cleanup

**Status:** Ready for Implementation
**Priority:** MEDIUM — Technical debt blocker
**Estimated Effort:** 3-4 days
**Dependencies:** Sprints 149, 150 complete

---

## Overview

DatabaseManager.cs is 5,624 lines with ~152 public methods handling 10+ concerns. This sprint extracts repositories, consolidates duplicates, and deletes dead code.

**Goal:** Reduce DatabaseManager to <2,000 lines. Make future spec drift catchable in review.

**MAINTENANCE.md Update:** After completion, add "Database access — CatalogRepository owns catalog_items methods (Sprint 151). Remaining concerns stay in DatabaseManager until future sprints."

---

## Task H-3: Extract CatalogRepository

**Target:** Reduce DatabaseManager by ~1,000 lines

**MAINTENANCE.md Reference:** Data integrity rules #5, #6 (blocked filtering, pin guards)

### Part 1: Create new repository

**File:** Data/Repositories/CatalogRepository.cs

```csharp
using System.Data.SQLite;
using MediaBrowser.Model.Logging;

namespace EmbyStreams.Data.Repositories
{
    public class CatalogRepository
    {
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly string _dbPath;

        public CatalogRepository(string dbPath, ILogger logger)
        {
            _dbPath = dbPath;
            _logger = logger;
        }

        private async Task<SQLiteConnection> OpenConnectionAsync(CancellationToken ct)
        {
            var conn = new SQLiteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync(ct);

            // Enable WAL mode on this connection
            await using var cmd = new SQLiteCommand("PRAGMA journal_mode=WAL;", conn);
            await cmd.ExecuteNonQueryAsync(ct);

            return conn;
        }

        // Move these ~35 methods from DatabaseManager:
        // - GetActiveCatalogItemsAsync (with blocked_at IS NULL filter!)
        // - GetCatalogItemByIdAsync
        // - GetCatalogItemsByStateAsync
        // - GetCatalogItemsWithExpiringTokensAsync
        // - GetCatalogItemByStrmPathAsync
        // - UpsertCatalogItemAsync
        // - UpsertCatalogItemsAsync (NEW - batched version)
        // - DeleteCatalogItemAsync
        // - GetItemsMissingStrmAsync
        // - GetItemsByNfoStatusAsync (with blocked_at IS NULL filter!)
        // - GetBlockedItemsAsync
        // - UnblockItemAsync
        // - UpdateItemRetryInfoAsync
        // - SetNfoStatusAsync
        // - GetCatalogItemsByIdsAsync (NEW - for My Picks join)
        // ... etc
    }
}
```

**Critical:** Each method that queries "active" items MUST include `AND blocked_at IS NULL` filter (Sprint 149 C-5 requirement).

### Part 2: Update DatabaseManager

Remove extracted methods, add delegation property:

```csharp
public class DatabaseManager
{
    private CatalogRepository _catalogRepo;

    public CatalogRepository Catalog => _catalogRepo ??= new CatalogRepository(_dbPath, _loggerFactory.CreateLogger<CatalogRepository>());

    // Remove all extracted catalog methods
    // Keep: schema migrations, metadata KV store, discover_catalog, stream candidates, etc.
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
- Services/AdminService.cs
- Services/UserService.cs

**Priority:** P1 | **Effort:** 1-2 days

---

## Task H-1: Unify CatalogItem / MediaItem Models

**File:** Data/DatabaseManager.cs:4504-4650

**MAINTENANCE.md Reference:** Data correctness (single source of truth for catalog_items table)

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
grep -r "MediaItem[^s]" --include="*.cs" | grep -v "CatalogItem" | grep -v "// " | grep -v "using"
```

If any live callers found, convert them to use CatalogItem first.

### Part 3: Add computed property to CatalogItem

```csharp
public class CatalogItem
{
    // Existing properties...
    public string? BlockedAt { get; set; }
    public string? BlockedBy { get; set; }

    // Computed — blocked_at IS NOT NULL is the canonical predicate
    [JsonIgnore]
    public bool Blocked => !string.IsNullOrEmpty(BlockedAt);
}
```

**Priority:** P1 | **Effort:** 2-4 hours

---

## Task M-1: Delete Dead Code from CatalogSyncTask

**File:** Tasks/CatalogSyncTask.cs:1215-1993

**MAINTENANCE.md Reference:** Sprint 147 — CatalogSyncTask no longer writes .strm files

**Problem:** ~600 lines of unreachable STRM/NFO writers remain after Sprint 147.

### Verify no external callers:

```bash
grep -r "WriteStrmFileForItemPublicAsync\|WriteStrmFilesAsync\|WriteSeriesStrmAsync\|WriteEpisodesFromSeasonsJsonAsync\|WriteNfoFileAsync" --include="*.cs"
```

If none found, delete methods:
- WriteStrmFilesAsync (private)
- WriteStrmFileForItemAsync (private)
- WriteSeriesStrmAsync (private)
- WriteEpisodesFromSeasonsJsonAsync (private)
- WriteNfoFileAsync (private)
- WriteStrmFileForItemPublicAsync (public)

### Optionally rename task for clarity:

```csharp
// Before
public class CatalogSyncTask : IScheduledTask
{
    public string Name => "EmbyStreams Catalog Sync";
}

// After (more accurate)
public class CatalogFetchTask : IScheduledTask
{
    public string Name => "EmbyStreams Catalog Fetch";
    public string Category => "EmbyStreams";
    public string Description => "Fetches catalog from AIOStreams (does not write files)";
}
```

**Note:** If renaming, update Plugin.cs registration and any TaskManager references.

**Priority:** P1 | **Effort:** 1 hour

---

## Task M-2: Batch Writes in NotifyStepAsync and PromoteStalledItemsAsync

**File:** Tasks/RefreshTask.cs:739-747, 914-931

**MAINTENANCE.md Reference:** Performance rule #10 (batch writes over N+1). Also mitigates Sprint 149 C-4 unbounded scan risk.

**Problem:** N+1 writes (42 individual UpsertCatalogItemAsync calls).

### Part 1: Add batched method to CatalogRepository

```csharp
/// <summary>
/// Batch upsert multiple catalog items in a single transaction.
/// 40x fewer round-trips than individual upserts.
/// </summary>
public async Task UpsertCatalogItemsAsync(IEnumerable<CatalogItem> items, CancellationToken ct)
{
    var itemsList = items.ToList();
    if (!itemsList.Any())
        return;

    await _writeLock.WaitAsync(ct);
    try
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();

        var sql = @"
            INSERT INTO catalog_items (
                id, imdb_id, tmdb_id, title, year, media_type,
                item_state, nfo_status, strm_path, retry_count,
                next_retry_at, blocked_at, blocked_by, created_at, updated_at
            ) VALUES (
                @id, @imdb_id, @tmdb_id, @title, @year, @media_type,
                @item_state, @nfo_status, @strm_path, @retry_count,
                @next_retry_at, @blocked_at, @blocked_by, @created_at, @updated_at
            )
            ON CONFLICT(id) DO UPDATE SET
                item_state = excluded.item_state,
                nfo_status = excluded.nfo_status,
                retry_count = excluded.retry_count,
                next_retry_at = excluded.next_retry_at,
                blocked_at = excluded.blocked_at,
                blocked_by = excluded.blocked_by,
                updated_at = excluded.updated_at;";

        foreach (var item in itemsList)
        {
            await using var cmd = new SQLiteCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@id", item.Id);
            cmd.Parameters.AddWithValue("@imdb_id", item.ImdbId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@tmdb_id", item.TmdbId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@title", item.Title);
            cmd.Parameters.AddWithValue("@year", item.Year ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@media_type", item.MediaType ?? "other");
            cmd.Parameters.AddWithValue("@item_state", item.ItemState.ToString());
            cmd.Parameters.AddWithValue("@nfo_status", item.NfoStatus ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@strm_path", item.StrmPath ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@retry_count", item.RetryCount);
            cmd.Parameters.AddWithValue("@next_retry_at", item.NextRetryAt ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@blocked_at", item.BlockedAt ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@blocked_by", item.BlockedBy ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", item.CreatedAt ?? DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("o"));

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogDebug("[CatalogRepository] Batch upserted {Count} items", itemsList.Count);
    }
    finally
    {
        _writeLock.Release();
    }
}
```

### Part 2: Update RefreshTask call sites

**Before (NotifyStepAsync ~line 739):**
```csharp
foreach (var item in writtenItems)
{
    cancellationToken.ThrowIfCancellationRequested();

    item.ItemState = ItemState.Notified;
    item.UpdatedAt = DateTime.UtcNow.ToString("o");
    await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item, cancellationToken);
}
```

**After:**
```csharp
foreach (var item in writtenItems)
{
    cancellationToken.ThrowIfCancellationRequested();
    item.ItemState = ItemState.Notified;
    item.UpdatedAt = DateTime.UtcNow.ToString("o");
}

// Single batched write
await Plugin.Instance!.DatabaseManager.Catalog.UpsertCatalogItemsAsync(writtenItems, cancellationToken);
```

Apply same pattern to:
- PromoteStalledItemsAsync (~line 914)

**Priority:** P2 | **Effort:** 2 hours

---

## Task M-3: Singleton HttpClient for AioMetadataClient

**File:** Services/AioMetadataClient.cs:26-33

**MAINTENANCE.md Reference:** Performance rule #11 (singleton HttpClient)

**Problem:** `new HttpClient()` instantiated per Refresh run can exhaust sockets under load.

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

    public async Task<EnrichedMetadata?> FetchAsync(string titleOrId, int? year, CancellationToken ct = default)
    {
        // Use static _httpClient instead of instance field
        var response = await _httpClient.GetAsync(url, ct);
        // ... rest of method
    }
}
```

Apply same pattern to:
- **Services/AioStreamsClient.cs** (if it has same anti-pattern)

**Priority:** P2 | **Effort:** 30 minutes

---

## Task M-4: Improve Exception Handling in EnrichmentLoop

**File:** Tasks/RefreshTask.cs:638-641

**MAINTENANCE.md Reference:** Data integrity rule #7 (distinguish transient from permanent failures)

**Problem:** Generic `catch (Exception)` treats all failures identically. Spec requires continuing on transient errors, failing fast on permanent ones.

### Fix:

```csharp
// In EnrichStepAsync enrichment loop
foreach (var item in needsEnrichItems)
{
    cancellationToken.ThrowIfCancellationRequested();

    try
    {
        var metadata = await aioClient.FetchAsync(item.Title, item.Year, cancellationToken);

        if (metadata != null)
        {
            await WriteEnrichedNfoAsync(item, metadata, cancellationToken);
            // ... update status
        }
        else
        {
            // Failure path - increment retry
            // ... existing logic
        }

        await Task.Delay(2000, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        throw; // Never suppress cancellation
    }
    catch (IOException ioEx)
    {
        // Disk full, permissions issue — fatal for the run
        _logger.LogError(ioEx, "[EmbyStreams] Disk I/O error during enrichment for {Title}", item.Title);
        throw;
    }
    catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
    {
        // Rate limited — back off and skip rest of batch
        _logger.LogWarning("[EmbyStreams] Rate limited by AIOMetadata, pausing enrichment");
        break;
    }
    catch (HttpRequestException httpEx)
    {
        // Transient network error — continue to next item
        _logger.LogWarning(httpEx, "[EmbyStreams] AIOMetadata fetch failed for {Title} - will retry", item.Title);
        continue;
    }
    catch (JsonException jsonEx)
    {
        // Bad data from API — continue
        _logger.LogWarning(jsonEx, "[EmbyStreams] Invalid JSON from AIOMetadata for {Title}", item.Title);
        continue;
    }
    catch (Exception ex)
    {
        // Unexpected — log but don't poison the whole batch
        _logger.LogError(ex, "[EmbyStreams] Unexpected error enriching {Title}", item.Title);
        continue;
    }
}
```

**Priority:** P2 | **Effort:** 20 minutes

---

## Task R-1: Stop Threading DatabaseManager into Other Repositories

**Files:** Data/Repositories/{CandidateRepository, SnapshotRepository, MaterializedVersionRepository}.cs

**MAINTENANCE.md Reference:** Refactoring opportunity from 2026-04-10 review

**Problem:** These "repositories" take DatabaseManager as constructor param and call back into it — circular coupling, not actual separation.

### Option A: Let them own connections directly (recommended for larger repos)

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
        await using var cmd = new SQLiteCommand("PRAGMA journal_mode=WAL;", conn);
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    // Direct SQL calls, no DatabaseManager dependency
    public async Task<StreamCandidate?> GetCandidateAsync(string id, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        // ... direct query
    }
}
```

### Option B: Roll back into DatabaseManager (for tiny repos)

If the repo is <100 lines and doesn't justify separate file, move methods back into DatabaseManager under a `#region` section.

**Recommendation:**
- **CandidateRepository** — Option A (already substantial)
- **SnapshotRepository** — Option A
- **MaterializedVersionRepository** — Option B (roll back, it's small)

**Update Plugin.cs constructor:**
```csharp
// Before
CandidateRepository = new CandidateRepository(DatabaseManager);

// After
CandidateRepository = new CandidateRepository(dbPath, loggerFactory.CreateLogger<CandidateRepository>());
```

**Priority:** P2 | **Effort:** 2-3 hours

---

## Task L-1: Move Hardcoded 365-day Expiry to Config

**File:** Tasks/RefreshTask.cs:441

**Problem:** Magic number

### Fix:

**PluginConfiguration.cs:**
```csharp
/// <summary>
/// Token expiry in days. Default: 365.
/// Renewal starts 90 days before expiry.
/// </summary>
public int TokenExpiryDays { get; set; } = 365;
```

**RefreshTask.cs:441:**
```csharp
// Before
var expiresAt = DateTimeOffset.UtcNow.AddDays(365);

// After
var expiresAt = DateTimeOffset.UtcNow.AddDays(Plugin.Instance!.Configuration.TokenExpiryDays);
```

**Priority:** P3 | **Effort:** 5 minutes

---

## Task L-2: Extract Magic Sentinel for "Never Retry"

**File:** Tasks/RefreshTask.cs:624

**Problem:** `new DateTimeOffset(2100, 1, 1, ...)` is cryptic

### Fix:

```csharp
// At class level in RefreshTask
private static readonly long NeverRetryUnixSeconds =
    new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

// Line 624
if (item.RetryCount >= 3)
{
    item.NfoStatus = "Blocked";
    item.NextRetryAt = NeverRetryUnixSeconds;
}
```

**Priority:** P3 | **Effort:** 2 minutes

---

## Task L-4: Document the "42" Magic Number

**File:** Tasks/DeepCleanTask.cs, Tasks/RefreshTask.cs

**Problem:** Why 42? Hitchhiker's reference or empirically tuned?

### Fix:

Add comment at each usage:

```csharp
// Process up to 42 items per run. This limit serves two purposes:
// 1. Bounds execution time to stay within the 6-minute cycle window
// 2. Prevents overwhelming Emby's metadata refresh queue
// (Also a nod to The Hitchhiker's Guide to the Galaxy)
const int ItemsPerPass = 42;
```

Apply to:
- RefreshTask.NotifyStepAsync
- RefreshTask.VerifyStepAsync
- DeepCleanTask.EnrichmentTrickleAsync

**Priority:** P3 | **Effort:** 5 minutes

---

## Task POST-SPRINT: Update MAINTENANCE.md

**File:** .ai/MAINTENANCE.md

After Sprint 151 completion, add to Decisions Log:

```markdown
| Decision | Resolved | Notes |
|---|---|---|
| Database access pattern | Sprint 151 | CatalogRepository owns catalog_items CRUD. CandidateRepository and SnapshotRepository own their tables. Remaining concerns (metadata KV, discover_catalog, schema migrations) stay in DatabaseManager. |
| Batch writes | Sprint 151 | UpsertCatalogItemsAsync(IEnumerable<>) for multi-item updates. Reduces N+1 anti-pattern. |
| HttpClient lifecycle | Sprint 151 | Static singleton in AioMetadataClient and AioStreamsClient. Never per-request instantiation. |
| Repository ownership | Sprint 151 | Repositories own connections directly. No circular DatabaseManager dependency. |
```

---

## Testing Checklist

```
[ ] Run full Refresh + DeepClean cycle after changes (no regression)
[ ] Verify inline Enrich still works for no-ID items
[ ] Verify token renewal still functions
[ ] H-3: All catalog queries work through new CatalogRepository
[ ] H-3: Verify blocked_at IS NULL filter present in all active item queries
[ ] H-1: Grep codebase for "MediaItem[^s]" references, verify none remain
[ ] M-1: Build succeeds after deleting 600 lines from CatalogSyncTask
[ ] M-1: Verify CatalogFetchTask (renamed) still appears in Scheduled Tasks
[ ] M-2: Check logs for "Batch upserted N items" messages
[ ] M-2: Verify NotifyStep completes faster (fewer DB round-trips)
[ ] M-3: Run 100 consecutive Refresh cycles, verify no socket exhaustion
[ ] M-4: Simulate IOException in enrichment, verify task fails fast
[ ] M-4: Simulate HttpRequestException, verify task continues to next item
[ ] R-1: Verify CandidateRepository/SnapshotRepository work without DatabaseManager dependency
[ ] L-1: Change TokenExpiryDays to 180, verify new tokens expire in 180 days
[ ] L-4: Code review — verify all "42" usages have explanatory comments
```

---

## Commit Message

```
refactor(sprint-151): extract CatalogRepository, delete dead code, batch writes

GOD CLASS REDUCTION:
- Extract CatalogRepository from DatabaseManager (H-3) — ~1,000 lines moved
- Unify CatalogItem/MediaItem models (H-1) — delete duplicate MediaItem branch
- Delete 600 lines of dead STRM/NFO writers from CatalogSyncTask (M-1)
- Optionally rename CatalogSyncTask → CatalogFetchTask for clarity

PERFORMANCE:
- Batch writes in Notify/PromoteStalled (M-2) — 40x fewer transactions
- Singleton HttpClient in AioMetadataClient/AioStreamsClient (M-3)
- Add bounded limit to PromoteStalledItems (fixes Sprint 149 C-4)

CODE QUALITY:
- Improve exception handling in enrichment loop (M-4)
- Stop threading DatabaseManager into other repos (R-1)
- Move hardcoded 365-day expiry to PluginConfiguration (L-1)
- Document "42" magic number with inline comments (L-4)
- Extract NeverRetryUnixSeconds sentinel constant (L-2)

CRITICAL FIXES PRESERVED:
- blocked_at IS NULL filter enforced in all active item queries (Sprint 149 C-5)
- Per-user pin checks before deletion maintained (Sprint 149 C-6)

DatabaseManager reduced from 5,624 to ~2,200 lines.

Fulfills: MAINTENANCE.md performance rules #10, #11 and code quality standards
Updates: MAINTENANCE.md Decisions Log with database access patterns
```

---

## Files Modified/Deleted

**New:**
- Data/Repositories/CatalogRepository.cs — ~800 lines extracted

**Modified:**
- Data/DatabaseManager.cs — ~1,200 lines deleted, delegation property added
- Data/Models/CatalogItem.cs — Add Blocked computed property
- Data/Repositories/CandidateRepository.cs — Remove DatabaseManager dependency
- Data/Repositories/SnapshotRepository.cs — Remove DatabaseManager dependency
- Tasks/RefreshTask.cs — Use Catalog repo, batch writes, improve exception handling
- Tasks/DeepCleanTask.cs — Use Catalog repo, add "42" comment
- Tasks/CatalogSyncTask.cs — Delete 600 lines, optionally rename to CatalogFetchTask
- Services/AioMetadataClient.cs — Static HttpClient
- Services/AioStreamsClient.cs — Static HttpClient (if same pattern exists)
- PluginConfiguration.cs — Add TokenExpiryDays property
- Plugin.cs — Update repository instantiation (pass dbPath instead of DatabaseManager)

**Deleted:**
- Data/Models/MediaItem.cs — Unified into CatalogItem
- Data/Repositories/MaterializedVersionRepository.cs — Rolled back into DatabaseManager (Option B)

**Total changes:** ~1,900 lines deleted, ~900 lines added (net -1,000)

---

## Next Steps After Sprint 151

1. Update .ai/MAINTENANCE.md Decisions Log with new patterns
2. Run full E2E test suite
3. Performance benchmark: measure Refresh cycle time before/after batch writes
4. Grep for any remaining `DatabaseManager.Get/Upsert` calls that should use `.Catalog`
5. Update developer docs with new CatalogRepository usage patterns
