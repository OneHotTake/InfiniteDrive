# Sprint 76 — Stremio-Kai Recommendations for EmbyStreams

**Date:** 2026-04-02
**Priority:** P0 = blockers / critical gaps, P1 = significant improvements, P2 = nice-to-have, P3 = future

## P0: Multi-Provider ID Support

### Problem
EmbyStreams only stores and queries by IMDB ID. Anime items frequently lack IMDB IDs in AIOStreams catalogs, causing them to be silently dropped by `ResolveImdbId()` which returns empty for non-IMDB formats.

### Recommendation
Implement `UniqueIdsJson` flexible storage in `catalog_items`:

**Schema change:**
```sql
ALTER TABLE catalog_items ADD COLUMN unique_ids_json TEXT;
```

**Storage format:**
```json
[{"provider":"imdb","id":"tt1160419"},{"provider":"kitsu","id":"48363"},{"provider":"anilist","id":"12345"}]
```

**Key changes:**
- `CatalogItem.cs`: Add `UniqueIdsJson` property
- `DatabaseManager.cs`: Add `GetCatalogItemByProviderIdAsync(provider, id)` method
- `MapMetaToItem`: Store all provider IDs from `meta.UniqueIds` or individual ID fields
- `GetEpisodeCountForSeason`: Fallback chain IMDB → Kitsu → AniList → MAL

**Effort:** Medium (3-5 files, schema migration)
**Impact:** High — unlocks anime support for items without IMDB IDs

---

## P0: Multi-Provider Episode Count Resolution

### Problem
`GetEpisodeCountForSeason` in `EmbyEventHandler.cs` only queries by IMDB provider ID. Anime series without IMDB IDs in Emby's library will always fall back to the hardcoded 30-episode cap.

### Recommendation
Implement provider fallback chain:

```csharp
private int GetEpisodeCountForSeason(string imdbId, int season)
{
    // Try IMDB first (existing logic)
    var count = QueryByProviderId("Imdb", imdbId, season);
    if (count > 0) return count;

    // Fallback to anime provider IDs from UniqueIdsJson
    var uniqueIds = GetUniqueIdsForItem(imdbId);
    foreach (var (provider, id) in uniqueIds)
    {
        count = QueryByProviderId(provider, id, season);
        if (count > 0) return count;
    }

    return 30; // existing fallback
}
```

**Effort:** Small (modify 1 method + DB helper)
**Impact:** High — fixes binge pre-warm for anime

---

## P1: Haglund API Client for ID Conversion

### Problem
No way to convert between provider ID formats (e.g., Kitsu ID → IMDB ID).

### Recommendation
Create `Services/HaglundIdConverter.cs`:

```csharp
public class HaglundIdConverter
{
    // GET https://arm.haglund.dev/api/v2/ids?source=kitsu&id=48363
    // Returns: { "imdb": "tt1160419", "tmdb": "4194", "kitsu": "48363", ... }

    // SQLite cache table: id_conversions (source, id, result_json, cached_at)
    // TTL: 24 hours
    // Rate limit: SemaphoreSlim(2) — 2 concurrent requests
}
```

**Effort:** Medium (new service + cache table)
**Impact:** Medium — enables ID conversion for items with only anime IDs

---

## P1: Basic Anime Detection Heuristic

### Problem
Anime detection relies entirely on AIOStreams catalog type field. Items from mixed catalogs (Marvel, StarWars) or Cinemeta won't be classified as anime even if they are.

### Recommendation
Implement Tier 3 detection (simplest, highest impact):

```csharp
internal static bool IsAnime(CatalogItem item, AioStreamsMeta meta)
{
    // Check catalog type (existing)
    if (item.CatalogType == "anime") return true;

    // Check for anime-specific provider IDs
    if (HasProviderId(meta, "kitsu") ||
        HasProviderId(meta, "anilist") ||
        HasProviderId(meta, "mal"))
        return true;

    return false;
}
```

**Effort:** Small (1 method, called from `MapMetaToItem`)
**Impact:** Medium — catches anime from non-anime catalogs

---

## P2: External API Rate Limiting

### Problem
No rate limiting for external API calls (Haglund, Jikan, TMDB). Risk of being blocked during large syncs.

### Recommendation
Create `Services/ApiRateLimiter.cs` using `SemaphoreSlim` + `Stopwatch`-based token bucket:

```csharp
public class ApiRateLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _requestsPerSecond;

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> fn) { ... }
}
```

Register as singleton in DI, inject into API-calling services.

**Effort:** Small (1 utility class)
**Impact:** Low (prevents rate-limit blocks, only matters with Haglund/Jikan)

---

## P2: Retry with Exponential Backoff

### Problem
External API calls use simple try/catch with no retry. Transient network errors cause permanent failures.

### Recommendation
Create `Helpers/RetryHelper.cs`:

```csharp
public static async Task<T> WithRetryAsync<T>(
    Func<Task<T>> fn,
    int maxRetries = 3,
    Func<int, TimeSpan> backoff = null)
{
    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        try { return await fn(); }
        catch when (attempt < maxRetries)
        {
            await Task.Delay(backoff?(attempt) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)));
        }
    }
    throw new InvalidOperationException("Unreachable");
}
```

**Effort:** Small (1 utility method)
**Impact:** Low (improves reliability of transient-failure-prone APIs)

---

## P3: Jikan API Integration for Anime Metadata

### Problem
No anime-specific metadata enrichment. Episode counts for anime come from Emby's library (which may not have them) or the fallback cap.

### Recommendation
Create `Services/JikanMetadataService.cs`:
- Fetch canonical anime metadata from MyAnimeList
- Use for episode count verification
- Store in cache with 7-day TTL

**Effort:** Medium (new service + cache)
**Impact:** Low-Medium (improves anime metadata accuracy)

---

## P3: Full 3-Tier Anime Detection

### Problem
Only basic detection via catalog type and provider IDs.

### Recommendation
Implement Tiers 1-2 from Stremio-Kai:
- Tier 1: Check for demographic genres (Shounen, Seinen, etc.)
- Tier 2: Check Japan origin + Animation genre
- Requires genre data from metadata providers

**Effort:** Medium (requires metadata enrichment pipeline)
**Impact:** Low (most anime already caught by Tier 3)

---

## Implementation Priority Summary

| Priority | Item | Effort | Impact | Sprint Estimate |
|----------|------|--------|--------|-----------------|
| P0 | UniqueIdsJson storage | Medium | High | 1 sprint |
| P0 | Multi-provider episode count | Small | High | Same sprint as above |
| P1 | Haglund ID converter | Medium | Medium | 1 sprint |
| P1 | Basic anime detection | Small | Medium | Same sprint as Haglund |
| P2 | API rate limiting | Small | Low | Half sprint |
| P2 | Retry with backoff | Small | Low | Half sprint |
| P3 | Jikan integration | Medium | Low-Med | 1 sprint |
| P3 | Full 3-tier detection | Medium | Low | 1 sprint |

**Recommended order:** P0 (Sprint 77) → P1 (Sprint 78) → P2 (Sprint 79) → P3 (future)
