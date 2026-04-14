# Sprint 217 — Anime NFO Enrichment, Raw JSON Storage & Silent-Drop Hardening

**Status:** Draft | **Risk:** MED | **Depends:** Sprint 216 (research) | **Target:** v0.8.x

## Why (2 sentences max)
Cross-referencing the NfoMetadata decorator, 4 anime plugins (AniDB, MAL, AniList, AniSearch), and InfiniteDrive's NFO output revealed 18 missing NFO elements, a wrong episode root element, and zero anime provider IDs reaching the filesystem. Items without IMDB/TMDB get NFOs with only title+year — invisible to both Emby's native resolution AND every installed anime plugin.

## Non-Goals
- No changes to AIOStreams manifest fetching or stream resolution.
- No UI changes (Health tab metrics are backend-only).
- No new external API dependencies.
- No image fetching (poster/fanart) — separate future sprint.

## Tasks

### FIX-217-01: Add raw JSON columns (V28 migration) + debug dump query
**Files:** Data/Schema.cs (modify), Data/DatabaseManager.cs (modify)
**Effort:** S
**What:** Add `raw_meta_json TEXT` to `media_items`, `raw_json TEXT` to `sources`, `raw_json TEXT` to `collections` via V28 migration. Add `DatabaseManager.DumpItemDebugAsync(string idOrPrefix)` that joins across all three tables + `media_item_ids` + `source_memberships` and returns the complete stored payload. This gives a one-call debug harness for any item (kitsu:, anilist:, tt, UUID). Gotcha: all columns nullable — existing rows have no raw JSON.

### FIX-217-02: Write ALL provider IDs to NFO uniqueid tags
**Files:** Services/StrmWriterService.cs (modify), Services/VersionMaterializer.cs (modify), Services/SeriesPreExpansionService.cs (modify)
**Effort:** M
**What:** Extend ALL three NFO writers to include `<uniqueid type="X">` for every ID in `media_item_ids`: kitsu, anilist, mal, anidb, tvdb — not just imdb/tmdb. This is what the NfoMetadata plugin does: `<uniqueid type="provider">value</uniqueid>` for any registered provider. Without these IDs in the NFO, Emby's anime plugins (AniDB, MAL, AniList, AniSearch) cannot match the item — they register as `IRemoteMetadataProvider` and look for their provider IDs in the NFO/Emby database. Gotcha: XML-escape all ID values. Only write tags for IDs that actually exist (nullable).

### FIX-217-03: Add rich metadata to NFO files (plot, genres, studio, rating, etc.)
**Files:** Services/StrmWriterService.cs (modify), Services/VersionMaterializer.cs (modify), Services/SeriesPreExpansionService.cs (modify)
**Effort:** L
**What:** The NfoMetadata plugin writes 20+ metadata elements per NFO. InfiniteDrive currently writes 3-4 (title, year, imdb, tmdb). For items that won't pass Emby's native metadata resolution (no IMDB/TMDB), the NFO is the ONLY metadata source — it must be rich enough for Emby to display something useful. Add from upstream provider data we already fetch: `<plot>` (CDATA-wrapped), `<outline>`, `<originaltitle>`, `<genre>` (multiple), `<studio>`, `<rating>`, `<premiered>`, `<status>` (Continuing/Ended for series), `<runtime>`. We already fetch this data from AIOStreams/Cinemeta — just not writing it to NFO. Gotcha: use CDATA for plot/outline to avoid XML entity issues. Match NfoMetadata's element names exactly for Emby compatibility.

### FIX-217-04: Fix episode NFO root element + add series-level fields
**Files:** Services/SeriesPreExpansionService.cs (modify)
**Effort:** S
**What:** Episode NFOs currently use `<episodedata>` as root element — the NfoMetadata plugin and Emby expect `<episodedetails>` (XBMC/Kodi standard). Fix the root element. Also add `<displayorder>absolute</displayorder>` to anime series NFOs (matches Emby.Plugins.Anime behavior) and `<episodeguide>` element to all series NFOs. Without `<episodedetails>`, Emby may not parse episode metadata at all. Without `<displayorder>absolute</displayorder>`, anime episodes may display with wrong numbering.

### FIX-217-05: Extend dedup to non-IMDB IDs
**Files:** Tasks/CatalogSyncTask.cs (modify)
**Effort:** M
**What:** `DeduplicateItems()` currently keys on `"{imdbId}|{source}"` — anime items with kitsu/anilist IDs bypass dedup entirely, causing duplicates. Extend to canonical primary ID (`primary_id_type|primary_id|source`) so any shared ID type triggers dedup.

### FIX-217-06: Anime-wins-folder enforcement on dedup merge
**Files:** Tasks/CatalogSyncTask.cs (modify)
**Effort:** S
**What:** When an item appears in both an anime catalog and a regular (movie/series) catalog, the merged record must always retain `mediaType = "anime"` and route to the anime folder. After dedup, check all `source_memberships` for the winning item — if any source is an anime catalog, force `mediaType = "anime"` on the final record. This is the definitive "anime always wins" rule.

### FIX-217-07: Log and count every silent drop + harden early guards
**Files:** Tasks/CatalogSyncTask.cs (modify), Services/StatusService.cs (modify)
**Effort:** M
**What:** Add structured logging at every drop point in CatalogSyncTask with item title, IDs, catalog name, and drop reason. Add an in-memory drop counter (keyed by reason) surfaced via `/InfiniteDrive/Status`. Harden the anime override as a top-of-function guard: `if (catalog.Type == "anime") mediaType = "anime"` before the type switch — covers edge cases where `meta` is null or incomplete after source merging. Currently 41 drop paths have zero or generic logging.

### FIX-217-08: Preserve raw JSON at sync time + dropped-item stubs
**Files:** Tasks/CatalogSyncTask.cs (modify), Data/DatabaseManager.cs (modify)
**Effort:** M
**What:** When `MapMetaToItem` succeeds, store the original AIOStreams meta JSON into `raw_meta_json` column (from FIX-217-01). When `MapMetaToItem` returns null (dropped item), still write a minimal stub row with `status='dropped'`, `failure_reason=<why>`, and `raw_meta_json=<original>` so dropped items are queryable for debugging. Also store raw JSON for sources and catalogs at fetch time. Currently all 41 drop paths lose the original provider data entirely.

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] `./emby-reset.sh` succeeds + Discover UI loads
- [ ] V28 migration runs clean on fresh DB
- [ ] Sync an anime catalog → check .nfo → confirm `<uniqueid type="kitsu">` present
- [ ] Sync an anime catalog → check .nfo → confirm `<plot>`, `<genre>`, `<studio>` present
- [ ] Sync an anime catalog → check episode .nfo → root element is `<episodedetails>` not `<episodedata>`
- [ ] Sync an anime series → check series .nfo → confirm `<displayorder>absolute</displayorder>`
- [ ] Sync same item from anime + regular catalog → single entry, anime folder wins
- [ ] Check logs during sync → structured drop messages with item title + reason
- [ ] `/InfiniteDrive/Status` response includes drop counters
- [ ] Query `media_items` → confirm `raw_meta_json` column populated for synced items

## Completion
- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated
- [ ] git commit -m "chore: end sprint 217"
