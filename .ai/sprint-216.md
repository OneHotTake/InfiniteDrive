# Sprint 216 — Anime Catalog Routing Fix

**Status:** Active | **Risk:** MED | **Depends:** Sprint 215 | **Target:** v0.22.1

## Why (2 sentences max)
81% of anime items are silently dropped because they use `kitsu:XXXXX` IDs without IMDB cross-refs.
The remaining 19% land in shows/movies dirs because `meta.Type` is `"series"`/`"movie"`, never `"anime"`.

## Non-Goals
- Changing stream resolution (kitsu IDs work for streams already)
- Refactoring AnimeDetector (it's fine, just never reached)
- Changing the DB schema

## Tasks

### FIX-216-01: Accept non-IMDB IDs for anime catalogs
**Files:** Tasks/CatalogSyncTask.cs (modify)
**Effort:** S
**What:** In `MapMetaToItem`, when `catalog.Type == "anime"` and item has no IMDB ID,
extract the Kitsu/AniList/MAL ID from the raw `meta.Id` field (e.g. `kitsu:46474`) and
use it as the primary ID instead of returning null. Update `GenerateDeterministicId` to
hash the provider:id combo. Also pass raw meta `JsonElement` to `BuildUniqueIdsJson`.

### FIX-216-02: Route anime catalog items to anime directory
**Files:** Tasks/CatalogSyncTask.cs (modify)
**Effort:** S
**What:** When `catalog.Type == "anime"`, force `mediaType = "anime"` instead of using
`meta.Type`. This ensures items from anime catalogs always get `MediaType = "anime"` in
the DB, which `StrmWriterService` already handles correctly.

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] `./emby-reset.sh` + full sync → anime STRM files written to `/media/infinitedrive/anime/`
- [ ] Items with `kitsu:` IDs appear in DB with valid deterministic IDs
- [ ] Non-anime catalogs unaffected (movies/series still route correctly)

## Completion
- [ ] All tasks done
- [ ] REPO_MAP.md updated
- [ ] git commit -m "fix: anime catalog routing — accept kitsu IDs, force anime mediaType"
