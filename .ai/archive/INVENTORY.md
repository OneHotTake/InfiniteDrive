# EmbyStreams Codebase Inventory

**Date:** 2026-04-08 | **Sprint:** Recovery Audit

---

## Controllers (7 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Controllers/ActionsController.cs | ActionsController | Plugin actions API endpoints |
| Controllers/CollectionsController.cs | CollectionsController | Collection management endpoints |
| Controllers/ConfigurationController.cs | ConfigurationController | Config page data API |
| Controllers/ItemsController.cs | ItemsController | Item metadata endpoints |
| Controllers/LogsController.cs | LogsController | Log retrieval API |
| Controllers/RemovalController.cs | RemovalController | Removal pipeline endpoints |
| Controllers/SavedController.cs | SavedController | Saved box sets API |
| Controllers/SourcesController.cs | SourcesController | Source management endpoints |
| Controllers/StatusController.cs | StatusController | Health/status endpoints |
| Controllers/VersionSlotController.cs | VersionSlotController | Versioned playback slot management |

---

## Models (26 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Models/AioMetaResponse.cs | AioMetaResponse | AIOStreams metadata response |
| Models/AioStreamsPrefixDefaults.cs | AioStreamsPrefixDefaults | Default ID prefixes per provider |
| Models/Candidate.cs | Candidate | Stream candidate for version slots |
| Models/CatalogItem.cs | CatalogItem | Catalog item with state machine |
| Models/ClientCompatEntry.cs | ClientCompatEntry | Client streaming compatibility profile |
| Models/Collection.cs | Collection | Emby collection definition |
| Models/DiscoverCatalogEntry.cs | DiscoverCatalogEntry | Discover API result |
| Models/FailureReason.cs | FailureReason | Failure reason enumeration |
| Models/HomeSectionTracking.cs | HomeSectionTracking | Home section usage tracking |
| Models/ItemPipelineResult.cs | ItemPipelineResult | Pipeline execution result |
| Models/ItemState.cs | ItemState | Doctor-era state enum (Catalogued, Present, Resolved, Retired, Orphaned, Pinned) |
| Models/ItemStatus.cs | ItemStatus | Status display helper |
| Models/LogEntry.cs | LogEntry | Log entry model |
| Models/ManifestEntry.cs | ManifestEntry | AIOStreams manifest entry |
| Models/MaterializedVersion.cs | MaterializedVersion | Version slot result |
| Models/MediaId.cs | MediaId | Multi-provider media ID wrapper |
| Models/MediaIdType.cs | MediaIdType | ID type enumeration |
| Models/MediaItem.cs | MediaItem | Media metadata wrapper |
| Models/PipelineTrigger.cs | PipelineTrigger | Pipeline trigger configuration |
| Models/PlaybackEntry.cs | PlaybackEntry | Playback log entry |
| Models/RemovalResult.cs | RemovalResult | Removal operation result |
| Models/ResolutionCacheStats.cs | ResolutionCacheStats | Cache statistics |
| Models/ResolutionCoverageStats.cs | ResolutionCoverageStats | Catalog coverage stats |
| Models/ResolutionEntry.cs | ResolutionEntry | Cache entry |
| Models/SaveReason.cs | SaveReason | Save reason enumeration |
| Models/Source.cs | Source | Source configuration model |
| Models/SourceType.cs | SourceType | Source type enumeration |
| Models/StreamCandidate.cs | StreamCandidate | Stream candidate for slots |
| Models/StreamQuality.cs | StreamQuality | Quality tier constants |
| Models/SyncState.cs | SyncState | Sync state per source |
| Models/VersionSlot.cs | VersionSlot | Versioned playback slot |
| Models/VersionSnapshot.cs | VersionSnapshot | Slot creation snapshot |

---

## Data (7 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Data/CandidateRepository.cs | CandidateRepository | Candidate data access layer |
| Data/DatabaseInitializer.cs | DatabaseInitializer | DB schema initialization |
| Data/DatabaseManager.cs | DatabaseManager | Core SQLite operations |
| Data/MaterializedVersionRepository.cs | MaterializedVersionRepository | Slot result persistence |
| Data/Schema.cs | Schema | Schema version constants |
| Data/SnapshotRepository.cs | SnapshotRepository | Slot snapshot persistence |
| Data/VersionSlotRepository.cs | VersionSlotRepository | Slot CRUD operations |

---

## Services (52 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Services/AioStreamsClient.cs | AioStreamsClient | AIOStreams API client |
| Services/AnimeDetector.cs | AnimeDetector | Anime detection from catalog metadata |
| Services/BoxSetService.cs | BoxSetService | BoxSet provider service |
| Services/CandidateNormalizer.cs | CandidateNormalizer | Candidate normalization logic |
| Services/CatalogDiscoverService.cs | CatalogDiscoverService | Catalog discovery from AIOStreams |
| Services/CinemetaProvider.cs | CinemetaProvider | Cinemeta metadata provider |
| Services/CollectionSyncService.cs | CollectionSyncService | Collection sync from provider |
| Services/CollectionsService.cs | CollectionsService | Collection CRUD operations |
| Services/DigitalReleaseGateService.cs | DigitalReleaseGateService | New release gating logic |
| Services/DiscoverInitializationService.cs | DiscoverInitializationService | First-run discover setup |
| Services/DiscoverService.cs | DiscoverService | Manual "Add to Library" feature |
| Services/EmbyEventHandler.cs | EmbyEventHandler | Emby event subscriptions |
| Services/HomeSectionManager.cs | HomeSectionManager | Home section routing |
| Services/HomeSectionStub.cs | HomeSectionStub | Section placeholder |
| Services/HomeSectionTracker.cs | HomeSectionTracker | Section usage tracking |
| Services/HousekeepingService.cs | HousekeepingService | Periodic cleanup tasks |
| Services/IManifestProvider.cs | IManifestProvider | Manifest provider interface |
| Services/ItemPipelineService.cs | ItemPipelineService | Item processing pipeline |
| Services/LibraryProvisioningService.cs | LibraryProvisioningService | Library folder setup |
| Services/ManifestDiff.cs | ManifestDiff | Manifest comparison logic |
| Services/ManifestFetcher.cs | ManifestFetcher | HTTP manifest fetcher |
| Services/ManifestFilter.cs | ManifestFilter | Provider/quality filtering |
| Services/ManifestUrlParser.cs | ManifestUrlParser | Manifest URL parsing |
| Services/MetadataChainService.cs | MetadataChainService | Metadata provider chain |
| Services/MetadataHydrator.cs | MetadataHydrator | Metadata enrichment logic |
| Services/PlaybackService.cs | PlaybackService | Stream URL resolution & caching |
| Services/ProgressEndpoint.cs | ProgressEndpoint | Live progress streaming |
| Services/ProgressStreamer.cs | ProgressStreamer | SSE event streaming |
| Services/ProxySessionStore.cs | ProxySessionStore | Proxy session management |
| Services/RehydrationService.cs | RehydrationService | Slot rehydration trigger |
| Services/RemovalPipeline.cs | RemovalPipeline | Item removal pipeline |
| Services/RemovalService.cs | RemovalService | Item removal operations |
| Services/SavedBoxSetService.cs | SavedBoxSetService | Saved box set operations |
| Services/SavedService.cs | SavedService | Saved items CRUD |
| Services/SeriesPreExpansionService.cs | SeriesPreExpansionService | Series episode expansion |
| Services/SetupService.cs | SetupService | First-run wizard service |
| Services/SignedStreamService.cs | SignedStreamService | Versioned playback stream endpoint |
| Services/SingleFlight.cs | SingleFlight | Per-item deduplication gate |
| Services/SlotMatcher.cs | SlotMatcher | Slot selection logic |
| Services/sourcesService.cs | SourcesService | Source CRUD operations |
| Services/StatusService.cs | StatusService | Health/status aggregation |
| Services/StreamCache.cs | StreamCache | Multi-layer stream cache |
| Services/StreamHelpers.cs | StreamHelpers | Stream utility methods |
| Services/StreamIdParser.cs | StreamIdParser | Stream ID parsing logic |
| Services/StreamProxyService.cs | StreamProxyService | HLS manifest proxy |
| Services/StreamResolutionHelper.cs | StreamResolutionHelper | Resolution quality helpers |
| Services/StreamResolutionService.cs | StreamResolutionService | Resolution cache operations |
| Services/StreamResolver.cs | StreamResolver | LinkResolverTask helper |
| Services/StreamUrlSigner.cs | StreamUrlSigner | HMAC-SHA256 URL signing |
| Services/StreamUrlValidator.cs | StreamUrlValidator | URL validation logic |
| Services/StremioMetadataProvider.cs | StremioMetadataProvider | Stremio metadata provider |
| Services/TestFailoverService.cs | TestFailoverService | Fallback testing service |
| Services/ThroughputTrackingStream.cs | ThroughputTrackingStream | Bandwidth monitoring |
| Services/TriggerService.cs | TriggerService | Manual task trigger API |
| Services/UniqueIdMapper.cs | UniqueIdMapper | Multi-provider ID mapping |
| Services/VersionMaterializer.cs | VersionMaterializer | Slot materialization logic |
| Services/VersionPlaybackService.cs | VersionPlaybackService | Versioned playback orchestrator |
| Services/VersionPlaybackStartupDetector.cs | VersionPlaybackStartupDetector | Startup detection & provisioning |
| Services/WebhookService.cs | WebhookService | Emby webhook handlers |
| Services/YearParser.cs | YearParser | Year extraction logic |
| Services/YourFilesConflictResolver.cs | YourFilesConflictResolver | YourFiles conflict resolution |
| Services/YourFilesMatcher.cs | YourFilesMatcher | YourFiles pattern matching |
| Services/YourFilesScanner.cs | YourFilesScanner | YourFiles filesystem scanner |

---

## Tasks (14 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Tasks/CatalogDiscoverTask.cs | CatalogDiscoverTask | Periodic catalog discovery |
| Tasks/CatalogSyncTask.cs | CatalogSyncTask | Daily catalog sync from sources |
| Tasks/CollectionSyncTask.cs | CollectionSyncTask | Periodic collection sync |
| Tasks/CollectionTask.cs | CollectionTask | Periodic collection ingest |
| Tasks/EpisodeExpandTask.cs | EpisodeExpandTask | Series episode expansion |
| Tasks/FileResurrectionTask.cs | FileResurrectionTask | [DEPRECATED] Missing file detection |
| Tasks/LibraryReadoptionTask.cs | LibraryReadoptionTask | Library path detection |
| Tasks/LinkResolverTask.cs | LinkResolverTask | Stream URL resolution |
| Tasks/MetadataFallbackTask.cs | MetadataFallbackTask | NFO enrichment fallback |
| Tasks/RehydrationTask.cs | RehydrationTask | Slot rehydration triggers |
| Tasks/RemovalTask.cs | RemovalTask | Item removal from library |
| Tasks/SyncTask.cs | SyncTask | General sync orchestrator |
| Tasks/YourFilesTask.cs | YourFilesTask | YourFiles scan trigger |

---

## Configuration (9 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Configuration/Attributes/DangerousAttribute.cs | DangerousAttribute | Dangerous action marker |
| Configuration/Attributes/DataGridAttribute.cs | DataGridAttribute | Data grid directive |
| Configuration/Attributes/FilterOptionsAttribute.cs | FilterOptionsAttribute | Filter options directive |
| Configuration/Attributes/RunButtonAttribute.cs | RunButtonAttribute | Run button directive |
| Configuration/Attributes/TabGroupAttribute.cs | TabGroupAttribute | Tab grouping directive |
| Configuration/BasePluginViewModel.cs | BasePluginViewModel | Base view model |
| Configuration/ContentManagementViewModel.cs | ContentManagementViewModel | Content tab model |
| Configuration/MyLibraryViewModel.cs | MyLibraryViewModel | MyLibrary tab model |
| Configuration/RowModels.cs | RowModels | Grid row models |
| Configuration/WizardViewModel.cs | WizardViewModel | Setup wizard model |

---

## Repositories (4 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Repositories/CatalogRepository.cs | CatalogRepository | Catalog CRUD via DatabaseManager |
| Repositories/Interfaces/ICatalogRepository.cs | ICatalogRepository | Catalog repository interface |
| Repositories/Interfaces/IPinRepository.cs | IPinRepository | PIN repository interface |
| Repositories/Interfaces/IResolutionCacheRepository.cs | IResolutionCacheRepository | Cache repository interface |

---

## Other (2 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Plugin.cs | Plugin | Main plugin entry point |
| PluginConfiguration.cs | PluginConfiguration | Configuration model |

---

## Tests (6 files)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Tests/PlaybackTests.cs | PlaybackTests | Playback tests |
| Tests/StreamUrlTests.cs | StreamUrlTests | URL signing tests |
| Tests/SyncPipelineTests.cs | SyncPipelineTests | Pipeline tests |
| Tests/UniqueIdTests.cs | UniqueIdTests | ID mapping tests |
| Tests/UserActionTests.cs | UserActionTests | User action tests |
| Tests/YourFilesTests.cs | YourFilesTests | YourFiles tests |

---

## Resilience (1 file)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Resilience/AIOStreamsResiliencePolicy.cs | AIOStreamsResiliencePolicy | AIOStreams retry policy |

---

## Logging (1 file)

| File | Primary Class | Description |
|-------|---------------|-------------|
| Logging/EmbyLoggerAdapter.cs | EmbyLoggerAdapter | Emby logger adapter |

---

## Total Count: **129 source .cs files**
