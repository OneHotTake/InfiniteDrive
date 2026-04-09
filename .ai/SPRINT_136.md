# Sprint 136 — Improbability Drive Admin Page

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 135

---

## Overview

Add an "Improbability Drive" tab to the admin UI with a light HHGTTG theme. The page shows system health status derived from Doctor + resolver logs and provides a single "Summon Marvin" diagnostic button.

---

## Phase 136A — Improbability Drive UI

### FIX-136A-01: Add Improbability Drive tab to configuration page

**File:** `Configuration/configurationpage.html` (modify)

**What:**
1. Add "Improbability Drive" tab button to tab bar
2. Add tab content panel with:
   - `DON'T PANIC` header (large, friendly letters)
   - Status indicator: 🟢 / 🟡 / 🔴
     - Green: "All systems nominal" — no mismatches or failures
     - Yellow: "Not again…" — quality mismatches detected
     - Red: "Oh no, not again…" — debrid unreachable
   - One button: **Summon Marvin** (default state)
   - On press: label changes to "Marvin is grumbling…" + spinner
   - On completion: label resets, indicator refreshes
3. Design: no logs visible, no technical details, calm, Apple-like, slightly cheeky
4. Light HHGTTG theme throughout (subtle, not overwhelming)

**Depends on:** Sprint 135

### FIX-136A-02: Add Improbability Drive JavaScript

**File:** `Configuration/configurationpage.js` (modify)

**What:**
1. `showTab('improbability')` handler
2. `summonMarvin()` — triggers Doctor run + stream health check + resolver cache validation
3. `refreshImprobabilityStatus()` — polls `/EmbyStreams/Status` for health indicators
4. Status derived from:
   - Doctor last run: errors/warnings → yellow/red
   - Resolver cache: miss rate → yellow
   - AIOStreams reachability → red if unreachable
5. On completion: reset button label, refresh indicator

**Depends on:** FIX-136A-01

---

## Phase 136B — Backend Support

### FIX-136B-01: Add Improbability Drive status to StatusService

**File:** `Services/StatusService.cs` (modify)

**What:**
1. Add `ImprobabilityDriveStatus` field to `StatusResponse`
2. Derive status from:
   - `DoctorLastRun` timestamp + error count
   - `ResolverCacheHitRate` (from in-memory cache metrics)
   - `AioStreamsReachable` boolean
   - `QualityMismatchCount` from resolver logs
3. Include Marvin-triggerable actions list (doctor, health-check, cache-validate)

**Depends on:** Sprint 135

---

## Sprint 136 Dependencies

- **Previous Sprint:** 135 (Doctor Multi-Tier)
- **Blocked By:** Sprint 135
- **Blocks:** Sprint 137 (Cleanup can reference Improbability Drive instead of old status panels)

---

## Sprint 136 Completion Criteria

- [ ] Improbability Drive tab renders in admin UI
- [ ] Status indicator shows correct color based on system health
- [ ] Summon Marvin button triggers diagnostic actions
- [ ] Button state transitions: default → grumbling → reset
- [ ] StatusService exposes Improbability Drive status data
- [ ] No visible logs or technical details on page
- [ ] Light HHGTTG theme applied
- [ ] Build succeeds with 0 errors

---

## Sprint 136 Notes

**Files modified:** ~3 (`configurationpage.html`, `configurationpage.js`, `Services/StatusService.cs`)

**Risk assessment:** LOW. Pure UI work + one backend status field. No breaking changes to existing functionality.

**Design decisions:**
- Single button design (Summon Marvin) — no technical controls, no knobs
- Status derived from existing metrics — no new logging infrastructure
- Apple-like calm aesthetic — the point is to reassure, not overwhelm
- "Don't Panic" is the most important words in the galaxy
- Health checks validate token auth works (resolve + stream tokens from Sprint 132)
