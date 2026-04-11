---
status: ready
task: Sprints 204–205 — Ready
phase: Sprint 203 Complete
last_updated: 2026-04-11

## Summary

Sprint 203 complete. Settings tab restructured as 5 flat cards, vocabulary pass complete, tab bar reduced to 5 tabs. Sprints 204-205 pending.

## Completed (Sprint 202)
- ✅ Deleted dead pins-related code (UserItemPin, IPinRepository, UserPinRepository, UserService)
- ✅ Removed GetUserPinnedImdbIdsAsync from DiscoverService
- ✅ Renamed DeepCleanTask → MarvinTask (TaskName: "InfiniteDrive Marvin", TaskKey: "InfiniteDriveMarvin")
- ✅ Updated all references to MarvinTask throughout codebase

## Completed (Sprint 203)
- ✅ Tab bar: 8→5 tabs (Setup, Overview, Settings, Content, Marvin)
- ✅ Overview tab: Merged Health content (System Health, Sources Table, Resolution Coverage, Background Tasks, Debug Tools)
- ✅ Marvin tab: Moved Improbability Drive content with updated heading
- ✅ Content tab: Merged Blocked Items + Content Mgmt
- ✅ Settings tab: 7 accordions→5 flat cards (Sources, Playback & Cache, Library Paths, Security, Danger Zone)
- ✅ Deleted all accordion CSS and markup
- ✅ Vocabulary pass: "Catalog"/"Catalogs" → "Source"/"Sources" in admin strings
- ✅ showTab() fixes: Overview/Marvin mappings, refreshSourcesTab() trigger
- ✅ User tabs: Hidden with display:none (Discover, My Picks, My Lists)

## Next Sprints

**Sprint 204:** Create InfiniteDriveChannel (IChannel) + DiscoverService un-gating + parental filtering

**Sprint 205:** Delete user tabs from config page (Discover, My Picks, My Lists tab bodies)

## Next Action

Ready to start Sprint 204 — InfiniteDriveChannel implementation for user-facing browse surface.
