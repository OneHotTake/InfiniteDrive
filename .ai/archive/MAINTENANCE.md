# EmbyStreams — Library Worker Design Spec
### Version: Current · Sprints 142–148 · All Decisions Locked

---

## Overview

EmbyStreams keeps your library correct, current, and healthy. It replaces the old `DoctorTask` with two plain, purposeful workers: **Refresh** and **Deep Clean**.

Regular quick updates. Occasional thorough maintenance. One obvious button when you're impatient.

**Goal:** New items from your lists appear in Emby — searchable and playable — within a few minutes, automatically. Click **Refresh Now** for immediate action. Items with no known ID (anime, unknown titles) get metadata enriched in the same Refresh cycle they arrive.

---

## The Two Workers

### Refresh — IScheduledTask · Runs every 6 minutes

Fast and bounded. Target runtime: under 6 minutes. A concurrency guard silently skips overlapping runs.

| Step | Name | What It Does | Throttle |
|---|---|---|---|
| 1 | **Collect** | Pull only new/changed items since last watermark | 1 AIOStreams fetch per source per run |
| 2 | **Write** | Write .strm files for all Queued items | None |
| 3 | **Hint** | Write Identity Hint .nfo alongside every .strm | None |
| 4 | **Enrich** | Immediate AIOMetadata call for no-ID items (cap: 10) | 1 call per 2s |
| 5 | **Notify** | Tell Emby the new files exist | None |
| 6 | **Verify** | Check Emby has caught up; renew expiring tokens | None |

### Deep Clean — IScheduledTask · Every 18 hours

Slower and thorough. Runs overnight.

- Full validation pass over the entire library
- Token renewal for items expiring within 90 days
- Orphan file cleanup and pruning
- Enriched metadata trickle for NeedsEnrich backlog (no-ID items first)
- Promotion of Notified items stalled >24h to NeedsEnrich

---

## Critical Design Rules (From 2026-04-10 Code Review)

### Security
1. **HMAC comparisons MUST use CryptographicOperations.FixedTimeEquals** — Never use == for signature validation
2. **All Discover endpoints MUST use AdminGuard.RequireAdmin** — DiscoverService handles file writes
3. **All file paths MUST be sanitized** — SanitizeFilename() on ALL user inputs before Path.Combine()
4. **SQL MUST be parameterized** — Zero string concatenation in queries

### Data Integrity
5. **Blocked items MUST be filtered** — AND blocked_at IS NULL in all active items queries
6. **User pins MUST be checked before deletion** — UserPinRepository.HasAnyPinsAsync() guard in DeepClean
7. **Enrichment success/failure logic MUST NOT be inverted** — if (metadata != null) = success
8. **XML elements MUST use SecurityElement.Escape()** — All NFO content including uniqueid values

### Performance
9. **Bounded queries everywhere** — No int.MaxValue limits in worker loops (max: 100 for PromoteStalled)
10. **Batch writes over N+1** — Use UpsertCatalogItemsAsync(IEnumerable<>) for multi-item updates
11. **Singleton HttpClient** — Never new HttpClient() per-request

---

## Known Issues (From 2026-04-10 Code Review)

### P0 — Must Fix Before Ship
- **C-1:** DeepCleanTask enrichment logic inverted (success→retry, failure→NRE)
- **C-2:** HMAC comparison not timing-safe in PlaybackTokenService:75,209
- **C-3:** DiscoverService endpoints missing AdminGuard.RequireAdmin
- **C-4:** PromoteStalledItems uses unbounded query (int.MaxValue)
- **C-5:** blocked_at IS NULL missing from active item queries
- **C-6:** DeepClean deletes user-pinned items (no HasAnyPinsAsync check)

### P1 — Fix in Next Sprint
- **H-1:** CatalogItem/MediaItem model bifurcation
- **H-2:** uniqueid values not escaped in enriched NFO
- **H-3:** DatabaseManager is 5,624-line God class
- **H-4:** "In My Library" uses global status instead of per-user pins
- **H-7:** imdbId not sanitized before Path.Combine

---

## Decisions Log

| Decision | Resolved | Notes |
|---|---|---|
| Token lifetime | 365 days | Renewal window: 90 days before expiry |
| Enrichment retry cadence | Immediate → +4h → +24h → Blocked | Non-configurable |
| Progress reporting | 6 steps at 16%, 33%, 50%, 67%, 83%, 100% | |
| HMAC timing safety | **CRITICAL** | Use CryptographicOperations.FixedTimeEquals |
| Blocked item filtering | **CRITICAL** | blocked_at IS NULL required in all active queries |

---

**Version:** Post-Sprint 148 + 2026-04-10 Code Review Findings  
**Status:** Ground truth — code must match this spec
