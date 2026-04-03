# Sprint 76 — Stremio-Kai Anime Handling

**Date:** 2026-04-02
**Source:** `/home/onehottake/research/Stremio-Kai/portable_config/webmods/Metadata/`

## 3-Tier Anime Detection (`anime-detection.js`)

### Tier 1: Demographics

Check if the item has anime-demographic genres:
- Shounen, Shoujo, Seinen, Josei
- These are definitive indicators — if present, item IS anime

### Tier 2: Origin + Genre

If no demographics found:
1. Check `country_of_origin === "JP"` (Japan)
2. Check if genres include `"Animation"` or `"Anime"`
3. Both conditions → classified as anime

### Tier 3: Explicit Anime DB IDs

If still undetermined:
1. Check for MAL, AniList, or Kitsu IDs in metadata
2. Presence of any anime-database ID → classified as anime
3. This catches anime that may not have Japanese origin flag

### Detection Flow

```
Metadata Input
    │
    ├─ Has Shounen/Shoujo/Seinen/Josei genre? → YES = ANIME
    │
    ├─ Japan origin + Animation/Anime genre? → YES = ANIME
    │
    └─ Has MAL/AniList/Kitsu ID? → YES = ANIME
                                  → NO = NOT ANIME
```

## Anime Metadata Enrichment (`metadata-service.js`)

### Jikan API Integration

- **Purpose:** Fetch detailed anime metadata from MyAnimeList
- **Endpoint:** `https://api.jikan.moe/v4/anime/{mal_id}`
- **Rate limit:** 3 requests/sec (enforced by rate-limiter.js)
- **Returns:** Episode counts, studios, demographics, themes, scores

### Parallel Fetching Pattern

```javascript
const promises = {};
if (hasMalId) promises.jikan = fetchJikanDetails(malId);
if (hasImdbId) promises.tmdb = fetchTmdbDetails(imdbId);
if (hasKitsuId) promises.kitsu = fetchKitsuDetails(kitsuId);

const results = await Promise.allSettled(Object.values(promises));
// Merge results, preferring most detailed source
```

### Data Merge Strategy

1. Fetch from all available sources in parallel
2. Merge results with priority: Jikan > TMDB > Kitsu > Cinemeta
3. Store merged result in IndexedDB with all provider IDs
4. Use for both classification and UI display

## Anime Episode Handling (`details-enhancer.js`)

### Hybrid Episode Matching

For anime series, standard S01E01 numbering may not match the catalog:
- Anime often uses absolute episode numbering
- Some seasons are split across multiple cours
- Episode counts vary between providers

### Resolution Strategy

1. Try season/episode numbering from primary source
2. Fall back to absolute numbering
3. Use Jikan for canonical episode counts per season
4. Handle "Specials" (Season 0) separately

### Episode Count Sources (Priority Order)

1. Jikan API (most accurate for anime)
2. TMDB (good for mainstream anime)
3. Kitsu API (good for niche anime)
4. Cinemeta (fallback, may be inaccurate)
5. Haglund cross-reference (last resort)

## Anime ID Storage (`metadata-storage.js`)

### Multi-Entry Indexes

```javascript
// Anime-specific indexes for fast lookup
store.createIndex('kitsu_id', 'ids.kitsu');
store.createIndex('anilist_id', 'ids.anilist');
store.createIndex('mal_id', 'ids.mal');
```

### Anime ID Arrays

Some anime have multiple entries per provider (e.g., different MAL entries per season):
```javascript
{
    ids: {
        mal: ["37510", "38408"],  // Multiple MAL entries
        anilist: ["12345"],
        kitsu: ["48363"]
    }
}
```

## Rate Limiting (`rate-limiter.js`)

### Per-API Queue System

```javascript
const LIMITS = {
    haglund:  { maxConcurrent: 2, requestsPerSecond: 2 },
    jikan:    { maxConcurrent: 3, requestsPerSecond: 3 },
    tmdb:     { maxConcurrent: 5, requestsPerSecond: 5 },
    mdblist:  { maxConcurrent: 3, requestsPerSecond: 1 },
};
```

### Implementation

- Queue-based: requests wait in line per API
- Token bucket: respects requests-per-second
- Timeout: each request has configurable timeout
- Retry: up to 3 retries with exponential backoff

## Error Handling (`fetch-utils.js`)

### Retry with Exponential Backoff

```javascript
async function withRetry(fn, maxRetries = 3) {
    for (let attempt = 0; attempt <= maxRetries; attempt++) {
        try {
            return await fn();
        } catch (err) {
            if (attempt === maxRetries) throw err;
            const delay = Math.min(1000 * Math.pow(2, attempt), 30000);
            await sleep(delay);
        }
    }
}
```

### Timeout Protection

```javascript
async function withTimeout(fn, defaultTimeout = 5000) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), defaultTimeout);
    try {
        return await fn(controller.signal);
    } finally {
        clearTimeout(timeout);
    }
}
```

### Response Validation

```javascript
function validateShape(data, shape) {
    for (const [key, type] of Object.entries(shape)) {
        if (!(key in data) || typeof data[key] !== type) return false;
    }
    return true;
}
```

## Relevance to EmbyStreams

### Current EmbyStreams Anime Handling

- Detects anime via AIOStreams catalog type field (`"anime"`)
- Routes to `SyncPathAnime` directory if enabled
- No anime-specific metadata enrichment
- No cross-provider ID resolution
- No demographic or genre-based detection
- `GetEpisodeCountForSeason` only queries by IMDB (fails for anime without IMDB)

### Proposed Improvements (Priority Order)

1. **P0: Multi-provider episode count** — Use Kitsu/AniList/MAL fallback when IMDB returns 0 episodes
2. **P0: UniqueIdsJson storage** — Store all provider IDs in catalog_items for anime items
3. **P1: Haglund API client** — Convert between provider ID formats
4. **P1: Anime detection heuristic** — Basic tier-3 detection (has MAL/AniList/Kitsu ID = anime)
5. **P2: Jikan enrichment** — Fetch canonical anime metadata for episode counts
6. **P2: Rate limiting** — SemaphoreSlim per external API
7. **P3: Retry with backoff** — Replace simple try/catch with retry logic
8. **P3: Full 3-tier detection** — Implement all three detection tiers
