SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: complete
task: Sprint 219 — IChannel SDK Reality Check (Research)
phase: Complete
last_updated: 2026-04-14

## Summary

**IChannel search is IMPOSSIBLE.** Definitive SDK analysis proves ISearchableChannel is a dead marker interface (zero members, zero server references). InternalChannelItemQuery has no SearchTerm. ChannelManager has zero search methods.

### Findings (research only, no code changes)
1. `ISearchableChannel` exists but is empty — never used by Emby server
2. `InternalChannelItemQuery` has 6 props: FolderId, UserId, StartIndex, Limit, SortBy, SortDescending — NO search
3. `IChannel.GetChannelItems` has a single overload, no search variant
4. `ChannelManager` in `Emby.Server.Implementations` has zero methods containing "Search"
5. Browse-only IChannel IS possible via FolderId routing

### Deliverables
- `.ai/research/sprint-219-dll-inspection.txt` — raw DLL analysis
- `.ai/research/sprint-219-live-props.json` — InternalChannelItemQuery properties
- `.ai/research/sprint-219-ichannel-methods.json` — IChannel method signatures
- `.ai/research/sprint-219-findings.md` — final findings with go/no-go

### Next: Sprint 220 — browse-only InfiniteDriveChannel + Sprint 221 — Discover web UI deeplink for search
