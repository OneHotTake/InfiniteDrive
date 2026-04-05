---
status: ready_for_sprint_105
task: Sprint 104 Complete — Beta Software Migration Done
next_action: Begin Sprint 105 tasks
last_updated: 2026-04-05

## Beta Software Migration — COMPLETE

All project references updated to use beta software locations:
- **Emby Server:** Updated to use `../emby-beta/opt/emby-server` (version 4.10.0.8)
- **Emby SDK:** Available at `../emby.SDK-beta/`
- **Scripts updated:** emby-start.sh, emby-reset.sh now use relative paths to emby-beta
- **.csproj updated:** SQLite DLL references now point to emby-beta/system/
- **Documentation updated:** CLAUDE.md and docs/RUNBOOK.md reflect new locations

---

## Sprint 103 — COMPLETED

### DESIGN REVIEW — COMPLETE

Comprehensive design review covering 10 dimensions:
1. Architecture & Component Design
2. Data Flow & State Management
3. Separation of Concerns & Design Patterns
4. Interfaces & Contracts
5. Error Handling & Resilience
6. Scalability & Performance
7. Library & Catalog Management Design
8. Configuration & Extensibility
9. Testability
10. Code Quality & Maintainability

**Findings documented in:** `.ai/SPRINT_103_DESIGN_REVIEW.md`

### TOP 3 CRITICAL DESIGN ISSUES

1. **God class: DatabaseManager** — 3000+ lines, 20+ responsibilities
2. **Static global state accumulation** — Plugin.Instance, RateLimitBucket, _episodeCountCache cause memory leaks
3. **No clear boundary for ephemeral vs durable state** — in-memory caches without TTL or cleanup

### PRIORITIZED REMEDIATION

**P0 — Fix Immediately (Blocks Scale/Reliability):**
1. Split DatabaseManager into focused repositories
2. Implement circuit breaker for AIOStreams
3. Fix N+1 in PruneSourceAsync (O(n²) catalog pruning
4. Implement bounded in-memory caches
5. Make filesystem + DB operations atomic

**P1 — Fix Soon (Improves Maintainability):**
6. Replace Plugin.Instance with DI
7. Introduce repository interfaces
8. Create Emby API abstraction
9. Provider registration pattern
10. Break down long methods

**Overall Assessment:** 6.3/10 — Solid foundation with critical testability and scalability debt

---

### FIX-104A-01 COMPLETE

✓ ICatalogRepository interface defined
  - File: `Repositories/Interfaces/ICatalogRepository.cs`
  - 5 methods: GetAllAsync, GetByIdAsync, UpsertAsync, DeleteAsync, GetBySourceAsync

### FIX-104A-02 COMPLETE

✓ IPinRepository interface defined
  - File: `Repositories/Interfaces/IPinRepository.cs`
  - 4 methods: IsPinnedAsync, PinAsync, UnpinAsync, GetAllPinnedIdsAsync

### FIX-104A-03 COMPLETE

✓ IResolutionCacheRepository interface defined
  - File: `Repositories/Interfaces/IResolutionCacheRepository.cs`
  - 5 methods: GetCachedUrlAsync, SetCachedUrlAsync, InvalidateAsync, PurgeExpiredAsync

### FIX-104A-04 COMPLETE

✓ DatabaseManager implements all three repository interfaces
✓ Explicit interface implementations added (adapter methods)
✓ Plugin.Instance exposes interfaces as properties for gradual migration

---
## Phase 104B — Quick Wins

### FIX-104B-01 COMPLETE

✓ Fixed N+1 in PruneSourceAsync — batch UPDATE with IN clause instead of individual queries

### FIX-104B-02 COMPLETE

✓ Added lazy cleanup for _episodeCountCache — removes expired entries every 100 accesses

### FIX-104B-03 COMPLETE

✓ Added lazy cleanup for RateLimitBucket — removes old entries every 100 accesses

---
## Phase 104C — Resilience: Polly Pipeline

### FIX-104C-01 COMPLETE

✓ Added Polly 8.4.0 NuGet package to EmbyStreams.csproj

### FIX-104C-02 COMPLETE

✓ Created Resilience/AIOStreamsResiliencePolicy.cs with retry, circuit breaker, and timeout

### FIX-104C-03 COMPLETE

✓ Applied Polly policy to all AioStreamsClient.GetAsync calls

---
## Phase 104D — First DatabaseManager Split

### FIX-104D-01 COMPLETE

✓ Created Repositories/CatalogRepository.cs implementing ICatalogRepository

### FIX-104D-02 COMPLETE

✓ Updated Plugin.cs to initialise CatalogRepository
✓ CatalogRepository available via Plugin.Instance.CatalogRepository

### FIX-104D-03: Deferred

Removing catalog methods from DatabaseManager requires migrating all callers first.
Can be addressed in future sprint once ICatalogRepository adoption is complete.

---
## Sprint 104 Complete ✓

**Build Status:** SUCCESS (dotnet build -c Release)
**Deployment Status:** SUCCESS (DLL deployed to ~/emby-dev-data/plugins/)
**Runtime Status:** SUCCESS (Plugin loaded without errors, no Polly/Resilience errors found)

**E2E Testing:** COMPLETE
- Test plan created: `.ai/E2E_TEST_PLAN.md`
- Smoke test script: `scripts/e2e-smoke-test.sh`
- Test results: `.ai/E2E_TEST_RESULTS.md`

**Build Status:** SUCCESS (dotnet build -c Release)
**Deployment Status:** SUCCESS (DLL deployed to ~/emby-dev-data/plugins/)
**Runtime Status:** SUCCESS (Plugin loaded without errors, no Polly/Resilience errors found)

**Compilation Fixes Required:**
1. Added `using EmbyStreams.Repositories.Interfaces;` to DatabaseManager.cs
2. Added `using System;` to IResolutionCacheRepository.cs
3. Changed `const string` to `string` for interpolated SQL (PruneSourceAsync N+1 fix)
4. Fixed Polly API compatibility (Polly 8.x changes)
5. Fixed BindInt → BindNullableInt in IPinRepository implementation
6. Made resilience policy instance-based (not static) to use instance logger
7. Copied Polly.dll to plugins directory (Emby doesn't auto-copy NuGet dependencies)

---
## Sprint 104 Summary

Phase 104A — Repository Interfaces (Safety Net): COMPLETE
- FIX-104A-01: ICatalogRepository defined ✓
- FIX-104A-02: IPinRepository defined ✓
- FIX-104A-03: IResolutionCacheRepository defined ✓
- FIX-104A-04: Wire interfaces into DI ✓ (Plugin.Instance properties + explicit implementations)

Phase 104B — Quick Wins: COMPLETE
- FIX-104B-01: Fix N+1 in PruneSourceAsync ✓ (batch UPDATE)
- FIX-104B-02: Bounded _episodeCountCache ✓ (lazy cleanup)
- FIX-104B-03: Bounded RateLimitBucket ✓ (lazy cleanup)

Phase 104C — Resilience: COMPLETE
- FIX-104C-01: Add Polly NuGet dependency ✓
- FIX-104C-02: Define AIOStreams resilience pipeline ✓
- FIX-104C-03: Apply pipeline to AIOStreams calls ✓

Phase 104D — First DatabaseManager Split: PARTIAL
- FIX-104D-01: CatalogRepository implemented ✓
- FIX-104D-02: DI/Plugin.Instance updated ✓
- FIX-104D-03: Remove catalog methods from DatabaseManager — DEFERRED
