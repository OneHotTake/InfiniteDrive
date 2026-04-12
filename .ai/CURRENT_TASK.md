---
status: pending
task: Sprint 210 — User Discover UI (Proper)
phase: Planning - sprint file created, awaiting approval
last_updated: 2026-04-12

## Summary

Sprint 210 created to build a proper user-facing Discover UI with three tabs:
- **Discover**: Browse catalog, search, add to library
- **My Picks**: View and manage saved items
- **My Lists**: Subscribe to RSS feeds, manage custom lists

**Key Decision:** This UI will be **web-only**. Trade-off explicitly documented in `docs/USER_DISCOVER_UI.md`. Native Emby apps (mobile, smart TV) will not have this feature. This is acceptable given alternative is no UI at all.

**InfiniteDriveChannel** will be deprecated with `[Obsolete]` attribute but kept for backward compatibility.

## What Was Done This Session

- Created `.ai/sprints/sprint-210.md` with full implementation plan
- Documented web-only trade-off explicitly
- Listed all existing APIs that are ready to use
- Planned six phases: Infrastructure, Discover, My Picks, My Lists, Deprecation, Build/Verification

## Next Action

Await user approval of Sprint 210 before starting implementation.
