SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: complete
task: Sprint 312 — Dead Code Cleanup
last_updated: 2026-04-15

## Completed

- FIX-312-01: No-op — DoctorTask already deleted in prior sprint
- FIX-312-02: Deleted Tasks/FileResurrectionTask.cs; cleaned TriggerService (constant, case, docs), comments in CatalogItem, StatusService, DatabaseManager, LibraryReadoptionTask
- FIX-312-03: Updated misleading "direct debrid fallback (Sprint 14)" comments in StreamCandidate.cs, StreamHelpers.cs, DatabaseManager.cs — InfoHash code is actively used by AIOStreams
- FIX-312-04: No-op — no Doctor-era comments found in .cs files; "Phase X" refs are Marvin phases
- FIX-312-05: No-op — Sprint 310 already cleaned all DirectStreamUrl references
- FIX-312-06: Removed unused LastKnownServerAddress field from PluginConfiguration.cs (VersionPlaybackStartupDetector never implemented)

## Files Changed

- Tasks/FileResurrectionTask.cs: DELETED
- Services/TriggerService.cs: Removed TaskFileResurrection constant + case + docs
- Models/CatalogItem.cs: Updated ResurrectionCount comment
- Services/StatusService.cs: Updated ResurrectionCount comment
- Data/DatabaseManager.cs: Updated 3 comments (FileResurrection + debrid fallback)
- Tasks/LibraryReadoptionTask.cs: Updated doc comment
- Models/StreamCandidate.cs: Removed misleading debrid fallback comment
- Services/StreamHelpers.cs: Updated InfoHash comment
- Configuration/PluginConfiguration.cs: Removed dead LastKnownServerAddress field
