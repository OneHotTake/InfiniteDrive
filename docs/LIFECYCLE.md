# Content Lifecycle & Flow

Every content item follows a specific state progression from discovery to full library integration.

## Stage 1: Discovery & Seed (Optimistic)
**Trigger:** Manual Sync or Catalog Task.
**Logic:** `DiscoverService` -> `StrmWriterService`.
**Outcome:** A `.strm` file exists and a "Seed NFO" is written containing only `tmdbid`, `imdbid`, or `tvdbid`. The item is now visible in Emby.

## Stage 2: Expansion (Series Only)
**Trigger:** `SeriesPreExpansionService`.
**Logic:** Fetching the full episode list for a series based on the primary ID.
**Outcome:** All season folders and episode `.strm` files are created using the "Optimistic" pattern.

## Stage 3: Hydration (Pessimistic)
**Trigger:** `MarvinTask` or `RefreshTask`.
**Logic:** Calling `NfoWriterService.WriteEnrichedNfo()`.
**Outcome:** The "Seed NFO" is overwritten with full metadata (Plots, Ratings, Genres). This stage may take days for large libraries due to provider API limits.

## Stage 4: Validation (The Deletion Guard)
**Trigger:** Health Check / Resolver Cycles.
**Logic:** `StreamResolutionHelper` returns a `ResolutionResult`.
**The Contract:**
- **Success:** Do nothing.
- **Throttled (429):** Sleep and skip. Do **NOT** delete the item.
- **ProviderDown (5xx):** Attempt failover to secondary manifest.
- **ContentMissing (404):** Only if 404 is confirmed on **ALL** manifest sources is the item marked for "Pessimistic Deletion."


