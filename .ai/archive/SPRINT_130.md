# Sprint 130 — Versioned Playback: Integration Testing

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 129, Sprint 121

---

## Overview

 Sprint 130 performs integration testing with a live Emby server. This is the final validation sprint that that as end-to-end manual testing with dev server reset scripts.

 **Key Principle:** Test against a live Emby server with all versioned playback features enabled. Not against a static codebase.



---

## Phase 130A — Live Server Integration Test

### FIX-130A-01: Test Version Playback Against Live Emby

**What:** Test the full playback flow against a running Emby server with a test-signed-stream.sh.

**Test Scenarios:**

1. **Single version playback:**
   - Fresh install with only `hd_broad` enabled
 - Sync catalog → .strm files written with `slot=hd_broad`
 in URL
 - Play item → resolves stream → 302 redirect
 - Verify .strm URL format: `http://host:port/EmbyStreams/play?titleId=tt...&slot=hd_broad&token=...`


2. **Multi-version playback:**
   - Enable `4k_hdr` slot via settings page
 - Confirm rehydration dialog
 - Rehydration writes `4K HDR` .strm files
 - Play item → select 4K HDR version → resolves different stream
 - Play item → select default (hd_broad) → resolves default stream
 - Verify both .strm files exist in library

   3. **Slot removal:**
   - Disable `4k_hdr` via settings page
 - Confirm removal dialog
 - .strm files for `4K HDR` suffix deleted from disk
 - Play item → only default version available
 - Verify Emby library updated correctly

   4. **Default change:**
   - Change default from `hd_broad` to `4k_hdr` via settings page
 - Confirm default change dialog
 - Base .strm files renamed to 4K HDR base
 - Suffixed .strm files renamed to hd_broad suffix
 - Play item → 4K HDR plays by default
 - Net file count unchanged per   5. **Server address change:**
   - Stop server → change LAN address → start server
   - Verify URL rewrite sweep triggered
   - All .strm files updated with new address
   - Play item → still works with new address

**Depends on:** Sprint 129
**Must not break:** Server must start successfully.



---

## Phase 130B — Manual E2E Checklist

### FIX-130B-01: E2E Test Plan Update

**File:** `Tests/E2E/README.md` (modify)

**What:** Add versioned playback scenarios to the existing E2E test plan.

**New scenarios:**
- Versioned playback: single version (HD Broad)
- Versioned playback: multi-version (HD Broad + 4K HDR)
- Versioned playback: slot removal
- Versioned playback: default change
- Versioned playback: server address change
- Versioned playback: candidate normalization accuracy
- Versioned playback: slot matching correctness
- Versioned playback: rehydration through trickle pipeline
 - Versioned playback: 8-slot maximum enforcement
 - Versioned playback: HD Broad cannot be disabled



**Depends on:** Sprint 129

---

## Sprint 130 Dependencies

- **Previous Sprint:** 129 (Build Verification)
- **Blocked By:** Sprint 129
- **Blocks:** None (Versioned Playback complete)

---

## Sprint 130 Completion Criteria

 - [ ] Single version playback works end E2E)
 - [ ] Multi-version playback works ( E2E)
 - [ ] Slot removal works ( E2E)
 - [ ] Default change works ( E2E) - [ ] Server address change detection works ( E2E)
 - [ ] 8-slot maximum enforced ( E2E)
 - [ ] HD Broad cannot be disabled ( E2E)
 - [ ] Build succeeds ( 0 warnings, 0 errors)

---

## Sprint 130 Notes

 **Test Environment:**
- Use `./emby-reset.sh` for clean start
 Use `./emby-start.sh` for quick iteration
 - Use `./test-signed-stream.sh` for URL verification



 **Test Data:**
- Use test items with known IMDB IDs ( - Verify against both movies and series
 - Test with items that have no candidates for some slots ( silent absence)

 - Test with items that have candidates for all slots
 full coverage)

 ``` |
