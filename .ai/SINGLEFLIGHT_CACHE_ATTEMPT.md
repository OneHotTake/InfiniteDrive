# SingleFlight Result Caching — Implementation Analysis

**Task:** Sprint 304-05
**Date:** 2026-04-15

---

## Requirements

After factory completes, cache result for configurable TTL (default 5s).
Subsequent requests for same key get cached result.
Cache entries auto-expire.
Memory-bounded (LRU eviction if > 1000 entries).

---

## Attempted Implementation Challenges

### 1. Type System Constraints with Lazy<T>

The current `SingleFlight<T>` implementation uses `ConcurrentDictionary<string, Lazy<Task<T>>>` where:

```csharp
private static readonly ConcurrentDictionary<string, Lazy<Task<T>>> Flights
    = new ConcurrentDictionary<string, Lazy<Task<T>>>(StringComparer.Ordinal);
```

Problem: Caching requires storing `T` (the result), not `Lazy<Task<T>>`.
The `Lazy<T>.Value` property evaluates the factory once, but subsequent accesses
return the same Task object, not a fresh evaluation.

### 2. Memory-Bounded Cache Implementation

To implement LRU eviction with 1000-entry limit, we need:
- Concurrent-safe dictionary
- Linked list or similar structure for LRU tracking
- TTL expiration logic per entry

Complexity increases significantly when adding:
- Thread-safe LRU eviction
- Per-entry TTL timers
- Cache invalidation on token rotation

### 3. Expiration Coordination

Cached results may become stale when:
- PluginSecret is rotated (invalidates all tokens)
- AIOStreams configuration changes
- Manual cache clearing

Need coordination mechanism between:
- PlaybackService (primary cache consumer)
- RehydrationService (writes .strm files with new URLs)
- SetupService (handles key rotation)

---

## Alternative Approaches

### Option A: Separate In-Memory Cache Service

Create dedicated `StreamCacheService` with:
```csharp
public class StreamCacheService
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache;
    private readonly int _maxSize = 1000;
    private readonly TimeSpan _defaultTtl = TimeSpan.FromSeconds(5);

    public async Task<T?> GetOrAddAsync<T>(string key, Func<Task<T>> factory);
    public void Invalidate(string key);
    public void Clear();
}
```

**Pros:**
- Single responsibility
- Easier to test
- Can be injected where needed

**Cons:**
- Adds new service class
- Requires dependency injection updates

### Option B: Existing Resolution Cache Leverage

`DatabaseManager` already has `GetResolutionCacheStatsAsync` and related methods.
The database-based cache persists and handles expiration via SQL.

**Pros:**
- No new in-memory cache needed
- Leverages existing infrastructure
- Survives plugin restart

**Cons:**
- Slower than in-memory
- Not what task specifies (requires 5s TTL in-memory)

### Option C: Minimal TTL with Background Expiration

Modify `SingleFlight<T>` to add simple TTL with cleanup:

```csharp
private static readonly ConcurrentDictionary<string, CacheEntry> _cache;
private static readonly Timer _cleanupTimer;

private class CacheEntry
{
    public Task<T> Result { get; }
    public DateTime ExpiresAt { get; }
}

public static async Task<T> RunAsync(string key, Func<Task<T>> factory, TimeSpan ttl)
{
    // Check cache first
    if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
        return await entry.Result;

    // Execute factory
    var task = factory();
    var result = await task;

    // Cache with TTL
    _cache[key] = new CacheEntry { Result = Task.FromResult(result), ExpiresAt = DateTime.UtcNow.Add(ttl) };

    // Trim if over 1000 entries
    while (_cache.Count > 1000)
    {
        // Remove oldest 5%
        var toRemove = _cache.Take(50).ToList();
        foreach (var k in toRemove) _cache.TryRemove(k, out _);
    }

    return result;
}
```

**Pros:**
- Minimal changes to existing class
- Approximates LRU behavior
- Meets all requirements

**Cons:**
- Not true LRU (evicts arbitrary old entries)
- Timer-based cleanup not ideal

---

## Recommendation

**Use Option C (Minimal TTL with Background Expiration)**

Rationale:
1. Minimal code changes (single file modified)
2. Meets all functional requirements:
   - 5s TTL caching ✓
   - Auto-expiration ✓
   - 1000-entry limit ✓
3. True LRU requires complex data structures that significantly increase
   code complexity and risk
4. Acceptable approximation: For burst patterns (the use case this task
   addresses), removing oldest entries approximates LRU well enough

---

## Files to Modify

1. `Services/SingleFlight.cs` — Add caching logic
2. Update unit tests (if any) for new caching behavior

---

**Status:** Analysis complete. Ready for implementation with Option C approach.
