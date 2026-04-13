# EmbyStreams Architecture Review — Sprint Report

**Date:** 2026-04-02  **Reviewed against:** Gelato, AIOStreams (inferred)

> AIOStreams source not available locally (`/workspace/emby-debrid/aiostreams` = AmigaOS scripts).
> AIOStreams patterns inferred from `AioStreamsClient.cs` client contract.

---

## Critical (P0)

1. **`MaxFallbacksToStore` is dead config** — Legacy field ignored since v0.60 when `CandidatesPerProvider` replaced it. Still validated in `Validate()`. Remove to prevent confusion.
2. **`AioStreamsStreamIdPrefixes` is dead config** — Written during sync but never consumed for any filtering or gating decision. Remove or implement.

---

## High Impact (P1)

1. **Structured catalog config** — Replace flat comma strings and raw JSON with typed `CatalogConfig` objects (as Gelato uses). Enables per-catalog UI cards with validation.
2. **Multi-ID provider matching** — Add TMDB/AniList fallback when IMDB match fails. Pattern from Gelato's `FindByProviderIds()`. Reduces metadata misses for anime.
3. **Stale-while-revalidate** — Serve stale cache immediately, refresh in background. Currently blocks playback on full sync resolve when cache expires.

---

## Quick Wins (P2)

- **Inline config validation** — `Validate()` silently clamps values; show feedback (from Gelato)
- **Collection auto-creation** — Emby collections from catalog groups (from Gelato's `CreateCollections`)
- **Per-catalog UI cards** — Replace raw strings in Advanced tab with structured config cards

---

## Borrowed Patterns

| Pattern | From | Complexity | Risk |
|---------|------|------------|------|
| Per-catalog config objects | Gelato `CatalogConfig` | S | LOW |
| Multi-ID provider matching | Gelato `FindByProviderIds()` | M | LOW |
| Per-user config overrides | Gelato `UserConfig.ApplyOverrides()` | M | LOW |
| Direct BaseItem insertion | Gelato `GelatoManager.InsertMeta()` | L | HIGH |
| In-memory manifest cache | Gelato `_manifest` field | S | LOW |

---

## Config Audit Summary

| Field | Status | Action |
|-------|--------|--------|
| `MaxFallbacksToStore` | UNUSED | Remove — legacy, always overridden by CandidatesPerProvider |
| `AioStreamsStreamIdPrefixes` | MISCONFIGURED | Remove or implement consumption — written but never read |
| All other 43 fields | USED | No action needed |

---

## Deadlock Risk Summary

| Location | Pattern | Risk |
|----------|---------|------|
| PlaybackService.cs | `lock (RateLimitLock)` | LOW |
| UnauthenticatedStreamService.cs | `lock (RateLimitLock)` | LOW |
| DatabaseManager.cs | `SemaphoreSlim(1,1)` write gate | LOW |
| LinkResolverTask.cs | `SemaphoreSlim(concurrency)` API throttle | LOW |

**Overall: LOW deadlock risk.** No sync-over-async, no nested locks, proper try/finally throughout.

---

## Architectural Comparison

| Aspect | EmbyStreams | Gelato |
|--------|-------------|--------|
| Library items | `.strm` files + SQLite catalog | Direct BaseItem tree insertion |
| Stream caching | 4-layer cache + ranked fallbacks | No cache (fresh each time) |
| Metadata | `.nfo` files with IMDB/TMDB IDs | Emby DB provider IDs |
| Config fields | 45 (flat strings) | 18 + per-catalog + per-user |
| Multi-provider | Primary + Secondary round-robin | Single provider |
| User customization | Server-wide | Per-user URL/path overrides |
| Subtitles | Not handled | Stremio subtitle injection |

---

## Files Produced

- `.ai/REVIEW_FINDINGS.md` — Full structured comparison across 6 axes
- `.ai/REVIEW_SUMMARY.md` — Executive summary with ranked improvements
- `.ai/SPRINT_REPORT.md` — This report
- `BACKLOG.md` — Updated with [P0] [P1] [P2] tagged items
