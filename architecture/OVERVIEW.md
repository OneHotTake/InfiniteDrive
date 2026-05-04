# InfiniteDrive — Architecture Overview

> Last reconciled: 2026-05-04 (post-Sprint 516)

## System Purpose

InfiniteDrive is an Emby server plugin that provides on-demand streaming from AIOStreams, Cinemeta, and user RSS catalogs (Trakt, MDBList). It manages the full lifecycle: catalog sync, metadata enrichment, .strm/.nfo file creation, Emby library integration, and playback resolution.

Plugin name: InfiniteDrive. Binary: EmbyStreams.dll. Namespace: InfiniteDrive. Database: infinitedrive.db (SQLite with WAL mode).

## Decomposed Service Architecture

The system uses a registry of domain-specific services stitched together by a single Emby-visible scheduled task (MarvinTask) and internal helpers.

```
+----------------------------------------------------------------------+
|                          Plugin.cs (Entry Point)                     |
|                                                                      |
|  Static singletons:                                                  |
|    Plugin.Instance       -> MEF singleton, holds all services        |
|    Plugin.SyncLock       -> SemaphoreSlim(1,1) for catalog syncs    |
|    Plugin.Manifest       -> ManifestState (status + staleness)      |
|    Plugin.Pipeline       -> PipelinePhaseTracker (real-time phases) |
|    Plugin.ProgressStreamer -> SSE broadcaster                        |
|                                                                      |
|  Instance properties:                                                |
|    DatabaseManager, StreamCacheService,                              |
|    ResolverHealthTracker, CooldownGate,                              |
|    StrmWriterService, CandidateNormalizer,                           |
|    AioStreamsClientFactory, NamingPolicyService,                     |
|    NfoWriterService, MetadataEnrichmentService                       |
+----------------------------------+-----------------------------------+
                                   |
          +------------------------+------------------------+
          |                        |                        |
          v                        v                        v
+-------------------+  +----------------------+  +-------------------+
|  Background       |  |  API Endpoints       |  |  Playback         |
|  Tasks            |  |  (Services/Api/)     |  |  (Services/)      |
|  (Tasks/)         |  |                      |  |                   |
|                   |  |  StatusService       |  |  AioMediaSource   |
|  MarvinTask       |  |  CatalogEndpoints    |  |  Provider (3 files|
|  (sole Emby-      |  |  SearchEndpoints     |  |  partial class)   |
|   visible task)   |  |  12+ diagnostics     |  |                   |
|                   |  |  services            |  |  ResolverService  |
|  Internal helpers:|  |                      |  |  (2 files)        |
|  CatalogSyncTask  |  |                      |  |                   |
|  RefreshTask      |  |                      |  |  AioStreamsClient |
|  PreCacheAio...   |  |                      |  |  (factory)        |
+--------+----------+  +----------+-----------+  +--------+----------+
         |                        |                        |
         v                        v                        v
+----------------------------------------------------------------------+
|                     Domain Services (Services/)                       |
|                                                                      |
|  StreamCacheService     StreamResolutionHelper      StreamHelpers    |
|  CooldownGate           ResolverHealthTracker       ProgressStreamer |
|  CandidateNormalizer    StrmWriterService           DiscoverService  |
|  SavedService           ManifestFetcher             CollectionService|
|  SeriesPreExpansion     EpisodeDiffService          GracePeriodPolicy|
|  MetadataEnrichmentService  NamingPolicyService     NfoWriterService |
|  AioStreamsClientFactory  CertificationResolver      ListFetcher      |
+----------------------------------------------------------------------+
                                   |
                                   v
+----------------------------------------------------------------------+
|                        Data Layer                                     |
|                                                                      |
|  DatabaseManager (6 partial class files)                              |
|    .cs (core)  .MediaItems  .Catalog  .StreamCache                   |
|    .Operations  .Discover                                             |
|                                                                      |
|  Single consolidated cache table: stream_resolution_cache            |
|  Primary key: aio_id (AIOStreams top-level id)                       |
+----------------------------------------------------------------------+
```

## Key Design Constraints

1. **MEF instantiation**: Emby creates tasks via parameterless constructors. `Plugin.Instance` is the service locator. A DI container facade would add indirection without decoupling.
2. **Global sync lock**: `Plugin.SyncLock` (SemaphoreSlim) serializes catalog operations.
3. **Secure playback via RequiresOpening**:
   - `AioMediaSourceProvider` implements `IMediaSourceProvider`
   - `RequiresOpening = true` forces Emby to call `OpenMediaSource()` behind auth layer
   - CDN URLs materialize server-side only, never in .strm files or picker display
4. **Stream identity**: `infoHash + fileIdx` survives CDN URL rotation.

## What This Is NOT

- **Not a saga/orchestration framework.** MarvinTask IS the orchestrator. Tasks call services directly.
- **Not a DI container.** Plugin.Instance serves that role due to MEF constraints.
- **Not microservices.** All services are in-process. The "decomposition" is about single-responsibility classes, not network boundaries.

## See Also

| Document | Content |
|----------|---------|
| [SERVICES.md](SERVICES.md) | Complete service inventory with decomposed file structure |
| [CONTROL_FLOW.md](CONTROL_FLOW.md) | Playback pipeline and control flow |
| [STATE_MANAGEMENT.md](STATE_MANAGEMENT.md) | Database schema, manifest state, item lifecycle |
| [DTO_SCHEMAS.md](DTO_SCHEMAS.md) | Consolidated DTO structures |
| [TASKS.md](TASKS.md) | MarvinTask orchestration and internal helpers |
