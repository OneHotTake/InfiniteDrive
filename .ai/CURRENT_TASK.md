SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: in_progress
task: Sprint 302 — Reliability & Resilience
phase: Task 302-01: Per-Resolver Circuit Breaker
last_updated: 2026-04-15

## Summary

Closes the gap detection → repair loop. Sprint 220 detects gaps; this sprint writes the missing .strm + .nfo files. 0 build errors.

### Design Note
Fixed Sprint 220's seasons_json format to include `missingEpisodeNumbers` field so repair service can identify gaps.

### Deliverables
- `Data/DatabaseManager.cs` — added `GetSeriesWithGapsAsync()` with `json_extract`
- `Services/StrmWriterService.cs` — added `WriteEpisodeStrm()` + `WriteEpisodeNfo()` for single-episode repair
- `Services/SeriesGapRepairService.cs` — core repair service (batch + single-series)
- `Tasks/SeriesGapRepairTask.cs` — IScheduledTask, 6h interval
- `Services/TriggerService.cs` — added `series_gap_repair` trigger key
- `Services/SeriesGapDetector.cs` — added `autoRepair` param + auto-repair hook
- `Services/StatusService.cs` — extended `GapScanSummary` with repair stats
