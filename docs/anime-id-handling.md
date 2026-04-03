# Anime ID Handling — Codebase Audit (Sprint 61)

## Audit Scope

AIOMetadata (and anime-sourced catalog entries from Kitsu/AniList/MAL) may surface items with non-IMDB IDs:
- `kitsu:12345`
- `anilist:67890`
- `mal:456`

AIOMetadata performs cross-ID mapping, so many anime items will have an IMDB equivalent — but not all anime have IMDB entries. This document records what the code does when it encounters a non-tt ID.

## Findings

### 1. Database Schema — Assumes tt-prefixed IDs

**All database columns are named `imdb_id`** and the schema uses `imdb_id` as a primary/upsert key:

- `catalog_items.imdb_id` — UNIQUE constraint with `source`
- `resolution_cache.imdb_id` — UNIQUE constraint with `season`, `episode`
- `stream_candidates.imdb_id` — part of composite key with `season`, `episode`
- `discover_catalog.imdb_id` — used for deduplication
- `playback_log.imdb_id` — logged for analytics

**Risk:** A non-tt ID like `kitsu:12345` will be stored as-is in these columns. No data corruption occurs at the database level (SQLite stores arbitrary TEXT). However, any code that parses the `tt` prefix to validate or extract a numeric ID will fail.

### 2. .strm File Naming — Broken for Non-tt IDs

**`CatalogSyncTask.BuildFolderName()`** generates:
```
{Title} ({Year}) [imdbid-{imdbId}]
```

For `kitsu:12345`, this produces:
```
Attack on Titan (2013) [imdbid-kitsu:12345]
```

The `[imdbid-kitsu:12345]` folder name is syntactically valid but **Emby's metadata scanner does not recognize non-tt IDs**. The scanner looks for `[imdbid-ttXXXXXXX]` patterns. This means:
- The folder will be created on disk
- Emby will scan it
- Emby will NOT match it to any metadata provider (no AniDB, no TVDB, no IMDB)
- Result: orphan folder with no metadata, no poster art

**Severity: HIGH** — silent user-facing breakage.

### 3. Signed URL Generation — Functional but Semantically Wrong

**`StreamUrlSigner.GenerateSignedUrl()`** uses `imdbId` as a path parameter:
```
/EmbyStreams/Stream?id=kitsu:12345&type=series&...
```

The URL is syntactically valid and will be processed by the handler. However:
- The `id` parameter value contains a colon, which may cause issues with URL parsing in some Emby clients
- The HMAC signature covers the full ID string, so validation still works
- The handler's `PlaybackService.Get()` will attempt to query AIOStreams with `kitsu:12345` as the stream ID — AIOStreams accepts non-tt IDs for anime content, so resolution may actually work

**Severity: LOW** — functional but fragile.

### 4. CatalogDiscoverService.NormalizeMediaType()

**Line 222:**
```csharp
"anime" => "series",
```

This silently maps all `type: "anime"` items to `"series"`. This is wrong because:
- Anime films should be `"movie"`, not `"series"`
- Without a dedicated anime routing path, anime content falls through to the generic series handling
- The `type` information that distinguishes anime from regular series is lost

**Severity: MEDIUM** — addressed by Sprint 64.

### 5. Emby Scanner Expectations

Emby's native scanner for Series libraries expects:
- Folder names matching the series title (or containing `[imdbid-ttXXX]`)
- Episode files named `S01E01.ext` or `S01E01.strm`

For anime with non-tt IDs:
- AniDB-named folders work with `Emby.Plugins.Anime` (which uses AniDB-sourced titles)
- Without the anime plugin, Emby falls back to TMDB/TVDB matching by folder name alone
- A folder named `Shingeki no Kyojin (2013) [imdbid-kitsu:12345]` will get no metadata match

### 6. Code Paths That Assume tt Format

The following code paths make assumptions about `tt`-prefixed IDs:

| File | Line | Assumption |
|------|------|------------|
| `CatalogSyncTask.cs` | 1627 | `BuildFolderName` prefixes with `[imdbid-` regardless of ID format |
| `EpisodeExpandTask.cs` | ~similar | Uses same `BuildFolderName` |
| `DiscoverService.cs` | 677-678 | Routes to SyncPathMovies/SyncPathShows based on media type |
| `SignedStreamService.cs` | 93 | Uses `id` parameter as-is for stream lookup |
| `PlaybackService.cs` | 342 | Uses `imdb` for AIOStreams stream queries |
| `EmbyEventHandler.cs` | 160 | Parses `imdb` from .strm URL |

## Summary

| Area | Status | Action Required |
|------|--------|----------------|
| Database storage | Safe | No change needed — TEXT columns accept any ID format |
| .strm folder naming | **BROKEN** | Must detect non-tt IDs and either skip or use alternative naming |
| Signed URLs | Functional | URL-encoding handles colons; no breakage |
| AIOStreams resolution | Likely works | AIOStreams accepts non-tt IDs for anime |
| Emby metadata matching | **BROKEN** | Non-tt IDs won't match any metadata provider without anime plugin |
| Type routing | Partially wrong | `"anime" => "series"` is incorrect for films |

## Recommended Fix (Sprint 64 follow-on)

1. **Graceful degradation:** When a non-tt ID is encountered during .strm generation, skip the item and log a structured warning. Do not write a broken .strm file.
2. **ID cross-reference:** For items with a non-tt primary ID, attempt to extract an IMDB ID from AIOMetadata's cross-ID mapping (if available in the catalog response).
3. **Configurable behavior:** Add a config option for how to handle non-tt IDs: `skip`, `attempt_crossref`, or `write_anyway`.
