# Sprint 142 — Schema + Ingestion State

**Version:** v4.0 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 141

---

## Overview

Lay the database foundation for the Library Worker architecture. Add the `ingestion_state` and `refresh_run_log` tables, expand the `catalog_items` schema with new lifecycle columns (`nfo_status`, `retry_count`, `next_retry_at`), and widen `media_type` to accept `anime`/`episode`/`other`. This sprint does not introduce any new workers — it only prepares the data layer so subsequent sprints can build on a stable schema.

### Why This Exists

The current schema supports a batch-oriented Doctor that runs every 4 hours. The Library Worker design needs:
- Per-source watermark tracking for incremental polling (`ingestion_state`)
- Structured run logging for the Health Panel (`refresh_run_log`)
- NFO enrichment lifecycle tracking (`nfo_status`, `retry_count`, `next_retry_at`)
- Expanded media type support (`anime`, `episode`, `other`)

All subsequent sprints (143-147) depend on this schema being in place.

---

## Phase 142A — New Tables

### FIX-142A-01: Create `ingestion_state` table

**File:** `Data/DatabaseManager.cs` (modify — CreateSchema and MigrateSchema)

**What:**
1. Add `ingestion_state` to `Tables` constant class
2. In `CreateSchema`, add:
```sql
CREATE TABLE IF NOT EXISTS ingestion_state (
    source_id      TEXT PRIMARY KEY,
    last_poll_at   TEXT,        -- ISO8601 UTC
    last_found_at  TEXT,        -- ISO8601 UTC
    watermark      TEXT         -- cursor / ETag / last item ID for delta pulls
);
```
3. In `MigrateSchema`, add V24 migration block
4. Add repository methods:
   - `Task<IngestionState?> GetIngestionStateAsync(string sourceId, CancellationToken ct)`
   - `Task UpsertIngestionStateAsync(IngestionState state, CancellationToken ct)`
5. Create `Models/IngestionState.cs` — simple POCO with SourceId, LastPollAt, LastFoundAt, Watermark properties

**Depends on:** Nothing (foundation)

### FIX-142A-02: Create `refresh_run_log` table

**File:** `Data/DatabaseManager.cs` (modify)

**What:**
1. Add `refresh_run_log` to `Tables` constant class
2. In `CreateSchema`, add:
```sql
CREATE TABLE IF NOT EXISTS refresh_run_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_at          TEXT NOT NULL DEFAULT (datetime('now')),
    worker          TEXT NOT NULL,       -- "Refresh" or "Deep Clean"
    step            TEXT NOT NULL,       -- "Collect", "Write", "Hint", "Notify", "Verify"
    status          TEXT NOT NULL DEFAULT 'started',
    items_affected  INTEGER DEFAULT 0,
    notes           TEXT
);
```
3. In `MigrateSchema`, include in V24 block
4. Add repository methods:
   - `Task<long> InsertRunLogAsync(string worker, string step, CancellationToken ct)`
   - `Task UpdateRunLogAsync(long id, string status, int itemsAffected, string? notes, CancellationToken ct)`
   - `Task<RefreshRunLog?> GetLatestRunAsync(string worker, CancellationToken ct)`
5. Create `Models/RefreshRunLog.cs` — POCO mapping

**Depends on:** Nothing

---

## Phase 142B — Catalog Item Schema Expansion

### FIX-142B-01: Add lifecycle columns to `catalog_items`

**File:** `Data/DatabaseManager.cs` (modify)

**What:**
1. In V24 migration block, add ALTER TABLE statements:
```sql
ALTER TABLE catalog_items ADD COLUMN nfo_status TEXT;
ALTER TABLE catalog_items ADD COLUMN retry_count INTEGER DEFAULT 0;
ALTER TABLE catalog_items ADD COLUMN next_retry_at INTEGER;  -- Unix timestamp, consistent with strm_token_expires_at
```
2. Update `catalog_items` CREATE TABLE to include the new columns in fresh installs
3. Add `NfoStatus`, `RetryCount`, `NextRetryAt` properties to `Models/CatalogItem.cs`
4. Update `UpsertCatalogItemAsync` to include new columns in INSERT/ON CONFLICT
5. Update `ReadCatalogItem` to read new columns
6. Add repository methods:
   - `Task UpdateNfoStatusAsync(string imdbId, string source, string nfoStatus, int? retryCount, string? nextRetryAt, CancellationToken ct)`
   - `Task<List<CatalogItem>> GetItemsByNfoStatusAsync(string nfoStatus, int limit, CancellationToken ct)`

**Depends on:** FIX-142A-01

### FIX-142B-02: Expand `media_type` CHECK constraint

**File:** `Data/DatabaseManager.cs` (modify)

**What:**
1. The current CHECK is `CHECK(media_type IN ('movie', 'series'))`. SQLite does not support ALTER TABLE ... ALTER CONSTRAINT. Strategy:
   - In V24 migration: create new table `catalog_items_v24` with widened constraint `CHECK(media_type IN ('movie', 'series', 'anime', 'episode', 'other'))`
   - Copy data across
   - Drop old table
   - Rename
   - Recreate indexes
2. Update `CREATE TABLE IF NOT EXISTS catalog_items` in `CreateSchema` to use widened constraint
3. Keep lowercase values to match existing codebase convention

**IMPORTANT:** This is a table rebuild migration. It must be done inside a transaction.

**Depends on:** FIX-142A-01

---

## Phase 142C — Model Updates

### FIX-142C-01: Update ItemState enum and CatalogItem model

**File:** `Models/ItemState.cs`, `Models/CatalogItem.cs` (modify)

**What:**
1. Add new states to `ItemState` enum for the Refresh lifecycle:
```csharp
Queued = 6,      // New/changed item awaiting .strm write
Written = 7,     // .strm on disk, awaiting Emby notification
Notified = 8,    // Emby notified, awaiting verification
Ready = 9,       // Fully verified, item is live
NeedsEnrich = 10, // NFO enrichment needed
Blocked = 11     // Enrichment failed after max retries
```
2. Add properties to `CatalogItem.cs`:
   - `string? NfoStatus`
   - `int RetryCount`
   - `long? NextRetryAt`  -- Unix timestamp (INTEGER), matches strm_token_expires_at

**Depends on:** FIX-142B-01

---

## Phase 142D — Build Verification

### FIX-142D-01: Build + schema migration smoke test

**What:**
1. `dotnet build -c Release` — 0 errors, 0 new warnings
2. `./emby-reset.sh` — server starts, plugin loads
3. Verify V24 migration applied: `SELECT version FROM schema_version ORDER BY version DESC LIMIT 1;` should return 24
4. Verify `ingestion_state` table exists and is empty
5. Verify `refresh_run_log` table exists and is empty
6. Verify `catalog_items` has new columns: `nfo_status`, `retry_count`, `next_retry_at`
7. Verify `media_type` CHECK accepts 'anime': `INSERT INTO catalog_items (...) VALUES (..., 'anime', ...)` should succeed
8. Verify DoctorTask still runs (existing token rotation code works)

**Depends on:** FIX-142C-01, FIX-142B-02

---

## Sprint 142 Dependencies

- **Previous Sprint:** 141 (Token Rotation Housekeeping)
- **Blocked By:** Sprint 141
- **Blocks:** Sprint 143 (RefreshTask needs ingestion_state and new ItemState values)

---

## Sprint 142 Completion Criteria

- [ ] `ingestion_state` table created with correct schema
- [ ] `refresh_run_log` table created with correct schema
- [ ] `catalog_items` has `nfo_status`, `retry_count`, `next_retry_at` columns
- [ ] `media_type` CHECK accepts 'movie', 'series', 'anime', 'episode', 'other'
- [ ] `strm_token_expires_at` remains INTEGER (no type change — deviation from spec)
- [ ] `ItemState` enum has Queued, Written, Notified, Ready, NeedsEnrich, Blocked
- [ ] `CatalogItem` model has NfoStatus, RetryCount, NextRetryAt properties
- [ ] `IngestionState` and `RefreshRunLog` models created
- [ ] Repository methods for new tables exist
- [ ] Build succeeds with 0 errors, 0 new warnings
- [ ] V24 migration applies cleanly on fresh install and on existing DB
- [ ] DoctorTask Phase 6 token rotation still works
- [ ] No integer collision between new ItemState values (6-11) and existing enum assignments verified

---

## Sprint 142 Notes

**Files created:** 2 (`Models/IngestionState.cs`, `Models/RefreshRunLog.cs`)
**Files modified:** ~3 (`Data/DatabaseManager.cs`, `Models/CatalogItem.cs`, `Models/ItemState.cs`)

**Risk assessment:** MEDIUM. The table rebuild migration (expanding CHECK) is the most dangerous operation. It runs inside a transaction and copies all data. On a library of 10,000 items, this completes in seconds.

**Spec deviation:** `strm_token_expires_at` remains INTEGER. The spec says TEXT ISO8601, but the column is a scheduling hint only — the actual expiry is baked into the HMAC token payload. A table rebuild to change the type is unnecessary risk. All existing code writes Unix timestamps correctly. If the user wants ISO8601, it can be done in a future sprint.

**Why media_type expansion is a table rebuild:** SQLite does not support `ALTER TABLE ... ALTER COLUMN` or modifying CHECK constraints. The standard approach is create-new / copy / drop-old / rename.

**Timestamp consistency:** `next_retry_at` is INTEGER (Unix timestamp), matching `strm_token_expires_at`. All time comparisons in the same table use the same format. The ingestion_state table uses TEXT ISO8601 for its timestamps (last_poll_at, last_found_at) — these are display-only fields, never compared against catalog_items timestamps in SQL.

**Run log status lifecycle:** Default is `'started'`. Workers must close every row with `'completed'`, `'faulted'`, or `'skipped'` in a finally block. If the server crashes mid-run, `refresh_run_log` rows stuck in `'started'` are distinguishable from completed runs. The Health Panel should treat `'started'` rows older than 30 minutes as indicative of a crash.

---
