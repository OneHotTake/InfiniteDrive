# Sprint 218 — Implement robust series/episode pre-expansion for TV & anime

**Status:** Draft | **Risk:** MED | **Depends:** none | **Target:** v0.41

## Why (2 sentences max)
After catalog sync, only one `.strm` file appears per season for `type=series` (TV shows and anime). This breaks Emby’s native season/episode scanner because the `SeriesPreExpansionService` (the intended harness) is not yet producing individual episode-level catalog items with proper Emby naming (`Show/Season 01/Show - S01E01.strm` + matching NFOs).

## Non-Goals (if any)
- Do not change AIOStreams manifest fetching or real-time stream resolution.
- Do not add new external dependencies.

## Tasks
### FIX-218-01: Make SeriesPreExpansionService fully functional
**Files:** Services/SeriesPreExpansionService.cs (modify/create if stubbed)  
**Effort:** L  
**What:** Implement or complete `PreExpandSeriesAsync` / `ExpandSeasonToEpisodes` to iterate over season data from AIOStreams catalog items, synthesize per-episode `CatalogItem`s (including episode number, title if available, season number), preserve IDs (tmdb/tvdb/kitsu/imdb), set correct `mediaType` (especially "anime"), and output a flat list of episode items. Handle cases where manifest gives only season-level stream URL (synthesize episode URLs or reuse season URL with episode param if supported). Add robust null/empty checks and logging.

### FIX-218-02: Wire pre-expansion into the main pipeline
**Files:** Tasks/CatalogSyncTask.cs, Services/ItemPipelineService.cs (if exists)  
**Effort:** M  
**What:** Ensure `SeriesPreExpansionService` is injected and called for every item where `type == "series"` or `mediaType` is "tv" / "anime" *before* items reach `StrmWriterService`. Skip or pass-through movies and already-expanded items. Add clear debug logging for "Expanding season X with Y episodes".

### FIX-218-03: Update StrmWriterService for proper Emby series naming
**Files:** Services/StrmWriterService.cs  
**Effort:** M  
**What:** Enhance filename/NFO generation logic so expanded episode items produce correct folder structure and filenames: `Show Name/Season {season:00}/Show Name - S{season:00}E{episode:00}.strm` (and matching `.nfo` with provider IDs). Support anime naming conventions (e.g. absolute episode numbering fallback). Ensure NFO includes episode title, season/episode numbers, and ID hints that Emby scanner recognizes.

### FIX-218-04: Improve anime & multi-season detection
**Files:** Services/AnimeDetector.cs (if exists), Services/SeriesPreExpansionService.cs  
**Effort:** S  
**What:** Leverage recent anime `mediaType` + Kitsu ID fixes. Ensure expansion correctly detects and handles anime series (absolute ordering option, special episode handling). Add fallback when episode count is missing in manifest.

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] `./emby-reset.sh` succeeds + Discover UI loads
- [ ] Manual test: Sync a known multi-episode TV show and anime (e.g. 2+ seasons); verify full folder structure with one `.strm` + `.nfo` per episode, seasons appear correctly in Emby with proper episode count and playback.
- [ ] Manual test: Check logs for "Expanding season" messages and confirm no duplicate or missing episodes.
- [ ] Edge case: Single-season show and season with missing episode list still produces usable files.

## Completion
- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated
- [ ] git commit -m "chore: end sprint 218"
