---
    status: complete
    task: Sprint 129-130 — Edge Case Hardening + Live Testing
    phase: Sprint 129 complete, live testing complete, Sprint 130 remaining
    last_updated: 2026-04-07

## Current State

Sprint 129 edge-case fixes applied and verified via live API testing. All endpoints functional.

### Sprint 129 Fixes Applied
1. RehydrationService.cs — null-guard on Plugin.Instance (was `!` bang operator)
2. VersionPlaybackService.cs — singleton repos via Plugin.Instance (was new per request)
3. VersionPlaybackService.cs — snapshot existence check before CachePlaybackUrlAsync
4. VersionSlotController.cs — ILogManager DI instead of direct repo injection
5. MaterializedVersionRepository.cs — renamed GetAllWithStrmPathsAsync, removed broken SQL filter
6. VersionPlaybackStartupDetector.cs — scheme-agnostic address replacement (http:// + https://)
7. CandidateRepository.cs — fixed log message group count
8. **Route collision fix** — VersionPlaybackService changed from `/EmbyStreams/Play` to `/EmbyStreams/VersionedPlay` to avoid conflict with existing PlaybackService
9. VersionMaterializer.cs — updated BuildStrmUrl to use `/EmbyStreams/VersionedPlay`

### Live API Test Results (all passing)
- GET /EmbyStreams/Versions → 200, 7 slots returned
- POST /EmbyStreams/Versions → 200, enabled 4k_hdr
- POST /EmbyStreams/Versions/Rehydrate → 200, enqueued
- GET /EmbyStreams/VersionedPlay?titleId=tt0000000 → 404 (expected, no catalog data)
- GET /EmbyStreams/VersionedPlay (no titleId) → 400 (validation works)
- GET /EmbyStreams/VersionedPlay (no auth) → 401 (auth enforced)

### Build Status
- 1 warning (pre-existing EMBY_HAS_CONTENTSECTION_API define), 0 errors
- Schema v22, all 7 version_slots seeded

### Next Action
Sprint 130 (integration testing) or end-of-sprint commit
