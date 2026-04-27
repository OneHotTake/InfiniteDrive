# InfiniteDrive Architectural Overview

## 1. The Core Philosophy: "Optimistic Load, Pessimistic Consistency"
InfiniteDrive is built on the principle of **Immediate Availability**. We do not allow slow provider response times or throttling to degrade the Emby user experience. 

### The Two-Phase Lifecycle
* **Optimistic Phase (Discovery/Sync):** * **Goal:** Create a "playable" entity in the library within seconds.
    * **Action:** Generate minimal `.strm` files and "Seed" NFOs (basic IDs only).
    * **Assumption:** The content exists and the provider will resolve it eventually.
* **Pessimistic Phase (Hydration/Validation):** * **Goal:** Converge local state with provider reality and metadata richness.
    * **Action:** Deep-expand series, write Enriched NFOs (plot, cast), and validate stream health.
    * **Constraint:** This phase is subject to heavy throttling. Failures here must be handled gracefully without deleting the "Optimistic" work unless a permanent 404 is confirmed.

## 2. System Guardrails
* **No Direct IO:** All filesystem operations (writes, deletes, moves) MUST pass through `StrmWriterService`. Manual `System.IO` calls are architectural violations.
* **Naming Authority:** All paths, folder names, and file naming patterns are the exclusive domain of `NamingPolicyService`.
* **Fail-Closed Security:** HMAC signing for playback URLs must throw an exception if the `PluginSecret` is unconfigured. We never serve unsigned or insecure legacy `/Play` URLs.
* **Centralized Metadata:** All XML generation is handled by `NfoWriterService` to ensure consistent escaping and schema compliance.

## 3. Stream Pre-Cache Layer
The pre-cache system eliminates the 20-40s cold-browse delay by proactively resolving stream metadata before users navigate to items.

* **Background Task:** `PreCacheAioStreamsTask` runs on a configurable interval (default 6h), queries the library for uncached items, resolves streams via AIO, scores them, and stores up to 6 variants per item in the `cached_streams` table.
* **Cache Service:** `StreamCacheService` reads/writes the `cached_streams` table and converts cached variants into Emby `MediaSourceInfo[]` with `RequiresOpening=true`.
* **Primary Key Strategy:** TMDB-first (`tmdb-{id}-movie`), IMDB-fallback (`imdb-{id}-movie`). Critical for anime/foreign titles without TMDB IDs.
* **Durable Identity:** `infoHash + fileIdx` survives CDN URL rotation. Open tokens encode these for fresh URL resolution in `OpenMediaSource`.
* **Rate-Limit Aware:** Catches `AioStreamsRateLimitException`, applies exponential backoff (5s → 60s max) with jitter. Budget-gated per item.
* **Integration:** `AioMediaSourceProvider` checks `StreamCacheService` before legacy cache. On miss, live resolves and writes through to `cached_streams`.

## 4. Dependency Management
The project is moving away from `Plugin.Instance` as a service locator. New logic should favor constructor injection where possible to improve testability and reduce the blast radius of refactors.
