# Sprint 148 — Inline Enrichment + Deep Clean Priority

**Status:** Complete ✅  
**Completed:** 2026-04-10  
**Blocked by:** None  
**Depends on:** Sprints 142-147 (completed)

---

## Overview

Complete the RefreshTask enrichment pipeline by adding inline metadata enrichment for no-ID items, fixing Deep Clean priority ordering, and updating progress reporting to match the 6-step spec.

**Current gap:** Items with no TMDB/IMDB ID sit in `NeedsEnrich` state until the next Deep Clean (up to 18 hours). They have zero chance of Emby self-resolving from title-only hint NFOs.

**Solution:** Add `EnrichStepAsync()` between Hint and Notify — processes up to 10 no-ID items per Refresh run (every 6 minutes).

---

## Tasks

### 1. Add EnrichStepAsync to RefreshTask ✅

**File:** `Tasks/RefreshTask.cs`

- [x] Insert after HintStepAsync, before NotifyStepAsync
- [x] Query no-ID items from current run only (created_at >= runStartedAt)
- [x] Cap: 10 items per run, throttle: 2s per AIOMetadata call
- [x] Retry backoff: Immediate -> +4h -> +24h -> Blocked
- [x] Update NfoStatus to "Enriched" or "Blocked"
- [x] Write enriched NFO with SecurityElement.Escape() for XML sanitization

### 2. Update Progress Reporting ✅

**File:** `Tasks/RefreshTask.cs`

- [x] Update progress percentages to 6-step mapping:
  - Collect: 0.08 → 0.16
  - Write: 0.25 → 0.33
  - Hint: 0.42 → 0.50
  - Enrich: 0.67 (NEW)
  - Notify: 0.58 → 0.83
  - Verify: 0.75 → 1.00
- [x] Add runStartedAt tracking at start of ExecuteInternalAsync
- [x] Remove Promote from progress (now sub-step of Verify)

### 3. Fix Deep Clean Priority Ordering ✅

**File:** `Tasks/DeepCleanTask.cs`

- [x] Update EnrichmentTrickleAsync query with priority ORDER BY
- [x] No-ID items prioritized first (CASE WHEN no-ID THEN 0 ELSE 1)
- [x] Then by created_at ASC
- [x] LIMIT 42

**Why:** No-ID items (anime, unknown titles) are prioritized first — they have the worst user experience and zero chance of Emby self-resolving.

### 4. Add Title-Based Search to AioMetadataClient ✅

**File:** `Services/AioMetadataClient.cs`

- [x] Add FetchByTitleAsync(string title, int? year, CancellationToken) overload
- [x] Implement ParseAioMetadataSearchResponse for search results
- [x] Add using System.Linq for FirstOrDefault()
- [x] 10-second timeout, rate-limited by caller

### 5. Update VerifyStep Integration ✅

**File:** `Tasks/RefreshTask.cs`

- [x] Move PromoteStalledItemsAsync call to VerifyStepAsync as sub-step
- [x] Remove PromoteStalledItemsAsync as top-level step

### 6. Add Required Using ✅

**File:** `Tasks/RefreshTask.cs`

- [x] Add `using System.Security;` for SecurityElement.Escape()

---

## Testing Checklist

```
[ ] Add anime item with no IMDB/TMDB ID via Discover
[ ] Trigger Refresh Now
[ ] Verify EnrichStep runs and processes item within same cycle
[ ] Check .nfo file written with enriched metadata
[ ] Verify item appears in Emby with correct metadata
[ ] Check Health Panel shows "Enrich" step during run
[ ] Verify Deep Clean processes no-ID items first
[ ] Check blocked items after 3 failed enrichment attempts
```

---

## Validation

**Before:**
- No-ID items sit in `NeedsEnrich` until next Deep Clean (up to 18 hours)
- Deep Clean processes items in random order
- Progress reporting shows 5 steps instead of 6

**After:**
- No-ID items enriched inline within same Refresh cycle (6-minute max delay)
- Deep Clean prioritizes no-ID items first
- Progress reporting shows correct 6-step pipeline
- Health Panel displays "Enrich" step (if UI updated)

---

## Commit Message

```
feat(sprint-148): inline enrichment + deep clean priority

- Add EnrichStepAsync to RefreshTask (processes up to 10 no-ID items per run)
- Fix Deep Clean priority ordering (no-ID items first, then created_at)
- Update progress reporting to 6 steps (Collect → Write → Hint → Enrich → Notify → Verify)
- Add FetchByTitleAsync to AioMetadataClient for no-ID items
- Update VerifyStep to call PromoteStalledItems as sub-step
- Add using System.Security for SecurityElement.Escape()

Closes gap where no-ID items waited up to 18 hours for metadata.
Now enriched inline within same Refresh cycle (every 6 minutes).

Sprint 148 complete.
```

---

## Files Modified

- `Tasks/RefreshTask.cs` — Add EnrichStepAsync, update progress, add WriteEnrichedNfoAsync
- `Tasks/DeepCleanTask.cs` — Fix enrichment query with priority ORDER BY
- `Services/AioMetadataClient.cs` — Add FetchByTitleAsync title-based search
