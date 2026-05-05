# InfiniteDrive Architectural Overview

## 1. The Core Philosophy: "Optimistic Load, Pessimistic Consistency"

InfiniteDrive is built on the principle of **Immediate Availability**. We do not allow slow provider response times or throttling to degrade the Emby user experience.

### The Two-Phase Lifecycle

* **Optimistic Phase (Discovery/Sync):**
    * **Goal:** Create a "playable" entity in the library within seconds.
    * **Action:** Generate minimal `.strm` files and "Seed" NFOs (basic IDs only).
    * **Assumption:** The content exists and the provider will resolve it eventually.
* **Pessimistic Phase (Hydration/Validation):**
    * **Goal:** Converge local state with provider reality and metadata richness.
    * **Action:** Deep-expand series, write Enriched NFOs (plot, cast), and validate stream health.
    * **Constraint:** This phase is subject to heavy throttling. Failures here must be handled gracefully without deleting the "Optimistic" work unless a permanent 404 is confirmed.

## 2. System Guardrails

* **No Direct IO:** All filesystem operations (writes, deletes, moves) MUST pass through `StrmWriterService`. Manual `System.IO` calls are architectural violations.
* **Naming Authority:** All paths, folder names, and file naming patterns are the exclusive domain of `NamingPolicyService`.
* **Fail-Closed Security:** HMAC signing for playback URLs must throw an exception if the `PluginSecret` is unconfigured. We never serve unsigned or insecure legacy `/Play` URLs.
* **Centralized Metadata:** All XML generation is handled by `NfoWriterService` to ensure consistent escaping and schema compliance.

## 3. Task Orchestration

**MarvinTask** is the sole Emby-visible scheduled task. It orchestrates all background work through phased execution:

* **CatalogSyncTask** — internal helper that ingests catalogs from AIOStreams, Trakt, MDBList, and custom addons into the `catalog_items` table.
* **RefreshTask** — internal helper that validates library state, processes membership changes, and triggers metadata refreshes.
* **PreCacheAioStreamsTask** — internal helper that proactively resolves stream metadata and subtitles for uncached library items. Includes batch jitter and post-loop dead-link probing.

All three helpers are invoked by MarvinTask on its configurable interval. They are not registered as independent `IScheduledTask` implementations.

## 4. Stream Resolution Cache

A single consolidated cache table, `stream_resolution_cache`, replaces the former `resolution_cache`, `stream_candidates`, and `cached_streams` tables.

* **Primary key:** `aio_id` TEXT NOT NULL — stores the AIOStreams top-level identifier (IMDB, KITSU, MAL, etc.).
* **Secondary lookups:** `imdb_id` (nullable, real tt-prefixed IDs only), `tmdb_key` (nullable, compound key).
* **Unique constraint:** `(aio_id, COALESCE(season,-1), COALESCE(episode,-1), rank)`.
* **Stream identity:** `infoHash + fileIdx` survives CDN URL rotation. Open tokens encode these for fresh URL resolution in `OpenMediaSource`.

The `StreamCacheService` reads/writes this table and converts cached entries into Emby `MediaSourceInfo[]` with `RequiresOpening=true`.

## 5. Service Decomposition

### Media Source Provider (3 partial class files)
* `AioMediaSourceProvider.cs` — `GetMediaSources` entry point; cache-first lookup, live resolve on miss.
* `AioMediaSourceProvider.Open.cs` — `OpenMediaSource` flow; validates open tokens, returns `InfiniteDriveLiveStream`.
* `AioMediaSourceProvider.StreamBuilding.cs` — stream builders that construct `MediaSourceInfo` from AIOStreams responses.

### Resolver Service (2 partial class files)
* `ResolverService.cs` — resolve endpoint handling.
* `ResolverService.Cache.cs` — cache read/write operations.

### Catalog Sync
* `CatalogSyncTask.cs` — catalog ingestion logic.
* `CatalogProviders.cs` — `ICatalogProvider` interface and implementations for AIOStreams, Trakt, MDBList, Cinemeta, and custom addons.

### HTTP Client
* `AioStreamsClient.cs` — HTTP client for AIOStreams API.
* `AioStreamsClientFactory` — factory methods: `Create()`, `CreateForProvider()`, `TryCreateForManifest()`. Centralizes client configuration and auth.

### API Surface
* `StatusService` + 10+ API services in `Services/Api/` — REST endpoints for status, search, discover, triggers, and health.

### Throttling
* `CooldownGate` — single gate coordinating all AIOStreams/Cinemeta HTTP throttling. Two `CooldownKind` profiles: `Default` and `SeriesMeta`. Auto-detects shared vs. private instances.

### Removal
* `GracePeriodPolicy` — shared removal policy for catalog items. `RemovalService` and `RemovalPipeline` enforce it.

### Database Layer
* `DatabaseManager.cs` — core SQLite singleton (partial class with 5 companion files).
* `DatabaseManager.MediaItems.cs` — media_items, sources, memberships.
* `DatabaseManager.Catalog.cs` — catalog CRUD, user catalogs.
* `DatabaseManager.StreamCache.cs` — stream_resolution_cache operations.
* `DatabaseManager.Operations.cs` — ingestion, playback log, API budget.
* `DatabaseManager.Discover.cs` — discover catalog CRUD.
* Database file: `infinitedrive.db`.

## 6. Playback Pipeline

All playback is gated by `RequiresOpening=true`:

1. `AioMediaSourceProvider.GetMediaSources()` checks `StreamCacheService` first.
2. On cache hit: returns pre-built sources with `Path=""`, `RequiresOpening=true`, and an `OpenToken`.
3. On cache miss: live resolves via AIOStreams, returns sources, fires-and-forget writes to cache.
4. Emby calls `OpenMediaSource(openToken)`.
5. `OpenMediaSource` validates the token, resolves a fresh CDN URL, returns `InfiniteDriveLiveStream`.

See [REQUIRES_OPENING_PIPELINE.md](REQUIRES_OPENING_PIPELINE.md) and [STREAM_RESOLUTION.md](STREAM_RESOLUTION.md) for full details.

## 7. Dependency Management

The project is moving away from `Plugin.Instance` as a service locator. New logic should favor constructor injection where possible to improve testability and reduce the blast radius of refactors.
