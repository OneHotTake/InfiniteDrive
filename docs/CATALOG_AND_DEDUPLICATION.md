# Catalog Management & Deduplication

## 1. The Global Identity (The "Golden Record")
An item's identity is defined by its external IDs (TMDB, IMDB, TVDB). 
- **Rule:** If two items in different manifests share the same `tmdbid`, they are the SAME item.
- **Rule:** The `Primary Manifest` takes precedence for metadata. The `Backup Manifest` is treated as a secondary source for resolution only.

## 2. Deduplication Logic
During the **Discovery Phase**, the `DatabaseManager` performs a "Merge-on-ID" operation:
1. If a new item matches an existing ID, we append the secondary manifest's `SourceId` to the existing record.
2. We do NOT create a second folder or `.strm` file.
3. Instead, we register "Version Slots" for the various qualities offered by both manifests under a single library entry.

## 3. Item Blocking
Users can "Block" items via the UI to prevent them from reappearing in the library.
- **Implementation:** Blocked items are moved to a `Blacklist` table. 
- **Enforcement:** During the Optimistic Sync, the `DiscoverService` must check the Blacklist before calling `StrmWriterService`.
