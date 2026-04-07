# Sprint 128 — Versioned Playback: Plugin Registration + Build + Test

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 127, Sprint 121

---

## Overview

 Sprint 128 ties all versioned playback components together in Plugin.cs, registers new services and pages, and verifies the complete build.

 **Key Principle:** Wire everything together, verify build, run existing tests to ensure no regressions.

---

## Phase 128A — Plugin Registration

### FIX-128A-01: Register Services in Plugin.cs

**File:** `Plugin.cs` (modify)

**What:** Register all new services in the plugin's DI container (or manual constructor injection pattern).

**New registrations:**

| Service | Lifetime | Notes |
|---|---|---|
| `VersionSlotRepository` | Singleton | Data access for version_slots |
| `CandidateRepository` | Singleton | Data access for candidates |
| `VersionSnapshotRepository` | Singleton | Data access for version_snapshots |
| `MaterializedVersionRepository` | Singleton | Data access for materialized_versions |
| `CandidateNormalizer` | Singleton | Stream normalization |
| `SlotMatcher` | Singleton | Candidate filtering + ranking |
| `VersionPlaybackService` | Transient | Per-request playback |
| `VersionedStreamCache` | Singleton | Slot-aware cache |
| `VersionMaterializer` | Singleton | File writing |
| `RehydrationService` | Singleton | Rehydration orchestration |
| `RehydrationTask` | Singleton | Scheduled task |
| `VersionPlaybackStartupDetector` | Singleton | IServerEntryPoint |
| `VersionSlotController` | Transient | API endpoint |

**Depends on:** Sprints 122-127
**Must not break:** Existing service registrations. Order matters for DI.

---

### FIX-128A-02: Register Config Pages

**File:** `Plugin.cs` (modify)

**What:** Register new config page for Stream Versions in `GetPages()`.

**New pages:**
- Stream Versions page (settings page for version slot management)

**Depends on:** Sprint 126 (UI ViewModels)
**Must not break:** Existing page registrations.

---

## Phase 128B — Build Verification

### FIX-128B-01: Clean Build

**What:** Run `dotnet build -c Release` and verify 0 warnings, 0 errors.

**Fix any build issues:**
- Missing using directives
- Missing interface implementations
- Constructor parameter mismatches
- Type reference errors

**Depends on:** FIX-128A-01
**Must not break:** All existing functionality.

---

### FIX-128B-02: Run Existing Tests

**What:** Run existing test suite to verify no regressions from versioned playback changes.

**Focus areas:**
- Database initialization tests (new tables created correctly)
- Schema migration tests (version 1 → version 2)
- Playback tests (slot=null falls back to default slot)
- Stream URL signing tests (slot parameter in signed URL)

**Depends on:** FIX-128B-01

---

## Sprint 128 Dependencies

- **Previous Sprint:** 127 (Startup Detection)
- **Blocked By:** Sprint 127
- **Blocks:** Sprint 129 (Build Verification)

---

## Sprint 128 Completion Criteria

- [ ] All new services registered in Plugin.cs
- [ ] Config pages registered in GetPages()
- [ ] Clean build (0 warnings, 0 errors)
- [ ] Existing tests pass
- [ ] Database initializes with new tables
- [ ] Schema migration works (version 1 → version 2)
- [ ] Playback with slot=null works (backward compatible)

---

## Sprint 128 Notes

 **DI Pattern:**
- Emby plugin framework uses constructor injection, not a full DI container
- Services are instantiated manually in Plugin.cs constructor orinitialization methods
- Follow existing pattern from other service registrations in the codebase

 **Registration Order:**
1. Repositories (data layer first)
2. Services (depend on repositories)
3. Controllers (depend on services)
4. Tasks (depend on services)
5. Startup entry points (IServerEntryPoint)



 **Backward Compatibility:**
- `PlaybackService` must still handle requests without `slot` parameter
- Default behavior: resolve using default slot (hd_broad)
- This ensures existing `.strm` files ( without slot parameter) continue working
 ``` |
