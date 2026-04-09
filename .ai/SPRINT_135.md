# Sprint 135 — Catalog Doctor Multi-Tier Extension

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 134

---

## Overview

Extend `DoctorTask` and `MetadataFallbackTask` to handle per-tier operations. The doctor must now diff, write, adopt, and health-check per quality tier rather than per item.

---

## Phase 135A — DoctorTask Multi-Tier

### FIX-135A-01: Extend Phase 1 (Fetch & Diff) for per-tier diffing

**File:** `Tasks/DoctorTask.cs` (modify)

**What:**
1. Diff per-tier: detect tiers that appeared (enabled) or disappeared (disabled) since last run
2. Build change lists per tier: `toWrite`, `toRetire`, `toResolve`, `orphans`
3. Track tier state in `doctor_last_run.json`

**Depends on:** Sprint 134

### FIX-135A-02: Extend Phase 2 (Write) for per-tier .strm/.nfo

**What:**
1. Add .strm + .nfo per tier for items in `toWrite`
2. Delete .strm + .nfo per tier for items in `toRetire` (tier disabled)
3. Use `VersionMaterializer` for suffixed filenames

**Depends on:** FIX-135A-01

### FIX-135A-03: Extend Phase 3 (Adopt) for per-tier retirement

**What:**
1. Detect real files per tier — if user has real 4K file, only retire 4K tier
2. Other tiers (HD Broad, SD Broad) remain active

**Depends on:** FIX-135A-02

### FIX-135A-04: Extend Phase 4 (Health Check) for tier-aware validation

**What:**
1. Validate per-tier: check that enabled tiers have .strm files on disk
2. Report missing .strm per tier

**Depends on:** FIX-135A-03

### FIX-135A-05: Rate limiting

**What:**
1. 1 AIOStreams request every 8 seconds (~450 items/hour)
2. Doctor logging: `ts | id | action | tier` — 30-day retention

**Depends on:** FIX-135A-01

---

## Phase 135B — MetadataFallbackTask Multi-Tier

### FIX-135B-01: Write one .nfo per quality tier

**File:** `Tasks/MetadataFallbackTask.cs` (modify)

**What:**
1. Write one version .nfo per quality tier
2. Include resolution, bitrate, audio metadata per tier
3. Use `VersionMaterializer.WriteNfoFile()` for suffixed naming

**Depends on:** Sprint 134

---

## Sprint 135 Dependencies

- **Previous Sprint:** 134 (Multi-Tier Hydration Pipeline)
- **Blocked By:** Sprint 134
- **Blocks:** Sprint 136 (Improbability Drive reads Doctor logs)

---

## Sprint 135 Completion Criteria

- [ ] Doctor Phase 1 diffs per-tier
- [ ] Doctor Phase 2 writes/deletes per tier
- [ ] Doctor Phase 3 adopts per tier
- [ ] Doctor Phase 4 validates per tier
- [ ] Rate limiting at 8-second intervals
- [ ] Doctor log includes tier column
- [ ] MetadataFallbackTask writes per-tier .nfo
- [ ] Build succeeds with 0 errors

---

## Sprint 135 Notes

**Files modified:** ~2 (`Tasks/DoctorTask.cs`, `Tasks/MetadataFallbackTask.cs`)

**Risk assessment:** MEDIUM. The Doctor is a critical reconciliation engine. Per-tier changes increase complexity but follow the existing 5-phase pattern.

**Design decisions:**
- Per-tier adoption prevents full-item retirement when only one tier has a real file
- Rate limiting protects AIOStreams API budget during large catalog reconciliation
- Doctor log retains tier info for Improbability Drive status indicators
- .strm files use resolve tokens (Sprint 134) — Doctor writes same format
