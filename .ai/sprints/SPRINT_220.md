# Sprint 220 — Emby TV API: Canonical Series Structure + Gap Detection

**Status:** Draft | **Risk:** MED | **Depends:** Sprint 218 | **Target:** v0.53

## Why (2 sentences max)
`SeriesPreExpansionService` derives episode structure entirely from AIOStreams/Stremio metadata, which is often incomplete, missing episode counts, or absent for anime — meaning gaps in a season silently vanish rather than appearing as placeholders. Emby's own TV REST endpoints (`GET /Shows/{Id}/Seasons`, `GET /Shows/{Id}/Episodes`, `GET /Shows/Missing`) give us a canonical, Emby-authoritative episode map that we can use to validate our `.strm` coverage, detect missing episodes, and trigger targeted repairs without ever touching the media files themselves.

Key findings:
- `SeriesPreExpansionService` currently sources episode data from **Stremio/AIOStreams metadata** — it has no Emby TV API awareness at all.
- `StrmWriterService` writes only a **seed S01E01** stub for series — full expansion is the job of `SeriesPreExpansionService`.
- The Emby TV endpoints (`GET /Shows/{Id}/Seasons`, `GET /Shows/{Id}/Episodes`, `GET /Shows/Missing`) can provide a **canonical season/episode map** that the plugin currently lacks.
- The `EmbyEventHandler` already uses `ILibraryManager` / Emby SDK internally, so there's an established pattern for calling back into Emby.
- `DatabaseManager` has `seasons_json` on `catalog_items` and `GetSeriesWithoutSeasonsJsonAsync()` — a perfect hook for gap detection.
- The library scan trigger pattern is well established (`ValidateMediaLibrary` / `POST /Library/Refresh`).

---

## Non-Goals
- Do not use this to download or acquire media files; gap detection only.
- Do not replace `SeriesPreExpansionService`'s AIOStreams write path — this supplements it post-write.
- Do not add any new external HTTP dependencies beyond the existing Emby loopback (`EmbyBaseUrl`).
- Do not change the `.strm` URL format, signing, or resolution logic.

---

## Tasks

### FIX-220-01: Create `EmbyTvApiClient.cs`
**Files:** `Services/EmbyTvApiClient.cs` (create)
**Effort:** M
**What:** Thin HTTP client wrapping the three Emby TV REST endpoints using `EmbyBaseUrl` + `PluginSecret` (already available from `PluginConfiguration`). Expose three async methods:

```csharp
// GET /Shows/{embySeriesId}/Seasons?userId=...&Fields=ProviderIds
Task<List<EmbySeasonInfo>> GetSeasonsAsync(string embySeriesId, CancellationToken ct);

// GET /Shows/{embySeriesId}/Episodes?SeasonId={seasonId}&Fields=ProviderIds,Path&IsMissing=false
Task<List<EmbyEpisodeInfo>> GetEpisodesAsync(string embySeriesId, string seasonId, CancellationToken ct);

// GET /Shows/Missing?ParentId={embySeriesId}&Fields=ProviderIds&IsUnaired=false
Task<List<EmbyEpisodeInfo>> GetMissingEpisodesAsync(string embySeriesId, CancellationToken ct);
```

Use lightweight record types `EmbySeasonInfo` and `EmbyEpisodeInfo` (only the fields we care about: `Id`, `IndexNumber`, `ParentIndexNumber`, `IndexNumberEnd`, `Name`, `ProviderIds`, `LocationType`). Auth via `X-Emby-Token: {PluginSecret}` header. Add a 5-second timeout and null-safe JSON deserialization. Log at `Debug` level for each call; log at `Warn` on non-2xx. **Gotcha:** `emby_item_id` on `catalog_items` may be null for newly-synced items — callers must guard against this and skip gracefully.

---

### FIX-220-02: Add `GetSeriesWithEmbyIdAsync()` to `DatabaseManager`
**Files:** `Data/DatabaseManager.cs` (modify)
**Effort:** S
**What:** Add one query method returning all `catalog_items` rows where `media_type IN ('series','anime')` AND `emby_item_id IS NOT NULL` AND `strm_path IS NOT NULL`. This is the bounded input set for the gap scan — only items Emby has already indexed are candidates.

```csharp
Task<List<CatalogItem>> GetIndexedSeriesAsync(CancellationToken ct);
```

No schema change required. Follow the existing pattern in `GetSeriesWithoutSeasonsJsonAsync()`.

---

### FIX-220-03: Create `SeriesGapDetector.cs`
**Files:** `Services/SeriesGapDetector.cs` (create)
**Effort:** L
**What:** Core logic service. For each `CatalogItem` returned by `GetIndexedSeriesAsync()`:

1. Call `GetSeasonsAsync(item.EmbyItemId)` to get the canonical season list.
2. For each season, call `GetEpisodesAsync(item.EmbyItemId, season.Id)` to get present episodes.
3. Call `GetMissingEpisodesAsync(item.EmbyItemId)` to get the gap list.
4. Build a `SeriesGapReport` per item:

```csharp
record SeasonCoverage(int SeasonNumber, int PresentCount, int MissingCount, List<int> MissingEpisodeNumbers);
record SeriesGapReport(string ImdbId, string Title, List<SeasonCoverage> Seasons, bool IsComplete);
```

5. Log a summary per series: `"[GapDetector] {Title}: S{n} — {present}/{total} episodes present, gaps: {gaps}"`.
6. Persist the gap report into `catalog_items.seasons_json` (serialized) via the existing `UpdateSeasonsJsonAsync()` — **overwrite only if Emby data is richer than what's stored** (compare total episode count). This enriches the `seasons_json` column that `SeriesPreExpansionService` already reads.

**Gotcha:** Use `IsUnaired=false` on `GetMissingEpisodesAsync` — never treat future unaired episodes as actionable gaps. Rate-limit with a 200ms delay between series to avoid hammering the Emby loopback.

---

### FIX-220-04: Create `Tasks/SeriesGapScanTask.cs`
**Files:** `Tasks/SeriesGapScanTask.cs` (create), `Plugin.cs` (modify — register task)
**Effort:** M
**What:** Scheduled `IScheduledTask` that runs `SeriesGapDetector` for all indexed series. Schedule: every 12 hours (align with `EpisodeExpandTask`'s 4h schedule so it runs after expansion settles). Expose as trigger key `series_gap_scan` via the existing `TriggerService` map. Progress reporting via `IProgress<double>` (per-series increment). Task name: `"InfiniteDrive: Series Gap Scan"`. Category: `"InfiniteDrive"`.

Register in `Plugin.cs` the same way `EpisodeExpandTask` and `FileResurrectionTask` are registered. **Gotcha:** Guard against concurrent runs with the same `SingleFlight`/lock pattern used in `RehydrationService`.

---

### FIX-220-05: Expose gap summary on `GET /EmbyStreams/Status`
**Files:** `Services/StatusService.cs` (modify)
**Effort:** S
**What:** Add a `SeriesGapSummary` section to the existing Status JSON response:

```json
"seriesGapSummary": {
  "totalSeriesScanned": 42,
  "completeSeriesCount": 38,
  "seriesWithGaps": 4,
  "totalMissingEpisodes": 17,
  "lastScanAt": "2026-05-01T03:00:00Z"
}
```

Read from a small in-memory snapshot that `SeriesGapDetector` updates after each scan run (a static `LastScanResult` on the detector, or a lightweight singleton). No new DB table needed. **Gotcha:** The Status endpoint is admin-only — no user-facing surface change required.

---

### FIX-220-06: Wire `SeriesGapDetector` as post-expansion hook in `SeriesPreExpansionService`
**Files:** `Services/SeriesPreExpansionService.cs` (modify)
**Effort:** S
**What:** After `ExpandSeriesFromMetadataAsync` successfully writes all episode `.strm` files and the library scan fires, enqueue a **deferred** (fire-and-forget, 30s delay) call to `SeriesGapDetector.ScanSeriesAsync(item)` for the just-expanded item only. This ensures the gap detector runs with fresh Emby data after the scan settles. Use `Task.Run` + `Task.Delay(30_000)` with a `CancellationToken.None` guard — same pattern used in `DiscoverService`'s post-add scan. **Gotcha:** Do not await this; the expansion call must return promptly. Log `"[PreExpansion] Scheduled gap scan for {Title} in 30s"`.

---

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] `./emby-reset.sh` succeeds + Discover UI loads
- [ ] Manual test: Trigger `series_gap_scan` via `POST /EmbyStreams/Trigger?task=series_gap_scan`; confirm logs show per-series gap output and `seasons_json` is updated in SQLite for at least one series with a known gap.
- [ ] Manual test: `GET /EmbyStreams/Status` returns `seriesGapSummary` block with non-null `lastScanAt`.
- [ ] Manual test: Expand a multi-season series via `SeriesPreExpansionService`; confirm a gap scan log entry appears ~30 seconds later.
- [ ] Edge case: Series with `emby_item_id = NULL` is silently skipped (no exception, debug log only).
- [ ] Edge case: Emby loopback returns 401 or 503 → `EmbyTvApiClient` logs `Warn`, returns empty list, scan continues without crashing.

## Completion
- [ ] All tasks done
- [ ] `BACKLOG.md` updated
- [ ] `REPO_MAP.md` updated (add `Services/EmbyTvApiClient.cs`, `Services/SeriesGapDetector.cs`, `Tasks/SeriesGapScanTask.cs`)
- [ ] `git commit -m "chore: end sprint 220"`
