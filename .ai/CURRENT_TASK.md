SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: complete
task: Sprint 353 — Centralized .strm Authority + Version Slot Consistency
last_updated: 2026-04-15

## Completed

- FIX-353-01: DeleteWithVersions + DeleteEpisodesWithVersions static helpers on StrmWriterService
- FIX-353-02: WriteEpisodeAsync (repair + versions) + WriteStrmWithVersionsAsync (known-path + versions)
- FIX-353-03: All 6 delete paths now route through StrmWriterService (RemovalService, EpisodeRemovalService, AdminService, YourFilesConflictResolver, LibraryPostScanReadoptionService, LibraryReadoptionTask)
- FIX-353-04: SeriesPreExpansionService routes all 3 write paths through WriteStrmWithVersionsAsync, BuildStrmContent deleted
- FIX-353-05: EpisodeExpandTask routes through WriteStrmWithVersionsAsync, version slot init code removed

## Files Changed

- Services/StrmWriterService.cs: +DeleteWithVersions, +DeleteEpisodesWithVersions, +WriteEpisodeAsync, +WriteStrmWithVersionsAsync
- Services/RemovalService.cs: movie delete → DeleteWithVersions
- Services/EpisodeRemovalService.cs: episode delete → DeleteEpisodesWithVersions
- Services/AdminService.cs: block delete → DeleteWithVersions
- Services/YourFilesConflictResolver.cs: conflict delete → DeleteWithVersions
- Services/LibraryPostScanReadoptionService.cs: readoption delete → DeleteWithVersions
- Tasks/LibraryReadoptionTask.cs: readoption delete → DeleteWithVersions
- Services/SeriesPreExpansionService.cs: 3 write paths → WriteStrmWithVersionsAsync, BuildStrmContent removed
- Tasks/EpisodeExpandTask.cs: write + version loop → WriteStrmWithVersionsAsync
