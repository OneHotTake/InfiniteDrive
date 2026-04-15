# Sprint 304 — Nice-to-Have Improvements

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 303

## Overview

Quality of life improvements, better UX, and proactive maintenance features.

## Task 304-01: Proactive Token Refresh

**Problem:** 365-day token expiry is a cliff — entire library dies with no warning

**Files:** `Tasks/TokenRefreshTask.cs` (new), `Services/StrmWriterService.cs`

**Changes:**
- Create scheduled task running weekly
- Query items with tokens expiring within 90 days
- Re-sign `.strm` files with fresh tokens
- Log summary: "Refreshed {count} tokens, {remaining} still valid"
- Add `token_expires_at` tracking if not present

**Acceptance Criteria:**
- [ ] Tokens refreshed before expiry (no manual intervention)
- [ ] Task runs reliably on schedule
- [ ] Large libraries handled in batches (cap 1000/run)
- [ ] No disruption to playback during refresh

**Effort:** M

---

## Task 304-02: Cache Pre-Warm on Detail View

**Problem:** First play of item hits AIOStreams; could pre-warm when user browses

**Files:** `Services/DiscoverService.cs` (or equivalent), `Services/CachePreWarmService.cs` (new)

**Changes:**
- When user views item detail page, trigger lightweight cache check
- If cached URL exists: probe it (HEAD request)
- If probe fails or no cache: queue background resolution
- Resolution happens async — user doesn't wait
- Play button works immediately if cache valid, or waits briefly for background resolve

**Acceptance Criteria:**
- [ ] Item detail view triggers pre-warm (non-blocking)
- [ ] Valid cache confirmed without full re-resolution
- [ ] Invalid cache triggers background refresh
- [ ] Play button never slower than without pre-warm

**Effort:** M

---

## Task 304-03: Anime Canonical ID Dedup

**Problem:** Anime without IMDB IDs bypass dedup, creates duplicate library entries

**Files:** `Services/IdResolverService.cs`, `Tasks/CatalogSyncTask.cs`

**Changes:**
- Dedup key: use canonical ID (IMDB preferred, then TMDB, then Kitsu)
- Store all known IDs for cross-reference
- When new item arrives, check if any ID matches existing item
- Merge rather than duplicate

**Acceptance Criteria:**
- [ ] Same anime from two catalogs doesn't duplicate
- [ ] Kitsu-only items can still be deduped by Kitsu ID
- [ ] IMDB match takes precedence if available
- [ ] Existing duplicates can be merged (or flagged for manual review)

**Effort:** M

---

## Task 304-04: Identity Verification Warning

**Problem:** ID resolution can return wrong content, user plays wrong movie silently

**Files:** `Services/IdResolverService.cs`

**Changes:**
- After resolving ID, compare returned metadata (title, year) with source
- If title similarity < 80% OR year differs by > 1: flag as "unverified"
- Store `identity_confidence` score on item
- Show warning badge in Discover UI for low-confidence items
- Log at Warn when confidence is low

**Acceptance Criteria:**
- [ ] Obvious mismatches flagged (different title entirely)
- [ ] Minor variations pass (punctuation, "The" prefix)
- [ ] User sees visual indicator of uncertainty
- [ ] False positives minimized (don't flag everything)

**Effort:** M

---

## Task 304-05: SingleFlight Result Caching

**Problem:** Requests 100ms apart both hit API (SingleFlight only collapses concurrent)

**Files:** `Services/SingleFlight.cs`

**Changes:**
- After factory completes, cache result for configurable TTL (default 5s)
- Subsequent requests for same key get cached result
- Cache entries auto-expire
- Memory-bounded (LRU eviction if > 1000 entries)

**Acceptance Criteria:**
- [ ] Rapid sequential requests don't all hit API
- [ ] Cache expires appropriately (stale data clears)
- [ ] Memory usage bounded
- [ ] No behavior change for long-gap requests

**Effort:** S

---

## Task 304-06: State Machine Consolidation (Design Only)

**Problem:** Two competing state enums (`ItemState`, `ItemStatus`) create confusion

**Files:** `Models/ItemState.cs`, `Models/ItemStatus.cs`

**Changes (Design Document Only — Implementation deferred):**
- Document all current states and their transitions
- Design unified state machine covering all use cases
- Map migration path from current states to new
- Identify code paths that need updating
- Estimate effort for full implementation

**Acceptance Criteria:**
- [ ] Design document produced (not code changes)
- [ ] All current states mapped to proposed unified model
- [ ] Migration path documented
- [ ] Effort estimate for implementation sprint

**Effort:** S (design only)

---

## Sprint 304 Completion Criteria

- [ ] All 6 tasks implemented (Task 304-06 is design doc only)
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Token refresh: scheduled task runs, tokens renewed
- [ ] Pre-warm: detail view triggers background resolution
- [ ] Anime dedup: Kitsu-only items don't duplicate
- [ ] Identity verification: mismatches flagged visually
- [ ] SingleFlight: short TTL caching works
- [ ] State machine: design document complete

---

