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

## 3. Dependency Management
The project is moving away from `Plugin.Instance` as a service locator. New logic should favor constructor injection where possible to improve testability and reduce the blast radius of refactors.
