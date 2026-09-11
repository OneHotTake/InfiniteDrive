# Catalog Management & Deduplication

## 1. The Global Identity (The "Golden Record")
An item's identity is defined by its external IDs (TMDB, IMDB, TVDB). 
- **Rule:** If two items in different manifests share the same `tmdbid`, they are the SAME item.
- **Rule:** Every configured manifest is an active peer. Catalog and search
  results are unioned; duplicate provider IDs collapse to one logical item.

## 2. Deduplication Logic
During the **Discovery Phase**, the `DatabaseManager` performs a "Merge-on-ID" operation:
1. If a new item matches an existing provider ID, it merges into the existing record.
2. We do NOT create a second folder or `.strm` file.
3. Resolution evaluates all configured manifests for the one library entry; it
   does not persist a dynamic movie/show secondary URL.

## Catalog-less manifests

Lists are independent content sources and still synchronize when a manifest has
zero catalogs. If neither a list nor any configured manifest yields content, a
small Cinemeta starter catalog is derived for that run. This fallback disappears
automatically once real catalog/list state exists.

## 3. Item Blocking
Users can "Block" items via the UI to prevent them from reappearing in the library.
- **Implementation:** Blocked items are moved to a `Blacklist` table. 
- **Enforcement:** During the Optimistic Sync, the `DiscoverService` must check the Blacklist before calling `StrmWriterService`.
