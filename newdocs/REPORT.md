# InfiniteDrive — Documentation Audit Report

## What Was Done
Audited all `docs/*.md` files and `README.md` against the C# implementation (source of truth).
All changes written to `./newdocs/`. Dead/outdated docs flagged below.

---

## Critical Issues Found (Code Overrides Docs)

### 1. Config File Name — WRONG everywhere
- **Doc says:** `EmbyStreams.xml`
- **Code says:** `InfiniteDrive.xml` (PluginConfiguration.cs:10)
- **Impact:** All docs, troubleshooting guides, and security docs reference wrong filename
- **Files affected:** docs/README.md, docs/configuration.md, docs/SECURITY.md, docs/troubleshooting.md, docs/USER_DISCOVER.md, docs/COOLDOWN.md, docs/features/discover.md, docs/HISTORY.md, Plugin.cs comment

### 2. Plugin Name — "EmbyStreams" branding persists in docs
- **Doc says:** "EmbyStreams" (product name in README, docs/README, VERSIONED_PLAYBACK, SECURITY, etc.)
- **Code says:** `Name = "InfiniteDrive"` (Plugin.cs:254)
- **Impact:** Everything user-facing references wrong product name
- **Files affected:** docs/README.md, docs/VERSIONED_PLAYBACK.md, docs/SECURITY.md, docs/configuration.md, docs/features/discover.md, docs/HISTORY.md, docs/CUSTOM_PLAYBACK_HANDLER.md, docs/GELATO_STRM_ANALYSIS.md, docs/GELATO_PLAYBACK_ANALYSIS.md, docs/FINDINGS.md, docs/IChannel_Implementation_Verification.md, docs/failure-scenarios.md

### 3. Endpoint Routes — All WRONG
- **Doc says:** `/EmbyStreams/Play` (old, deleted in Sprint 137)
- **Code says:** `/InfiniteDrive/Resolve` (ResolverService.cs:39, Route attr) + `/InfiniteDrive/Stream` (StreamEndpointService.cs:20)
- **Impact:** SECURITY.md, VERSIONED_PLAYBACK.md, failure-scenarios.md, CUSTOM_PLAYBACK_HANDLER.md, features/discover.md, FINDINGS.md — all describe non-existent endpoints
- **Files affected:** docs/SECURITY.md, docs/VERSIONED_PLAYBACK.md, docs/failure-scenarios.md, docs/features/discover.md, docs/CUSTOM_PLAYBACK_HANDLER.md, docs/GELATO_STRM_ANALYSIS.md, docs/GELATO_PLAYBACK_ANALYSIS.md, docs/FINDINGS.md, docs/IChannel_Implementation_Verification.md

### 4. Authentication Model — WRONG
- **Doc says:** `api_key=YOUR_KEY_HERE` (plain API key in URL), `PlaybackApiKey` config, simple string comparison
- **Code says:** HMAC-SHA256 signed tokens via `PlaybackTokenService.ValidateStreamToken()`, `PluginSecret` (base64), FixedTimeEquals comparison
- **Impact:** SECURITY.md is fundamentally wrong — describes simple API key where code uses HMAC signatures
- **Files affected:** docs/SECURITY.md (entire security model is incorrect)

### 5. Emby Version Requirement — Wrong floor
- **Doc says:** "Emby Server 4.8 or later" (getting-started.md)
- **Code says:** `targetAbi: "4.10.0.6"` (plugin.json:9)
- **Files affected:** docs/getting-started.md

### 6. Installation Path — Wrong folder name
- **Doc says:** `/var/lib/emby/plugins/EmbyStreams`
- **Code says:** Plugin folder is `InfiniteDrive`, not `EmbyStreams`
- **Files affected:** docs/getting-started.md, docs/troubleshooting.md

### 7. Plugin DLL Name — Wrong
- **Doc says:** `EmbyStreams.dll`
- **Code says:** `InfiniteDrive.dll` (AssemblyName in .csproj:10)
- **Files affected:** README.md (root), docs/getting-started.md

---

## Dead Configuration Fields (Documented but Not in Code)

| Field in Docs | Status | Notes |
|---|---|---|
| `AioStreamsUrl` | **DEAD** | Replaced by `PrimaryManifestUrl` |
| `AioStreamsUuid` | **DEAD** | Parsed from `PrimaryManifestUrl` |
| `AioStreamsToken` | **DEAD** | Parsed from `PrimaryManifestUrl` |
| `AioStreamsFallbackUrls` | **DEAD** | Replaced by `SecondaryManifestUrl` |
| `WebhookSecret` | **DEAD** | No webhook endpoint exists |
| `DontPanic` | **DEAD** | Never implemented |
| `EnableMetadataFallback` | **DEAD** | `MetadataFallbackTask` exists but no config flag |
| `FilterAdultCatalogs` | **DEAD** | Not in PluginConfiguration.cs |
| `ApiCallDelayMs` | **DEAD** | Replaced by `CooldownGate` (no config field) |
| `EnableTraktSource` | **DEAD** | Trakt integration never built |
| `EnableMdbListSource` | **DEAD** | MDBList integration never built |
| `EnableCatalogAddon` | **DEAD** | No separate addon enable flag |
| `PlaybackApiKey` | **DEAD** | Renamed to `PluginSecret` |
| `LibraryReadoptionTask` | **DEAD** | Task deleted in Sprint 312 |
| `FileResurrectionTask` | **DEAD** | Deleted in Sprint 312 |

---

## Docs with Correct Content (Minimal Changes Needed)
- `docs/LIFECYCLE.md` — Correct flow, references correct service names
- `docs/STREAM_RESOLUTION.md` — Correct architectural pattern (ResolutionResult contract)
- `docs/PROVIDER_HEALTH_AND_CIRCUIT_BREAKER.md` — Mostly correct, ResolverHealthTracker singleton matches code
- `docs/VERSION_SLOTS_AND_REWRITING.md` — Need verification
- `docs/PERSISTENCE_AND_DELETION.md` — Need verification
- `docs/CATALOG_AND_DEDUPLICATION.md` — Need verification
- `docs/COOLDOWN.md` — References `InfiniteDrive.xml` correctly (minor fix: "EmbyStreams" branding)

---

## Missing from Docs (Implemented but Not Documented)

### Fields in PluginConfiguration.cs with no docs:
- `EmbyApiKey` — API key for .strm file authentication
- `EmbyBaseUrl` — URL written into .strm files
- `SecondaryManifestUrl` — Backup AIOStreams instance
- `EnableBackupAioStreams` — Toggle for secondary failover
- `SignatureValidityDays` — HMAC token expiry
- `AioMetadataBaseUrl` — AIOMetadata enrichment endpoint
- `SystemRssFeedUrls` — Admin RSS feeds
- `BlockUnratedForRestricted` — Parental filtering toggle
- `TmdbApiKey` — TMDB cert lookup for parental controls
- `DeleteStrmOnReadoption` — Library re-adoption behavior
- `NextUpLookaheadEpisodes` — Next-up pre-warming
- `DefaultSlotKey` — Default quality slot
- `CandidateTtlHours` — Candidate expiry
- `PendingRehydrationOperations` — Rehydration queue

### Services/Endpoints NOT documented:
- `/InfiniteDrive/Admin/ClearSentinel` — Clear circuit breaker sentinel
- `/InfiniteDrive/Admin/UnhealthyItems` — Health endpoint
- `/InfiniteDrive/Admin/DebugSeedMatrix` — Debug endpoint
- `ActiveProviderState` — Primary/Secondary failover tracking (Sprint 311)
- `ResolverHealthTracker` — Shared circuit breaker singleton

### Architecture NOT documented:
- `SyncLock` (SemaphoreSlim) — Catalog mutation serialization
- `ProgressStreamer` — SSE event streamer
- `ManifestStatus` state machine — ok/stale/error tracking
- Repository layer: `CatalogRepository`, `VersionSlotRepository`, `CandidateRepository`, `SnapshotRepository`, `MaterializedVersionRepository`
- `CooldownGate` — Rate limiting service (replaces ApiCallDelayMs)
- `StreamProbeService` — Stream availability checking
- `IdResolverService` — ID normalization chain
- `CertificationResolver` — TMDB parental rating lookup
- `NfoWriterService` — Centralized NFO authority (Sprint 356)

---

## Files Written to ./newdocs/

| File | Change Type |
|------|-------------|
| `README.md` | Rewrite — fix plugin name, version, endpoints, architecture, Emby version |
| `docs_README.md` | Update — fix branding from EmbyStreams to InfiniteDrive |
| `ARCHITECTURE.md` | Complete rewrite — document actual service layer, DI, guardrails |
| `getting-started.md` | Update — Emby 4.10.0.6+, plugin path, endpoints, EmbyApiKey |
| `configuration.md` | Complete rewrite — correct fields, remove dead fields, add missing fields |
| `SECURITY.md` | Complete rewrite — HMAC auth model, correct endpoints, PluginSecret |
| `VERSIONED_PLAYBACK.md` | Major update — correct endpoint, remove wizard references, correct .strm format |
| `REPORT.md` | This file — dead documentation inventory |

---

## Namespace Verification

All documented namespaces are correct:
- `MediaBrowser.Model.*` — Used in DTOs, services, configuration
- `MediaBrowser.Controller.*` — Used in entities, library managers
- `MediaBrowser.Common.*` — Used in plugins, configuration
- `MediaBrowser.Model.Services` — Used for all IService endpoints
- `MediaBrowser.Controller.Net` — Used for IRequiresRequest

---

## Assembly Info (Emby Plugin Loading Critical)

From `plugin.json`:
```json
{
  "guid": "3c45a87e-2b4f-4d1a-9e73-8f12c3456789",
  "name": "InfiniteDrive",
  "version": "0.40.0.0",
  "targetAbi": "4.10.0.6",
  "framework": "net8.0"
}
```

From `InfiniteDrive.csproj`:
- `AssemblyVersion`: 0.40.0.0
- `FileVersion`: 0.40.0.0
- `Version`: 0.40.0.0
- `TargetFramework`: net8.0
- `RootNamespace`: InfiniteDrive
- `AssemblyName`: InfiniteDrive

**GUID mismatch risk:** The docs do NOT reference the GUID in any user-facing content (correctly — this is an internal detail). The GUID in `plugin.json` matches `PluginGuid` in `Plugin.cs` (both: `3c45a87e-2b4f-4d1a-9e73-8f12c3456789`). **This is correct.**

---

## Invisible Guardrails Discovered

1. **Path traversal guard** (Sprint 350): `NamingPolicyService` must validate all paths
2. **Circuit breaker fail-closed**: `ResolverHealthTracker` trips on 5 consecutive errors, diverts to secondary
3. **PluginSecret fail-closed** (Sprint 310): `StreamEndpointService` returns 503 when `PluginSecret` is empty
4. **Gap repair verification** (Sprint 311): `SeriesGapRepairService` probes upstream streams before writing .strm
5. **Rate limiter hardening** (Sprint 350): `RateLimiter` enforces per-client resolve limits
6. **IMDB ID validation** (Sprint 350): `IdResolverService` validates ID format before resolution
7. **DB write serialization** (Sprint 312): `DatabaseManager._dbWriteGate` serializes all writes (WAL mode constraint)
8. **Manifest TTL** (Sprint 102A-01): Manifest expires after 12 hours; status tracked as ok/stale/error
9. **Version slot floor** (Sprint 127): `hd_broad` slot cannot be disabled — always present
10. **CooldownGate** (Sprint 155): Replaces scattered `Task.Delay(ApiCallDelayMs)` with profile-aware delays
