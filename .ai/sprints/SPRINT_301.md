# InfiniteDrive — Implementation Sprint Plan

## Sprint Structure

| Sprint | Focus | Type |
|--------|-------|------|
| **Sprint 301** | Breaking/Core Logic Changes | Code |
| **Sprint 302** | Reliability & Resilience | Code |
| **Sprint 303** | Cleanup & Dead Code Removal | Code |
| **Sprint 304** | Nice-to-Have Improvements | Code |
| **Sprint 305** | Automated Testing | Test |
| **Sprint 306** | Integration Validation | Validation |

---

# Sprint 301 — Core Logic Fixes

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** None

## Overview

Fix the core playback pipeline issues that cause dead-ends for users. These are breaking changes to fundamental resolution logic.

## Task 301-01: Quality Tier Fallback

**Problem:** User on 4K tier gets 404 when content only exists at 1080p

**Files:** `Services/ResolverService.cs`

**Changes:**
- Add ordered quality fallback chain: `4k_hdr` → `4k_sdr` → `1080p` → `720p` → `sd` → `any`
- When filter returns empty at requested tier, iterate down chain until streams found
- Log quality degradation at Info level when fallback used
- Return all streams as final fallback (let user get *something*)

**Acceptance Criteria:**
- [ ] 4K user can play 1080p-only content (with log noting degradation)
- [ ] Quality preference still respected when available
- [ ] No streams = no streams (don't invent them)

**Effort:** S

---

## Task 301-02: Series Episode Pre-Expansion

**Problem:** Series items have S01E01 placeholder, user plays S02E05 → wrong content

**Files:** `Services/SeriesPreExpansionService.cs`, `Tasks/CatalogSyncTask.cs`, `Services/StrmWriterService.cs`

**Changes:**
- Make episode expansion mandatory during catalog sync (not deferred)
- Block series from appearing in library until all episodes have `.strm` files
- Add `episodes_expanded` boolean column to `catalog_items`
- Expansion must complete atomically (all or nothing per series)

**Acceptance Criteria:**
- [ ] Series only visible to Emby after all episodes written
- [ ] Each episode has correct season/episode in `.strm` filename
- [ ] Interrupted expansion resumes cleanly on next sync
- [ ] S02E05 resolves to S02E05 (not S01E01)

**Effort:** M

---

## Task 301-03: Distinct Error Responses

**Problem:** All failures return generic "no streams" — user can't tell what's wrong

**Files:** `Services/ResolverService.cs`, `Models/ResolverError.cs` (new)

**Changes:**
- Create `ResolverError` enum: `NoStreamsExist`, `QualityMismatch`, `PrimaryResolverDown`, `AllResolversDown`, `RateLimited`, `InvalidToken`
- Return structured error response with code + human message
- Map to appropriate HTTP status: 404 (no content), 503 (service down), 429 (rate limited), 401 (token)

**Acceptance Criteria:**
- [ ] "No streams for this title" vs "Service temporarily unavailable" distinguishable
- [ ] Error code in response body for programmatic handling
- [ ] Human-readable message suitable for UI display

**Effort:** S

---

## Task 301-04: VideosJson Deletion Safety Guard

**Problem:** Corrupted VideosJson → parser returns empty → all episodes deleted

**Files:** `Services/EpisodeDiffService.cs`

**Changes:**
- Before applying diff, validate: if removing >50% of episodes AND old count ≥ 5, ABORT
- Log at Error level with details when guard triggers
- Set item to `NeedsReview` state instead of deleting
- Admin can manually clear after investigation

**Acceptance Criteria:**
- [ ] Corrupted JSON does not trigger mass deletion
- [ ] Guard triggers → no files deleted, item flagged
- [ ] Normal diff (add 2, remove 1) proceeds normally
- [ ] Edge case: series ends, final season removes many episodes → still works (removal < 50%)

**Effort:** S

---

## Task 301-05: Primary/Secondary Resolver Failover Clarity

**Problem:** Failover exists but behavior is unclear, timeouts may be too aggressive

**Files:** `Services/ResolverService.cs`, `Services/AioStreamsClient.cs`

**Changes:**
- Document and enforce: Primary → Secondary → Hard Fail flow
- Increase per-resolver timeout to 15s (AIOStreams can be slow)
- On Primary fail, log at Warn and immediately try Secondary
- On Secondary fail, log at Error with clear "TOTAL RESOLUTION FAILURE" message
- Remove any dead code suggesting additional fallback layers

**Acceptance Criteria:**
- [ ] Primary timeout → Secondary tried (no 15s+ delay before failover)
- [ ] Both fail → single clear error log (not scattered across methods)
- [ ] No false promise of Layer 3 / debrid direct fallback in code or logs

**Effort:** S

---

## Sprint 301 Completion Criteria

- [ ] All 5 tasks implemented
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Quality fallback: 4K user plays 1080p content successfully
- [ ] Series: S02E05 plays correct episode
- [ ] Error messages: "service down" distinct from "no streams"
- [ ] Deletion guard: corrupt JSON doesn't delete episodes
- [ ] Resolver failover: Primary→Secondary→Fail with clear logging

