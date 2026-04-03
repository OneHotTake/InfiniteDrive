---
status: completed
task: Sprint 100B — Anime, Series and Content Type Completeness
next_action: Review and test Sprint 100B implementations
last_updated: 2026-04-03

## Sprint 100A — Foundation Hardening (COMPLETED)

All 13 fixes in Sprint 100A have been implemented. See detailed summary in task history.

---

## Sprint 100B — Anime, Series and Content Type Completeness (COMPLETED)

All 10 fixes in Sprint 100B have been implemented:

**FIX-100B-01:** Anime type in catalog type switch ✅
- Updated MapMetaToItem in CatalogSyncTask.cs
- Recognizes: movie, series, anime, channel, tv
- Skips channel/tv with info log
- Skips unknown types with warning log

**FIX-100B-02:** Anime path routing (two-tier) ✅
- Created Services/AnimeDetector.cs
- AnimeDetector.IsAnime() implements Tier 1 (catalogType == "anime") and Tier 2 (has AniList/Kitsu/MAL without IMDB)
- Updated WriteSeriesStrmAsync to use two-tier detection
- SyncPathAnime defaults to SyncPathShows when not configured

**FIX-100B-03:** NFO library tag for anime ✅
- Added `<library>anime</library>` to WriteNfoFileAsync in CatalogSyncTask.cs
- Tag is added when MediaType == "anime"

**FIX-100B-04:** UniqueID type attribute correctness ✅
- Created Tests/UniqueIdTests.cs
- Unit tests for: IMDB (default), TMDB, AniList, Kitsu, MyAnimeList
- Tests uniqueid type attributes and default flag

**FIX-100B-05:** Kitsu/AniList absolute episode numbering ✅
- Added GetAnimeStreamUrl() to AioStreamsClient.cs
- Format: {stremioBase}/stream/series/{provider}:{seriesId}:{absoluteEpisode}.json
- Added CalculateAbsoluteEpisode() static method
- Uses 12 as default episode count estimate for unknown season lengths

**FIX-100B-06:** Episode stream ID format test ✅
- Created Tests/StreamUrlTests.cs
- Integration tests for movie, series, and anime stream URL construction
- Test for absolute episode number calculation

**FIX-100B-07:** GetYear with ReleaseInfo range ✅
- Created Services/YearParser.cs
- Handles: "2015", "2007-2019", "2020-", null/empty
- Parse() and ParseRange() methods
- Updated ParseYear in CatalogSyncTask.cs to use YearParser

**FIX-100B-08:** Catalog pagination ✅
- Already implemented (verified):
  - pageSize=100 (AioCatalogPageSize constant)
  - MaxCatalogItems configurable via config.CatalogItemCap (default 500)
  - skip/offset parameter used in FetchOneCatalogAsync

**FIX-100B-09:** Episode fallback count ✅
- Already implemented (verified):
  - config.DefaultSeriesEpisodesPerSeason (default 10)
  - Used in WriteDefaultEpisodesAsync in SeriesPreExpansionService.cs

**FIX-100B-10:** Unknown provider edge case ✅
- Created Services/StreamIdParser.cs
- ParseStreamId() extracts provider and validates format
- Handles: tt{imdbid}, kitsu:{id}, anilist:{id}, tmdb:{id}, mal:{id}
- Logs warning for unknown providers
- Stores as unknown_{prefix} for unknown formats

---

## New Files Created

- Services/AnimeDetector.cs - Two-tier anime detection
- Services/YearParser.cs - Year range parsing
- Services/StreamIdParser.cs - Stream ID prefix extraction and validation
- Tests/UniqueIdTests.cs - UniqueID attribute tests
- Tests/StreamUrlTests.cs - Stream URL format tests
