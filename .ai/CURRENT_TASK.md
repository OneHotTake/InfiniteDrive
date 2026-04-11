---
status: completed
task: Sprints 200 + 201 — Wizard UX Overhaul & Backend Wiring
phase: Complete
last_updated: 2026-04-11

## Summary

Implemented Sprint 200 (wizard UX redesign) and Sprint 201 (backend wiring). Build verified: 0 errors, 1 warning.

## Sprint 200 — Wizard UX Overhaul

- [x] `EnableBackupAioStreams` + `SystemRssFeedUrls` added to `PluginConfiguration.cs`
- [x] Step 1 (Providers): AIOStreams primary (required, accent card), backup toggle, AIOMetadata, Cinemeta, RSS Feeds
- [x] Step 2 (Your Setup): Library Locations, Emby Server URL, Language & Region
- [x] Step 3 (Catalogs): Pure catalog picker, auto-loads on navigation
- [x] Step labels: "Providers" / "Your Setup" / "Catalogs"
- [x] Apple TV-style nav: `< Back` + `Finish & Sync` / `Next →`
- [x] Settings page: backup toggle + RSS feeds textarea wired
- [x] Build: dotnet build -c Release → 0 errors

## Sprint 201 — Backend Wiring

- [x] `AioStreamsClient` backup fallback gated behind `EnableBackupAioStreams`
- [x] `LibraryProvisioningService` fully rewritten — no stubs, uses `ILibraryManager.AddVirtualFolder`
- [x] `POST /InfiniteDrive/Setup/ProvisionLibraries` endpoint in `SetupService`
- [x] Anime routing: `CatalogType == "anime"` + `EnableAnimeLibrary` → `SyncPathAnime` (mixed library)
- [x] `WarnIfLibrariesMissing` checks anime path
- [x] `finishWizard` calls ProvisionLibraries before sync trigger

## Next Sprint

Sprint 202+. See BACKLOG.md for queued items. RSS feed plumbing + RSS user limits are open design questions (see TODO.md).
**New Sprint Template:** Use `.ai/SPRINT_TEMPLATE.md` when defining new sprints.
