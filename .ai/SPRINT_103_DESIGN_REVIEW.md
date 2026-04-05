# Sprint 103 — Design Review Findings

**Date:** 2026-04-04
**Reviewer:** Claude (Architecture Analysis)
**Scope:** Full EmbyStreams codebase design review across 10 dimensions

---

## Executive Summary

EmbyStreams demonstrates a **well-thought-through domain design** with clear separation of concerns. The state machine (Doctor), tiered resolution (LinkResolver), and caching strategy are sound. However, several **critical design issues** will cause problems as the system scales:

1. **Static global state accumulation** (Plugin.Instance, RateLimitBucket, _episodeCountCache) — memory leaks inevitable under concurrent access
2. **God-method anti-patterns** in DatabaseManager (3000+ lines, 20+ responsibilities) — violates single responsibility
3. **No clear boundary for "ephemeral vs durable state"** — in-memory caches without TTL or cleanup strategies

**Overall Assessment:** 7/10 — solid foundation with architectural debt that needs addressing.

---

## 1. Architecture & Component Design

### Strengths

| Aspect | Assessment |
|--------|------------|
| **Separation of concerns** | ✅ Clear separation: Data layer (DatabaseManager), Services layer (AioStreamsClient, PlaybackService), Tasks layer (scheduled jobs) |
| **Domain modeling** | ✅ State machine (ItemState) is well-designed; transitions are documented and enforced |
| **Plugin entry point** | ✅ Plugin.cs is lean; only handles initialization and singleton access |
| **Task scheduling** | ✅ IScheduledTask pattern used consistently; each task has clear responsibility |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **1.1** | **God class: DatabaseManager** — 3000+ lines, handles schema, migrations, 8 tables, integrity checks, WAL mode, transactions, API budget tracking, sync state tracking, catalog operations, resolution cache operations... | **High** — Hard to reason about, violates SRP, difficult to test |
| **1.2** | **No repository abstraction** — Direct SQLite coupling throughout services. Changing storage strategy requires touching dozens of files | **Medium** — Reduces testability and flexibility |
| **1.3** | **Static global state (Plugin.Instance)** — Directly accessed from 20+ call sites. No interface, no testability. Singleton pattern with lazy initialization race conditions. | **High** — Thread-safety unclear, cannot mock for tests |
| **1.4** | **Tight coupling to PluginConfiguration** — Passed as parameters to nearly every method. Configuration reads scattered everywhere instead of being injected. | **Medium** — Makes code harder to reason about state flow |

**Top 3 Critical Issues:**
1. **DatabaseManager God class** — must be split
2. **Static Plugin.Instance singleton** — dependency injection needed
3. **No repository pattern** — data access tightly coupled to SQLite

### Recommendations

1. **Split DatabaseManager** into focused repositories:
   - `ICatalogRepository` — catalog_items CRUD
   - `IResolutionCacheRepository` — resolution_cache CRUD
   - `IPlaybackLogRepository` — playback_log CRUD
   - `IApiBudgetRepository` — api_budget tracking
   - `ISyncStateRepository` — sync_state tracking

2. **Introduce repository interfaces** in `Data/IRepositories.cs` for testability

3. **Replace Plugin.Instance with DI**:
   ```csharp
   // Instead of:
   var db = Plugin.Instance?.DatabaseManager;

   // Use:
   public class PlaybackService(IRepositoryFactory repoFactory) { ... }
   ```

4. **Configuration object pattern**:
   ```csharp
   public interface IEmbyStreamsConfig
   {
       string PrimaryManifestUrl { get; }
       int CacheLifetimeMinutes { get; }
       // ...
   }
   ```

---

## 2. Data Flow & State Management

### Catalog Item Lifecycle Trace

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                         CATALOG ITEM LIFECYCLE                          │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Catalog Sync (AIOStreams/Trakt)                                     │
│       ↓                                                               │
│  UpsertCatalogItemAsync → ItemState = CATALOGUED                         │
│       ↓                                                               │
│  Doctor Phase 2: WriteStrmForItemPublicAsync → ItemState = PRESENT       │
│       ↓                                                               │
│  LinkResolver: ResolveOneAsync → UpsertResolutionCacheAsync                 │
│       ↓                                                               │
│  ItemState = RESOLVED (via DB update)                                   │
│       ↓                                                               │
│  Playback: Cached URL served, client_compat learned (redirect/proxy)          │
│       ↓                                                               │
│  Doctor Phase 3: Real file detected → DeleteStrm → ItemState = RETIRED  │
│                                                                     │
│  Orphan detection (Doctor Phase 1): .strm exists, no DB record → delete │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Strengths

| Aspect | Assessment |
|--------|------------|
| **State transitions** | ✅ ItemState enum clearly defined; transitions documented in ItemState.cs |
| **Soft-delete pattern** | ✅ `removed_at` provides audit trail |
| **Pin protection** | ✅ PINNED items protected from catalog removal (line 166-171 in DoctorTask) |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **2.1** | **No clear boundary for ephemeral vs durable state** — RateLimitBucket, _episodeCountCache, ProxySessionStore are in-memory caches with no documented TTL or cleanup strategy | **High** — Memory leaks inevitable under load |
| **2.2** | **Race condition in _episodeCountCache** — EmbyEventHandler line 47 uses `ConcurrentDictionary` but cache expiry check not atomic | **Medium** — Could serve stale episode counts |
| **2.3** | **No transactional consistency guarantees** — File system writes (.strm) and DB updates happen in separate try-catch blocks (DoctorTask line 256-271) | **High** — Can leave orphaned .strm files on failure |
| **2.4** | **State drift between DB and filesystem** — `strm_path` in DB can point to non-existent files; orphan detection only runs during Doctor | **Medium** — Inconsistent state persists for hours |

**Top 3 Critical Issues:**
1. **No ephemeral cache cleanup strategy** — memory leaks
2. **Non-atomic filesystem + DB operations** — inconsistent state possible
3. **Orphan detection only in scheduled task** — state drift

### Recommendations

1. **Implement cache cleanup strategy**:
   ```csharp
   public interface IExpirableCache<T>
   {
       void Set(string key, T value, TimeSpan ttl);
       bool TryGet(string key, out T? value);
       Task CleanupExpiredAsync(CancellationToken ct);
   }
   ```

2. **Use repository transactions for state changes**:
   ```csharp
   public async Task<WriteResult> WriteStrmWithTransactionAsync(...)
   {
       await using var tx = _db.BeginTransactionAsync();
       try {
           await WriteStrmFileAsync(...);
           await UpdateStrmPathAsync(...);
           await tx.CommitAsync();
       } catch {
           await tx.RollbackAsync();
           File.Delete(...); // cleanup
       }
   }
   ```

3. **Add background orphan scanner** — runs every 5 minutes, not just in Doctor

---

## 3. Separation of Concerns & Design Patterns

### Applied Patterns

| Pattern | Where | Assessment |
|----------|---------|------------|
| **Repository (de facto)** | DatabaseManager | ⚠️ Implicit, not abstracted |
| **Strategy** | ICatalogProvider | ✅ Good abstraction |
| **Factory** | AioStreamsClient constructors | ⚠️ Overloaded constructors, no factory class |
| **Singleton** | Plugin.Instance | ⚠️ Static anti-pattern |
| **Observer** | EmbyEventHandler | ✅ Clean event subscription |
| **SingleFlight** | SingleFlight<T> | ✅ Good deduplication |
| **State Machine** | Doctor state machine | ✅ Well-implemented |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **3.1** | **Business logic in I/O layers** — StreamUrlSigner (signing), StreamProxyService (proxy logic) mix business rules with HTTP I/O | **Medium** — Hard to unit test |
| **3.2** | **No clear service layer** — PlaybackService, DiscoverService, StatusService mix business logic with HTTP handling directly | **Medium** — Business rules scattered |
| **3.3** | **Configuration reads everywhere** — 50+ call sites read Plugin.Instance.Configuration directly | **Medium** — No single source of truth for config-derived values |
| **3.4** | **No domain services** — LibraryItemMap building logic in CatalogSyncTask (line 1905) duplicated in LibraryReadoptionTask | **Medium** — Duplicated code, no shared abstraction |

**Top 3 Critical Issues:**
1. **Business logic in I/O layers** — needs domain services
2. **No repository interface** — can't mock for tests
3. **Static singleton** — prevents DI

### Recommendations

1. **Extract domain services**:
   ```csharp
   public interface IStreamSigningService
   {
       string GenerateSignedUrl(...);
       bool ValidateSignature(...);
   }

   public interface IProxySessionService
   {
       string CreateSession(StreamUrl url);
       ProxySession? GetSession(string token);
   }
   ```

2. **Consolidate shared logic**:
   ```csharp
   // Move CatalogSyncTask.BuildLibraryItemMapPublic to:
   public static class EmbyLibraryScanner
   {
       public static Dictionary<string, string> BuildItemMap(...);
   }
   ```

3. **Create service layer abstractions** — separate HTTP concerns from business rules

---

## 4. Interfaces & Contracts

### Internal Contracts

| Interface | Purpose | Assessment |
|-----------|---------|------------|
| **ICatalogProvider** | Catalog data source abstraction | ✅ Well-designed |
| **IManifestProvider** | Stremio addon abstraction | ✅ Good, generic |
| **IScheduledTask** | Emby task contract | ✅ Framework-provided |
| **IService** | Emby REST API handlers | ✅ Framework-provided |

### External Contracts

| Contract | Provider | Assessment |
|----------|-----------|------------|
| **AIOStreams API** | Primary manifest/catalog/stream | ✅ AioStreamsClient handles it well |
| **Cinemeta API** | Metadata fallback | ⚠️ No abstraction, directly called |
| **Emby REST API** | Library scan triggers, BoxSet creation | ⚠️ Direct calls in multiple tasks |
| **Stremio addon spec** | Standard catalog/meta/stream | ✅ AioStreamsManifest models match |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **4.1** | **No abstraction for Emby API calls** — Library scans, BoxSet creation, item lookups are direct calls to `ILibraryManager` scattered across tasks | **High** — Changing Emby API requires touching 10+ files |
| **4.2** | **Cinemeta directly called** — No IManifestProvider for Cinemeta; MetadataFallbackTask calls directly | **Medium** — Can't swap metadata providers |
| **4.3** | **No provider factory** — AioStreamsClient has 3 constructors; no clear way to create providers by type | **Medium** — Provider selection logic implicit |
| **4.4** | **No retry policy abstraction** — HTTP retries are inline (if statements), no Polly/Resilience4j style abstraction | **Low** — Retry logic inconsistent |

**Top 3 Critical Issues:**
1. **No Emby API abstraction** — tight coupling to Emby internals
2. **No metadata provider factory** — can't swap Cinemeta easily
3. **No retry policy abstraction** — inconsistent error handling

### Recommendations

1. **Create Emby API abstraction**:
   ```csharp
   public interface IEmbyLibraryService
   {
       Task<Dictionary<string, string>> BuildLibraryItemMapAsync(...);
       Task ScanLibrariesAsync(...);
       Task CreateBoxSetAsync(...);
   }
   ```

2. **Metadata provider factory**:
   ```csharp
   public interface IMetadataProviderFactory
   {
       IManifestProvider CreateForType(MetadataProviderType type, string config);
   }

   public enum MetadataProviderType { Cinemeta, AIOMetadata, TMDB }
   ```

3. **Retry policy abstraction**:
   ```csharp
   public interface IRetryPolicy
   {
       Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken ct);
   }

   public class ExponentialBackoffRetry : IRetryPolicy { ... }
   ```

---

## 5. Error Handling & Resilience

### Current Error Handling Strategy

| Component | Error Handling | Assessment |
|-----------|----------------|------------|
| **PlaybackService** | Try-catch on SyncResolveAsync, returns 503 on failure | ✅ Decent, but no retry on transient errors |
| **LinkResolverTask** | Try-catch per-item, logs failures, continues | ✅ Fault-tolerant per-item |
| **DoctorTask** | Try-catch around phases, logs and continues | ✅ Fault-tolerant |
| **AioStreamsClient** | Throws AioStreamsUnreachableException | ⚠️ Caller must handle, no retry built-in |

### Strengths

| Aspect | Assessment |
|--------|------------|
| **Error propagation** | ✅ Exceptions bubble up, logged at catch points |
| **Graceful degradation** | ✅ Tasks continue on item failures |
| **Error logging** | ✅ Consistent logging pattern |
| **HTTP 429 handling** | ✅ ApiBudgetRecordRateLimitHitAsync, exponential backoff |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **5.1** | **No retry on transient errors** — PlaybackService catches AioStreamsUnreachableException but doesn't retry | **Medium** — Playback fails on temporary network issues |
| **5.2** | **Error codes inconsistent** — Some return JSON error bodies (PlayErrorResponse), some throw exceptions | **Medium** — Client handling unpredictable |
| **5.3** | **"Never-give-up" not fully implemented** — PlaybackService MaxResolutionAttempts = 3, but fallback logic incomplete | **Medium** — May give up too early |
| **5.4** | **No circuit breaker pattern** — If AIOStreams is down, requests keep hitting it | **High** — Wastes resources, poor UX |
| **5.5** | **Silent failures** — Some Task.Run() calls have no error handling (EmbyEventHandler line 110, 184) | **Medium** — Background exceptions lost |

**Top 3 Critical Issues:**
1. **No circuit breaker** — cascades failures when AIOStreams is down
2. **No retry on transient errors** — temporary network issues cause playback failure
3. **Silent Task.Run failures** — background errors unhandled

### Recommendations

1. **Implement circuit breaker**:
   ```csharp
   public interface ICircuitBreaker
   {
       Task<T> ExecuteAsync<T>(string key, Func<Task<T>> action);
       void RecordSuccess(string key);
       void RecordFailure(string key);
   }

   // State transitions: Closed → Open → HalfOpen
   ```

2. **Add retry with exponential backoff**:
   ```csharp
   private async Task<ResolutionEntry?> SyncResolveWithRetryAsync(...)
   {
       var policy = new RetryPolicy(maxAttempts: 3, backoff: Exponential)
           .RetryOn<HttpRequestException>()
           .RetryOn<AioStreamsUnreachableException>();
       return await policy.ExecuteAsync(() => SyncResolveAsync(...));
   }
   ```

3. **Handle all Task.Run errors**:
   ```csharp
   _ = Task.Run(async () =>
   {
       try {
           await HandleNewEpisodeIndexedAsync(item);
       } catch (Exception ex) {
           _logger.LogError(ex, "[EmbyStreams] Background task failed");
       }
   });
   ```

---

## 6. Scalability & Performance

### Current Performance Profile

| Operation | Performance | Assessment |
|------------|---------------|------------|
| **Playback (cache hit)** | <100ms | ✅ Excellent |
| **Playback (cache miss)** | 3-30s (AIOStreams resolve) | ⚠️ Variable, depends on addon count |
| **Catalog sync** | Depends on catalog size (500 item cap) | ⚠️ Linear scan in PruneSourceAsync |
| **Link resolution** | Tiered, concurrent (MaxConcurrentResolutions = 3) | ✅ Bounded concurrency |
| **Doctor execution** | O(n) scan of all catalog items | ⚠️ Linear, becomes slow at scale |

### Strengths

| Aspect | Assessment |
|--------|------------|
| **Caching strategy** | ✅ resolution_cache with tiered pre-resolution |
| **Concurrency limits** | ✅ MaxConcurrentResolutions, ApiCallDelayMs |
| **SingleFlight dedup** | ✅ Prevents duplicate AIOStreams calls |
| **API budget enforcement** | ✅ ApiDailyBudget prevents runaway usage |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **6.1** | **N+1 in PruneSourceAsync** — Fetches all items for source, then iterates to find removed ones. O(n²) for large catalogs | **High** — Performance degrades with catalog size |
| **6.2** | **Unbounded RateLimitBucket** — Dictionary grows forever with no cleanup | **High** — Memory leak over long runtime |
| **6.3** | **Synchronous file I/O in hot path** — WriteStrmFileForItemPublicAsync does File.WriteAllText synchronously | **Medium** — Blocks thread pool under load |
| **6.4** | **No batch operations** — Each catalog item upserted individually (line 106 in DatabaseManager) | **Medium** — Could use SQLite batch UPSERT |
| **6.5** | **Doctor full scan every 4h** — O(n) scan of all items becomes expensive with 10k+ items | **Medium** — Linear scaling problem |

**Top 3 Critical Issues:**
1. **N+1 in PruneSourceAsync** — O(n²) catalog pruning
2. **Unbounded in-memory caches** — memory leaks
3. **No batch database operations** — unnecessary round-trips

### Recommendations

1. **Fix N+1 in PruneSourceAsync**:
   ```csharp
   // Instead of: fetch all, then iterate to find missing
   // Use:
   const string sql = @"
       UPDATE catalog_items
       SET removed_at = datetime('now')
       WHERE source = @source
         AND imdb_id NOT IN (@imdb_ids)";
   await ExecuteWriteAsync(sql, cmd => {
       BindText(cmd, "@source", source);
       BindText(cmd, "@imdb_ids", string.Join(",", currentImdbIds));
   });
   ```

2. **Implement bounded in-memory caches**:
   ```csharp
   public class BoundedCache<TKey, TValue>
   {
       private readonly ConcurrentDictionary<TKey, (TValue, DateTime)> _cache;
       private readonly TimeSpan _ttl;
       private readonly int _maxSize;

       public bool TryGet(TKey key, out TValue? value) {
           if (_cache.TryGetValue(key, out var entry)) {
               if (DateTime.UtcNow - entry.expireAt < _ttl) {
                   value = entry.value;
                   return true;
               }
               _cache.TryRemove(key, out _);
           }
           value = default;
           return false;
       }

       public void Set(TKey key, TValue value) {
           if (_cache.Count >= _maxSize) {
               EvictOldest();
           }
           _cache[key] = (value, DateTime.UtcNow);
       }
   }
   ```

3. **Batch database operations**:
   ```csharp
   public async Task UpsertCatalogItemsBatchAsync(IEnumerable<CatalogItem> items)
   {
       await using var tx = _db.BeginTransactionAsync();
       foreach (var item in items) {
           await UpsertCatalogItemInTransactionAsync(tx, item);
       }
       await tx.CommitAsync();
   }
   ```

---

## 7. Library & Catalog Management Design

### Pin/Unpin Model

| Requirement | Implementation | Assessment |
|------------|----------------|------------|
| **Pinned items persist** | ItemState = PINNED, protected in Doctor Phase 1 (line 166-171) | ✅ Correct |
| **Catalog-only items ephemeral** | Items without PIN removed when not in catalog | ✅ Correct |
| **Pinned can transition to RETIRED** | Real file detection in Doctor Phase 3 | ✅ Correct |

### Strengths

| Aspect | Assessment |
|--------|------------|
| **Pin protection** | ✅ PINNED items survive catalog removal |
| **State machine enforcement** | ✅ Clear transition rules in ItemState.cs |
| **Library re-adoption** | ✅ FileResurrectionTask replaces missing files |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **7.1** | **Anime/Adult library enforcement not architecturally protected** — Rule is in CatalogSyncTask (line 800+), not enforced at DB or API level | **Medium** — Can be bypassed by adding items manually |
| **7.2** | **BoxSet creation not transactional** — CollectionSyncTask creates BoxSets via Emby API but has no rollback strategy | **Medium** — Partial BoxSets on failure |
| **7.3** | **Library scan triggers uncoordinated** — Doctor triggers Emby scan (line 360+), but sync interval is separate | **Low** — Potential for duplicate scans |
| **7.4** | **No catalog deduplication** — Same item from AIOStreams and Trakt can exist as duplicate rows (different source) | **Low** — Emby may see duplicate library items |

**Top 3 Critical Issues:**
1. **Anime/Adult rules not architecturally enforced** — bypass possible
2. **BoxSet creation non-transactional** — partial states possible
3. **No catalog deduplication** — duplicates allowed

### Recommendations

1. **Enforce library rules at DB level**:
   ```csharp
   // Add check constraints or triggers:
   CREATE TRIGGER enforce_anime_library
   BEFORE INSERT ON catalog_items
   BEGIN
       SELECT CASE
           WHEN NEW.media_type = 'anime' AND @target_library != 'anime' THEN
               RAISE(ABORT, 'Anime items must go to anime library')
       END;
   END;
   ```

2. **Transactional BoxSet creation**:
   ```csharp
   public async Task CreateBoxSetWithRollbackAsync(...)
   {
       var createdItems = new List<BaseItem>();
       try {
           createdItems.Add(await _embyApi.CreateCollectionAsync(...));
           // ... add more items
           await _embyApi.ScanLibrariesAsync();
       } catch {
           foreach (var item in createdItems) {
               await _embyApi.DeleteItemAsync(item.Id);
           }
           throw; // Re-throw after cleanup
       }
   }
   ```

3. **Add catalog deduplication**:
   ```csharp
   // Use a canonical key (tmdb_id preferred, then imdb_id)
   // Store in catalog_items with unique constraint on canonical_id
   ```

---

## 8. Configuration & Extensibility

### Configuration Structure

| Aspect | Assessment |
|--------|------------|
| **Single source of truth** | ✅ PluginConfiguration.cs with DataMember attributes |
| **Validation** | ✅ Validate() method clamps values |
| **Extensibility** | ⚠️ Adding new catalog providers requires modifying CatalogSyncTask |

### Strengths

| Aspect | Assessment |
|--------|------------|
| **Type-safe config** | ✅ Strong typing with validation |
| **Persistence** | ✅ Emby handles XML serialization |
| **Runtime validation** | ✅ OnDeserialized callback |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **8.1** | **Provider registration hard-coded** — CatalogSyncTask line 95-100 creates providers manually, no plugin architecture | **High** — Adding new provider requires code changes |
| **8.2** | **CatalogItemCap applies uniformly** — Per-provider limits in JSON, but no UI for managing them | **Low** — Configuration UX could be better |
| **8.3** | **No feature flag abstraction** — Booleans scattered throughout code (EnableAnimeLibrary, EnableCinemetaDefault, etc.) | **Medium** — Feature toggles not centrally managed |
| **8.4** | **No provider factory** — AIOStreams vs Cinemeta handling is if/else logic in CatalogSyncTask | **Medium** — Hard to add new providers |

**Top 3 Critical Issues:**
1. **Hard-coded provider registration** — not extensible
2. **No provider factory** — tight coupling to specific providers
3. **No feature flag abstraction** — scattered booleans

### Recommendations

1. **Provider registry pattern**:
   ```csharp
   public interface ICatalogProviderRegistry
   {
       void Register(ICatalogProvider provider);
       IEnumerable<ICatalogProvider> GetEnabledProviders(PluginConfiguration config);
   }

   public class CatalogProviderRegistry : ICatalogProviderRegistry
   {
       private readonly List<ICatalogProvider> _providers = new();

       public void Register(ICatalogProvider provider) =>
           _providers.Add(provider);

       public IEnumerable<ICatalogProvider> GetEnabledProviders(PluginConfiguration config)
       {
           return _providers.Where(p => p.IsEnabled(config));
       }
   }
   ```

2. **Feature flag service**:
   ```csharp
   public interface IFeatureFlagService
   {
       bool IsEnabled(string featureKey);
       void SetEnabled(string featureKey, bool enabled);
   }

   // Usage:
   if (_featureFlags.IsEnabled("anime_library")) { ... }
   ```

3. **Provider factory interface**:
   ```csharp
   public interface ICatalogProviderFactory
   {
       ICatalogProvider CreateForSource(string sourceType, PluginConfiguration config);
   }
   ```

---

## 9. Testability

### Current Testability Profile

| Component | Testable? | Barrier |
|-----------|-------------|----------|
| **Plugin.cs** | ❌ No | Static singleton, file system access |
| **DatabaseManager** | ❌ No | Tightly coupled to SQLitePCL.pretty, no interface |
| **AioStreamsClient** | ⚠️ Partial | Can mock HttpClient, but dependencies concrete |
| **PlaybackService** | ❌ No | Depends on Plugin.Instance, RateLimitBucket (static) |
| **DoctorTask** | ❌ No | Depends on Plugin.Instance, ILibraryManager (concrete) |
| **CatalogSyncTask** | ❌ No | Static methods, file system, Plugin.Instance |
| **LinkResolverTask** | ❌ No | AioStreamsClient concrete, Plugin.Instance |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **9.1** | **No repository interfaces** — Can't mock DatabaseManager | **High** — No unit testing possible |
| **9.2** | **Static Plugin.Instance** — Cannot inject test doubles | **High** — Integration tests only |
| **9.3** | **Static RateLimitBucket** — Cannot be reset between tests | **Medium** — Tests interfere with each other |
| **9.4** | **File system operations concrete** — No IFileSystem abstraction | **Medium** — Tests require real files |
| **9.5** | **No integration test strategy** — No test fixtures, no test Emby instance | **High** — End-to-end testing impossible |

**Top 3 Critical Issues:**
1. **No repository interfaces** — cannot mock data layer
2. **Static singleton** — cannot inject test doubles
3. **No integration test infrastructure** — no way to test end-to-end

### Recommendations

1. **Introduce repository interfaces** (see Section 1.1)

2. **Dependency injection everywhere**:
   ```csharp
   public class PlaybackService(
       IResolutionCacheRepository resolutionCache,
       IStreamSigningService signing,
       IEmbyLibraryService emby,
       ILogger<PlaybackService> logger) { ... }
   ```

3. **File system abstraction**:
   ```csharp
   public interface IFileSystem
   {
       Task WriteAllTextAsync(string path, string content);
       bool Exists(string path);
       string ReadAllText(string path);
       void Delete(string path);
   }

   public class DiskFileSystem : IFileSystem { /* real implementation */ }
   public class MemoryFileSystem : IFileSystem { /* test implementation */ }
   ```

4. **Integration test project**:
   ```csharp
   // EmbyStreams.Tests.Integration/
   public class PlaybackServiceTests : IClassFixture<TestEmbyServer>
   {
       [Fact]
       public async Task Playback_ResolvesStreamFromCache()
       {
           // Spin up test Emby, register plugin, test playback
       }
   }
   ```

---

## 10. Code Quality & Maintainability

### Anti-Patterns Detected

| # | Anti-Pattern | Location | Severity |
|---|---------------|----------|-----------|
| **10.1** | **God class: DatabaseManager** | Data/DatabaseManager.cs | **High** |
| **10.2** | **Static global state** | Plugin.Instance, RateLimitBucket | **High** |
| **10.3** | **God method: Execute()** | DoctorTask.Execute (400+ lines) | **Medium** |
| **10.4** | **Magic strings** | ProviderPriorityOrder parsing, stream type policies | **Low** |
| **10.5** | **Commented-out code** | Various (see git history) | **Low** |

### Naming & Conventions

| Aspect | Assessment |
|--------|------------|
| **Class naming** | ✅ PascalCase, descriptive |
| **Async suffix** | ✅ Consistent |
| **XML doc comments** | ✅ Comprehensive |
| **Field naming** | ✅ _camelCase for privates |
| **Constants** | ✅ UPPER_CASE or PascalCase with const |

### Issues

| # | Issue | Impact |
|---|---------|--------|
| **10.6** | **Inconsistent logging levels** | Mix of LogDebug/LogInformation/LogWarning without clear policy | **Low** |
| **10.7** | **Comment duplication** | Some XML docs duplicate inline comments | **Low** |
| **10.8** | **Long methods** | DoctorTask.Execute, CatalogSyncTask.Execute > 200 lines | **Medium** |
| **10.9** | **Magic numbers** | 70% TTL threshold (PlaybackService line 274), 12-hour manifest TTL (Plugin line 95) | **Low** |

**Top 3 Critical Issues:**
1. **God class (DatabaseManager)** — maintainability impact
2. **Static global state** — thread safety unclear
3. **Long methods** — cognitive load

### Recommendations

1. **Extract constants**:
   ```csharp
   public static class PlaybackConstants
   {
       public const double CacheAgingThresholdPercent = 0.70;
       public const TimeSpan ManifestTtl = TimeSpan.FromHours(12);
       public const int MaxResolutionAttempts = 3;
   }
   ```

2. **Break down long methods**:
   ```csharp
   // DoctorTask.Execute becomes:
   public async Task Execute(...)
   {
       await FetchAndDiffPhase(progress, ct);
       await WritePhase(progress, ct);
       await AdoptPhase(progress, ct);
       await HealthCheckPhase(progress, ct);
       await ReportPhase(progress, ct);
   }
   ```

3. **Logging policy**:
   ```csharp
   public static class Log
   {
       public static void Debug(this ILogger logger, string message, params object[] args) { ... }
       public static void Info(this ILogger logger, string message, params object[] args) { ... }
       // Centralize log level decisions
   }
   ```

---

## Prioritized Remediation List

### 🔴 P0 — Fix Immediately (Blocks Scale/Reliability)

| Priority | Issue | Fix Effort | Impact |
|----------|-------|-------------|--------|
| 1 | **Split DatabaseManager into repositories** | 2-3 days | Enables unit testing, reduces complexity |
| 2 | **Implement circuit breaker for AIOStreams** | 1-2 days | Prevents cascades failures |
| 3 | **Fix N+1 in PruneSourceAsync** | 2 hours | Fixes O(n²) catalog pruning |
| 4 | **Implement bounded in-memory caches** | 1 day | Prevents memory leaks |
| 5 | **Make filesystem + DB operations atomic** | 1 day | Prevents inconsistent state |

### 🟡 P1 — Fix Soon (Improves Maintainability)

| Priority | Issue | Fix Effort | Impact |
|----------|-------|-------------|--------|
| 6 | **Replace Plugin.Instance with DI** | 3-4 days | Enables testing, cleaner code |
| 7 | **Introduce repository interfaces** | 1 day (after P0-1) | Prerequisite for testing |
| 8 | **Create Emby API abstraction** | 2 days | Reduces coupling |
| 9 | **Provider registration pattern** | 2 days | Enables extensibility |
| 10 | **Break down long methods** | 1-2 days | Improves readability |

### 🟢 P2 — Nice to Have (Polishes/UX)

| Priority | Issue | Fix Effort | Impact |
|----------|-------|-------------|--------|
| 11 | **Feature flag abstraction** | 1 day | Centralizes toggles |
| 12 | **Batch database operations** | 2 days | Improves performance |
| 13 | **File system abstraction** | 1 day | Enables testing |
| 14 | **Retry policy abstraction** | 1 day | Consistent error handling |
| 15 | **Consolidate shared logic** | 2 hours | Reduces duplication |
| 16 | **Extraction of domain services** | 2 days | Separates concerns |

### 🔵 P3 — Accept as-Is (Low Priority)

| Item | Reason |
|-------|--------|
| Magic numbers constants | Can be documented inline |
| Logging consistency | Can be improved iteratively |
| Comment cleanup | Can happen naturally during development |
| Anime/Adult enforcement rules | Current implementation works for typical use case |

---

## Summary Statistics

| Dimension | Score | Notes |
|-----------|-------|-------|
| **Architecture** | 6/10 | Good separation, god class issue |
| **Data Flow** | 6/10 | Clear lifecycle, consistency issues |
| **SoC & Patterns** | 7/10 | Good patterns, need services |
| **Interfaces** | 6/10 | Internal OK, external needs abstraction |
| **Error Handling** | 5/10 | Basic, missing retry/circuit breaker |
| **Scalability** | 5/10 | Caching good, N+1 issues |
| **Library Management** | 7/10 | Pin model good, rules not enforced |
| **Configuration** | 6/10 | Validation good, not extensible |
| **Testability** | 2/10 | **Critical issue** — no interfaces |
| **Code Quality** | 7/10 | Clean, some anti-patterns |

**Overall: 6.3/10** — Solid foundation with critical testability and scalability debt

---

## Acceptable Design Decisions (As-Is)

The following are **acceptable trade-offs** given current project stage:

1. **Single SQLite database** — Simpler than multi-DB, acceptable for current scale
2. **STRM-based approach** — Correct architecture for Emby streaming integration
3. **Emby REST API direct usage** — No official library, acceptable trade-off
4. **Configuration XML via Emby** — Framework limitation, acceptable
5. **In-memory proxy session store** — 4h TTL acceptable for use case
6. **Rate limiting per-IP/user** — Simple and effective
7. **Doctor as monolithic task** — Acceptable for now, can be split later
8. **No message queue** — Acceptable for single-server deployment
9. **Manual library scan triggers** — Emby limitation, acceptable
10. **No caching layer abstraction** — Acceptable for single implementation

---

## Conclusion

EmbyStreams has a **well-designed core** with clear domain modeling and a thoughtful state machine. The critical issues are primarily around:

1. **Testability** — No repository interfaces, static globals
2. **Scalability** — N+1 queries, unbounded caches
3. **Extensibility** — Hard-coded provider registration

Addressing the **P0 items** (first 5) will dramatically improve:
- **System reliability** (circuit breaker, atomic operations)
- **Performance** (N+1 fix, bounded caches)
- **Maintainability** (repository split)

The **P1 items** (next 5) are prerequisites for a mature, testable codebase.

---

**Next Steps:** Create Sprint 104 backlog with P0 items as first stories.
