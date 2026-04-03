# Architecture Review Summary — EmbyStreams vs Gelato

**Date:** 2026-04-02  **Reviewed against:** Gelato (local), AIOStreams (inferred from client contract)

> AIOStreams source not available locally — `/workspace/emby-debrid/aiostreams` contains unrelated AmigaOS scripts.
> AIOStreams patterns inferred from `AioStreamsClient.cs` and Stremio addon protocol.

---

## Top 5 Actionable Improvements (by impact/effort ratio)

1. **Remove `MaxFallbacksToStore` config field** — Legacy field, ignored since CandidatesPerProvider replaced it. Dead validation code. Effort: S. Impact: config clarity.
2. **Remove or wire `AioStreamsStreamIdPrefixes`** — Populated during sync but never consumed. Either delete the field or use it to gate stream resolution by ID format. Effort: S. Impact: removes confusion.
3. **Structured catalog config** — Replace flat `AioStreamsCatalogIds` (comma string) + `CatalogItemLimitsJson` (raw JSON) with typed `CatalogConfig` objects (as Gelato does). Enables per-catalog UI cards, validation, deletion. Effort: M. Impact: significant UX improvement.
4. **Multi-ID provider matching** — Add TMDB/AniList lookup as fallback when IMDB match fails (from Gelato's `FindByProviderIds`). Reduces "item not found" for anime and non-English content. Effort: M. Impact: reduces metadata misses.
5. **Stale-while-revalidate playback** — Serve stale cached URL immediately on cache expiry, refresh in background. Currently either serves fresh or blocks on sync resolve. Effort: M. Impact: smoother playback UX during provider outages.

---

## Config Fields to Remove or Deprecate

| Field | Status | Action |
|-------|--------|--------|
| `MaxFallbacksToStore` | UNUSED | Remove in next sprint. Always overridden by `CandidatesPerProvider`. |
| `AioStreamsStreamIdPrefixes` | MISCONFIGURED | Remove or implement consumption. Currently written but never read for filtering. |

All other 43 config fields are actively used and correctly configured.

---

## Deadlock Risks

**No critical deadlock risks found.** All synchronization patterns are properly implemented:

| Location | Pattern | Risk |
|----------|---------|------|
| PlaybackService.cs | `lock (RateLimitLock)` | LOW — simple dict access |
| UnauthenticatedStreamService.cs | `lock (RateLimitLock)` | LOW — localhost only |
| DatabaseManager.cs | `SemaphoreSlim(1,1)` write gate | LOW — proper async + try/finally |
| LinkResolverTask.cs | `SemaphoreSlim(concurrency)` | LOW — API throttle |

**No sync-over-async patterns.** No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` found.
**No nested locks.** All primitives are single-level.

---

## 3 UX Improvements from Gelato

1. **Per-catalog config cards** — Gelato uses typed `CatalogConfig` objects with per-row UI (enabled, maxItems, url). EmbyStreams uses raw comma strings. Migrate to structured objects with a card-based UI.

2. **Inline validation feedback** — Gelato shows config errors at the field level. EmbyStreams' `Validate()` silently clamps values. Add visible feedback when config values are auto-corrected.

3. **Collection auto-creation** — Gelato can create Emby collections from catalog groups (`CreateCollections`, `MaxCollectionItems`). EmbyStreams has no collection feature. Adding this would significantly improve library organization for large catalogs.

---

## Architectural Comparison Summary

| Aspect | EmbyStreams | Gelato |
|--------|-------------|--------|
| Library approach | `.strm` files + SQLite catalog | Direct BaseItem insertion |
| Stream caching | 4-layer cache + ranked fallbacks | No cache (fresh fetch each time) |
| Metadata storage | `.nfo` files (IDs only) | Emby DB provider IDs |
| Config complexity | 45 fields, flat | 18 fields + per-catalog + per-user |
| User customization | Server-wide | Per-user URL/path overrides |
| Concurrency | Write gate + semaphores | Emby-native (SDK managed) |
| Deadlock risk | LOW (good async hygiene) | N/A (delegates to Emby) |

---

## Items Added to BACKLOG.md

See BACKLOG.md for full details. Priority tags applied:
- **[P0]** Config dead field cleanup (MaxFallbacksToStore, AioStreamsStreamIdPrefixes)
- **[P1]** Structured catalog config, multi-ID matching, stale-while-revalidate
- **[P2]** Inline validation feedback, collection auto-creation, per-catalog UI cards
