# Sprint 76 — Stremio-Kai Provider ID Handling

**Date:** 2026-04-02
**Source:** `/home/onehottake/research/Stremio-Kai/portable_config/webmods/Metadata/`

## Supported Provider ID Formats

| Provider | Format | Example | Detection |
|----------|--------|---------|-----------|
| IMDb | `tt\d{7,8}` | `tt1160419` | `tt` prefix |
| TMDB | `tmdb:\d+` | `tmdb:4194` | `tmdb:` prefix |
| TVDB | `tvdb:\d+` | `tvdb:81741` | `tvdb:` prefix |
| Kitsu | `kitsu:\d+` | `kitsu:48363` | `kitsu:` prefix |
| AniList | `anilist:\d+` | `anilist:12345` | `anilist:` prefix |
| MAL | `mal:\d+` | `mal:37510` | `mal:` prefix |

## ID Parsing (`route-detector.js`)

```javascript
function parseId(raw) {
    if (!raw) return null;
    // IMDb: tt prefix
    if (/^tt\d+$/.test(raw)) return { type: 'imdb', id: raw };
    // Provider:ID format
    const match = raw.match(/^(\w+):(\d+)$/);
    if (match) return { type: match[1].toLowerCase(), id: match[2] };
    // Plain number (ambiguous)
    if (/^\d+$/.test(raw)) return { type: 'unknown', id: raw };
    return null;
}
```

## ID Conversion (`id-conversion.js`)

### Haglund API

- **Endpoint:** `https://arm.haglund.dev/api/v2/ids`
- **Parameters:** `source={provider}&id={id}`
- **Returns:** JSON with all known provider IDs for the item
- **Rate limit:** 2 requests/sec (enforced by rate-limiter.js)
- **Cache:** LRU, 10,000 entries, 24-hour TTL

### Conversion Flow

```
Input ID → parseId() → detect type
  → Check LRU cache
  → If miss: Haglund API call
  → Cache result (all provider IDs)
  → Return requested provider ID
```

### LRU Cache Implementation

- Max 10,000 entries
- 24-hour TTL per entry
- Keyed by `{source}:{id}`
- Stores complete cross-reference (all provider IDs)
- Cache hit returns immediately without API call

## Cross-Reference Lookup (`id-lookup.js`)

### Purpose

Given any single provider ID, resolve all other provider IDs.

### Implementation

1. Check local cache (LRU)
2. If miss, query Haglund API
3. Store full cross-reference in cache
4. Return requested ID(s)

### Fallback Chain

When Haglund fails:
1. Try TMDB API directly (for movie/series → anime ID mapping)
2. Try Jikan API (for MAL → AniList/Kitsu mapping)
3. Return partial data with available IDs

## ID Storage (`metadata-storage.js`)

### IndexedDB Schema

```javascript
// Multi-entry index for each provider type
store.createIndex('imdb_id', 'ids.imdb', { unique: false });
store.createIndex('tmdb_id', 'ids.tmdb', { unique: false });
store.createIndex('kitsu_id', 'ids.kitsu', { unique: false });
store.createIndex('anilist_id', 'ids.anilist', { unique: false });
store.createIndex('mal_id', 'ids.mal', { unique: false });
store.createIndex('tvdb_id', 'ids.tvdb', { unique: false });
```

### Storage Format

```javascript
{
    id: "tt1160419",  // Primary key (first known ID)
    ids: {
        imdb: "tt1160419",
        tmdb: "4194",
        kitsu: "48363",
        anilist: "12345",
        mal: "37510",
        tvdb: "81741"
    },
    type: "series",  // movie | series
    lastUpdated: "2026-04-02T00:00:00Z"
}
```

## Relevance to EmbyStreams

### Current EmbyStreams State

- `CatalogItem` has `ImdbId` (required) and `TmdbId` (optional) as separate columns
- `ResolveImdbId()` discards non-IMDB IDs (Kitsu, AniList, MAL return empty string)
- No cross-provider ID conversion capability
- No caching of ID conversions

### Proposed EmbyStreams Equivalent

Based on Stremio-Kai patterns:

1. **`UniqueIdsJson` column** — `[{"provider":"kitsu","id":"48363"},{"provider":"anilist","id":"12345"}]`
2. **Haglund API client** — C# HttpClient wrapper for ID conversion
3. **SQLite-backed cache** — Replace LRU with SQLite table `id_conversions`
4. **Multi-provider lookup** — `GetCatalogItemByProviderIdAsync(provider, id)`
5. **NFO enrichment** — Write all known provider IDs to NFO files

### Key Differences

| Aspect | Stremio-Kai | Proposed EmbyStreams |
|--------|-------------|---------------------|
| Runtime | Browser | Server (C#) |
| Storage | IndexedDB | SQLite |
| Cache | LRU (memory) | SQLite table |
| ID format | `provider:id` strings | JSON array in column |
| Conversion API | Haglund | Haglund (same) |
| Rate limiting | Per-API queues | SemaphoreSlim per provider |
