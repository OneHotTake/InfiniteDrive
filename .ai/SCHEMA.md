# Database Schema — All CREATE TABLE Statements

**Date:** 2026-04-08 | **Source:** Data/Schema.cs & Data/DatabaseManager.cs

---

## Schema Version Tables

```sql
CREATE TABLE schema_version (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL,
    migration_script TEXT
);
```

---

## Core Catalog Tables

```sql
-- Sources and collections
CREATE TABLE IF NOT EXISTS sources (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    type TEXT NOT NULL,
    config TEXT NOT NULL,
    enabled INTEGER DEFAULT 1,
    last_sync_at TEXT,
    last_error TEXT
);

CREATE TABLE IF NOT EXISTS collections (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    source_id TEXT NOT NULL,
    config TEXT NOT NULL,
    enabled INTEGER DEFAULT 1,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS collection_membership (
    collection_id TEXT NOT NULL,
    imdb_id TEXT NOT NULL,
    added_at TEXT NOT NULL,
    PRIMARY KEY (collection_id, imdb_id),
    FOREIGN KEY (collection_id) REFERENCES collections(id) ON DELETE CASCADE
);
```

---

## Media ID and Metadata Tables

```sql
CREATE TABLE IF NOT EXISTS media_items (
    id TEXT PRIMARY KEY,
    provider TEXT NOT NULL,
    provider_id TEXT NOT NULL,
    imdb_id TEXT NOT NULL,
    tmdb_id TEXT,
    kitsu_id TEXT,
    mal_id TEXT,
    title TEXT NOT NULL,
    year INTEGER,
    poster_url TEXT,
    backdrop_url TEXT,
    overview TEXT,
    first_air_date TEXT,
    episode_count INTEGER,
    season_count INTEGER,
    status TEXT,
    last_updated TEXT
);

CREATE TABLE IF NOT EXISTS media_item_ids (
    media_item_id TEXT NOT NULL,
    id_type TEXT NOT NULL,
    provider_id TEXT NOT NULL,
    PRIMARY KEY (media_item_id, id_type, provider_id),
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE
);
```

---

## Discovery and Sync Tables

```sql
-- AIOStreams discovery catalog
CREATE TABLE IF NOT EXISTS discover_catalog (
    id TEXT PRIMARY KEY,
    imdb_id TEXT NOT NULL UNIQUE,
    tmdb_id TEXT,
    title TEXT NOT NULL,
    year INTEGER,
    media_type TEXT NOT NULL,  -- 'movie' or 'series'
    seasons_json TEXT,
    catalog_type TEXT NOT NULL,  -- 'anime', 'series', 'movie'
    source_list_id TEXT,
    source TEXT NOT NULL,  -- 'trakt', 'mdblist', 'aiostreams'
    added_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- Sync state tracking
CREATE TABLE IF NOT EXISTS sync_state (
    source_id TEXT NOT NULL,
    watermark TEXT,
    last_successful_at TEXT,
    last_error TEXT,
    retry_count INTEGER DEFAULT 0,
    PRIMARY KEY (source_id),
    FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);
```

---

## Resolution and Cache Tables

```sql
-- Stream resolution log
CREATE TABLE IF NOT EXISTS stream_resolution_log (
    id TEXT PRIMARY KEY,
    imdb_id TEXT NOT NULL,
    quality TEXT NOT NULL,
    resolved_url TEXT,
    provider TEXT,
    source TEXT,
    created_at TEXT NOT NULL,
    expires_at TEXT
);

-- Multi-layer stream cache
CREATE TABLE IF NOT EXISTS stream_cache (
    id TEXT PRIMARY KEY,
    imdb_id TEXT NOT NULL,
    quality TEXT NOT NULL,
    tier INTEGER NOT NULL,
    url TEXT NOT NULL,
    headers_json TEXT,
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    access_count INTEGER DEFAULT 0,
    last_accessed_at TEXT,
    UNIQUE (imdb_id, quality, tier)
);

CREATE TABLE IF NOT EXISTS resolution_cache (
    id TEXT PRIMARY KEY,
    imdb_id TEXT NOT NULL,
    quality TEXT NOT NULL,
    resolved_url TEXT NOT NULL,
    headers_json TEXT,
    cached_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    provider TEXT,
    source TEXT,
    stream_id TEXT,
    is_4k INTEGER,
    is_hdr INTEGER,
    bitrate INTEGER,
    UNIQUE (imdb_id, quality)
);
```

---

## Versioned Playback Tables (Sprint 122-129)

```sql
-- Version slots configuration
CREATE TABLE IF NOT EXISTS version_slots (
    id TEXT PRIMARY KEY,
    title_id TEXT NOT NULL,  -- catalog_items.imdb_id
    quality TEXT NOT NULL,
    enabled INTEGER DEFAULT 1,
    priority INTEGER DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- Candidate streams for slots
CREATE TABLE IF NOT EXISTS candidates (
    id TEXT PRIMARY KEY,
    slot_id TEXT NOT NULL,
    provider TEXT NOT NULL,
    source TEXT NOT NULL,
    stream_id TEXT,
    url TEXT NOT NULL,
    is_4k INTEGER DEFAULT 0,
    is_hdr INTEGER DEFAULT 0,
    bitrate INTEGER,
    codec TEXT,
    container TEXT,
    headers_json TEXT,
    created_at TEXT NOT NULL,
    FOREIGN KEY (slot_id) REFERENCES version_slots(id) ON DELETE CASCADE,
    UNIQUE (slot_id, provider, source, stream_id)
);

-- Version snapshots
CREATE TABLE IF NOT EXISTS version_snapshots (
    id TEXT PRIMARY KEY,
    slot_id TEXT NOT NULL,
    snapshot TEXT NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (slot_id) REFERENCES version_slots(id) ON DELETE CASCADE
);

-- Materialized versions (written to .strm files)
CREATE TABLE IF NOT EXISTS materialized_versions (
    id TEXT PRIMARY KEY,
    slot_id TEXT NOT NULL,
    snapshot_id TEXT NOT NULL,
    strm_path TEXT NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (slot_id) REFERENCES version_slots(id) ON DELETE CASCADE,
    FOREIGN KEY (snapshot_id) REFERENCES version_snapshots(id) ON DELETE CASCADE
);
```

---

## Item Pipeline Tables

```sql
CREATE TABLE IF NOT EXISTS item_pipeline_log (
    id TEXT PRIMARY KEY,
    imdb_id TEXT NOT NULL,
    pipeline_step TEXT NOT NULL,
    status TEXT NOT NULL,
    error_message TEXT,
    started_at TEXT NOT NULL,
    completed_at TEXT,
    FOREIGN KEY (imdb_id) REFERENCES catalog_items(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS stream_candidates (
    id TEXT PRIMARY KEY,
    catalog_item_id TEXT NOT NULL,
    provider TEXT,
    stream_id TEXT,
    url TEXT,
    created_at TEXT NOT NULL,
    FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
);
```

---

## Plugin Metadata Tables

```sql
CREATE TABLE IF NOT EXISTS plugin_metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
```

---

## Home Section Tracking Table

```sql
CREATE TABLE IF NOT EXISTS home_section_tracking (
    id TEXT PRIMARY KEY,
    user_id TEXT,
    section_name TEXT NOT NULL,
    view_count INTEGER DEFAULT 0,
    last_viewed_at TEXT
);
```

---

## Playback and Client Tracking Tables

```sql
CREATE TABLE IF NOT EXISTS playback_log (
    id TEXT PRIMARY KEY,
    imdb_id TEXT NOT NULL,
    user_id TEXT,
    client TEXT,
    played_at TEXT NOT NULL,
    quality TEXT,
    duration_seconds INTEGER,
    FOREIGN KEY (imdb_id) REFERENCES catalog_items(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS client_compat (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    profile_json TEXT NOT NULL,
    last_updated TEXT NOT NULL
);
```

---

## Main Catalog Table

```sql
-- Primary catalog items table (Doctor-era state machine)
CREATE TABLE IF NOT EXISTS catalog_items (
    id TEXT PRIMARY KEY,
    imdb_id TEXT NOT NULL,
    tmdb_id TEXT,
    unique_ids_json TEXT,
    title TEXT NOT NULL,
    year INTEGER,
    media_type TEXT NOT NULL,
    source TEXT NOT NULL,
    source_list_id TEXT,
    catalog_type TEXT,
    seasons_json TEXT,
    strm_path TEXT,
    local_path TEXT,
    local_source TEXT,
    added_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    removed_at TEXT,
    resurrection_count INTEGER DEFAULT 0,
    catalog_type TEXT,
    item_state INTEGER DEFAULT 0,  -- ItemState enum: 0=Catalogued, 1=Present, 2=Resolved, 3=Retired, 4=Orphaned, 5=Pinned
    pin_source TEXT,
    pinned_at TEXT
);
```

---

## Summary

**Total Tables:** 18

| Category | Tables |
|----------|---------|
| Schema & Metadata | schema_version, plugin_metadata |
| Sources & Collections | sources, collections, collection_membership |
| Media IDs | media_items, media_item_ids |
| Discovery | discover_catalog, sync_state |
| Resolution & Cache | stream_resolution_log, stream_cache, resolution_cache |
| Versioned Playback | version_slots, candidates, version_snapshots, materialized_versions |
| Item Pipeline | item_pipeline_log, stream_candidates |
| Home Tracking | home_section_tracking |
| Playback & Clients | playback_log, client_compat |
| Main Catalog | catalog_items |

---

## Notes

- **State Machine:** `catalog_items.item_state` uses Doctor-era values (Catalogued, Present, Resolved, Retired, Orphaned, Pinned)
- **Versioned Playback:** Tables added in Sprint 122-129 for multi-version playback with slots
- **Missing from Lost Sprints:** No tables for run logs, enrichment status, or refresh/health worker state
