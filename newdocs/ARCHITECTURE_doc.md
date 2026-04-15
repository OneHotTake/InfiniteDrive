# InfiniteDrive Architectural Overview

## 1. The Core Philosophy: "Optimistic Load, Pessimistic Consistency"

### The Two-Phase Lifecycle
- **Optimistic Phase (Discovery/Sync):** Goal: Create a "playable" entity in the library within seconds. Action: Generate minimal `.strm` files and "Seed" NFOs (basic IDs only) via `StrmWriterService` + `NfoWriterService`. Assumption: the content exists and the provider will resolve it eventually.
- **Pessimistic Phase (Hydration/Validation):** Goal: Converge local state with provider reality and metadata richness. Action: Deep-expand series, write Enriched NFOs (`NfoWriterService.WriteEnrichedNfo`), validate stream health via `StreamProbeService`. Constraint: Subject to heavy throttling. Failures handled gracefully — no deletion unless permanent 404 on ALL manifests.

## 2. Entry Points & Lifecycle

### IServerEntryPoint Implementations
| Service | Trigger | Responsibility |
|---------|---------|----------------|
| `InfiniteDriveInitializationService` | Server startup | DB init (`DatabaseManager.Initialise`), PluginSecret generation, `CooldownGate` setup |
| `DiscoverInitializationService` | Server startup | Discover UI state |
| `EmbyEventHandler` | Server startup | Library change events |

Run order: Plugin constructor → IServerEntryPoint.Run() → (DB ready) → Scheduled tasks fire

### BasePlugin<T> Configuration
- Config persisted at: `{DataPath}/plugins/configurations/InfiniteDrive.xml`
- `Validate()` called on deserialization — clamps all numeric fields to safe ranges

## 3. System Guardrails

- **No Direct IO:** All filesystem operations pass through `StrmWriterService`. Manual `System.IO` calls are architectural violations.
- **Naming Authority:** All paths, folder names, and file naming patterns are the exclusive domain of `NamingPolicyService`.
- **Fail-Closed Security:** HMAC signing for playback URLs throws if `PluginSecret` is unconfigured. `/InfiniteDrive/Resolve` and `/InfiniteDrive/Stream` return 503. No unsigned or legacy `/Play` URLs.
- **Centralized Metadata:** All XML generation via `NfoWriterService` for consistent escaping and schema compliance.
- **No Service Locator:** New logic favors constructor injection over `Plugin.Instance` where possible.

## 4. Dependency Management

Emby injects `ILogManager` into all services. All other dependencies are instantiated directly or via `Plugin.Instance` (database, config, shared state). The project is moving away from `Plugin.Instance` as a service locator.

## 5. Data Integrity

### SQLite Persistence
- **Location:** `{DataPath}/InfiniteDrive/infinitedrive.db`
- **Schema version:** 30
- **Self-healing:** Integrity check on startup; recreate if corrupt
- **WAL mode:** Concurrent reads, serialized writes via `_dbWriteGate` SemaphoreSlim
- **Repositories:** `CatalogRepository`, `VersionSlotRepository`, `CandidateRepository`, `SnapshotRepository`, `MaterializedVersionRepository`, `ResolutionCacheRepository`

## 6. Resource Management

- **HttpClient:** `SocketsHttpHandler` with `PooledConnectionLifetime = 5 min`
- **Concurrency:** `Plugin.SyncLock` SemaphoreSlim (1,1) — only one catalog sync or Marvin task at a time
- **DB writes:** Serialized via `DatabaseManager._dbWriteGate` (WAL constraint)
- **No open file handles:** All file I/O goes through `StrmWriterService`; no service holds persistent file streams

## 7. Invisible Guardrails

1. **Path traversal guard** — `NamingPolicyService` validates all paths
2. **Circuit breaker fail-closed** — `ResolverHealthTracker` trips on 5 consecutive errors; diverts to secondary
3. **PluginSecret fail-closed** — `StreamEndpointService` returns 503 when `PluginSecret` is empty
4. **Gap repair verification** — `SeriesGapRepairService` probes upstream streams before writing `.strm`
5. **Rate limiter** — `RateLimiter` enforces per-client resolve limits; excess → 429
6. **IMDB ID validation** — `IdResolverService` validates ID format before resolution
7. **DB write gate** — `DatabaseManager._dbWriteGate` prevents "database is locked" under concurrent access
8. **Manifest TTL** — Status tracked as ok/stale/error; stale after 12 hours
9. **CooldownGate** — Profile-aware HTTP throttling; replaces scattered `Task.Delay`
10. **Version slot floor** — `hd_broad` cannot be disabled; always present
