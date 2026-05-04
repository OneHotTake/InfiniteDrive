# BACKLOG.md — Technical Debt (Compiled from 3 audits: Claude, Qwen, Grok)

## Attack Order

```
511 (quick wins) → 512 (factory) → 513 (DB decomposition) → 514 (service extraction)
                  ↘ 515 (model consolidation) → 516 (god class internals)
```

## Priority Map

| ID | Priority | Sprint | Description |
|----|----------|--------|-------------|
| DEBT-01 | HIGH | 511 | Triplicated ranking logic (RankCandidates/IsRemux/TierScore) in 3 files |
| DEBT-02 | HIGH | 512 | 22 scattered `new AioStreamsClient()` instantiations |
| DEBT-03 | HIGH | 513/515 | DatabaseManager god class — decomposed into 6 partial classes (6092→2341 lines) |
| DEBT-04 | HIGH | Gradual | Plugin.Instance singleton across 37 files — migrate per-sprint |
| DEBT-05 | HIGH | 515 DONE | Three competing stream models — merged into single StreamCandidate, Candidate.cs deleted |
| DEBT-06 | HIGH | 515 DONE | Dual ICatalogRepository — CatalogRepository.cs deleted, unified in DatabaseManager partial |
| DEBT-07 | HIGH | 515 DONE | Two parallel state machines — merged into single ItemLifecycle |
| DEBT-08 | HIGH | 511 | RateLimiter memory leak — entries never evicted |
| DEBT-09 | HIGH | 511 | PluginSecret race condition — non-thread-safe boolean |
| DEBT-10 | MED | 514/515 DONE | ResolverService — split into ResolverService.cs (349) + ResolverService.Cache.cs (267) |
| DEBT-11 | MED | 514/515 DONE | AioMediaSourceProvider — split into 3 partials (966+430+389 lines) |
| DEBT-12 | MED | 514/515 DONE | CatalogSyncTask — extracted CatalogProviders.cs (859), main now 738 lines |
| DEBT-13 | MED | 516 DONE | DiagnosticsEndpoints — split into 10 individual service files, deleted DebugSeedMatrixService |
| DEBT-14 | MED | 516 DONE | StreamHelpers — deleted dead ExponentialBackoffMs, pragmatic no-split (7 callers unchanged) |
| DEBT-15 | MED | 516 DONE | StrmWriterService — deleted duplicate WriteEpisodeStrm, extracted BuildEpisodePath helper |
| DEBT-16 | MED | 516 DONE | AioStreamsClient — DTOs extracted to Models/AioStreams.cs (534), dead methods deleted, stream methods merged (1482→882) |
| DEBT-17 | MED | 513 | Sync-over-async `.Result` blocking in DatabaseManager (3 sites) |
| DEBT-18 | MED | — | Resolution/codec/HDR parsing duplicated across StreamHelpers + CandidateNormalizer |
| DEBT-19 | MED | — | RemovalPipeline/RemovalService grace period duplication |
| DEBT-20 | MED | — | PluginConfiguration duplicate properties (MetadataCertificationCountry vs CertificationCountry, etc.) |
| DEBT-21 | MED | — | RateLimiter CheckResolveLimit/CheckStreamLimit structural clones |
| DEBT-22 | MED | — | ToDisplayString() duplicated 5x across enums |
| DEBT-23 | MED | — | IdResolverService 6x repeated prefix parsing pattern |
| DEBT-24 | MED | — | Legacy migration code runs every startup (180 lines) |
| DEBT-25 | MED | — | CooldownGate hardcoded JSON construction |
| DEBT-26 | LOW | 511 | Dead model AioStreamsPrefixDefaults.cs |
| DEBT-27 | LOW | 511 | NotImplementedException in AioMetadataProvider |
| DEBT-28 | LOW | 511 | RemovalService no-op RemoveFromEmbyAsync method |
| DEBT-29 | LOW | 511 | DatabaseManager phantom column mapper (strm_token_expires_at) |
| DEBT-30 | LOW | 511 | PluginConfiguration no-op self-assignment |
| DEBT-31 | LOW | 511 | Version number "1.0" in AioStreamsClient UserAgent |
| DEBT-32 | LOW | — | PlayRequest defined outside namespace |
| DEBT-33 | LOW | — | CandidateNormalizer duplicate pattern matches |
| DEBT-34 | LOW | — | RemovalService.IsGracePeriodExpiredAsync unnecessary async |
| DEBT-35 | LOW | — | RemovalService.RemoveStrmFileAsync misleading Async suffix |
| DEBT-36 | LOW | — | RateLimiter.GetClientIp doc claims forwarded header support, doesn't implement |
| DEBT-37 | LOW | — | AioMetaResponse 8 types in 1 file (388 lines) |
| DEBT-38 | LOW | — | ResolutionCoverageStats integer division precision loss |
| DEBT-39 | LOW | — | 24 sprint references in code comments |
| DEBT-40 | LOW | — | Old .embystreams_probe naming convention |

## Sprint 511 — DRY Quick Wins + Bug Fixes
**Status:** Draft | **Risk:** LOW | **Scope:** DEBT-01, 08, 09, 26-31
- Consolidate RankCandidates/IsRemux/TierScore into StreamHelpers
- Fix RateLimiter memory leak (add eviction)
- Fix PluginSecret race condition (volatile/Interlocked)
- Delete: AioStreamsPrefixDefaults.cs, no-op methods, phantom column mapper, self-assignment, version number

## Sprint 512 — AioStreamsClient Factory
**Status:** Draft | **Risk:** MED | **Scope:** DEBT-02
- Create AioStreamsClientFactory singleton
- Replace 22 `new AioStreamsClient()` sites across 14 files

## Sprint 513 — DatabaseManager Decomposition (Phase 1)
**Status:** Draft | **Risk:** HIGH | **Scope:** DEBT-03, 17
- Extract ~20 cache query methods into StreamCacheRepository
- Move catalog queries into CatalogRepository
- Fix 3 `.Result` blocking calls
- Target: DatabaseManager < 3500 lines

## Sprint 514 — Service Extraction & Slimming
**Status:** Draft | **Risk:** MED | **Scope:** DEBT-10, 11, 12
- Extract OpenMediaSourceHandler from AioMediaSourceProvider
- Move probe logic into StreamProbeService
- Split ResolverService: resolution vs cache-write
- Extract CatalogSyncTask phases into helpers
- Targets: AioMediaSourceProvider < 1200, ResolverService < 400, CatalogSyncTask < 800

## Sprint 515 — Model Consolidation + Service Decomposition
**Status:** DONE | **Risk:** HIGH | **Scope:** DEBT-05, 06, 07, 03(continued), 10-12
- Merged 3 stream DTOs into single StreamCandidate; deleted Candidate.cs
- Deleted CatalogRepository.cs (dual ICatalogRepository eliminated)
- Merged 2 state machines into single ItemLifecycle
- DatabaseManager decomposed into 6 partial classes (6092→2341 main + 5 partials)
- AioMediaSourceProvider split into 3 partials (966+430+389)
- ResolverService split into ResolverService.cs (349) + ResolverService.Cache.cs (267)
- CatalogSyncTask extracted CatalogProviders.cs (859), main now 738 lines

## Sprint 516 — God Class Internals
**Status:** DONE | **Risk:** MED | **Scope:** DEBT-13, 14, 15, 16
- FIX-516-01: Split DiagnosticsEndpoints (1012 lines, 11 services) into 10 individual files; deleted DebugSeedMatrixService
- FIX-516-02: Deleted dead ExponentialBackoffMs from StreamHelpers; pragmatic no-split (7 callers unchanged)
- FIX-516-03: Deleted duplicate WriteEpisodeStrm from StrmWriterService; extracted BuildEpisodePath helper
- FIX-516-04: Extracted 18 DTOs + exceptions to Models/AioStreams.cs (534 lines); deleted MapHttpStatusCodeToMessage, IsAllowedAioStreamsUrl; merged GetMovieStreamsAsync/GetSeriesStreamsAsync into GetStreamsCoreAsync; AioStreamsClient 1482→882 lines

## Cross-cutting: DEBT-04 (Plugin.Instance DI migration)
**Strategy:** Gradual. Each sprint replaces a few Plugin.Instance accesses with constructor-injected dependencies. No dedicated sprint.

## Unplanned MEDIUM items (DEBT-18 through DEBT-25)
Address opportunistically during related sprints or batch into a future cleanup sprint.
