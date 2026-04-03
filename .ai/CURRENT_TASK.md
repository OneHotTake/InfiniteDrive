---
status: in_progress
task: Sprint 101A — NFO Correctness & Plugin Compatibility
next_action: Build, test, verify Sprint 101A
last_updated: 2026-04-03

## Sprint 100 — COMPLETED

Sprint 100 is complete with all phases implemented:
- Sprint 100A: Foundation Hardening (13/13 fixes)
- Sprint 100B: Anime, Series and Content Type Completeness (10/10 fixes)
- Sprint 100C: Collections, Metadata Chain and Security (3/3 fixes)

---

## Sprint 101A — NFO Correctness & Plugin Compatibility (IN PROGRESS)

Status: IN PROGRESS
Last completed fix: FIX-101A-05 (Absolute episode number storage and NFO)
Last checkpoint: All 5 fixes complete
Build status: (pending)
Test status: (pending)

### FIX-101A-01: UniqueID type attribute audit — COMPLETED
- Created Services/UniqueIdMapper.cs with centralized provider-to-NFO-type mapping
- Updated CatalogSyncTask.cs WriteUniqueIds to use UniqueIdMapper.MapProviderToNfoType()
- Supports: imdb/tmdb/anilist/kitsu/mal/anidb → Imdb/Tmdb/AniList/Kitsu/MyAnimeList/AniDB

### FIX-101A-02: AIOMetadata deserialization — COMPLETED
- Created Models/AioMetaResponse.cs with comprehensive typed model
- Added GetMetaAsyncTyped method to AioStreamsClient
- Updated MetadataFallbackTask to use typed deserialization with fallback

### FIX-101A-03: Anime subtype routing (OVA/ONA/SPECIAL) — COMPLETED
- Extended AnimeDetector.IsAnime with Tier 3 (subtype-based detection)
- Added AnimeSubtype enum (TvSeries, OVA, ONA, Special, Unknown)
- Added GetAnimeSubtype method to detect subtypes from metadata
- Updated NFO writer with <contenttype>tvshows</contenttype> and <season>0</season> for specials

### FIX-101A-04: OriginalTitle and SortTitle in all NFO paths — COMPLETED
- Updated WriteNfoFileAsync in CatalogSyncTask to write originaltitle and sorttitle
- Added BuildSortTitle helper to strip articles (The, A, An)
- Updated WriteFullNfo and WriteFullNfoTyped in MetadataFallbackTask
- Uses Titles.Romaji from AIOMetadata when available

### FIX-101A-05: Absolute episode number storage and NFO — COMPLETED
- Added absolute_episode_number column to stream_candidates table (migration V18→V19)
- Updated CurrentSchemaVersion to 19
- Added AbsoluteEpisodeNumber property to StremioVideo model
- Added displayepisodenumber element to episode NFO in SeriesPreExpansionService
- Added AnimePendingItems field to health response
