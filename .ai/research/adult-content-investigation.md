# Sprint 78 — Adult Content Investigation

**Date:** 2026-04-02
**Source:** `https://aiostreams.fortheweak.cloud/stremio/f8378a2d-130c-4d51-a947-c2f9bd0c4184/eyJpIjoib1BHTXJja0JmaEpTdm0vRjVBNis5UT09IiwiZSI6IldGZHVGUDFsSWdhMWlLMUtON3hjN0lLRUVxc05yOGdzREQwYm80ZGZxK1k9IiwidCI6ImEifQ/manifest.json`
**Status:** Research complete — NO code changes

---

## Phase 1: AIOStreams Adult Catalog Response

### Manifest Analysis

**Source:** Duck Streams AIOStreams setup
**Manifest URL:** `https://aiostreams.fortheweak.cloud/stremio/f8378a2d-130c-4d51-a947-c2f9bd0c4184/eyJpIjoib1BHTXJja0JmaEpTdm0vRjVBNis5UT09IiwiZSI6IldGZHVGUDFsSWdhMWlLMUtON3hjN0lLRUVxc05yOGdzREQwYm80ZGZxK1k9IiwidCI6ImEifQ/manifest.json`

**Catalog IDs for Adult/Anime Content:**

| Catalog ID | Type | Name | ID Prefix |
|-----------|------|------|-----------|
| `bc2e3b0.anidb_popular` | anime | Dubbed Top All Time MyAnimeList | `bc2e3b0.` (AniList) |
| `bc2e3b0.anilist_trending` | anime | Dubbed Trending Now AniList | `bc2e3b0.` (AniList) |
| `bc2e3b0.kitsu_top_airing` | anime | Dubbed Top Airing Kitsu | `bc2e3b0.kitsu:` (Kitsu) |
| `bc2e3b0.kitsu_anime_popular` | anime | Dubbed Most Popular Kitsu | `bc2e3b0.kitsu:` (Kitsu) |
| `bc2e3b0.notifymoe_airing` | anime | Dubbed Airing Now Notify.Moe | `bc2e3b0.notifymoe:` (Notify.Moe) |
| `bc2e3b0.livechart_popular` | anime | Dubbed Popular LiveChart.me | `bc2e3b0.livechart:` (LiveChart) |
| `bc2e3b0.anisearch_trending` | anime | Dubbed Trending aniSearch | `bc2e3b0.` (Search) |

**Key Finding:** Adult/Anime catalogs use **AniList/MAL/Kitsu ID formats**, NOT IMDB.

**Genre Options:** All anime catalogs include adult genre markers:
- "Hentai", "Ecchi", "Yaoi", "Yuri" (adult content)
- Also includes standard anime genres: Action, Adventure, Comedy, Drama, etc.

**Catalog Content:** Catalogs exist but **meta endpoints return empty**:
```
curl -s ".../catalog/anime/bc2e3b0.anidb_popular.json"
{"metas": []}
```

This suggests the Duck Streams AIOStreams instance has no actual anime content populated.

---

## Phase 2: ID Format Analysis

### AIOStreams ID Prefixes for Adult/Anime

From manifest `idPrefixes`:
```json
["tt","imdb","mal","tvdb","tmdb","kitsu","kitsu:","mal:","tmdb"]
```

| Prefix | Database | Example | Usage by AIOStreams |
|--------|----------|--------|------------------|
| `tt` | IMDb | Standard IMDB ID (`tt1234567`) |
| `mal` | MyAnimeList | AniList numeric ID (e.g., `bc2e3b0.12345`) |
| `kitsu:` | Kitsu | Kitsu ID with colon prefix (e.g., `kitsu:48363`) |
| `kitsu:mal:` | Kitsu:Mal dual ID | Combines both |

**Expected ID Format for Anime Items:**
- `bc2e3b0.` prefix (AniList) for all anime catalogs
- Format: `bc2e3b0.<anilist_id>` (e.g., `bc2e3b0.37510`)

**Implication for EmbyStreams:**
- Current `ResolveImdbId` will return empty for AniList/MAL/Kitsu IDs
- Sprint 77 `UniqueIdsJson` handles IMDB + TMDB only, not anime-specific IDs
- **GAP:** EmbyStreams cannot store or lookup anime items by AniList/MAL/Kitsu IDs

---

## Phase 3: EmbyStreams Current Handling

### Current Configuration

| Setting | Location | Current State |
|---------|----------|--------------|
| `EnableAnimeLibrary` | PluginConfiguration.cs | Present in config |
| `SyncPathAnime` | PluginConfiguration.cs | `/media/embystreams/anime` (default) |
| EnableAdultLibrary | Not configured | No adult library flag exists |

### Anime Detection in CatalogSyncTask (MapMetaToItem)

```csharp
var animeEnabled = Plugin.Instance?.Configuration?.EnableAnimeLibrary ?? false;
var mediaType = rawType switch
{
    "anime"  => animeEnabled ? "anime" : null,
    ...
};
```

- **Detection method:** Catalog type field `"anime"`
- **Fallback:** If `EnableAnimeLibrary` is false, anime items are filtered out
- **Issue:** No genre-based detection (no check for hentai/ecchi/yaoi genres)

### ID Handling

| Component | Current Behavior | Gap? |
|-----------|-----------------|------|
| `ResolveImdbId` | Discards non-IMDB IDs (incl. `bc2e3b0.`) | **YES** |
| `UniqueIdsJson` | Stores IMDB + TMDB (from BuildUniqueIdsJson) | **PARTIAL** |
| `GetCatalogItemByProviderIdAsync` | Uses SQLite JSON search | Works, but slow |

### NFO Generation

Current `WriteUniqueIds` method writes:
- IMDB ID (`<uniqueid type="imdb">`)
- TMDB ID (`<uniqueid type="tmdb">`)
- Metadata provider IDs (`<tmdbid>`, `<anilistid>`, `<kitsuid>`, `<malid>`)

**GAP:** Does not write from `UniqueIdsJson` column (only metadata)

---

## Phase 4: Emby Library State

### Database Check

```bash
sqlite3 /home/onehottake/emby-dev-data/config/data/embystreams.db \
  "SELECT DISTINCT title, media_type, source FROM catalog_items WHERE media_type='anime' LIMIT 10"
```

**Result:** No anime items found

**Reason:** AIOStreams catalogs are empty (`"metas": []`)

---

## Phase 5: Stream Resolution Assessment

Not tested (no catalog items available to fetch streams).

---

## Phase 6: Gap Analysis

| Aspect | Expected | Actual | Gap? |
|---------|----------|--------|------|
| **ID Format Support** | AniList/MAL/Kitsu handled | Only IMDB + TMDB | **YES** |
| **Adult Detection** | Genre-based filtering | Catalog type only | **YES** |
| **NFO ID Storage** | All provider IDs written | Only IMDB + TMDB | **YES** |
| **Path Routing** | Separate adult library path | Not implemented | **YES** |
| **UniqueIdsJson Population** | Adult IDs stored | Only IMDB + TMDB | **YES** |

### Current EmbyStreams Implementation Summary

**What Works:**
- Separate anime path (`SyncPathAnime`)
- Enable/disable anime library (`EnableAnimeLibrary`)
- Catalog type detection for anime
- UniqueIdsJson for IMDB + TMDB

**What Doesn't Work for Adult Content:**
1. **AniList ID parsing** — `bc2e3b0.` prefix is discarded by `ResolveImdbId`
2. **MAL/Kitsu ID parsing** — Same issue as AniList
3. **Adult genre detection** — No check for hentai/ecchi/yaoi markers
4. **Adult path routing** — No separate library path for adult content
5. **UniqueIdsJson enrichment** — Adult provider IDs not stored from metadata

---

## Phase 7: Recommendations

### Priority 1 (Blocking Adult Content Support)

**1A. Update ResolveImdbId to handle AniList/MAL/Kitsu prefixes**

Location: `Tasks/CatalogSyncTask.cs` (MapMetaToItem)

```csharp
// Current (returns empty for bc2e3b0.):
var imdbId = ResolveImdbId(meta.ImdbId ?? meta.Id);
if (string.IsNullOrEmpty(imdbId))
    return null;

// Proposed (handles AniList/MAL/Kitsu):
if (meta.Id?.StartsWith("bc2e3b0.", StringComparison.OrdinalIgnoreCase))
    // Keep as-is — AniList ID format
else if (meta.Id?.StartsWith("bc2e3b0.kitsu:", StringComparison.OrdinalIgnoreCase))
    // Extract numeric part for Kitsu
    // Store as: "kitsu:" + numericId
else if (long.TryParse(meta.Id, out var numericId))
    // Parse bare numbers as IMDB (backward compatible)
    imdbId = $"tt{numericId}";
```

**1B. Update BuildUniqueIdsJson to support anime provider IDs**

```csharp
private static string? BuildUniqueIdsJson(
    string imdbId, 
    string? tmdbId, 
    string? anilistId,
    string? kitsuId, 
    string? malId)
{
    var ids = new List<System.Text.Json.Nodes.JsonNode>();

    if (!string.IsNullOrEmpty(imdbId))
        ids.Add(CreateProviderId("imdb", imdbId));

    if (!string.IsNullOrEmpty(tmdbId))
        ids.Add(CreateProviderId("tmdb", tmdbId));

    if (!string.IsNullOrEmpty(anilistId))
        ids.Add(CreateProviderId("anilist", anilistId));

    if (!string.IsNullOrEmpty(kitsuId))
        ids.Add(CreateProviderId("kitsu", kitsuId));

    if (!string.IsNullOrEmpty(malId))
        ids.Add(CreateProviderId("mal", malId));

    return ids.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(ids) : null;
}
```

**1C. Update MapMetaToItem to pass anime IDs**

```csharp
// Extract anime IDs from AioStreamsMeta (need to add fields to model):
var anilistId = ExtractAniListId(meta.Id);
var kitsuId = ExtractKitsuId(meta.Id);
var malId = ExtractMalId(meta.Id);

return new CatalogItem
{
    ...
    UniqueIdsJson = BuildUniqueIdsJson(imdbId, tmdbId, anilistId, kitsuId, malId),
    ...
};
```

### Priority 2 (Adult Path Routing)

**2A. Add SyncPathAdult configuration**

Location: `PluginConfiguration.cs`

```csharp
/// <summary>Filesystem path for adult content (optional).</summary>
public string? SyncPathAdult { get; set; }
```

**2B. Update catalog sync to route adult content**

Location: `Tasks/CatalogSyncTask.cs`

```csharp
// In WriteSeriesStrmAsync (or similar):
var syncPath = item.MediaType switch
{
    "anime"  => config.SyncPathAnime,  // Existing
    "adult" => config.SyncPathAdult ?? config.SyncPathAnime,  // NEW
    _        => config.SyncPathMovies ?? config.SyncPathSeries,
};
```

### Priority 3 (Adult Detection)

**3A. Add genre-based adult detection**

Location: `Tasks/CatalogSyncTask.cs` (MapMetaToItem or CreateCatalogItem)

```csharp
// Detect adult content from genre options
internal static bool IsAdultContent(AioStreamsMeta meta)
{
    if (meta.Genres == null) return false;
    
    var genres = meta.Genres.Select(g => g.ToLowerInvariant());
    
    // Adult genre markers from AIOStreams anime catalogs
    var adultGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "hentai", "ecchi", "yaoi", "yuri",
        "harem", "incest", "tentacle", "lolicon", "futanari",
        "guro", "ryona", "vore", "scat", "piss"
        // Add more as needed
    };
    
    return genres.Any(g => adultGenres.Contains(g));
}
```

### Priority 4 (NFO Enhancement)

**4A. Update WriteUniqueIds to write from UniqueIdsJson**

Location: `Tasks/CatalogSyncTask.cs`

```csharp
// Add to WriteUniqueIds:
private static void WriteUniqueIds(StringBuilder sb, CatalogItem item, JsonElement? meta)
{
    // Existing metadata IDs...
    if (!string.IsNullOrEmpty(item.ImdbId))
        sb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{item.ImdbId}</uniqueid>");
    if (!string.IsNullOrEmpty(item.TmdbId))
        sb.AppendLine($"  <uniqueid type=\"tmdb\">{item.TmdbId}</uniqueid>");
    
    // NEW: Write from UniqueIdsJson
    if (!string.IsNullOrEmpty(item.UniqueIdsJson))
    {
        var uniqueIds = System.Text.Json.JsonSerializer.Deserialize<UniqueId[]>(item.UniqueIdsJson);
        foreach (var uid in uniqueIds)
        {
            sb.AppendLine($"  <uniqueid type=\"{uid.Provider}\">{uid.Id}</uniqueid>");
        }
    }
}
```

---

## Definition of Done

- [x] Manifest analyzed
- [x] AIOStreams catalog formats documented
- [x] ID format analysis completed
- [x] EmbyStreams current handling assessed
- [x] Gap analysis completed
- [x] Prioritized recommendations documented
- [x] Research document created

---

## Summary

**Key Finding:** AIOStreams adult/anime catalogs use AniList/MAL/Kitsu ID formats (`bc2e3b0.` prefix) which are **not currently handled** by EmbyStreams.

**Critical Gaps:**
1. `ResolveImdbId` discards AniList IDs (returns empty)
2. `BuildUniqueIdsJson` only stores IMDB + TMDB (no anime provider IDs)
3. No adult path routing (`SyncPathAdult`)
4. No genre-based adult detection

**Recommendation:** Implement Priority 1 fixes before adult content can be properly supported. Estimated 1 sprint (8+ hours).
