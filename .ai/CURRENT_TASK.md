---
status: complete
task: Sprint 210 — User Discover UI (Proper) + Sprint 209 — Parental Filtering
phase: Complete
last_updated: 2026-04-12

## Summary

**Sprint 209 Complete:** Content Ratings in Discover
- Added CertificationResolver for fetching TMDB/RPDB certifications
- Added certification column to discover_catalog (Schema V27)
- Implemented parental filtering in DiscoverService
- Added TmdbApiKey, RpdbApiKey, HideUnratedContent to config
- Added certification to DiscoverItem DTO and browse/search results

**Sprint 210 Complete:** User-Facing Discover UI
- Created InfiniteDiscover plugin page with three tabs
- Discover Tab: Browse catalog, search, add/remove from library
- My Picks Tab: View and manage saved items
- My Lists Tab: Subscribe to Trakt/MDBList RSS feeds
- Created discoverpage.html, discoverpage.js, discoverpage.css
- Deprecated InfiniteDriveChannel with [Obsolete] attribute
- Created docs/USER_DISCOVER_UI.md with full user documentation
- Updated README.md with UI section

## Files Created
- Configuration/discoverpage.html, .js, .css
- Services/CertificationResolver.cs
- docs/USER_DISCOVER_UI.md
- .ai/sprints/sprint-209.md
- .ai/sprints/sprint-210.md

## Files Modified
- Plugin.cs: Register Discover page, register CertificationResolver
- PluginConfiguration.cs: Add rating API keys and toggle
- Data/Schema.cs: Set CurrentSchemaVersion = 27
- Data/DatabaseManager.cs: V27 migration, certification CRUD, plugin_metadata safeguard
- Models/DiscoverCatalogEntry.cs: Add Certification property
- Services/DiscoverService.cs: Parental filtering, certification support
- Services/CatalogDiscoverService.cs: Batch certification fetch
- Services/InfiniteDriveChannel.cs: Add [Obsolete] attribute
- Configuration/configurationpage.html: Add ratings config UI
- Configuration/configurationpage.js: Load/save rating settings
- InfiniteDrive.csproj: Add discoverpage embedded resources
- README.md: Add User Interface section

## Build Status
✅ Build succeeded (0 errors, 0 warnings)
✅ Server running cleanly on port 8096
✅ All services initialized including CertificationResolver

## Commits
1. `feat: Sprint 209 + 210 — Parental Filtering + User Discover UI`
2. `fix: Add safeguard for plugin_metadata table`

## Next Actions
None. Both sprints are complete and committed.
