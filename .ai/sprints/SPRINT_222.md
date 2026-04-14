# Sprint 222 — Catalog-First Episode Sync

**Status:** Draft | **Risk:** MED | **Depends:** Sprint 221 | **Target:** v3.4

## Why (2 sentences max)
Current architecture writes only a seed S01E01 stub for series, relying on incomplete expansion paths that miss new episodes and can't detect removed content. This sprint makes the AIOStreams catalog the single source of truth — if it's in the catalog with a Videos[] array, we write all episodes; if episodes disappear from the catalog, we remove them from disk.

## Non-Goals
- Do not change movie sync logic (already correct — single file per catalog item)
- Do not add per-episode AIOStreams validation calls (catalog = truth)
- Do not remove SeriesPreExpansionService yet (still used by DiscoverService.AddToLibrary)
- Do not change the .strm URL format or signing logic

## Tasks

### FIX-222-01: Store Videos[] in catalog_items table
**Files:** Data/DatabaseManager.cs (modify), Data/Migrations/V25_VideosJson.cs (create)
**Effort:** S
**What:** Add `videos_json TEXT` column to `catalog_items` table. Stores the raw Videos[] array from AIOStreams catalog response for series items. Used for diff detection on subsequent syncs. Migration: `ALTER TABLE catalog_items ADD COLUMN videos_json TEXT;`

### FIX-222-02: Parse and store Videos[] during catalog fetch
**Files:** Services/CatalogDiscoverService.cs (modify), Services/AioStreamsClient.cs (modify if needed)
**Effort:** M
**What:** When processing a series catalog item, extract the `videos` array from the AIOStreams response and serialize to `videos_json`. Ensure `AioStreamsMeta` model includes `Videos` property (List<AioStreamsVideo> with Season, Episode, Title, Id fields). **Gotcha:** Some catalogs may omit Videos[] for series — treat as "unknown episode count" and fall back to seed-only behavior.

### FIX-222-03: Create EpisodeDiffService for Videos[] comparison
**Files:** Services/EpisodeDiffService.cs (create)
**Effort:** M
**What:** Service that compares previous `videos_json` vs current Videos[] and returns: `AddedEpisodes`, `RemovedEpisodes`, `UnchangedCount`. Signature:
```csharp
EpisodeDiff DiffEpisodes(string? previousVideosJson, List<AioStreamsVideo>? currentVideos);
```
Handle nulls gracefully: null previous = all episodes are "added"; null current = all episodes are "removed" (series dropped from catalog).

### FIX-222-04: Write all episodes during series catalog sync
**Files:** Tasks/CatalogSyncTask.cs (modify), Services/StrmWriterService.cs (modify if needed)
**Effort:** L
**What:** When a NEW series arrives in catalog with Videos[], iterate and write .strm + .nfo for each episode immediately (not just seed S01E01). Use existing `StrmWriterService.WriteEpisodeStrmAsync()` from Sprint 221. For EXISTING series, call `EpisodeDiffService` to get added/removed episodes. Write added episodes, delete removed episodes. **Gotcha:** Check if .strm already exists before writing (idempotent). Batch disk writes with 10ms delay to avoid I/O spikes.

### FIX-222-05: Handle episode removal when Videos[] shrinks
**Files:** Services/EpisodeRemovalService.cs (create)
**Effort:** M
**What:** New service to delete episode .strm + .nfo files for removed episodes. Signature:
```csharp
Task<int> RemoveEpisodesAsync(CatalogItem series, List<EpisodeKey> removedEpisodes, CancellationToken ct);
```
Derives file paths from series.StrmPath pattern. Deletes both .strm and .nfo. If season folder becomes empty after deletion, remove the folder. Returns count of files deleted. Log at Info: `"[EpisodeRemoval] {Title} — removed {N} episodes: {list}"`.

### FIX-222-06: Handle full series removal (not saved)
**Files:** Services/RemovalService.cs (modify)
**Effort:** S
**What:** When a series is removed from catalog AND not saved, delete the entire show folder (all seasons, all episodes). Use existing `RemovalService` pattern but ensure it handles the folder structure: `/Shows/{Title}/Season XX/*.strm`. **Gotcha:** Check `user_item_saves` before deletion — if ANY user has saved the series, keep all files.

### FIX-222-07: Integrate diff + write + remove into CatalogSyncTask
**Files:** Tasks/CatalogSyncTask.cs (modify)
**Effort:** L
**What:** Refactor sync loop to handle series items with full episode lifecycle:
```
For each catalog item:
  if MOVIE → existing logic (single .strm)
  if SERIES:
    1. Fetch current Videos[] from catalog response
    2. Load previous videos_json from DB
    3. Diff: added, removed, unchanged
    4. Write .strm for added episodes
    5. Delete .strm for removed episodes
    6. Update videos_json in DB
    7. Track changes for library scan
```
Only trigger library scan if any files were added or removed.

### FIX-222-08: Lightweight Emby verification pass (exception handling)
**Files:** Services/EmbyEpisodeVerifier.cs (create)
**Effort:** M
**What:** Post-sync verification that compares Emby's indexed episodes vs .strm files on disk. Only flags mismatches for manual review or repair. Does NOT call AIOStreams per-episode. Signature:
```csharp
Task<VerificationReport> VerifySeriesAsync(CatalogItem series, CancellationToken ct);
// Returns: { MatchCount, EmbyOnlyCount, DiskOnlyCount, Mismatches[] }
```
Run after main sync completes. Log summary at Info. If `EmbyOnlyCount > 0` (Emby knows episodes we don't have .strm for), these are candidates for manual investigation — likely metadata mismatch, not missing streams.

### FIX-222-09: Deprecate SeriesGapScanTask and SeriesGapRepairTask
**Files:** Tasks/SeriesGapScanTask.cs (modify), Tasks/SeriesGapRepairTask.cs (modify)
**Effort:** S
**What:** Add `[Obsolete("Superseded by catalog-first episode sync (Sprint 222)")]` to both task classes. Keep them functional for one release cycle as fallback. Remove from default schedule but keep trigger keys for manual invocation. Update TriggerService to log deprecation warning when these are manually triggered.

### FIX-222-10: Update StatusService with episode sync stats
**Files:** Services/StatusService.cs (modify)
**Effort:** S
**What:** Add to status response:
```json
"episodeSyncSummary": {
  "lastSyncAt": "2024-01-15T03:00:00Z",
  "seriesProcessed": 142,
  "episodesWritten": 37,
  "episodesRemoved": 5,
  "verificationMismatches": 0
}
```
`CatalogSyncTask` updates a static `LastEpisodeSyncResult` after each run.

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] `./emby-reset.sh` succeeds + Discover UI loads
- [ ] Manual test: Add a new series via Discover → all episodes appear (not just S01E01)
- [ ] Manual test: Modify test catalog to remove S03 from a series → S03 .strm files deleted, Emby shows S01, S02, S04+
- [ ] Manual test: Check logs for episode diff output showing added/removed counts
- [ ] Manual test: `GET /InfiniteDrive/Status` returns `episodeSyncSummary` with non-zero values

## Completion
- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated
- [ ] git commit -m "chore: end sprint 222"
