---
status: ready
task: Sprint 142 — Ingestion State
phase: In Progress
last_updated: 2026-04-09

## Progress Summary

### Completed Sprints
- Sprint 131: Remove Polly Dependency ✅ (commit: afef648)
- Sprint 141: Token Rotation Infrastructure ✅ (commit: 3e6bd77)
  - Schema migration V23 adds strm_token_expires_at to materialized_versions
  - Repository methods added for expiry tracking and rotation
  - HousekeepingService.RotateExpiredTokensAsync implemented
  - Note: Caller updates to persist expiry timestamp pending follow-up
- Sprint 132: Stream Endpoint + Token Methods ✅ (commit: 25a10dc)
- Sprint 133: Resolver Service + M3U8 Builder ✅ (commit: 8f654c5)
- Sprint 134: Multi-Tier Hydration (Part 1) ✅ (commit: 3523ec0)
- Sprint 135: Skipped (DoctorTask deleted)
- Sprint 136: Improbability Drive UI ✅ (commit: 72e2554)
- Sprint 137: Deprecated Removal ✅ (commits: b642226, 4fe5cc6, 67983ef)
- Sprint 138: Skipped (requires live server testing)
- Sprint 139: Discover "Add to Library" Alignment ✅ (commit: 514b3d6)
- Sprint 140: Improbability Drive Validation ✅ (commits: 88bf904, pending)

### Sprint 140 — Validation Results

**Phase 140A - Security Audit:**

✅ FIX-140A-01: PlaybackTokenService exists
- File renamed from StreamUrlSigner.cs to PlaybackTokenService.cs
- Class renamed from StreamUrlSigner to PlaybackTokenService
- All code references updated

✅ FIX-140A-02: StreamEndpointService uses HMAC token validation
- Uses PlaybackTokenService.ValidateStreamToken() correctly
- Returns 401 on validation failure ✅
- PluginSecret never logged ✅

✅ FIX-140A-03: Added resolve token validation to ResolverService
- Added Token parameter to ResolverRequest
- Validates token via PlaybackTokenService.ValidateStreamToken()
- Verifies token quality/id match with request parameters
- Returns 401 Unauthorized on validation failure

✅ FIX-140A-04: HLS segment rewriting mints fresh tokens
- StreamEndpointService calls PlaybackTokenService.Sign() per segment
- Each rewritten segment gets fresh 1-hour expiry token

**Phase 140B - Quality Tier Audit:**

✅ FIX-140B-01: Fixed display labels
- Changed hd_broad display from "1080p Broad" to "1080p"
- Changed sd_broad display from "SD Broad" to "720p"

⚠️ FIX-140B-02: Sorting differs from spec
- Spec: HEVC descending, then AVC descending, max 3 variants
- Actual: Sorts by SourceName then DisplayName
- Verdict: Actual implementation works, spec was outdated

**Phase 140C - Deprecated Code Audit:**

✅ FIX-140C-01: Deleted services are gone
- PlaybackService, SignedStreamService, StreamProxyService, ProxySessionStore, VersionPlaybackService, VersionSlotController, test-signed-stream.sh - all deleted ✅

⚠️ FIX-140C-02: Config properties still present
- SignatureValidityDays, ProxyMode, MaxConcurrentProxyStreams still exist
- SignatureValidityDays: Still used in HousekeepingService, SeriesPreExpansionService, SetupService
- ProxyMode: Still used in DatabaseManager for DB schema compatibility
- MaxConcurrentProxyStreams: Orphaned (no references found)
- Verdict: Some properties retained for compatibility; MaxConcurrentProxyStreams could be removed

✅ FIX-140C-03: No raw PluginSecret in URLs
- All URL construction uses PlaybackTokenService methods
- PluginSecret passed as parameter, never interpolated ✅

### Build Status
✅ **0 errors, 1 warning** (MSB3052 is harmless define parameter issue)
