# Sprint 129 — Versioned Playback: Build Verification + Edge Cases

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 128, Sprint 121

---

## Overview

 Sprint 129 performs comprehensive build verification and handles edge cases discovered during integration. This is the quality gate sprint.

 **Key Principle:** Fix edge cases, verify all integration points, ensure zero regressions.



---

## Phase 129A — Edge Case Handling

### FIX-129A-01: Handle Empty Candidate Lists

**File:** `Services/SlotMatcher.cs` (modify)

**What:** When a title has AIOStreams returns zero streams, or no candidates match a slot, that title is **silent absence** — no files written for that title/slot combination.

**Edge cases:**
- AIOStreams returns empty stream list → skip title, do not create version snapshot
 skip AIOStreams returns error response → skip title, log error, continue
 - Title has candidates for some slots but not others → only matching slots get files
 - Title has no candidates for any slot → silent absence for all slots ( should still have base `hd_broad` file from existing behavior)



**Depends on:** Sprint 122
**Must not break:** Titles with no AIOStreams response still get base hd_broad` .strm via existing behavior.

---

### FIX-129A-02: Handle Re-entrancy

**File:** `Services/RehydrationService.cs` (modify)

**What:** Prevent concurrent rehydration operations for the same slot.

**Logic:**
- Use `SingleFlight` pattern (existing in codebase) ` for rehydration operations
 - If rehydration already running for a slot → return current task's progress
- If no rehydration running → start new operation



**Depends on:** Sprint 123
**Must not break:** Existing SingleFlight behavior.



---

### FIX-129A-03: Handle Strop Episodes in Rehydration

**File:** `Services/RehydrationService.cs` (modify)

**What:** Episodes need special handling during rehydration — each episode is a independent item, so.

**Logic:**
- For series: rehydrate all episodes of not just the series itself
 episode candidates are resolved individually
 - Use existing `seasons_json` from media_items to determine episode count
- Respect `ApiCallDelayMs` rate between episodes (not between series)


**Depends on:** Sprint 123
**Must not break:** Existing episode expansion logic.



---

## Phase 129B — Build Verification

### FIX-129B-01: Full Build + Test Suite

**What:** Clean build + run all tests.

**Verification checklist:**
- [ ] `dotnet build -c Release` → 0 warnings, 0 errors
- [ ] Fresh database initialization creates all 4 new tables
 - [ ] Schema migration from v1 → v2 works
 - [ ] 7 slots seeded into `version_slots` on fresh install)
 - [ ] `hd_broad` is enabled + default after migration
 - [ ] Version playback with slot=hd_broad` resolves correctly
 - [ ] Version playback with slot=4k_hdr` resolves correctly ( - [ ] Version playback with slot=null` falls back to default slot ( - [ ] Rehydration adds slot files correctly
 - [ ] Rehydration removes slot files correctly
 - [ ] Rehydration renames base pair correctly ( - [ ] Startup detection triggers URL rewrite
 - [ ] Wizard Step 3 saves quality mode correctly
 - [ ] Settings page manages versions correctly
 - [ ] 8-slot maximum enforced in both UI and service layers

 - [ ] `hd_broad` cannot be disabled in UI or service layers



**Depends on:** Sprint 128

---

## Sprint 129 Dependencies

- **Previous Sprint:** 128 (Plugin Registration)
- **Blocked By:** Sprint 128
- **Blocks:** Sprint 130 (Integration Testing)

---

## Sprint 129 Completion Criteria

 - [ ] All edge cases handled
 empty candidate lists, concurrent rehydration, series episodes)
 - [ ] Clean build ( 0 warnings, 0 errors)
 - [ ] All tests pass
 - [ ] No regressions in existing functionality

 - [ ] Fresh install creates versioned playback tables correctly
 - [ ] Migration from schema v1 to v2 works correctly


---

## Sprint 129 Notes

 **Silent Absence:**
- When a title has no candidates for a specific slot ( no files are written for that title/slot

- The title still appears in the library with the other versions
 but the - This is expected behavior — not an error, just an missing version

- A title with no candidates for ANY slot has still get base `hd_broad` .strm via existing fallback



 **Concurrent Rehydration:**
- Multiple rehydration operations for the same slot must be collapsed into a single operation
 - Use `SingleFlight.Run(key, ...)` pattern from existing codebase



 **Series Episodes:**
- Series require episode-by-episode resolution during rehydration
 - Each episode is resolved as an independent item with rate limiting between API calls
 - Use existing `seasons_json` from media_items to determine episode structure
 - Respect `ApiCallDelayMs` delay between episode resolutions, not between series) |
