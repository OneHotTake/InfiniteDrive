# Sprint 145 — DeepCleanTask

**Version:** v4.0 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 144

---

## Overview

Create the `DeepCleanTask` scheduled task for 12-24 hour validation cycles. This replaces the "heavy" work that DoctorTask currently does: full library validation, orphan cleanup, enriched metadata trickle, token renewal, and integrity checks.

### Why This Exists

RefreshTask handles incremental changes every 6 minutes. DeepCleanTask handles the periodic full-pass operations:
- Orphan .strm file cleanup (no matching catalog item)
- Token renewal for items approaching expiry
- Enriched metadata for items in NeedsEnrich status
- Integrity checks (.strm exists on disk, DB state matches)
- Enrichment retry with exponential backoff (Immediate -> +4h -> +24h -> Blocked)

---

## Phase 145A — DeepCleanTask Skeleton

### FIX-145A-01: Create `Tasks/DeepCleanTask.cs`

**File:** `Tasks/DeepCleanTask.cs` (new)

**What:**
1. Implement `IScheduledTask`:
```csharp
public class DeepCleanTask : IScheduledTask
{
    private readonly ILogger<DeepCleanTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private static readonly SemaphoreSlim _runningGate = new(1, 1);
```
2. Schedule: `TaskTriggerInfo.TriggerInterval` with `TimeSpan.FromHours(18).Ticks`
3. TaskKey: `"EmbyStreamsDeepClean"`
4. Name: `"EmbyStreams Deep Clean"`
5. Constructor: `ILibraryManager`, `ILogManager` (same pattern as DoctorTask)
6. Concurrency guard: same SemaphoreSlim pattern as RefreshTask

**Depends on:** Sprint 144

---

## Phase 145B — Validation Pass

### FIX-145B-01: Full library integrity check

**File:** `Tasks/DeepCleanTask.cs` (modify)

**What:**
1. Load all active catalog items
2. For each item:
   a. If `ItemState = Ready` or `ItemState = Written` or `ItemState = Notified`:
      - Check .strm file exists on disk
      - If missing: transition to `ItemState = Queued` (will be re-written by RefreshTask)
      - Log: `action=integrity_fail, reason=strm_missing`
   b. If `ItemState = Retired`:
      - Verify real file still exists at `local_path`
      - If missing: transition back to `ItemState = Queued` (resurrection)
   c. Check DB consistency: `strm_path` should be non-null for Written/Notified/Ready items

3. Scan filesystem for orphan .strm files (same logic as DoctorTask Phase 5):
   - .strm files with no matching catalog item
   - Delete orphan .strm and empty parent folders
   - Also delete orphan .nfo files alongside orphans

**Depends on:** FIX-145A-01

---

## Phase 145C — Enrichment Trickle

### FIX-145C-01: Process NeedsEnrich items with retry logic

**File:** `Tasks/DeepCleanTask.cs` (modify), `Services/AioMetadataClient.cs` (new)

**What:**
1. Query items with `nfo_status = 'NeedsEnrich'`:
```sql
SELECT * FROM catalog_items
WHERE nfo_status = 'NeedsEnrich'
  AND (next_retry_at IS NULL OR next_retry_at <= unixepoch('now'))
  AND removed_at IS NULL;
```
2. For each NeedsEnrich item:
   a. Check `retry_count`:
      - 0: process immediately
      - 1: if `next_retry_at > now`, skip (waiting for +4h backoff)
      - 2: if `next_retry_at > now`, skip (waiting for +24h backoff)
      - 3: transition to `nfo_status = 'Blocked'` — do NOT change item_state (see design note below)
   b. Call `AioMetadataClient.FetchAsync(item)`:
      - Prefer IMDB ID as lookup key; fall back to `tmdb:{TmdbId}` if only TMDB available
      - AIOMetadata is the exclusive enrichment source — no Cinemeta, no other providers
      - 10-second per-item timeout (hard timeout via linked CancellationTokenSource)
      - Rate-limit at 1 call per 2 seconds (trickle, not flood)
      - Null response = failure, increment `retry_count`
   c. On success: write enriched .nfo with title, year, plot (overview), uniqueid (imdb/tmdb), genres:
```xml
<?xml version="1.0" encoding="utf-8"?>
<movie>  <!-- or <tvshow> for series -->
  <title>{meta.Name}</title>
  <year>{meta.Year}</year>
  <plot>{meta.Description}</plot>
  <uniqueid type="imdb" default="true">{meta.ImdbId}</uniqueid>
  <genre>Action</genre>
</movie>
```
   No poster/background URLs — Emby fetches artwork from its own providers using the `uniqueid` pivot.
   Set `nfo_status = 'Enriched'`, `retry_count = 0`.
   d. On failure: increment `retry_count`, set `next_retry_at` (Unix timestamp):
      - After attempt 1: `now + 4 hours`
      - After attempt 2: `now + 24 hours`
      - After attempt 3: set `nfo_status = 'Blocked'`

3. Non-configurable retry schedule: Immediate -> +4h -> +24h -> Blocked (max 3 attempts)
4. Notify Emby of updated NFO files (same pattern as RefreshTask Step 4)

**Depends on:** FIX-145B-01

### FIX-145C-02: Persist enrichment counts for Health Panel

**File:** `Tasks/DeepCleanTask.cs` (modify)

**What:**
1. Count items with `nfo_status = 'Blocked'` and `nfo_status = 'NeedsEnrich'`:
```csharp
await db.PersistMetadataAsync("blocked_enrichment_count", blockedCount.ToString(), ct);
await db.PersistMetadataAsync("needs_enrich_count", needsEnrichCount.ToString(), ct);
```
2. These counts are read by the Health Panel in Sprint 146

**Depends on:** FIX-145C-01

---

## Phase 145D — Token Renewal

### FIX-145D-01: Token renewal for expiring items

**File:** `Tasks/DeepCleanTask.cs` (modify)

**What:**
1. Query items with tokens expiring within 90 days:
```sql
SELECT * FROM catalog_items
WHERE (strm_token_expires_at < (unixepoch('now') + 7776000)
       OR strm_token_expires_at IS NULL)
  AND removed_at IS NULL;
```
2. For each: rewrite .strm with fresh resolve token (same atomic write pattern as DoctorTask Phase 6)
3. Update `strm_token_expires_at` to now + 365 days (Unix timestamp)
4. This is the same logic as DoctorTask Phase 6, moved to its new home
5. No rate limiting — local disk + DB only

**Depends on:** FIX-145B-01

---

## Phase 145E — Build Verification

### FIX-145E-01: Build + integration test

**What:**
1. `dotnet build -c Release` — 0 errors, 0 new warnings
2. `./emby-reset.sh` — server starts, plugin loads
3. Verify DeepCleanTask appears in Scheduled Tasks with 18h interval
4. Trigger manual DeepCleanTask run
5. Verify integrity check: delete a .strm file, verify DeepCleanTask detects and resets to Queued
6. Verify orphan cleanup: create an orphan .strm, verify DeepCleanTask deletes it
7. Verify enrichment: set item to NeedsEnrich, verify DeepCleanTask attempts enriched NFO
8. Verify retry backoff: set retry_count=1, verify next_retry_at is 4h from now
9. Verify Blocked transition: set retry_count=3, verify nfo_status becomes Blocked
10. Verify token renewal: set strm_token_expires_at to 30 days from now, verify renewal

**Depends on:** FIX-145D-01

---

## Sprint 145 Dependencies

- **Previous Sprint:** 144 (RefreshTask Notify + Verify)
- **Blocked By:** Sprint 144
- **Blocks:** Sprint 146 (Health Panel needs DeepCleanTask run metrics)

---

## Sprint 145 Completion Criteria

- [ ] `Tasks/DeepCleanTask.cs` created, implements IScheduledTask
- [ ] 18-hour default trigger interval
- [ ] SemaphoreSlim concurrency guard
- [ ] Full library integrity check detects missing .strm files
- [ ] Missing .strm items reset to Queued (for RefreshTask to re-write)
- [ ] Orphan .strm + .nfo cleanup
- [ ] NeedsEnrich items processed via `AioMetadataClient` with enriched NFO
- [ ] `Services/AioMetadataClient.cs` created — injectable client, not static method
- [ ] AIOMetadata is exclusive enrichment source (no Cinemeta, no other providers)
- [ ] Enrichment rate-limited at 1 call per 2 seconds, 10s per-item timeout
- [ ] Retry backoff: Immediate -> +4h -> +24h -> Blocked (max 3)
- [ ] Blocked items counted and persisted for Health Panel
- [ ] Token renewal for items expiring within 90 days
- [ ] Atomic .strm writes during token renewal
- [ ] Build succeeds with 0 errors, 0 new warnings

---

## Sprint 145 Notes

**Files created:** 2 (`Tasks/DeepCleanTask.cs`, `Services/AioMetadataClient.cs`)
**Files modified:** 0 (DeepCleanTask is self-contained, uses existing repository methods from Sprint 142)

**Risk assessment:** MEDIUM. DeepCleanTask does heavy lifting but runs infrequently. The enrichment retry logic is straightforward (counter + timestamp comparison). The main risk is the enriched NFO resolution — if metadata providers fail, items accumulate in NeedsEnrich. This is handled by the retry backoff culminating in Blocked, which is a safe terminal state (item still playable, just not enriched).

**Design decision:** DeepCleanTask runs at 18h instead of the spec's 12-24h range. This is a compromise. Configurable via Emby's Scheduled Tasks UI.

**Blocked item state:** When enrichment exhausts retries (retry_count=3), the item's `nfo_status` is set to `'Blocked'` but `item_state` is NOT changed. A Blocked item at `Ready` state means "Emby knows about this item and it plays fine, but metadata enrichment failed." The Health Panel shows Blocked count separately from the Ready count. This avoids the semantic mismatch of promoting to Ready on failure.

**Enrichment data source:** AIOMetadata is the exclusive enrichment source. Configured via Wizard (`_config.AioMetadataBaseUrl`). Endpoint: `GET https://<instance>/meta/{type}/{id}.json` where type=`movie`|`series`, id=IMDB ID first (`tt1234567`) or TMDB fallback (`tmdb:83533`). Response fields used: Name, Description, Year, ImdbId, TmdbId, Genres. No poster/background URLs — Emby fetches artwork from its own providers using the `uniqueid` pivot. This is the only external API call in DeepCleanTask. Never called during Refresh runs.

**`AioMetadataClient` design:** Injectable service in `Services/`, not a static method on DeepCleanTask. Constructor takes `IHttpClient`, `ILogger`, `PluginConfiguration`. Returns `EnrichedMetadata?` record — null means failure (caller handles retry). 10-second per-item hard timeout via linked CancellationTokenSource. Rate-limited at 1 call/2s by caller (DeepCleanTask trickle loop).

---
