# Sprint 64 — Anime NFO Pipeline: Upstream Metadata Passthrough

## Status: **PENDING APPROVAL**

---

## Architecture Decision

EmbyStreams does NOT implement its own anime metadata resolver. It does NOT call AniList, Kitsu, AniDB, or any external anime API directly. AIOStreams, Cinemeta, and AIOMetadata already return normalized metadata for all content including anime. EmbyStreams consumes whatever metadata those upstream providers return and writes it to NFO files.

If the upstream providers cannot resolve an item, it is logged and skipped — no NFO, no STRM.

This keeps EmbyStreams a sync/formatting tool, not a metadata resolver.

---

## Core Behavioral Change

### Current behavior (remove)
- Anime items with `kitsu:` or `anilist:` IDs are detected and skipped with a log entry
- The anime plugin is listed as a HARD requirement in config/UI/docs
- `WriteNfoHintFile` writes minimal NFOs (title, year, imdb/tmdb uniqueids only)

### New behavior (implement this)

1. Anime items flow through the **same metadata pipeline** as all other content:
   `AIOStreams → Cinemeta → AIOMetadata` (in that order, first hit wins)

2. If upstream metadata is returned successfully:
   - Write `tvshow.nfo` (series) or `movie.nfo` alongside the `.strm`
   - Write `SxxExx.nfo` per episode for series
   - All NFOs written with `<lockdata>false</lockdata>`
   - Include **all** IDs the upstream provider returned in `<uniqueid>` tags (no filtering)
   - Generate the `.strm` as normal

3. If upstream metadata returns nothing or fails for an item:
   - Do NOT generate a `.strm`
   - Do NOT generate an NFO
   - Log the item to the existing skip/error tracking
   - Continue processing remaining items

4. The anime plugin is **no longer a hard requirement** anywhere

---

## Implementation Phases

### Phase 1: Refactor — Remove hard plugin requirement

Remove everything that gates anime behind the Emby Anime Plugin:

**Files to change:**
| File | Change |
|------|--------|
| `Plugin.cs` | Remove `IsAnimePluginInstalled()`, remove `_appPaths` field if unused |
| `Services/StatusService.cs` | Remove `AnimePluginStatusService`, `AnimePluginStatusRequest`, `AnimePluginStatusResponse` |
| `PluginConfiguration.cs` | Remove `AnimeLibraryId` field; keep `EnableAnimeLibrary` and `SyncPathAnime` |
| `Configuration/configurationpage.html` | Remove plugin warning banner, remove `disabled` from toggle |
| `Configuration/configurationpage.js` | Remove `checkAnimePluginStatus()`, replace with approved soft-recommendation text |
| `docs/anime-library-setup.md` | Rewrite with soft-recommendation messaging |

**Approved messaging (long form — for docs/config page):**
> Anime content is imported using metadata from your configured providers — AIOStreams, Cinemeta, or AIOMetadata (in that order) — to build a complete baseline library entry including title, artwork, episode data, and ratings. This works out of the box with no additional plugins required.
>
> If you want Emby to manage and enrich anime metadata natively (recommended for power users), install the Emby Anime Plugin. When present, Emby will use any AniList or Kitsu IDs in your NFO files to enrich the baseline automatically, while gracefully falling back to the generated metadata for any titles it cannot resolve.

**Approved messaging (short form — for inline UI hints):**
> Works out of the box. Install the Emby Anime Plugin for enhanced native metadata support.

### Phase 2: NFO writer — Full metadata from upstream

Enhance the existing `WriteNfoHintFile` to write complete NFO files:

**`CatalogSyncTask.cs` — `WriteNfoHintFile` enhancements:**
- Accept richer metadata payload (title, plot, year, genres, studio, rating, poster, backdrop, all IDs)
- Write `<lockdata>false</lockdata>`
- Only write tags that have actual values — no empty tags
- Write **all** `<uniqueid>` tags from upstream (imdb, tmdb, anilist, kitsu, etc.)

**New: Episode NFO writer** for per-episode NFOs during pre-expansion:
- `<episodedetails>` root element
- `<lockdata>false</lockdata>`
- `<title>`, `<season>`, `<episode>`, `<plot>`, `<aired>`, `<thumb>`
- Season and episode numbers from upstream — no remapping

### Phase 3: Pipeline routing — Anime through standard path

**`CatalogSyncTask.cs`:**
- `MapMetaToItem`: Remove non-tt ID skip guard. Let upstream metadata determine viability.
- `WriteStrmFileForItemAsync`: Remove anime-specific skip for non-tt IDs. Anime flows through same write path as series.
- `WriteAnimeStrmAsync`: Keep using `SyncPathAnime` as destination, but use standard metadata resolution.
- `BuildFolderName`: Keep the tt-prefix guard on `[imdbid-X]` suffix — non-tt IDs just get title+year folder names (no broken `[imdbid-kitsu:12345]`).

**`CatalogDiscoverService.cs`:**
- `NormalizeMediaType`: Keep returning `"anime"` when enabled, `null` when disabled. No change needed from current Sprint 64 implementation.

### Phase 4: Unresolved item logging + sync summary

**Unresolved items:**
- Extend existing `sync_state` mechanism or add structured log entries
- Capture: title, contentType, externalId, provider attempted, reason, timestamp
- Reasons: `NoMetadataReturned`, `ProviderTimeout`, `UnsupportedIdNamespace`

**Sync summary counters:**
- Add `animeSynced` and `animeSkipped` to existing counter block
- Output format:
  ```
  [EmbyStreams] .strm write complete — 150 written, 12 in library (skipped), 3 other skipped
  Anime items: 44 synced, 3 skipped (see log)
  ```

### Phase 5: Config cleanup

- Keep `EnableAnimeLibrary` toggle (when false, anime filtered out before pipeline)
- Keep `SyncPathAnime` (dedicated path when anime enabled)
- Remove `AnimeLibraryId` from config
- Update UI messaging per approved text

### Phase 6: Integration tests

- Anime item with `anilist:` ID resolves via AIOStreams → NFO + STRM generated
- Anime item with no upstream metadata → no files generated, logged correctly
- Non-anime items unaffected by this change

---

## NFO Output Format

### tvshow.nfo
```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<tvshow>
  <lockdata>false</lockdata>
  <title>{title}</title>
  <originaltitle>{originalTitle}</originaltitle>
  <year>{year}</year>
  <plot>{plot}</plot>
  <genre>{genre}</genre>
  <thumb aspect="poster">{posterUrl}</thumb>
  <fanart><thumb>{fanartUrl}</thumb></fanart>
  <studio>{studio}</studio>
  <rating>{rating}</rating>
  <uniqueid type="{providerName}" default="true">{providerId}</uniqueid>
</tvshow>
```

### episodedetails NFO
```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<episodedetails>
  <lockdata>false</lockdata>
  <title>{episodeTitle}</title>
  <season>{season}</season>
  <episode>{episode}</episode>
  <plot>{episodePlot}</plot>
  <aired>{airedDate}</aired>
  <thumb>{episodeThumbUrl}</thumb>
</episodedetails>
```

Only write tags that have actual values. No empty tags.

---

## Files to Change (Summary)

| File | Phase | Change |
|------|-------|--------|
| `Plugin.cs` | 1 | Remove `IsAnimePluginInstalled()`, `_appPaths` |
| `Services/StatusService.cs` | 1 | Remove `AnimePluginStatusService` + DTOs |
| `PluginConfiguration.cs` | 1,5 | Remove `AnimeLibraryId`; keep toggle + path |
| `Configuration/configurationpage.html` | 1,5 | Remove plugin banner, update messaging |
| `Configuration/configurationpage.js` | 1,5 | Remove `checkAnimePluginStatus()`, update messaging |
| `docs/anime-library-setup.md` | 1 | Rewrite with soft-recommendation |
| `Tasks/CatalogSyncTask.cs` | 2,3,4 | NFO writer enhancements, pipeline routing, counters |
| `Services/CatalogDiscoverService.cs` | 3 | Verify NormalizeMediaType (no change expected) |
| `Data/DatabaseManager.cs` | 4 | Extend sync_state or add unresolved item tracking |
| `CLAUDE.md` | 5 | Update anime architecture decision |

---

## Definition of Done

- [ ] `dotnet build -c Release` → 0 errors, 0 warnings
- [ ] Anime items flow through metadata pipeline identically to non-anime items
- [ ] NFO files generated for all successfully resolved anime items
- [ ] No STRM generated without a corresponding NFO for anime content
- [ ] Unresolved anime items appear in existing skip/error log with structured reason
- [ ] Sync summary includes anime synced/skipped counters
- [ ] Hard plugin requirement removed from all surfaces
- [ ] Approved soft-recommendation messaging present everywhere anime plugin was previously mentioned as required
- [ ] No new external API dependencies introduced
