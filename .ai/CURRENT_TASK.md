SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: in_progress
task: Sprint 303 — Cleanup & Dead Code Removal
phase: Remaining tasks 303-05, 303-06
last_updated: 2026-04-15

## Summary

Completing remaining Sprint 303 tasks:
- Task 303-05: Remove Unused Configuration Options (audit PluginConfiguration.cs)
- Task 303-06: Logging Consistency Pass (audit log levels)

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

Sprint 303 (partial):
- Services/TestFailoverService.cs — Removed Layer3
- Services/StrmWriterService.cs — Path traversal blocking
- Services/UserCatalogsService.cs — Error logging
- Services/DiscoverService.cs — Error logging
