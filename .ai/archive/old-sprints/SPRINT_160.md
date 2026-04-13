# Sprint 160 — Robust ID Normalization + Correct NFO/Directory Decoration

**Version:** v3.3 | **Status:** Under Review — not yet approved | **Risk:** MEDIUM | **Depends:** Sprint 156

---

## Overview

Two tightly coupled problems fixed together:

1. **ID normalization at ingest** — when a catalog item arrives without
   a `tt`-style IMDb ID, attempt to resolve one via the source addon's
   own `/meta` endpoint, then AIOMetadata as fallback. Always store
   whatever IDs we have; never drop an item because we couldn't resolve.

2. **Correct file decoration** — `StrmWriterService` currently has
   multiple bugs that prevent Emby from auto-identifying items via
   directory name hints and NFO files. Fix all of them so Emby does its
   own job without us doing it for it.

### What the Research Found

**Catalog ID reality** (confirmed against live AIOStreams manifest):
- The same catalog mixes `tt`-prefixed and `tmdb_`-prefixed IDs in the
  same response (e.g. Batman Animations: `tt0075543` alongside
  `tmdb_260192`). No single format is guaranteed.
- ID separator is `tmdb_` (underscore), not `tmdb:` (colon) — both
  must be handled.
- The manifest declares `idPrefixes` but actual catalog items don't
  always follow them.

**Nuvio's approach** (confirmed via source): pass the ID as-is to
`/meta/{type}/{id}.json` on the **same addon** that served the catalog.
The addon handles its own IDs. No cross-service translation at browse
time. We adopt the same pattern — call the source addon's meta endpoint
first, not Cinemeta.

**Emby scanner hints** (confirmed via official docs):
- Directory: `[imdbid-tt...]`, `[tmdbid-12345]`, `[tvdbid-95491]`
  — both `-` and `=` separators work; both `[]` and `{}` work
- NFO: Emby-native tags (`<imdbid>`, `<tmdbid>`, `<tvdbid>`) are more
  reliable than Kodi `<uniqueid>` — write both
- `tvshow.nfo` must be in the **show root directory**, not Season 01/
- Root element must be `<tvshow>` for series/anime, `<movie>` for movies

**Cinemeta** cannot resolve `tmdb_` IDs — confirmed 404 for
`/meta/movie/tmdb_260192.json`. It is a metadata lookup service, not
an ID resolver. Do not use it in the resolution chain.

### Non-Goals

- ❌ Rename `imdb_id` column (80+ queries touch it; defer indefinitely)
- ❌ Retroactive cleanup of existing rows with bad IDs
- ❌ TMDB API key management (proxy through AIOStreams/AIOMetadata)
- ❌ Title+year fuzzy cross-catalog dedup
- ❌ AniDB plugin integration beyond writing `<anidbid>` to NFO

---

## Phase 160A — Database Schema

### FIX-160A-01: Add three columns via migration

**File:** `Data/DatabaseManager.cs` (modify — schema + migration block)

**What:**

Add a new schema version (V24 → V25) with three new columns:

```sql
-- tvdb_id: explicit slot for series Emby scanner hint ([tvdbid-xxx])
-- and NFO <tvdbid> tag. Separate from unique_ids_json for direct access.
ALTER TABLE catalog_items ADD COLUMN tvdb_id TEXT;

-- raw_meta_json: verbatim JSON response from the source addon's
-- /meta/{type}/{id}.json call. Null if call was skipped or failed.
-- Purpose: debugging ID resolution failures without re-fetching.
ALTER TABLE catalog_items ADD COLUMN raw_meta_json TEXT;

-- catalog_type: persisted media type from the source manifest
-- ('movie', 'series', 'anime'). Previously only carried in-memory
-- on CatalogItem.CatalogType (not persisted, lost after sync).
ALTER TABLE catalog_items ADD COLUMN catalog_type TEXT;
```

Use `ColumnExists()` guard on each (same pattern as all prior migrations).

---

### FIX-160A-02: Update CatalogItem model

**File:** `Models/CatalogItem.cs` (modify)

**What:**

1. Add `public string? TvdbId { get; set; }` with doc comment.
2. Add `public string? RawMetaJson { get; set; }` with doc comment.
3. Change `CatalogType` from `// Not persisted` to persisted (remove
   the caveat comment; it is now a real column).
4. Update doc comment on `ImdbId` to clarify it is the canonical
   primary ID — may be a `tt` ID when resolved, or a native provider
   ID (e.g. `tmdb_260192`) when resolution failed. It is NOT
   guaranteed to be an IMDb ID despite the column name.

---

### FIX-160A-03: Update UpsertCatalogItemAsync and SELECT queries

**File:** `Data/DatabaseManager.cs` (modify)

**What:**

1. Add `tvdb_id`, `raw_meta_json`, `catalog_type` to the INSERT column
   list and `@tvdb_id`, `@raw_meta_json`, `@catalog_type` bindings.
2. Add to ON CONFLICT DO UPDATE SET — same COALESCE pattern as existing
   columns (`raw_meta_json` overwrites; others use COALESCE to preserve
   first-writer value for tvdb_id/catalog_type).
3. Add to every SELECT that reads a full `CatalogItem` row (grep for
   `SELECT id, imdb_id, tmdb_id` — each needs the three new columns).
4. Add to the `ReadItem()` helper that maps rows to `CatalogItem`.

---

## Phase 160B — IdResolverService

### FIX-160B-01: Create IdResolverService

**File:** `Services/IdResolverService.cs` (create)

**What:**

```csharp
/// <summary>
/// Attempts to extract structured provider IDs from a raw manifest item ID
/// and enrich them by calling the source addon's /meta endpoint.
///
/// tt-style IMDb IDs are preferred as the canonical key because both
/// Emby's metadata engine and Cinemeta use them natively. However,
/// tmdb and tvdb IDs also give Emby enough to auto-identify items.
/// Items are NEVER dropped because ID resolution fails.
/// </summary>
public sealed class IdResolverService
```

**Dependencies:** `AioMetadataClient`, `ILogManager`, `IHttpClient`

**Public record:**
```csharp
public sealed record ResolvedIds(
    string CanonicalId,      // best available: tt > tmdb_ > tvdb_ > native
    string? ImdbId,          // tt-prefixed only, null if not resolved
    string? TmdbId,          // numeric string, null if not resolved
    string? TvdbId,          // numeric string, null if not resolved
    string? AniDbId,         // numeric string, null if not resolved
    string? RawMetaJson);    // verbatim /meta response, null if skipped
```

**Public method:**
```csharp
Task<ResolvedIds> ResolveAsync(
    string manifestId,       // raw ID from catalog: "tmdb_260192", "tt0075543", etc.
    string addonBaseUrl,     // source addon base URL (strip /manifest.json)
    string mediaType,        // "movie", "series", "anime"
    CancellationToken ct);
```

**Implementation — ID parsing (synchronous, before any network calls):**

```
Parse manifestId:
  starts with "tt"              → ImdbId = manifestId
  starts with "tmdb_" or "tmdb:"→ TmdbId = numeric part
  starts with "tvdb_" or "tvdb:"→ TvdbId = numeric part
  starts with "kitsu:" or "kitsu_" → store in AniDbId slot (cross-ref later)
  starts with "mal:" or "mal_" → note for AIOMetadata lookup
  starts with "imdb:"          → ImdbId = value after prefix
  anything else                → treat as opaque, use as CanonicalId only
```

**Implementation — resolution chain (network):**

```
Step 1: Call {addonBaseUrl}/meta/{mediaType}/{manifestId}.json
  - 1.5s timeout
  - On success: parse response for imdb_id, moviedb_id/tmdb_id, tvdb_id
  - Store verbatim response as RawMetaJson regardless of parse success
  - If imdb_id (tt-prefixed) found → done, return early

Step 2: If still no tt and have tmdb/kitsu/mal ID → call AIOMetadata
  - Use existing AioMetadataClient
  - On success: extract tt ID if returned

Step 3: Build CanonicalId from best available:
  ImdbId (tt) ?? "tmdb_" + TmdbId ?? "tvdb_" + TvdbId ?? manifestId

Step 4: Return ResolvedIds — never throw, never return null
```

Log at Debug for each step; Info when resolved to tt; Warn when
all steps fail and falling back to native ID.

---

## Phase 160C — Wire into CatalogDiscoverService

### FIX-160C-01: Replace raw ID extraction with IdResolverService

**File:** `Services/CatalogDiscoverService.cs` (modify)

**What:**

Replace the current naive ID extraction (~line 155):
```csharp
// BEFORE (naive — stores tmdb_260192 as imdb_id):
var imdbId = meta.ImdbId ?? meta.Id ?? "";

// AFTER:
var manifestId = meta.ImdbId?.StartsWith("tt") == true
    ? meta.ImdbId               // fast path: already canonical
    : (meta.Id ?? meta.ImdbId ?? "");

ResolvedIds resolved;
if (manifestId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
{
    // Fast path: no network call needed
    resolved = new ResolvedIds(manifestId, manifestId, null, null, null, null);
}
else
{
    resolved = await _idResolver.ResolveAsync(
        manifestId, addonBaseUrl, catalogDef.Type, ct);
}

var canonicalId = resolved.CanonicalId;
if (string.IsNullOrWhiteSpace(canonicalId)) continue; // truly empty — skip
```

Inject `IdResolverService` into `CatalogDiscoverService` constructor.

Populate `CatalogItem` fields:
```csharp
item.ImdbId      = resolved.CanonicalId;
item.TmdbId      = resolved.TmdbId;
item.TvdbId      = resolved.TvdbId;
item.RawMetaJson = resolved.RawMetaJson;
item.CatalogType = catalogDef.Type;  // now persisted
// Merge AniDbId into UniqueIdsJson if present
```

Also populate `UniqueIdsJson` — merge all resolved IDs into the JSON
array, preserving any existing entries for items being updated.

---

## Phase 160D — StrmWriterService Decoration Fixes

All changes are in `Services/StrmWriterService.cs`.

### FIX-160D-01: BuildFolderName — add TMDB and TVDB hints

**Current (line 165–174):** only adds `[imdbid-tt...]` hint.

**Replace with:**
```csharp
private static string BuildFolderName(
    string title, int? year, string? imdbId, string? tmdbId, string? tvdbId,
    string mediaType)
{
    var sb = new StringBuilder(title);
    if (year.HasValue) sb.Append($" ({year})");

    // Priority: tt > tvdb (series) > tmdb > nothing
    // Emby scanner reads the FIRST recognised hint in the folder name.
    if (!string.IsNullOrEmpty(imdbId) &&
        imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
    {
        sb.Append($" [imdbid-{imdbId}]");
    }
    else if (!string.IsNullOrEmpty(tvdbId) &&
             (mediaType == "series" || mediaType == "anime"))
    {
        sb.Append($" [tvdbid-{tvdbId}]");
    }
    else if (!string.IsNullOrEmpty(tmdbId))
    {
        sb.Append($" [tmdbid-{tmdbId}]");
    }
    // No hint if we have nothing — Emby will fuzzy-match by title+year

    return sb.ToString();
}
```

Update callers (lines 63 and 76) to pass `item.TmdbId`, `item.TvdbId`,
`item.MediaType`.

---

### FIX-160D-02: WriteNfoFileIfEnabled — fix all bugs

**Current bugs:**
1. `<movie>` root hardcoded for all media types
2. `tvshow.nfo` written alongside episode `.strm` in `Season 01/`, not
   show root
3. `<uniqueid type="imdb">` written even when `ImdbId` is `tmdb_260192`
4. No Emby-native tags (`<imdbid>`, `<tmdbid>`, `<tvdbid>`)
5. No `<anidbid>` for anime

**Replace with two private methods:**

```csharp
private void WriteMovieNfo(CatalogItem item, string strmPath, SourceType src)
{
    // movie.nfo sits alongside the .strm file
    var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
    WriteNfo(nfoPath, "movie", item, src);
}

private void WriteShowNfo(CatalogItem item, string showRootDir, SourceType src)
{
    // tvshow.nfo goes in the show ROOT, not Season 01/
    // Emby will not find it if placed inside a season folder.
    var nfoPath = Path.Combine(showRootDir, "tvshow.nfo");
    WriteNfo(nfoPath, "tvshow", item, src);
}

private void WriteNfo(string path, string rootElement,
    CatalogItem item, SourceType src)
{
    try
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        w.WriteLine($"<{rootElement}>");
        w.WriteLine($"  <title>{EncodeXml(item.Title)}</title>");
        if (item.Year.HasValue)
            w.WriteLine($"  <year>{item.Year.Value}</year>");

        // ── Emby-native tags (most reliable) ──────────────────────────
        // Only write <imdbid> when we actually have a tt-style ID.
        // Writing a tmdb_ value here poisons Emby's IMDb slot.
        var hasTt = !string.IsNullOrEmpty(item.ImdbId) &&
                    item.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase);
        if (hasTt)
            w.WriteLine($"  <imdbid>{EncodeXml(item.ImdbId)}</imdbid>");
        if (!string.IsNullOrEmpty(item.TmdbId))
            w.WriteLine($"  <tmdbid>{EncodeXml(item.TmdbId)}</tmdbid>");
        if (!string.IsNullOrEmpty(item.TvdbId))
            w.WriteLine($"  <tvdbid>{EncodeXml(item.TvdbId)}</tvdbid>");

        // AniDB: write if present in UniqueIdsJson (AniDB plugin reads this)
        var aniDbId = ExtractProviderIdFromJson(item.UniqueIdsJson, "anidb");
        if (!string.IsNullOrEmpty(aniDbId))
            w.WriteLine($"  <anidbid>{EncodeXml(aniDbId)}</anidbid>");

        // ── Kodi-style uniqueid (backwards compat) ─────────────────────
        if (hasTt)
            w.WriteLine($"  <uniqueid type=\"imdb\" default=\"true\">" +
                        $"{EncodeXml(item.ImdbId)}</uniqueid>");
        if (!string.IsNullOrEmpty(item.TmdbId))
            w.WriteLine($"  <uniqueid type=\"tmdb\"" +
                        $"{(hasTt ? "" : " default=\"true\"")}>" +
                        $"{EncodeXml(item.TmdbId)}</uniqueid>");
        if (!string.IsNullOrEmpty(item.TvdbId))
            w.WriteLine($"  <uniqueid type=\"tvdb\">{EncodeXml(item.TvdbId)}</uniqueid>");

        w.WriteLine($"  <source>{src.ToDisplayString()}</source>");
        w.WriteLine($"</{rootElement}>");
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex,
            "[EmbyStreams] StrmWriterService: failed to write NFO for {Id}", item.ImdbId);
    }
}

/// <summary>
/// Extracts a provider ID from UniqueIdsJson by provider name.
/// Format: [{"provider":"anidb","id":"1234"}, ...]
/// Returns null if not found or JSON is malformed.
/// </summary>
private static string? ExtractProviderIdFromJson(string? json, string provider)
{
    if (string.IsNullOrEmpty(json)) return null;
    // Simple scan — avoid JsonDocument dependency for a 2-field object
    var key = $"\"provider\":\"{provider}\"";
    var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
    if (idx < 0) return null;
    var idIdx = json.IndexOf("\"id\":\"", idx, StringComparison.Ordinal);
    if (idIdx < 0) return null;
    idIdx += 6;
    var end = json.IndexOf('"', idIdx);
    return end < 0 ? null : json.Substring(idIdx, end - idIdx);
}
```

Update `WriteAsync` to call the correct method:
- Movie path → `WriteMovieNfo(item, path, originSourceType)`
- Series/anime path → `WriteShowNfo(item, showDir, originSourceType)`
  (pass `showDir`, not `strmPath`, so NFO lands in root not Season 01)

---

## Phase 160E — Registration

### FIX-160E-01: Register IdResolverService

**File:** `Plugin.cs` (modify)

Register `IdResolverService` as singleton. Single line alongside existing
service registrations.

---

## Phase 160F — Build & Verification

### FIX-160F-01: Build

`dotnet build -c Release` — 0 errors, 0 net-new warnings.

---

### FIX-160F-02: Grep checklist

| Pattern | Expected |
|---|---|
| `meta.ImdbId ?? meta.Id` | 0 — old naive extraction gone |
| `uniqueid type="imdb".*tmdb` | 0 — no TMDB value in IMDb slot |
| `<movie>` in NFO write for series | 0 |
| `tvshow.nfo` written to Season | 0 |
| `tvdb_id` in INSERT statement | ≥ 1 |
| `raw_meta_json` in INSERT statement | ≥ 1 |
| `catalog_type` in INSERT statement | ≥ 1 |
| `IdResolverService` | ≥ 3 (create, inject, Plugin.cs) |
| `BuildFolderName.*tvdbId` | ≥ 1 |
| `BuildFolderName.*tmdbId` | ≥ 1 |

---

### FIX-160F-03: Manual test — mixed-ID catalog

1. Sync the Batman Animations catalog (contains both `tt...` and `tmdb_...` items).
2. Assert: `tt`-prefixed items stored with `imdb_id = tt...`, folder has `[imdbid-tt...]`.
3. Assert: `tmdb_`-prefixed items stored with canonical `imdb_id` = resolved tt if
   the source addon returned one, or `tmdb_260192` if not.
4. Assert: `tmdb_`-prefixed items with no tt resolution have folder hint `[tmdbid-xxx]`.
5. Assert: `raw_meta_json` is non-null for items where source addon was called.
6. Assert: no item is dropped — count before and after should match catalog size.

---

### FIX-160F-04: Manual test — NFO correctness

1. Find a synced series item. Assert `tvshow.nfo` exists in the **show root**
   directory (not in `Season 01/`).
2. Open `tvshow.nfo`. Assert root element is `<tvshow>`, not `<movie>`.
3. Assert `<imdbid>` is only present when value starts with `tt`.
4. Assert `<tmdbid>` present when TMDB ID was resolved.
5. Find a movie item. Assert `movie.nfo` (not `tvshow.nfo`) alongside `.strm`.
6. Assert root element is `<movie>`.

---

### FIX-160F-05: Schema migration test

1. `./emby-reset.sh` — fresh install. Assert new columns present in
   `catalog_items` table.
2. Restore pre-Sprint-160 DB and start plugin. Assert migration runs
   cleanly, old rows get `NULL` for new columns (not errors).

---

## Sprint 160 Completion Criteria

- [ ] `tvdb_id`, `raw_meta_json`, `catalog_type` columns added via migration
- [ ] `CatalogItem` model updated with `TvdbId`, `RawMetaJson`, persisted `CatalogType`
- [ ] All SELECT/INSERT queries updated for new columns
- [ ] `Services/IdResolverService.cs` created
- [ ] Resolution chain: parse → source addon `/meta` → AIOMetadata → fallback
- [ ] `raw_meta_json` stored verbatim from source addon response
- [ ] `CatalogDiscoverService` uses `IdResolverService` — naive extraction removed
- [ ] `CatalogItem` fully populated: `ImdbId`, `TmdbId`, `TvdbId`, `UniqueIdsJson`, `CatalogType`
- [ ] `BuildFolderName` adds `[tvdbid-xxx]` for series, `[tmdbid-xxx]` as fallback
- [ ] `WriteMovieNfo` writes `<movie>` NFO alongside `.strm`
- [ ] `WriteShowNfo` writes `<tvshow>` NFO in **show root directory**
- [ ] `<imdbid>` only written when value starts with `tt`
- [ ] Emby-native tags (`<imdbid>`, `<tmdbid>`, `<tvdbid>`, `<anidbid>`) written
- [ ] Kodi `<uniqueid>` tags retained for backwards compat
- [ ] `IdResolverService` registered in `Plugin.cs`
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Grep checklist clean
- [ ] Mixed-ID catalog test passes (no items dropped)
- [ ] NFO correctness test passes (root element, placement, no poisoned slots)
- [ ] Schema migration fresh and upgrade both succeed

---

## Notes

**Files created:** 1 (`Services/IdResolverService.cs`)

**Files modified:** 4 (`Data/DatabaseManager.cs`, `Models/CatalogItem.cs`,
`Services/CatalogDiscoverService.cs`, `Services/StrmWriterService.cs`, `Plugin.cs`)

**Files deleted:** 0

**Risk: MEDIUM** — touches the ingest ID path and file output.
Mitigated by:
1. Items are NEVER dropped — all fallback paths store native ID.
2. New columns are nullable — migration is additive, no data loss.
3. `raw_meta_json` gives us a debug record of every resolution attempt.
4. NFO changes are purely additive (more tags written, not fewer).
5. Directory rename only happens on fresh `.strm` writes — existing
   library items are not disrupted.
