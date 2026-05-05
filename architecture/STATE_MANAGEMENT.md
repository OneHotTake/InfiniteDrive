# InfiniteDrive — State Management

> Last reconciled: 2026-05-05 (post-Sprint 519)

## 1. Database Layer

**Database:** infinitedrive.db (SQLite with WAL mode)
**Authority:** `DatabaseManager` — decomposed into 6 partial class files:

| File | Lines | Responsibility |
|------|-------|---------------|
| `DatabaseManager.cs` | 2341 | SQLite core, connection management, DDL |
| `DatabaseManager.MediaItems.cs` | 1022 | media_items, sources, memberships |
| `DatabaseManager.Catalog.cs` | 972 | catalog CRUD, user catalogs |
| `DatabaseManager.StreamCache.cs` | 460 | stream resolution cache, subtitle cache, dead-link probe |
| `DatabaseManager.Operations.cs` | 1028 | ingestion, playback log, API budget |
| `DatabaseManager.Discover.cs` | 404 | discover catalog CRUD |

### Row Mapper Rules

- **`SELECT *` queries:** MUST use name-based column lookup via `ColMap(table)` + `GetStr/GetReqStr/GetInt/...` helpers. Column order is not stable.
- **Explicit column list queries** (`SELECT id, imdb_id, ...`): Positional indexing (`r.GetString(0)`) is safe because column order is controlled by the query text.
- **Never share a mapper** between queries with different column lists.

### SQL Provider Matching

Always use `lower()` on BOTH sides of provider/id comparisons:
```sql
WHERE lower(json_extract(value, '$.provider')) = lower(@provider)
```

### JSON-First Pattern

- `raw_meta_json` column is the source of truth for rich metadata (images, cast, genres, etc.)
- `GetRawMetaJsonByProviderIdAsync()` is the canonical lookup — bypasses the row mapper entirely
- Prefer targeted queries over full-row mappers when only JSON data is needed

### Schema Policy (Alpha)

- No schema evolution / migrations allowed
- Only `CREATE TABLE IF NOT EXISTS` with bare-minimum columns + JSON fields
- All schema changes must be manual and destructive (drop & recreate is fine)
- Migration idempotency via `cache_migrated_v2` flag in `plugin_metadata` table

## 2. stream_resolution_cache (Consolidated Cache Table)

Replaces the former `resolution_cache`, `stream_candidates`, and `cached_streams` tables.

| Column | Type | Description |
|--------|------|-------------|
| `aio_id` | TEXT NOT NULL | Primary key — AIOStreams top-level id (supports IMDB, KITSU, MAL, etc.) |
| `imdb_id` | TEXT (nullable) | Only real tt-prefixed IDs |
| `tmdb_key` | TEXT (nullable) | Secondary lookup key |
| `season` | INTEGER (nullable) | Season number; NULL for movies |
| `episode` | INTEGER (nullable) | Episode number; NULL for movies |
| `rank` | INTEGER | Zero-based rank (0 = best) |
| `provider_key` | TEXT | Provider service key |
| `stream_type` | TEXT | Stream type (debrid, usenet, http) |
| `url` | TEXT | Direct playable HTTP URL |
| `headers_json` | TEXT (nullable) | JSON-serialized HTTP headers |
| `quality_tier` | TEXT (nullable) | Quality tier |
| `file_name` | TEXT (nullable) | Original filename from AIOStreams |
| `file_size` | INTEGER (nullable) | File size in bytes |
| `info_hash` | TEXT (nullable) | SHA1 info-hash |
| `file_idx` | INTEGER (nullable) | File index within torrent |
| `languages` | TEXT (nullable) | Comma-separated ISO 639-1 codes |
| `resolved_at` | TEXT | UTC timestamp when resolved |
| `expires_at` | TEXT | UTC timestamp when URL should be re-validated |
| `status` | TEXT | `valid`, `suspect`, or `failed` |

**UNIQUE constraint:** `(aio_id, COALESCE(season,-1), COALESCE(episode,-1), rank)`

**Stream identity:** `infoHash + fileIdx` — survives CDN URL rotation. Used for deduplication.

### catalog_items Table

Note: `catalog_items.imdb_id` stores the AIOStreams primary ID (confusing name — not always IMDB).

## 3. Manifest State Lifecycle

**Authority:** `Plugin.Manifest` (instance of `ManifestState`)

```
                    +------------------+
                    |  Error (default)  | <- Plugin startup, fetch failure
                    +--------+---------+
                             |
                   Successful fetch from
                   RefreshManifest endpoint
                             |
                             v
                    +------------------+
              +---->|       Ok         | <- Manifest loaded, within 12h TTL
              |     +--------+---------+
              |              |
              |     12 hours pass without
              |     refresh (CheckStale)
              |              |
              |              v
              |     +------------------+
              |     |      Stale       | <- Manifest loaded but > 12h old
              |     +--------+---------+
              |              |
              |     Successful refresh
              |              |
              +--------------+

          +------------------+
          |  NotConfigured   | <- No PrimaryManifestUrl in config
          +------------------+
```

### ManifestStatusState Enum

| Value | EnumMember | Meaning |
|-------|-----------|---------|
| `Error` | `"error"` | Fetch failed, never loaded, or exception during refresh. Default at startup. |
| `NotConfigured` | `"notConfigured"` | No PrimaryManifestUrl configured. |
| `Stale` | `"stale"` | Loaded successfully but > 12 hours since fetch. |
| `Ok` | `"ok"` | Loaded and within 12-hour TTL. |

### Staleness Threshold

12 hours, hardcoded in `ManifestState.StaleThreshold`. `CheckStale()` compares `DateTimeOffset.UtcNow - FetchedAt` against this threshold.

## 4. Pipeline Phase Tracking

**Authority:** `Plugin.Pipeline` (instance of `PipelinePhaseTracker`)

### PipelinePhase DTO

```
PipelinePhase(
    string TaskName,         // "Refresh", "Marvin", "CatalogSync"
    string PhaseName,        // "Collect", "Enrichment", "Fetch", etc.
    DateTimeOffset StartedAt,
    int ItemsTotal,
    int ItemsProcessed)
```

### Tasks Reporting Phases

| Task | Phases |
|------|--------|
| MarvinTask | Validation -> Enrichment -> TokenRenewal -> SaveMaintenance |
| CatalogSyncTask | BuildProviders -> Fetch |
| RefreshTask | Collect -> Write -> Hint -> Enrich -> Notify -> Verify |

### Visibility

- `Plugin.Pipeline.Current` -> thread-safe read
- `HealthResponse.ActivePipeline` -> exposed via `/InfiniteDrive/Health` endpoint
- Available to admin UI for real-time status display

## 5. Item Lifecycle

**Authority:** `ItemState` enum in `Models/ItemState.cs`

```
Catalogued --> Present --> Resolved --> Retired
     |            |            |            |
     |            |            |            +-- Re-enter via RehydrationService
     |            |            |
     |            v            v
     |        Pinned       Orphaned
     |                        |
     v                        +-- Cleaned up by HousekeepingService
  Queued
     |
     v
  Written --> Notified --> Ready
     |
     v
  NeedsEnrich --> (MetadataEnrichmentService)
     |
     v
  Blocked --> (permanently removed, no retry)
```

## 6. Plugin.Instance State

Key singletons accessible via `Plugin.Instance`:

| Property | Type | Purpose |
|----------|------|---------|
| `DatabaseManager` | DatabaseManager | SQLite data layer (6 partial class files) |
| `StreamCacheService` | StreamCacheService | Cached stream read/write, BuildMediaSources |
| `ResolverHealthTracker` | ResolverHealthTracker | Circuit breaker for stream providers |
| `CooldownGate` | CooldownGate | HTTP throttling with configurable cooldown |
| `ProgressStreamer` | ProgressStreamer | SSE broadcaster for real-time progress |

## 7. Provider Failover State

**Authority:** `Plugin.Instance.ActiveProviderState`

```
  Primary ------> Secondary ------> Primary
    |                |                  ^
    |   Primary      |   MarvinTask     |
    |   fails        |   TryRestore     |
    |   (circuit     |   PrimaryAsync   |
    |    breaker)    |   probes primary |
    v                v                  |
  (requests go     (requests go        |
   to secondary)    to secondary)      |
```

### ResolverHealthTracker (Circuit Breaker)

Per-provider circuit state. When failures exceed threshold, provider is marked "open" and skipped until `RestoreState()` is called (typically by MarvinTask).

## 8. CooldownGate

**Purpose:** HTTP throttling with configurable cooldown periods.

**CooldownKind enum:** `Default`, `SeriesMeta` (collapsed from 4 values)
**InstanceType:** `Shared`, `Private` — auto-detected from manifest URL

Centralized `ParseRetryAfter` for rate-limit handling. Exponential backoff in StreamHelpers.

## 9. Language & Localization

**Authority:** `PluginConfiguration.MetadataLanguage` + `PluginConfiguration.MetadataCountryCode`
**Default:** `en` / `US`

### Language Fallback Chain

```
1. User's PreferredMetadataLanguage (Emby user settings)
2. Plugin's MetadataLanguage (admin config)
3. Library's PreferredMetadataLanguage (Emby library settings)
4. Rank-order (no preference)
```

### Languages column (stream_resolution_cache)

Comma-separated ISO 639-1 codes (e.g. `"ja,en"`). Populated from `ParsedFile.Languages` when streams are resolved via AIOStreamsClient. Used by `MapCandidateToSource()` to build audio `MediaStreams` for cached candidates.
