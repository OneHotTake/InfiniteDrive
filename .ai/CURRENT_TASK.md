SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: complete
task: Sprint 218 — Series Episode Pre-Expansion Fix
phase: Complete
last_updated: 2026-04-14

## Summary

**Fixed critical episode expansion bug.** Series/anime items only produced a single `.strm` file instead of per-episode files because `SeriesPreExpansionService` was never wired into the pipeline.

### Fix (3 files)
1. **Services/SeriesPreExpansionService.cs** — Fixed anime path routing, NFO root element (`<episodedetails>`), non-IMDB ID handling
2. **Tasks/RefreshTask.cs** — Wired `SeriesPreExpansionService` into `WriteStepAsync`; series/anime items now expand to per-episode .strm + .nfo
3. **Tasks/EpisodeExpandTask.cs** — Fixed same NFO root element bug

### Build: 0 errors, 0 warnings

---
status: complete
task: Sprint 217 — Anime NFO Enrichment & Silent-Drop Hardening
phase: Complete
last_updated: 2026-04-14

## Summary

**Enriched NFO files for anime plugin matching and hardened silent drop paths.**

### Changes (3 files)
1. **Services/SeriesPreExpansionService.cs** — tvshow.nfo now includes all provider IDs (kitsu, anilist, mal, tmdb, tvdb) as `<uniqueid>` tags, CDATA-wrapped plot, genres, status, premiered, and `<displayorder>absolute</displayorder>` for anime series. Removed stale series overview from episode NFOs.
2. **Tasks/CatalogSyncTask.cs** — Extended dedup to match by TMDB ID cross-reference (FIX-217-05). Anime always wins on dedup merge (FIX-217-06). Added structured logging at all silent drop points (FIX-217-07). Preserves raw meta JSON on sync (FIX-217-08).
3. **Data/DatabaseManager.cs** — raw_meta_json column already existed from V25; no migration needed.

### Build: 0 errors, 0 warnings
