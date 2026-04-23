# InfiniteDrive Architectural Overview

## 1. Entry Points & Lifecycle

### Plugin.cs — BasePlugin<T>
```csharp
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage, IHasUIPages
```
- **Config persistence:** `BasePlugin<T>` serializes to `{DataPath}/plugins/configurations/InfiniteDrive.xml`
- **Singleton:** `Plugin.Instance` accessible globally for services and tasks
- **Heavy init deferred** to `InfiniteDriveInitializationService` (IServerEntryPoint) — constructor stays lightweight

### IServerEntryPoint Implementations

| Service | Trigger | Responsibility |
|---------|---------|----------------|
| `InfiniteDriveInitializationService` | Server startup | DB init, PluginSecret generation, CooldownGate |
| `DiscoverInitializationService` | Server startup | Discover UI state initialization |
| `EmbyEventHandler` | Server startup | Emby library change events |

### Run/Dispose Sequence
1. `Plugin` constructor: sets `Plugin.Instance`, initializes logging adapters
2. `InfiniteDriveInitializationService.Run()`: called before any scheduled tasks
   - `Plugin.InitialiseDatabaseManager()` — creates SQLite at `{DataPath}/InfiniteDrive/infinitedrive.db`
   - `Plugin.EnsurePluginSecret()` — generates HMAC key if absent
   - `CooldownGate` initialized with config accessor
3. Tasks fire per Emby schedule — DB and PluginSecret guaranteed ready

---

## 2. Dependency Injection

Emby injects the following interfaces into service constructors:

| Interface | Injected Into | Purpose |
|----------|--------------|---------|
| `ILogManager` | All services | `GetLogger<T>()` → MEL `ILogger<T>` adapter |
| `IApplicationPaths` | Plugin | `DataPath` for DB and config file locations |
| `IXmlSerializer` | Plugin | Config XML serialization (handled by BasePlugin) |
| `IUserManager` | — | (reserved for future auth use) |
| `ILibraryManager` | — | (available for future library ops) |
| `IHttpClient` | — | (not used — HttpClient is instantiated directly) |

All services use `EmbyLoggerAdapter<T>` to bridge Emby's `ILogger` to MEL `ILogger<T>`.

---

## 3. Service Architecture

### API Endpoint Services (IService — REST)
All inherit `IService` + `IRequiresRequest` from `MediaBrowser.Model.Services`:

| Endpoint | Route | Method | Auth |
|----------|-------|--------|------|
| `ResolverService` | `/InfiniteDrive/Resolve` | GET | HMAC token |
| `StreamEndpointService` | `/InfiniteDrive/Stream` | GET | HMAC token |
| `AdminService` | `/InfiniteDrive/Admin/*` | GET/POST | Admin only |
| `StatusService` | `/InfiniteDrive/Status` | GET | Authenticated |
| `CatalogService` | `/InfiniteDrive/Catalog/*` | GET | Authenticated |
| `TriggerService` | `/InfiniteDrive/Trigger` | POST | Admin |
| `SetupService` | `/InfiniteDrive/Setup/*` | GET/POST | Setup flow |
| `DiscoverService` | `/InfiniteDrive/Discover/*` | GET/POST | Authenticated |
| `SearchService` | `/InfiniteDrive/Search` | GET | Authenticated |
| `RefreshManifestService` | `/InfiniteDrive/RefreshManifest` | GET | Authenticated |
| `ProgressService` | `/InfiniteDrive/Progress` | GET | Authenticated |

### Core Business Services (no REST)

| Service | Responsibility |
|---------|---------------|
| `StrmWriterService` | Unified `.strm` write path (Sprint 156) |
| `NfoWriterService` | Centralized NFO authority (Sprint 356): seed, enriched, episode, identity hint |
| `NamingPolicyService` | Single naming authority for folder names and path sanitisation (Sprint 354) |
| `MetadataEnrichmentService` | Shared retry/backoff logic for metadata enrichment (Sprint 359): 4h→24h→block at 3 retries, 2s rate limit |
| `CatalogDiscoverService` | Fetches catalog items from AIOStreams + Cinemeta |
| `IdResolverService` | Normalizes IMDb/TMDB/TVDB IDs via source `/meta` endpoint |
| `StreamProbeService` | HEAD → GET-range probe: 500ms/probe, 1.5s total budget |
| `ResolverHealthTracker` | Circuit breaker: 5 consecutive failures → open, 5min → half-open probe |
| `CooldownGate` | Profile-aware HTTP throttling (replaces scattered `Task.Delay`) |
| `CandidateNormalizer` | Parses raw AIOStreams streams into normalized candidates |
| `SlotMatcher` | Filters and ranks candidates against slot policies |
| `PlaybackTokenService` | HMAC-SHA256 stream token generation + validation |
| `SeriesPreExpansionService` | Expands series with seed episode lists |
| `ItemPipelineService` | Orchestrates catalog item creation pipeline |
| `HousekeepingService` | Prunes expired candidates, orphaned files |

---

## 4. Data Integrity

### Database: `infinitedrive.db`
- **Location:** `{DataPath}/InfiniteDrive/infinitedrive.db`
- **Schema version:** 30 (tracked in `schema_version` table)
- **Self-healing:** Integrity check on startup; recreates if corrupt
- **WAL mode:** Concurrent reads, serialized writes via `_dbWriteGate` SemaphoreSlim

### Repository Layer (Sprint 122+)
| Repository | Table |
|-----------|-------|
| `CatalogRepository` | `catalog_items`, `sync_state` |
| `VersionSlotRepository` | `version_slots` |
| `CandidateRepository` | `stream_candidates` |
| `SnapshotRepository` | `snapshots` |
| `MaterializedVersionRepository` | `materialized_versions` |
| `ResolutionCacheRepository` | `resolution_cache` |

### Write Safety
- All DB writes wrapped in transactions
- All queries use named parameters — no string interpolation
- `_dbWriteGate` SemaphoreSlim serializes writes (WAL mode: one writer at a time)

---

## 5. Resource Management

### IDisposable Patterns
- `HttpClient` instances use `SocketsHttpHandler` with `PooledConnectionLifetime = 5 min`
- No service holds open file handles — all file I/O goes through `StrmWriterService`
- `CooldownGate` holds no unmanaged resources

### Concurrency Guards
- `Plugin.SyncLock` (SemaphoreSlim 1,1): only one catalog sync or Marvin task runs at a time
- `Plugin.Manifest` (`ManifestState`): single authority for manifest status (`ManifestStatusState` enum) and 12-hour staleness TTL
- `Plugin.Pipeline` (`PipelinePhaseTracker`): real-time task/phase visibility for diagnostics; tasks report `SetPhase()` at boundaries, `Clear()` on exit
- `DatabaseManager._dbWriteGate`: SQLite WAL writer serialization
- `RateLimiter`: per-client resolve rate limits

---

## 6. Two-Phase Content Lifecycle

### Phase 1: Optimistic (Discovery/Sync)
- **Goal:** Create playable entity in library within seconds
- **Action:** Minimal `.strm` + "Seed NFO" (IDs only) via `StrmWriterService` + `NfoWriterService.WriteSeedNfo`
- **Assumption:** Content exists; provider will resolve eventually

### Phase 2: Pessimistic (Hydration/Validation)
- **Goal:** Converge local state with provider reality and metadata richness
- **Action:** Deep-expand series, write Enriched NFOs (`NfoWriterService.WriteEnrichedNfo`), validate streams
- **Constraint:** Subject to heavy throttling. Failures handled gracefully — no deletion unless permanent 404 on ALL manifests

---

## 7. Architectural Guardrails

1. **No Direct IO:** All filesystem operations pass through `StrmWriterService`. Manual `System.IO` calls are architectural violations.
2. **Naming Authority:** All paths, folder names, file naming patterns are the exclusive domain of `NamingPolicyService`.
3. **Fail-Closed Security:** HMAC signing throws if `PluginSecret` is unconfigured. No unsigned legacy URLs served.
4. **Centralized Metadata:** All XML generation via `NfoWriterService` for consistent escaping.
5. **No Service Locator:** New logic favors constructor injection over `Plugin.Instance` where possible.
6. **Circuit Breaker:** `ResolverHealthTracker` trips at 5 consecutive failures, diverts to secondary manifest.
7. **Manifest TTL:** `Plugin.Manifest` tracks status as `ManifestStatusState` enum (Error/NotConfigured/Stale/Ok); stale after 12 hours via `CheckStale()`. Exposed in Health endpoint.
8. **Slot Floor:** `hd_broad` slot is permanent — cannot be disabled.
9. **Probe Before Serve:** `StreamProbeService` validates CDN URLs before returning to players.
10. **DB Write Serialization:** `_dbWriteGate` prevents "database is locked" errors under concurrent access.
11. **Pipeline Visibility:** `Plugin.Pipeline` provides real-time task/phase snapshot; exposed via `HealthResponse.ActivePipeline`.
12. **IChannel Integration:** `InfiniteDriveDiscoverChannel` auto-discovered by Emby via reflection. Browse-only channel surfaces 42 recent items from `discover_catalog` with library decoration (✓ prefix). No `Plugin.cs` registration required.

---

## 8. Configuration Schema (PluginConfiguration)

All fields in `PluginConfiguration.cs` with `[DataMember]` are persisted. Key groups:

| Group | Fields |
|-------|--------|
| AIOStreams Connection | `PrimaryManifestUrl`, `SecondaryManifestUrl`, `EnableBackupAioStreams` |
| Catalog Selection | `EnableAioStreamsCatalog`, `AioStreamsCatalogIds`, `AioStreamsAcceptedStreamTypes` |
| Emby Address | `EmbyBaseUrl`, `EmbyApiKey` |
| Storage Paths | `SyncPathMovies`, `SyncPathShows`, `SyncPathAnime`, `LibraryNameMovies`, `LibraryNameSeries`, `LibraryNameAnime` |
| Cache & Resolution | `CacheLifetimeMinutes`, `ApiDailyBudget`, `MaxConcurrentResolutions`, `CatalogItemCap`, `CatalogSyncIntervalHours`, `CandidatesPerProvider`, `SyncResolveTimeoutSeconds` |
| Streaming | `ProxyMode`, `MaxConcurrentProxyStreams` |
| Auth | `PluginSecret`, `PluginSecretRotatedAt`, `SignatureValidityDays` |
| Provider Priority | `ProviderPriorityOrder` |
| Versioned Playback | `CandidateTtlHours`, `DefaultSlotKey`, `PendingRehydrationOperations` |
| Parental Controls | `TmdbApiKey`, `BlockUnratedForRestricted` |
| Metadata | `MetadataLanguage`, `MetadataCountryCode`, `ImageLanguage`, `SubtitleDownloadLanguages`, `SkipFutureEpisodes`, `FutureEpisodeBufferDays`, `DefaultSeriesSeasons`, `DefaultSeriesEpisodesPerSeason`, `EnableNfoHints`, `AioMetadataBaseUrl` |
| Next-Up | `NextUpLookaheadEpisodes` |
| Schedule | `SyncScheduleHour` |
| Wizard | `IsFirstRunComplete` |

All numeric fields clamped to safe ranges in `Validate()` (called on deserialization).
