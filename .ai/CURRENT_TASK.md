SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: complete
task: Sprint 218 — Series Episode Pre-Expansion Fix
phase: Complete
last_updated: 2026-04-14

## Summary

**Fixed critical episode expansion bug.** Series/anime items only produced a single `.strm` file instead of per-episode files because `SeriesPreExpansionService` was never wired into the pipeline.

### Root Cause
`RefreshTask.WriteStepAsync` wrote one `.strm` per item regardless of media type. `SeriesPreExpansionService` had full expansion logic (season folders, per-episode `.strm` + `.nfo`) but was never called from any pipeline task.

### Fix (3 files)
1. **Services/SeriesPreExpansionService.cs** — Fixed anime path routing (uses `SyncPathAnime` for anime items), fixed NFO root element (`<episodedetails>` instead of `<episodedata>`), fixed non-IMDB ID handling (kitsu etc.) by passing separate parameters instead of splitting on `:`
2. **Tasks/RefreshTask.cs** — Wired `SeriesPreExpansionService` into `WriteStepAsync`: series/anime items now call `ExpandSeriesFromMetadataAsync()` which fetches full episode metadata and writes per-episode `.strm` + `.nfo` files with proper folder structure. Hint step skips expanded series items (they already have `tvshow.nfo`).
3. **Tasks/EpisodeExpandTask.cs** — Fixed same `<episodedata>` → `<episodedetails>` NFO root element bug.

### Build: 0 errors, 0 warnings

---
status: in-progress
task: Sprint 217 — Anime NFO Enrichment, Raw JSON Storage & Silent-Drop Hardening
