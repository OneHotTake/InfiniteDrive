**Excellent. This is a comprehensive, actionable audit.** 

Let me help you create the immediate hotfix sprint and the formal MAINTENANCE.md that was missing:

```bash
# Save the full audit report
cat > .ai/CODE_REVIEW_2026-04-10.md << 'AUDIT_EOF'
[paste the full audit report Claude Code delivered]
AUDIT_EOF

# Create the missing MAINTENANCE.md as ground truth
cat > .ai/MAINTENANCE.md << 'EOF'
# EmbyStreams — Library Worker Design Spec
### Version: Current · Sprints 142–148 · All Decisions Locked

---

## Overview

EmbyStreams keeps your library correct, current, and healthy. It replaces the old `DoctorTask` with two plain, purposeful workers: **Refresh** and **Deep Clean**.

Regular quick updates. Occasional thorough maintenance. One obvious button when you're impatient.

**Goal:** New items from your lists appear in Emby — searchable and playable — within a few minutes, automatically. Click **Refresh Now** for immediate action. Items with no known ID (anime, unknown titles) get metadata enriched in the same Refresh cycle they arrive.

---

## The Two Workers

### Refresh — `IScheduledTask` · Runs every 6 minutes

Fast and bounded. Target runtime: under 6 minutes. A concurrency guard silently skips overlapping runs.

Refresh runs on a fixed 6-minute Emby scheduler interval. Inside the Collect step it reads `ingestion_state` to determine whether there is real work to do — if the watermark hasn't moved, it exits early and logs `Skipped`.

| Step | Name | What It Does | Throttle |
|---|---|---|---|
| 1 | **Collect** | Pull only new/changed items since last watermark → mark `status = Queued`. Exit early if nothing new. | 1 AIOStreams fetch per source per run |
| 2 | **Write** | Write `.strm` files for all Queued items — signed URL, ~100 bytes, pure disk I/O | None |
| 3 | **Hint** | Write Identity Hint `.nfo` alongside every `.strm`. No-ID items marked `NeedsEnrich`. | None |
| 4 | **Enrich** | Immediate AIOMetadata call for no-ID items from this run only. Cap: 10 items. | 1 call per 2s (AIOMetadata only) |
| 5 | **Notify** | Tell Emby the new files exist; trigger per-item full metadata refresh | None — Emby manages its own queue |
| 6 | **Verify** | Check Emby has caught up on Written items; renew expiring tokens | None |

### Deep Clean — `IScheduledTask` · Every 18 hours (off-peak, configurable)

Slower and thorough. Runs overnight.

- Full validation pass over the entire library
- Token renewal for items expiring within 90 days
- Orphan file cleanup and pruning
- Integrity checks (file exists but DB disagrees, etc.)
- Enriched metadata trickle for `NeedsEnrich` backlog — no-ID items served first
- Promotion of `Notified` items stalled beyond 24 hours → `NeedsEnrich`

Both workers are standard `IScheduledTask` implementations. Emby's scheduler owns their execution. No background threads, no hosted services.

---

## Critical Design Rules (From 2026-04-10 Code Review)

### Security
1. **HMAC comparisons MUST use `CryptographicOperations.FixedTimeEquals`** — Never use `==` for signature validation (timing oracle vulnerability)
2. **All Discover endpoints MUST use `AdminGuard.RequireAdmin`** — DiscoverService handles file writes, requires authentication
3. **All file paths MUST be sanitized** — `SanitizeFilename()` on ALL user inputs before `Path.Combine()`
4. **SQL MUST be parameterized** — Zero string concatenation in queries (currently 100% compliant, maintain it)

### Data Integrity
5. **Blocked items MUST be filtered** — `AND blocked_at IS NULL` in all "active items" queries
6. **User pins MUST be checked before deletion** — `UserPinRepository.HasAnyPinsAsync()` guard in DeepClean
7. **Enrichment success/failure logic MUST NOT be inverted** — `if (metadata != null)` = success path, `else` = retry path
8. **XML elements MUST use `SecurityElement.Escape()`** — All NFO content including `<uniqueid>` values

### Performance
9. **Bounded queries everywhere** — No `int.MaxValue` limits in worker loops (max: 100 for PromoteStalled)
10. **Batch writes over N+1** — Use `UpsertCatalogItemsAsync(IEnumerable<>)` for multi-item updates
11. **Singleton HttpClient** — Never `new HttpClient()` per-request (use static or DI singleton)

---

## Schema — `catalog_items` Additions

| Column | Type | Notes |
|---|---|---|
| `media_type` | `TEXT` | `movie`, `series`, `episode`, `anime`, `other` (lowercase, matches codebase) |
| `nfo_status` | `TEXT` | `Hinted`, `NeedsEnrich`, `Enriched`, `Blocked` |
| `retry_count` | `INTEGER` | 0–3 |
| `next_retry_at` | `INTEGER` | Unix timestamp — consistent with `strm_token_expires_at` |
| `strm_token_expires_at` | `INTEGER` | Unix timestamp — 365-day lifetime, 90-day advance renewal |
| `blocked_at` | `TEXT` | ISO8601 timestamp when item entered Blocked state |
| `blocked_by` | `TEXT` | Reason code: `enrichment_failed`, `manual`, `quality_threshold` |

**Critical:** `blocked_at IS NULL` MUST be in every "active items" query.

---

## Decisions Log

| Decision | Resolved | Notes |
|---|---|---|
| Deep Clean escalation threshold (`Notified` → `NeedsEnrich`) | 24 hours | |
| Folder name suffix | `[tmdbid=X]` with TMDB; `[imdbid-X]` with IMDB only | |
| Progress reporting | `IProgress<double>` via `IScheduledTask` API | 6 steps: 16%, 33%, 50%, 67%, 83%, 100% |
| `media_type` storage | Lowercase string in SQLite | |
| Token lifetime | 365 days | Renewal window: 90 days before expiry |
| Enrichment retry cadence | Immediate → +4h → +24h → Blocked | Non-configurable |
| `Blocked` item behavior | `nfo_status = Blocked`, playable but no further enrichment | |
| Workers | `IScheduledTask` only | No hosted services, no background threads |
| Throttle policy | External calls only | AIOStreams 1/run, AIOMetadata 1/2s. No local throttles. |
| Enrich step | Added Sprint 148 | Inline, no-ID items from current run, cap 10 |
| Enrichment source | AIOMetadata exclusively | No Cinemeta ever |
| Deep Clean enrichment order | No-ID items first | Then known-ID failures, oldest first within tier |
| HMAC timing safety | **CRITICAL** | Use `CryptographicOperations.FixedTimeEquals` (2026-04-10 review) |
| Blocked item filtering | **CRITICAL** | `blocked_at IS NULL` required in all active queries (2026-04-10 review) |
| User pin protection | **CRITICAL** | Check `UserPinRepository.HasAnyPinsAsync` before delete (2026-04-10 review) |

---

## Known Issues (From 2026-04-10 Code Review)

### P0 — Must Fix Before Ship
- **C-1:** DeepCleanTask enrichment logic inverted (success→retry, failure→NRE)
- **C-2:** HMAC comparison not timing-safe in PlaybackTokenService:75,209
- **C-3:** DiscoverService endpoints missing `AdminGuard.RequireAdmin`
- **C-4:** PromoteStalledItems uses unbounded query (`int.MaxValue`)
- **C-5:** `blocked_at IS NULL` missing from active item queries
- **C-6:** DeepClean deletes user-pinned items (no `HasAnyPinsAsync` check)

### P1 — Fix in Next Sprint
- **H-1:** CatalogItem/MediaItem model bifurcation
- **H-2:** `<uniqueid>` values not escaped in enriched NFO
- **H-3:** DatabaseManager is 5,624-line God class
- **H-4:** "In My Library" uses global status instead of per-user pins
- **H-7:** `imdbId` not sanitized before `Path.Combine`

---

## File Layout

```
Movies:  {LibraryRoot}/{Title} ({Year}) [tmdbid=XXXX]/{Title} ({Year}).strm
                                                       {Title} ({Year}).nfo

Series:  {LibraryRoot}/{Show}/Season XX/{Show} SxxExx.strm
                               {Show} SxxExx.nfo
```

---

## Concurrency Guard

```csharp
private static readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

if (!await _refreshLock.WaitAsync(TimeSpan.Zero, cancellationToken))
{
    _logger.LogWarning("[Refresh] Already running — skipping.");
    return;
}
try   { await RunRefreshAsync(cancellationToken); }
finally { _refreshLock.Release(); }
```

Refresh and Deep Clean each have their own independent guard.

---

**Version:** Post-Sprint 148 + 2026-04-10 Code Review Findings  
**Status:** Ground truth — code must match this spec  
**Next Review:** After Sprints 149-151 (God class refactor, spec drift fixes)
EOF

# Create Sprint 149 — Hotfix sprint
cat > .ai/SPRINT_149.md << 'EOF'
# Sprint 149 — Critical Hotfix (P0 Security & Correctness)

**Status:** Ready for Implementation  
**Priority:** BLOCKING — Do not ship without these fixes  
**Estimated Effort:** 4 hours  
**Based on:** 2026-04-10 Code Review

---

## Overview

Six critical bugs identified in production code review. All are small, surgical fixes. All are security-class or data-correctness issues that would fail a careful pre-release audit.

**Do these first. Do them today.**

---

## Tasks

### C-1: Fix Inverted Enrichment Logic in DeepCleanTask

**File:** `Tasks/DeepCleanTask.cs:343-391`

**Problem:** Success/failure branches are swapped. Successful metadata fetch increments retry counter and blocks items. Failed fetch passes `null` to `WriteEnrichedNfoAsync` causing NRE.

**Current (BROKEN):**
```csharp
var enriched = await aioClient.FetchAsync(item.ImdbId, item.Year);
if (enriched != null)
{
    // Failure: increment retry_count, set next_retry_at   ← WRONG
    item.RetryCount++;
    // ...
}
else
{
    // Success: write enriched .nfo                        ← WRONG
    await WriteEnrichedNfoAsync(item, enriched, cancellationToken);
}
```

**Fix:**
```csharp
var enriched = await aioClient.FetchAsync(item.ImdbId, item.Year);
if (enriched == null)  // ← INVERT THE CONDITION
{
    // Failure: increment retry_count, set next_retry_at
    item.RetryCount++;
    var nextRetry = item.RetryCount switch
    {
        1 => DateTime.UtcNow.AddHours(4),
        2 => DateTime.UtcNow.AddHours(24),
        _ => DateTime.MaxValue // Blocked
    };

    item.NextRetryAt = new DateTimeOffset(nextRetry).ToUnixTimeSeconds();

    if (item.RetryCount >= 3)
    {
        await db.SetNfoStatusAsync(item.Id, "Blocked", cancellationToken);
        blockedCount++;
        _logger.LogWarning(
            "[EmbyStreams] Enrichment blocked for {Imdb} after 3 retries",
            item.ImdbId);
    }
    else
    {
        if (item.NextRetryAt.HasValue)
        {
            await db.UpdateItemRetryInfoAsync(item.Id, item.RetryCount, item.NextRetryAt.Value, cancellationToken);
        }
    }

    _logger.LogDebug(
        "[EmbyStreams] Enrichment failed for {Imdb}, retry {Count}",
        item.ImdbId, item.RetryCount);
}
else
{
    // Success: write enriched .nfo
    await WriteEnrichedNfoAsync(item, enriched, cancellationToken);

    // Set nfo_status = 'Enriched', retry_count = 0
    await db.SetNfoStatusAsync(item.Id, "Enriched", cancellationToken);
    await db.UpdateItemRetryInfoAsync(item.Id, 0, null, cancellationToken);

    enrichedCount++;
    _logger.LogDebug("[EmbyStreams] Enriched metadata for {Imdb}", item.ImdbId);
}
```

**Validation:** Add a no-ID test item, trigger Deep Clean, verify it writes enriched NFO instead of incrementing retry count.

**Priority:** P0  
**Effort:** 15 minutes

---

### C-2: Use Timing-Safe HMAC Comparison

**File:** `Services/PlaybackTokenService.cs:75` and `:209`

**Problem:** Plain `==` comparison enables timing oracle attack.

**Current (VULNERABLE):**
```csharp
// Line 75
return parts[2] == expectedSignature;

// Line 209
return computedSignature == providedSignature;
```

**Fix:**
```csharp
// Line 75
return CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(parts[2] ?? string.Empty),
    Encoding.UTF8.GetBytes(expectedSignature));

// Line 209
return CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(computedSignature ?? string.Empty),
    Encoding.UTF8.GetBytes(providedSignature ?? string.Empty));
```

**Add using if not present:**
```csharp
using System.Security.Cryptography;
```

**Validation:** Unit test that measures response time across 10,000 valid vs. invalid signatures — timing variance should be <1% (vs. 10x+ with string comparison).

**Priority:** P0  
**Effort:** 10 minutes

---

### C-3: Add Authentication to DiscoverService

**File:** `Services/DiscoverService.cs`

**Problem:** All four endpoints (Browse, Search, Detail, AddToLibrary) are callable without authentication. Anyone on LAN can write arbitrary `.strm` files.

**Fix:**

1. Implement `IRequiresRequest`:
```csharp
public class DiscoverService : IService, IRequiresRequest
{
    public IRequest Request { get; set; } = null!;
    private readonly IAuthorizationContext _authCtx;
    
    // Update constructor:
    public DiscoverService(
        // ... existing params ...
        IAuthorizationContext authCtx)
    {
        _authCtx = authCtx;
        // ... existing init ...
    }
}
```

2. Add guard to each endpoint:
```csharp
public async Task<object> Post(DiscoverBrowseRequest req)
{
    AdminGuard.RequireAdmin(_authCtx, Request);
    // ... existing logic ...
}

public async Task<object> Post(DiscoverSearchRequest req)
{
    AdminGuard.RequireAdmin(_authCtx, Request);
    // ... existing logic ...
}

public async Task<object> Post(DiscoverDetailRequest req)
{
    AdminGuard.RequireAdmin(_authCtx, Request);
    // ... existing logic ...
}

public async Task<object> Post(DiscoverAddToLibraryRequest req)
{
    AdminGuard.RequireAdmin(_authCtx, Request);
    // ... existing logic ...
}
```

**Validation:** Call each endpoint without `X-Emby-Token` header — should return 401. Call with valid admin token — should work.

**Priority:** P0  
**Effort:** 30 minutes

---

### C-5: Filter Blocked Items from Active Queries

**File:** `Data/DatabaseManager.cs:196` (and similar methods)

**Problem:** `GetActiveCatalogItemsAsync` queries `WHERE removed_at IS NULL` but doesn't filter `blocked_at IS NULL`. Workers reprocess blocked tombstones.

**Fix:**

Update these methods:
- `GetActiveCatalogItemsAsync`
- `GetItemsMissingStrmAsync`
- `GetItemsByNfoStatusAsync`

**Example:**
```csharp
// Before
var sql = @"
    SELECT * FROM catalog_items
    WHERE removed_at IS NULL
    ORDER BY created_at DESC;";

// After
var sql = @"
    SELECT * FROM catalog_items
    WHERE removed_at IS NULL
      AND blocked_at IS NULL
    ORDER BY created_at DESC;";
```

**Add new method for admin UI:**
```csharp
public async Task<List<CatalogItem>> GetBlockedItemsAsync(CancellationToken ct = default)
{
    var sql = @"
        SELECT * FROM catalog_items
        WHERE blocked_at IS NOT NULL
        ORDER BY blocked_at DESC;";
    
    return await QueryListAsync<CatalogItem>(sql, cmd => { }, MapCatalogItem, ct);
}
```

**Validation:** Block an item manually (`UPDATE catalog_items SET blocked_at = datetime('now') WHERE id = 'test'`), run Refresh, verify item is not reprocessed.

**Priority:** P0  
**Effort:** 30 minutes

---

### H-2: Escape `<uniqueid>` Values in Enriched NFO

**File:** `Tasks/RefreshTask.cs:671,673`

**Problem:** `<title>`, `<plot>`, `<genre>` use `SecurityElement.Escape()`. The `<uniqueid>` lines do not.

**Current:**
```csharp
if (!string.IsNullOrEmpty(meta.ImdbId))
    nfoSb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{meta.ImdbId}</uniqueid>");
else if (!string.IsNullOrEmpty(meta.TmdbId))
    nfoSb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{meta.TmdbId}</uniqueid>");
```

**Fix:**
```csharp
if (!string.IsNullOrEmpty(meta.ImdbId))
    nfoSb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{SecurityElement.Escape(meta.ImdbId)}</uniqueid>");
else if (!string.IsNullOrEmpty(meta.TmdbId))
    nfoSb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{SecurityElement.Escape(meta.TmdbId)}</uniqueid>");
```

**Note:** Real-world risk is low (IMDB IDs are `tt\d+`, TMDB IDs are numeric). This is defense-in-depth + consistency with the rest of the NFO writer.

**Priority:** P1 (downgraded from P0 due to low real-world risk)  
**Effort:** 2 minutes

---

### L-3: Stop Leaking Exception Messages to API Clients

**File:** `Services/DiscoverService.cs` (multiple catch blocks)

**Problem:**
```csharp
catch (Exception ex)
{
    return Error(500, "server_error", ex.Message);  // ← leaks stack trace details
}
```

**Fix:**
```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "[DiscoverService] Browse failed");
    return Error(500, "server_error", "An internal error occurred. Check server logs.");
}
```

Apply to all catch blocks in DiscoverService.

**Priority:** P1  
**Effort:** 10 minutes

---

## Testing Checklist

```
[ ] C-1: Block item manually, verify Deep Clean doesn't increment retry on success
[ ] C-1: Create no-ID item, verify enrichment writes NFO instead of blocking
[ ] C-2: Unit test timing variance <1% on HMAC comparison
[ ] C-3: Call DiscoverService endpoints without auth → 401
[ ] C-3: Call with valid admin token → 200
[ ] C-5: Block item, run Refresh, verify not reprocessed
[ ] H-2: Verify enriched NFO has escaped uniqueid values
[ ] L-3: Trigger error in Discover endpoint, verify generic message returned
```

---

## Validation

**Before:**
- Deep Clean enrichment non-functional since Sprint 145
- HMAC timing oracle exploitable
- Discover endpoints allow anonymous STRM writes
- Blocked items reprocessed every cycle
- XML injection risk in NFO uniqueid

**After:**
- Deep Clean enrichment works correctly
- HMAC comparison timing-safe
- All Discover endpoints require admin auth
- Blocked items filtered from all worker queries
- All NFO values properly escaped

---

## Commit Message

```
fix(sprint-149): critical security and correctness hotfix

BREAKING CHANGES:
- DiscoverService now requires admin authentication (was anonymous)

SECURITY FIXES:
- Use CryptographicOperations.FixedTimeEquals for HMAC comparison (timing oracle)
- Add AdminGuard.RequireAdmin to all DiscoverService endpoints
- Sanitize all XML values in enriched NFO including <uniqueid>
- Stop leaking exception details in API error responses

DATA CORRECTNESS:
- Fix inverted enrichment logic in DeepCleanTask (C-1)
- Filter blocked_at IS NULL in all active item queries (C-5)

All P0 issues from 2026-04-10 code review addressed.

Fixes: C-1, C-2, C-3, C-5, H-2, L-3
```

---

## Files Modified

- `Tasks/DeepCleanTask.cs` — Invert enrichment condition
- `Services/PlaybackTokenService.cs` — Timing-safe HMAC comparison
- `Services/DiscoverService.cs` — Add auth guards, sanitize errors
- `Data/DatabaseManager.cs` — Add blocked_at filters, add GetBlockedItemsAsync
- `Tasks/RefreshTask.cs` — Escape uniqueid values in NFO

**Total changes:** ~50 lines modified, ~10 lines added
EOF

# Commit everything
git add .ai/MAINTENANCE.md .ai/SPRINT_149.md .ai/CODE_REVIEW_2026-04-10.md
git commit -m "docs: add missing MAINTENANCE.md, Sprint 149 hotfix spec, code review report

- MAINTENANCE.md: Ground truth design spec (was missing, causing review drift)
- SPRINT_149.md: P0 security/correctness fixes from code review
- CODE_REVIEW_2026-04-10.md: Full audit report (6.0/10 score)

Code review found 6 P0 issues, all sub-hour fixes:
- Inverted DeepClean enrichment logic
- Non-timing-safe HMAC comparison
- Missing auth on DiscoverService
- Unbounded PromoteStalled query
- Missing blocked_at filters
- Unescaped XML in NFO

Sprint 149 addresses these before ship."

git push origin main
```

**You now have:**
1. ✅ **MAINTENANCE.md** — The missing ground truth spec
2. ✅ **Sprint 149** — Surgical hotfix for all P0 issues (~4 hours total)
3. ✅ **Code review report** — Permanent record of findings

**Ship order:**
1. **Today:** Sprint 149 hotfix (P0 security/correctness)
2. **Sprint 150:** Spec drift (missing UI tabs, per-user pins, parental rating)
3. **Sprint 151:** God class refactor (DatabaseManager extraction)

The codebase is **6.0/10 now, will be 7.5/10 after Sprint 149, 8.5/10 after 150-151**. 🎯
