# Sprint 221 — Series Gap Repair: Write Missing Episode `.strm` Files

**Status:** Draft | **Risk:** MED | **Depends:** Sprint 220 | **Target:** v0.53

## Why (2 sentences max)
Sprint 220 detects missing episodes via Emby's TV API and enriches `seasons_json` with the canonical episode map, but nothing consumes that data to repair gaps — the enriched `seasons_json` sits in the DB unused while `EpisodeExpandTask` is obsolete and `SeriesPreExpansionService` only runs at initial sync. This sprint closes the loop by adding a repair service that reads the gap report from `seasons_json`, writes `.strm` + `.nfo` files for each missing episode, and triggers a targeted library scan to make them appear in Emby.

## Non-Goals
- Do not replace `SeriesPreExpansionService` — this supplements it as a post-hoc repair pass, not a parallel write path.
- Do not remove or un-obsolete `EpisodeExpandTask` — it remains deprecated; this is its proper replacement.
- Do not change the `.strm` URL format, signing, or resolution logic — use the existing `BuildSignedStrmUrl()` method.
- Do not re-run `SeriesGapDetector` from within the repair task — assume `seasons_json` is already populated (Sprint 220 handles detection).
- Do not repair movies — this is series/anime only.

---

## Tasks

### FIX-221-01: Add `GetSeriesWithGapsAsync()` query to `DatabaseManager`
**Files:** `Data/DatabaseManager.cs` (modify)
**Effort:** S
**What:** Add a query that returns series items where `seasons_json` contains at least one missing episode. The `seasons_json` column (populated by Sprint 220's `SeriesGapDetector`) stores a `List<SeasonCoverage>` where each has `MissingEpisodeNumbers`. Query:

```csharp
Task<List<CatalogItem>> GetSeriesWithGapsAsync(int limit, CancellationToken ct);
// WHERE media_type IN ('series','anime')
//   AND seasons_json IS NOT NULL
//   AND seasons_json != ''
//   AND seasons_json != '[]'
//   AND json_extract(seasons_json, '$[0].MissingEpisodeNumbers') IS NOT NULL
//   AND strm_path IS NOT NULL
//   AND removed_at IS NULL
// LIMIT {limit}
```

The `json_extract` check ensures we only return items where at least one season has gaps. **Gotcha:** SQLite's `json_extract` returns `NULL` for missing keys and empty arrays — both are fine to skip. Log at `Debug` if JSON parse fails for any row (malformed legacy data).

---

### FIX-221-02: Add `WriteEpisodeStrmAsync()` method to `StrmWriterService`
**Files:** `Services/StrmWriterService.cs` (modify)
**Effort:** M
**What:** New method that writes a single episode `.strm` + `.nfo` file for a given series item + season + episode number:

```csharp
Task<string?> WriteEpisodeStrmAsync(
    CatalogItem seriesItem,
    int seasonNumber,
    int episodeNumber,
    string? episodeTitle,
    CancellationToken ct);
```

Implementation:
1. Derive `showDir` from `seriesItem.StrmPath` by walking up to the show root (the parent of `Season XX/`).
2. Create `Season {seasonNumber:00}/` directory if it doesn't exist.
3. Build filename: `{SanitisePath(seriesItem.Title)} S{seasonNumber:00}E{episodeNumber:00}.strm`.
4. Check if file already exists on disk → if yes, return existing path (idempotent).
5. Call `BuildSignedStrmUrl(config, seriesItem.ImdbId, "series", seasonNumber, episodeNumber)` to generate the signed resolve URL.
6. Write `.strm` file atomically (write to `.tmp`, then `File.Move` with overwrite).
7. Write episode `.nfo` via new helper `WriteEpisodeNfoFileAsync(seriesItem, seasonNumber, episodeNumber, episodeTitle, strmPath)` (see FIX-221-03).
8. Return the new `.strm` path.

**Gotcha:** The show root path derivation must handle both `/Show Name/Season 01/Show Name S01E01.strm` (standard) and `/Show Name/Season 01/filename.strm` (edge cases) — use `Path.GetDirectoryName()` twice to get show root. If `seriesItem.StrmPath` is null, log `Warn` and return null.

---

### FIX-221-03: Add `WriteEpisodeNfoFileAsync()` helper to `StrmWriterService`
**Files:** `Services/StrmWriterService.cs` (modify)
**Effort:** S
**What:** Private helper that writes an `episodedetails` NFO for a single episode:

```csharp
private void WriteEpisodeNfo(
    CatalogItem seriesItem,
    int seasonNumber,
    int episodeNumber,
    string? episodeTitle,
    string strmPath)
```

NFO format (minimal, Emby-compatible):

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<episodedetails>
  <title>{episodeTitle ?? $"Episode {episodeNumber}"}</title>
  <season>{seasonNumber}</season>
  <episode>{episodeNumber}</episode>
  <showtitle>{seriesItem.Title}</showtitle>
  <uniqueid type="imdb">{seriesItem.ImdbId}</uniqueid>
  <uniqueid type="tmdb">{seriesItem.TmdbId}</uniqueid>
  <uniqueid type="tvdb">{seriesItem.TvdbId}</uniqueid>
</episodedetails>
```

NFO path: `Path.ChangeExtension(strmPath, ".nfo")`. Only write non-null provider IDs. Skip NFO entirely if `config.WriteNfoFiles` is false. **Gotcha:** Do not include `<aired>` or `<plot>` — we don't have that data from the gap report. Emby will fetch it from its own metadata providers on scan.

---

### FIX-221-04: Create `Services/SeriesGapRepairService.cs`
**Files:** `Services/SeriesGapRepairService.cs` (create), `Plugin.cs` (modify — register singleton)
**Effort:** L
**What:** Core repair service that consumes the gap data and writes missing episode files. Expose one public method:

```csharp
Task<GapRepairResult> RepairSeriesGapsAsync(int batchLimit, CancellationToken ct);
```

Algorithm:
1. `GetSeriesWithGapsAsync(batchLimit)` from DB.
2. For each series item:
   a. Deserialize `seasons_json` to `List<SeasonCoverage>`.
   b. For each season with `MissingEpisodeNumbers.Count > 0`:
      - For each missing episode number:
        - Call `WriteEpisodeStrmAsync(item, season.SeasonNumber, episodeNumber, null, ct)`.
        - Track success/failure counts.
   c. After all episodes written for this series, update `seasons_json` to clear `MissingEpisodeNumbers` for repaired seasons (mark as complete).
3. After all series processed, trigger a single library scan via `POST /Library/Refresh` (batch, not per-item).
4. Return `GapRepairResult`:

```csharp
record GapRepairResult(
    int SeriesProcessed,
    int EpisodesWritten,
    int EpisodesSkipped,  // already existed on disk
    int EpisodesFailed,
    TimeSpan Duration);
```

Log summary at `Info`: `"[GapRepair] Processed {N} series, wrote {M} episodes, skipped {K}, failed {F} in {T}ms"`.

**Gotcha:** Use a 50ms delay between episode writes to avoid disk I/O spikes. Cap at 500 episodes per run to bound execution time. If `seriesItem.ImdbId` is null, skip the entire series (no way to generate resolve URL).

---

### FIX-221-05: Create `Tasks/SeriesGapRepairTask.cs`
**Files:** `Tasks/SeriesGapRepairTask.cs` (create), `Plugin.cs` (modify — register task)
**Effort:** M
**What:** Scheduled `IScheduledTask` that runs `SeriesGapRepairService.RepairSeriesGapsAsync()`. Schedule: every 6 hours (offset from `SeriesGapScanTask`'s 12h schedule so detection and repair alternate). Trigger key: `series_gap_repair` via `TriggerService`.

Task name: `"InfiniteDrive: Series Gap Repair"`. Category: `"InfiniteDrive"`.

Progress reporting: increment per-series (not per-episode) for smoother UI updates.

**Gotcha:** Guard with `SingleFlight` pattern — do not allow concurrent repair runs. If `SeriesGapScanTask` is currently running, defer (check via shared semaphore or simple static bool). Log at `Info` on start/complete with result summary.

---

### FIX-221-06: Wire gap repair as optional post-detection hook in `SeriesGapDetector`
**Files:** `Services/SeriesGapDetector.cs` (modify)
**Effort:** S
**What:** Add an optional `autoRepair` parameter to `ScanSeriesAsync()`:

```csharp
Task<SeriesGapReport> ScanSeriesAsync(CatalogItem item, bool autoRepair = false, CancellationToken ct = default);
```

If `autoRepair == true` and the scan finds gaps, immediately call `SeriesGapRepairService.RepairSingleSeriesAsync(item, ct)` (new method — FIX-221-07) before returning.

This allows the deferred post-expansion hook (from Sprint 220's FIX-220-06) to optionally trigger immediate repair for a single series, rather than waiting for the scheduled batch task.

Default `autoRepair = false` to preserve existing behavior for the scheduled scan task.

---

### FIX-221-07: Add `RepairSingleSeriesAsync()` to `SeriesGapRepairService`
**Files:** `Services/SeriesGapRepairService.cs` (modify)
**Effort:** S
**What:** Convenience method for repairing a single series (used by the post-expansion hook):

```csharp
Task<int> RepairSingleSeriesAsync(CatalogItem item, CancellationToken ct);
// Returns: number of episodes written
```

Implementation: same logic as the batch repair loop, but for one item only. Does NOT trigger library scan (caller is responsible — the post-expansion hook already fires a scan 30s later). Log at `Debug`: `"[GapRepair] Repaired {N} episodes for {Title}"`.

---

### FIX-221-08: Update `GET /InfiniteDrive/Status` with repair stats
**Files:** `Services/StatusService.cs` (modify)
**Effort:** S
**What:** Extend the `seriesGapSummary` section (added in Sprint 220) with repair stats:

```json
"seriesGapSummary": {
  "totalSeriesScanned": 42,
  "completeSeriesCount": 38,
  "seriesWithGaps": 4,
  "totalMissingEpisodes": 17,
  "lastScanAt": "2026-05-01T03:00:00Z",
  "lastRepairAt": "2026-05-01T09:00:00Z",
  "episodesRepairedLastRun": 12,
  "episodesRepairedTotal": 156
}
```

`SeriesGapRepairService` updates a static `LastRepairResult` after each run; `StatusService` reads it. `episodesRepairedTotal` is a simple counter incremented on each successful write (not persisted across plugin restarts — acceptable).

---

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] `./emby-reset.sh` succeeds + Discover UI loads
- [ ] Manual test: Add a series with known missing episodes (e.g., only S01E01 exists); run `POST /InfiniteDrive/Trigger?task=series_gap_scan`; confirm `seasons_json` shows gaps; run `POST /InfiniteDrive/Trigger?task=series_gap_repair`; confirm new `.strm` + `.nfo` files appear on disk in the correct `Season XX/` folder.
- [ ] Manual test: After repair, Emby library scan picks up new episodes — verify they appear in the Emby season view with correct SxxExx numbering.
- [ ] Manual test: `GET /InfiniteDrive/Status` shows `lastRepairAt` and `episodesRepairedLastRun` populated.
- [ ] Manual test: Run repair twice on the same series — second run should report 0 episodes written (idempotent; files already exist).
- [ ] Edge case: Series with `imdb_id = NULL` is skipped gracefully (log at `Warn`, no crash).
- [ ] Edge case: Series with `strm_path = NULL` is skipped gracefully (log at `Warn`, no crash).
- [ ] Edge case: Malformed `seasons_json` (invalid JSON) is skipped gracefully (log at `Warn`, continue to next series).
- [ ] Concurrency: Trigger `series_gap_repair` twice simultaneously — second invocation logs `"Repair already in progress, skipping"` and exits.

## Completion
- [ ] All tasks done
- [ ] `BACKLOG.md` updated
- [ ] `REPO_MAP.md` updated (add `Services/SeriesGapRepairService.cs`, `Tasks/SeriesGapRepairTask.cs`; note modifications to `StrmWriterService`, `SeriesGapDetector`, `StatusService`, `DatabaseManager`)
- [ ] `git commit -m "chore: end sprint 221"`
