---
status: in_progress
task: Sprint 148 — Inline Enrichment + Deep Clean Priority
phase: Implementation
last_updated: 2026-04-10

## Sprint 148 — Inline Enrichment + Deep Clean Priority

### Phase 148A: EnrichStepAsync to RefreshTask
- [x] FIX-148A-01: Add EnrichStepAsync method between Hint and Notify
  - Process up to 10 no-ID items per Refresh run
  - Query items from current run only (created_at >= runStartedAt)
  - Title-based metadata search via AioMetadataClient
  - Retry backoff: Immediate -> +4h -> +24h -> Blocked
  - 2-second throttle between API calls

### Phase 148B: WriteEnrichedNfoAsync Helper
- [x] FIX-148B-01: Implement NFO writing for enriched metadata
  - Use SecurityElement.Escape() for XML sanitization
  - Write title, year, plot, uniqueid, genres
  - Write to all version slots

### Phase 148C: Progress Reporting Update
- [x] FIX-148C-01: Update 5-step to 6-step mapping
  - Collect: 0.08 → 0.16
  - Write: 0.25 → 0.33
  - Hint: 0.42 → 0.50
  - Enrich: 0.67 (NEW)
  - Notify: 0.58 → 0.83
  - Verify: 0.75 → 1.00
  - Remove Promote from progress (now sub-step of Verify)

### Phase 148D: Deep Clean Priority
- [x] FIX-148D-01: Update EnrichmentTrickleAsync query ordering
  - Prioritize no-ID items first (CASE WHEN no-ID THEN 0 ELSE 1)
  - Then by created_at ASC
  - Limit 42 items

### Phase 148E: AioMetadataClient Extension
- [x] FIX-148E-01: Add title-based search overload
  - FetchByTitleAsync(string title, int? year)
  - /search?query={title}&year={year} endpoint
  - Parse search results array

### Phase 148F: Verify Integration
- [x] FIX-148F-01: Move PromoteStalledItems to Verify step
  - PromoteStalledItems now sub-step of Verify
  - VerifyStepAsync calls PromoteStalledItems before return

### Phase 148G: Build Verification
- [x] FIX-148G-01: Build succeeded, 0 errors

---

## Progress

### Sprint 148: 7/7 phases complete
