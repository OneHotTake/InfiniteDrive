SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: completed
task: Sprint 302 — Reliability & Resilience
phase: Task 302-03: CooldownGate Thread Safety
last_updated: 2026-04-15

## Summary

Completed Sprint 302 with all 6 tasks:
- 302-01: Circuit Breaker (ResolverHealthTracker.cs)
- 302-02: Burst-aware Rate Limiting (CooldownGate.cs)
- 302-03: CooldownGate Thread Safety (added lock, fixed await in lock)
- 302-04: StreamProbeService Implementation (updated timeouts to 2s, budget to 5s)
- 302-05: Public Endpoint Rate Limiting (RateLimiter.cs)
- 302-06: Marvin Sync Safety (last_verified_at column, 7-day grace period)

Partial Sprint 303 cleanup:
- Removed dead Layer3 debrid code
- Added path traversal blocking
- Added logging to empty catch blocks

## Deliverables
- Services/ResolverHealthTracker.cs (NEW)
- Services/RateLimiter.cs (NEW)
- Data/DatabaseManager.cs — Schema V30, last_verified_at column, UpdateLastVerifiedAtAsync
- Services/CooldownGate.cs — Thread-safe lock, _lock field
- Services/StreamProbeService.cs — 2s timeout, 5s budget
- Services/ResolverService.cs — Rate limiter integration
- Services/StreamEndpointService.cs — Rate limiter integration
- Services/TestFailoverService.cs — Removed Layer3 dead code
- Services/UserCatalogsService.cs — Added error logging
- Services/DiscoverService.cs — Added error logging
- Services/StrmWriterService.cs — Path traversal blocking
- Tasks/CatalogSyncTask.cs — Safety skip on resolver down
- Models/CatalogItem.cs — LastVerifiedAt property
