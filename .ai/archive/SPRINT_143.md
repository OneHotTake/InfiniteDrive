# Sprint 143 — RefreshTask: Collect + Write + Hint

**Version:** v4.0 | **Status:** Plan | **Risk:** HIGH | **Depends:** Sprint 142

---

## Overview

Create the `RefreshTask` scheduled task implementing the first three steps of the 5-step pipeline: Collect (pull new/changed items since watermark), Write (.strm files with resolve tokens), and Hint (Identity Hint NFO alongside every .strm). This is the core of the Library Worker design — a fast, 6-minute cycle that processes only incremental changes.

### Why This Exists

The current DoctorTask runs every 4 hours and processes the entire catalog. The RefreshTask replaces this with a 6-minute incremental cycle:
- **Collect** queries AIOStreams for items newer than the `ingestion_state` watermark
- **Write** produces ~100-byte .strm files with signed resolve URLs
- **Hint** writes minimal NFO files with catalog IDs for Emby scanner matching

Steps 4-5 (Notify, Verify) are deferred to Sprint 144.

---

## Phase 143A — RefreshTask Skeleton

### FIX-143A-01: Create `Tasks/RefreshTask.cs`

**File:** `Tasks/RefreshTask.cs` (new)

**What:**
1. Implement `IScheduledTask`:
```csharp
public class RefreshTask : IScheduledTask
{
    private readonly ILogger<RefreshTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private static readonly SemaphoreSlim _runningGate = new(1, 1);
```
2. Schedule: `TaskTriggerInfo.TriggerInterval` with `TimeSpan.FromMinutes(6).Ticks`
3. TaskKey: `"EmbyStreamsRefresh"`
4. Name: `"EmbyStreams Refresh Worker"`
5. `Execute` method:
   - Try-acquire `_runningGate` with 0 timeout — if acquisition fails, log "already running, skipping" and return
   - Acquire `Plugin.SyncLock`
   - Create run log entry
   - Call Step 1 (Collect)
   - Call Step 2 (Write)
   - Call Step 3 (Hint)
   - Update run log on completion
   - Release `Plugin.SyncLock` and `_runningGate` in finally blocks
6. Constructor: `ILibraryManager`, `ILogManager` (same pattern as DoctorTask)
7. Use `EmbyLoggerAdapter<RefreshTask>`

**Depends on:** Sprint 142 (ItemState.Queued, ingestion_state)

---

## Phase 143B — Step 1: Collect

### FIX-143B-01: Implement Collect step

**File:** `Tasks/RefreshTask.cs` (modify)

**What:**
1. For each configured source (from sync_state or catalog sources):
   a. Load `ingestion_state` for this source
   b. If no watermark exists, treat as "poll everything" (first run)
   c. Call AIOStreams catalog endpoint (full fetch + diff against DB)
   d. Compare returned items against existing `catalog_items`
   e. New items: set `ItemState = Queued`, insert to catalog_items
   f. Changed items (title/year update): set `ItemState = Queued`, update catalog_items
   g. Unchanged items: skip
   h. Update `ingestion_state`: set `last_poll_at = now`, `last_found_at = now`, `watermark = <latest>`
2. If no new/changed items found: log "nothing new" and return early (skip Steps 2-3)
3. Rate limit: 1 AIOStreams request per source per run
4. Write run log: step="collect", items_affected=count

**AIOStreams polling strategy:** Use the existing `AioStreamsClient` to fetch catalog entries. AIOStreams does not expose a "since" filter, so the diff is full-fetch + in-memory comparison against DB. This matches CatalogSyncTask's approach but runs more frequently.

**Circuit breaker:** The AIOStreams circuit breaker (Sprint 103, `AioStreamsClient` resilience policy) must be wired into the Collect step's HTTP call. If the circuit is open, Collect logs a warning and exits early — no full catalog fetch attempted. Verify `AioStreamsClient` resilience policy is active before relying on it here.

**Depends on:** FIX-143A-01

---

## Phase 143C — Step 2: Write

### FIX-143C-01: Implement Write step

**File:** `Tasks/RefreshTask.cs` (modify)

**What:**
1. Query all items with `ItemState = Queued`:
```sql
SELECT * FROM catalog_items WHERE item_state = @queued AND removed_at IS NULL;
```
2. Load enabled slots from `VersionSlotRepository` (same pattern as DoctorTask Phase 2)
3. For each queued item:
   a. Ensure target directory exists
   b. Build folder name: `{Title} ({Year}) [tmdbid=XXXX]` if TMDB ID available, else `{Title} ({Year}) [imdbid-{ImdbId}]`
   c. For each enabled slot: call `VersionMaterializer.BuildStrmUrl` + write
   d. Atomic write (.tmp -> File.Move)
   e. Update catalog_items: `item_state = Written`, set `strm_path`, `local_path`, `local_source = 'strm'`
   f. Update `strm_token_expires_at` to now+365 days (Unix timestamp)
4. No rate limiting on disk writes (pure disk I/O, no external API)

**Depends on:** FIX-143B-01

---

## Phase 143D — Step 3: Hint

### FIX-143D-01: Implement Identity Hint NFO

**File:** `Tasks/RefreshTask.cs` (modify)

**What:**
1. After each .strm write in Step 2, write an Identity Hint .nfo alongside:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<movie lockdata="false">  <!-- or <tvshow lockdata="false"> / <episodedetails lockdata="false"> -->
  <uniqueid type="tmdb" default="true">{tmdbId}</uniqueid>
</movie>
```
2. Rules:
   - If TMDB ID available: `<uniqueid type="tmdb" default="true">`
   - If only IMDB: `<uniqueid type="imdb" default="true">`
   - If neither: title only, set `nfo_status = 'NeedsEnrich'`
3. Set `nfo_status = 'Hinted'` on catalog_items after successful NFO write
4. Reuse/adapt existing `WriteMinimalNfo` from DiscoverService, enhanced for TMDB uniqueid
5. Use `VersionMaterializer.GetFileName` for suffixed naming (per-tier NFO)

**No external calls during Write:** The Hint NFO uses only data already in catalog_items (imdb_id, tmdb_id, title, year). No Cinemeta, no AIOStreams, no external metadata lookups.

### FIX-143D-02: Folder name suffix change

**What:**
1. When TMDB ID is available, use folder suffix `[tmdbid=XXXX]` instead of `[imdbid-{ImdbId}]`
2. When only IMDB ID available, keep existing `[imdbid-{ImdbId}]` format
3. Progressive enhancement — existing folders with `[imdbid-]` continue to work

**Depends on:** FIX-143C-01

---

## Phase 143E — Build Verification

### FIX-143E-01: Build + smoke test

**What:**
1. `dotnet build -c Release` — 0 errors, 0 new warnings
2. `./emby-reset.sh` — server starts, plugin loads
3. Verify RefreshTask appears in Emby Scheduled Tasks with 6-minute interval
4. Trigger manual RefreshTask run
5. Verify Collect step creates Queued items from AIOStreams catalog
6. Verify Write step creates .strm files and transitions items to Written
7. Verify Hint step creates .nfo files alongside .strm and sets nfo_status='Hinted'
8. Verify ingestion_state updated with watermark after run
9. Verify run log entries created
10. Verify concurrency guard: trigger two runs simultaneously, second should skip

**Depends on:** FIX-143D-01

---

## Sprint 143 Dependencies

- **Previous Sprint:** 142 (Schema + Ingestion State)
- **Blocked By:** Sprint 142
- **Blocks:** Sprint 144 (Notify + Verify need Written/Notified states)

---

## Sprint 143 Completion Criteria

- [ ] `Tasks/RefreshTask.cs` created, implements IScheduledTask
- [ ] 6-minute default trigger interval
- [ ] SemaphoreSlim concurrency guard — second run skips if first is active
- [ ] Step 1 Collect: queries AIOStreams, marks new/changed items as Queued
- [ ] Early exit if nothing new
- [ ] Step 2 Write: writes .strm per tier for Queued items, transitions to Written
- [ ] Atomic file writes (.tmp -> rename)
- [ ] Token expiry persisted as INTEGER Unix timestamp
- [ ] Step 3 Hint: writes Identity Hint .nfo alongside every .strm
- [ ] NFO includes TMDB uniqueid if available, IMDB as fallback
- [ ] nfo_status set to 'Hinted' after NFO write
- [ ] nfo_status set to 'NeedsEnrich' if no known IDs
- [ ] ingestion_state watermark updated per source
- [ ] refresh_run_log entries created per run
- [ ] No external calls during Write step
- [ ] Plugin.SyncLock acquired during run
- [ ] Build succeeds with 0 errors, 0 new warnings

---

## Sprint 143 Notes

**Files created:** 1 (`Tasks/RefreshTask.cs`)
**Files modified:** ~1 (`Services/VersionMaterializer.cs` for NFO enhancement)

**Risk assessment:** HIGH. RefreshTask is the core replacement for DoctorTask. It must handle the same write scenarios (movie paths, series paths, anime paths, per-tier files) while running 40x more frequently (6 min vs 4h). The concurrency guard and SyncLock are critical.

**Design decisions:**
- AIOStreams catalog fetch is full-fetch + diff, not incremental, because AIOStreams does not expose a "since" filter
- The 6-minute interval means RefreshTask runs 240x/day. On a stable library, each run exits early at Collect
- Steps 4-5 (Notify, Verify) are in Sprint 144. Without them, items stay in Written state until Sprint 144

**Token expiry clarification:** Resolve tokens have a **365-day lifetime**. The Write step persists `strm_token_expires_at = now + 365 days`. The 90-day figure in Sprint 144's Verify step is the **renewal window** — items are renewed when their token is within 90 days of expiry (i.e., after ~275 days). The HMAC token, the DB column, and the renewal query all agree: 365-day lifetime, 90-day advance renewal.

---
