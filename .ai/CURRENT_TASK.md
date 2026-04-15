SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: partial
task: Sprint 304 — Nice-to-Have Improvements
phase: Partial completion — 304-06 design done; 304-05 analyzed
last_updated: 2026-04-15

## Summary

Sprint 303 complete. Sprint 304 partial.

## Completed Work

Sprint 303 (complete):
- Task 303-01 through 303-06: All tasks implemented

Sprint 304 (partial):
- Task 304-06: State machine design document created (.ai/STATE_MACHINE_DESIGN.md)
- Task 304-05: SingleFlight caching analysis created (.ai/SINGLEFLIGHT_CACHE_ATTEMPT.md)

## Skipped Tasks (Too Complex for Current Scope)

Task 304-01: Proactive Token Refresh
- Requires new TokenRefreshTask.cs scheduled service
- Requires token_expires_at tracking in database
- Requires periodic .strm re-signing logic
- **Reason:** Multi-file infrastructure beyond scope ceiling

Task 304-02: Cache Pre-Warm on Detail View
- Requires new CachePreWarmService.cs
- Requires background resolution queuing infrastructure
- Requires DiscoverService integration with cache probing
- **Reason:** New service + extensive changes beyond scope ceiling

Task 304-03: Anime Canonical ID Dedup
- Requires IdResolverService cross-ID tracking
- Requires CatalogSyncTask dedup logic changes
- Requires media_item_ids table updates for all ID types
- **Reason:** Multi-service logic beyond scope ceiling

Task 304-04: Identity Verification Warning
- Requires metadata comparison algorithms
- Requires confidence scoring system
- Requires UI warning indicators in DiscoverService
- **Reason:** New algorithm + UI changes beyond scope ceiling

Task 304-05: SingleFlight Result Caching
- Previously attempted (failed due to Lazy<T> type constraints)
- Implementation approach documented (.ai/SINGLEFLIGHT_CACHE_ATTEMPT.md)
- **Reason:** Complex threading/caching logic beyond scope ceiling

## Completed Work

Sprint 302 (complete):
- Services/ResolverHealthTracker.cs (NEW)
- Services/RateLimiter.cs (NEW)
- Data/DatabaseManager.cs — Schema V30, last_verified_at column
- Services/CooldownGate.cs — Thread-safe _lock field
- Services/StreamProbeService.cs — 2s timeout, 5s budget
- Services/ResolverService.cs — Rate limiter integration
- Services/StreamEndpointService.cs — Rate limiter integration
- Services/TestFailoverService.cs — Removed Layer3 dead code
- Tasks/CatalogSyncTask.cs — Safety skip on resolver down
- Models/CatalogItem.cs — LastVerifiedAt property

Sprint 303 (complete):
- Task 303-01: Removed Layer3 dead code from TestFailoverService
- Task 303-02: Verified single .strm per item pattern (no multi-strm remnants)
- Task 303-03: Verified error handling in resolution path (no silent catch blocks)
- Task 303-04: Added path traversal blocking in StrmWriterService
- Task 303-05: Audited config — all fields active; fixed Schema.cs version (27→30)
- Task 303-06: Audited logging — silent catches in StatusService documented as non-critical
