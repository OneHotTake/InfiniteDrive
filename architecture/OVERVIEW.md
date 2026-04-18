# InfiniteDrive — Architecture Overview

> Last reconciled: 2026-04-18 (post Language & Localization sprint)

## System Purpose

InfiniteDrive is an Emby server plugin that provides on-demand streaming from AIOStreams, Cinemeta, and user RSS catalogs (Trakt, MDBList). It manages the full lifecycle: catalog sync, metadata enrichment, .strm/.nfo file creation, Emby library integration, and playback resolution.

## Decomposed Service Architecture

The system transitioned from a monolithic StatusService + "God Task" pattern (pre-Sprint 354) to a registry of domain-specific services stitched together by background tasks.

```
┌──────────────────────────────────────────────────────────────────────┐
│                          Plugin.cs (Entry Point)                     │
│                                                                      │
│  Static singletons:                                                  │
│    Plugin.Instance       → MEF singleton, holds all services         │
│    Plugin.SyncLock       → SemaphoreSlim(1,1) for catalog syncs     │
│    Plugin.Manifest       → ManifestState (status + staleness)       │
│    Plugin.Pipeline       → PipelinePhaseTracker (real-time phases)  │
│    Plugin.ProgressStreamer → SSE broadcaster                         │
│                                                                      │
│  Instance properties (DI via constructor):                           │
│    DatabaseManager, StrmWriterService, CooldownGate,                │
│    ResolverHealthTracker, ActiveProviderState, SlotMatcher,          │
│    CandidateNormalizer, IdResolverService, CertificationResolver,    │
│    HomeSectionTracker, HomeSectionManager,                          │
│    VersionSlotRepository, CandidateRepository,                      │
│    SnapshotRepository, MaterializedVersionRepository                │
└──────────────────────────────────┬───────────────────────────────────┘
                                   │
          ┌────────────────────────┼────────────────────────────┐
          │                        │                            │
          ▼                        ▼                            ▼
┌──────────────────┐  ┌─────────────────────┐  ┌──────────────────────┐
│  Background      │  │  API Endpoints      │  │  UI Rendering        │
│  Tasks           │  │  (Services/Api/)    │  │  (UI/)               │
│  (Tasks/)        │  │                     │  │                      │
│                  │  │  CatalogEndpoints   │  │  HealthUI            │
│  CatalogSyncTask │  │  DiagnosticsEn…    │  │  RepairUI            │
│  RefreshTask     │  │  SearchEndpoints    │  │                      │
│  MarvinTask      │  │                     │  │                      │
│  CollectionTask  │  │                     │  │                      │
│  RemovalTask     │  │                     │  │                      │
│  YourFilesTask   │  │                     │  │                      │
│  (15 tasks)      │  │                     │  │                      │
└────────┬─────────┘  └──────────┬──────────┘  └──────────────────────┘
         │                       │
         ▼                       ▼
┌──────────────────────────────────────────────────────────────────────┐
│                     Domain Services (Services/)                      │
│                                                                      │
│  NamingPolicyService   MetadataEnrichmentService   NfoWriterService  │
│  StrmWriterService     StreamResolutionHelper      ResolverService   │
│  StreamEndpointService ItemPipelineService         ManifestFetcher   │
│  ManifestFilter        ManifestDiff               VersionMaterializer│
│  SeriesPreExpansion    EpisodeDiffService          RemovalPipeline   │
│  CooldownGate          ResolverHealthTracker       RateLimiter       │
│  DiscoverService       SavedService                CollectionService │
│  (60+ services total)                                               │
└──────────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌──────────────────────────────────────────────────────────────────────┐
│                        Data Layer                                    │
│                                                                      │
│  DatabaseManager (SQLite)  — schema V32+                             │
│  Repositories: Catalog, Candidate, Snapshot, MaterializedVersion,    │
│                ResolutionCache, VersionSlot                          │
└──────────────────────────────────────────────────────────────────────┘
```

## Key Design Constraints

1. **MEF instantiation**: Emby creates tasks via parameterless constructors. `Plugin.Instance` is the service locator. A DI container facade would add indirection without decoupling.
2. **Global sync lock**: `Plugin.SyncLock` (SemaphoreSlim) serializes catalog operations. Used by CatalogSyncTask, MarvinTask, RefreshTask, and five other tasks. Not a problem — correct, obvious, consistent.
3. **Two .strm URL formats**:
   - `/InfiniteDrive/resolve?token=...` → ResolverService (sync pipeline movies)
   - `/InfiniteDrive/Stream?id=...&sig=...` → StreamEndpointService (series episodes)
   - `/InfiniteDrive/Play?imdb=...` → DEAD END, no handler. Legacy fallback only.

## What This Is NOT

- **Not a saga/orchestration framework.** MarvinTask IS the orchestrator. Tasks call services directly.
- **Not a DI container.** Plugin.Instance serves that role due to MEF constraints.
- **Not microservices.** All services are in-process. The "decomposition" is about single-responsibility classes, not network boundaries.

## See Also

| Document | Content |
|----------|---------|
| [SERVICES.md](SERVICES.md) | Complete service inventory with method signatures |
| [CONTROL_FLOW.md](CONTROL_FLOW.md) | Sync pipeline end-to-end control flow |
| [STATE_MANAGEMENT.md](STATE_MANAGEMENT.md) | Manifest state, item lifecycle, pipeline phases |
| [DTO_SCHEMAS.md](DTO_SCHEMAS.md) | Exact DTO structures |
| [TASKS.md](TASKS.md) | Background task reference with phases |
