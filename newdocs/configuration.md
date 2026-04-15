# InfiniteDrive — Configuration Reference

Every configurable option documented here, grouped by the tab where it appears in the plugin UI.

**Config file:** `{DataPath}/plugins/configurations/InfiniteDrive.xml`
Typical paths: `/var/lib/emby/data/plugins/configurations/InfiniteDrive.xml` (Linux), `C:\ProgramData\Emby-Server\data\plugins\configurations\InfiniteDrive.xml` (Windows).

> All numeric fields are clamped to safe ranges automatically on deserialization. Out-of-range values are silently corrected.

---

## Providers Tab

### `PrimaryManifestUrl`
**Type:** string · **Default:** empty

Full AIOStreams manifest URL. Paste the URL from DuckKota's wizard or your self-hosted AIOStreams instance.

Format: `https://host/stremio/{uuid}/{token}/manifest.json`

The plugin parses this automatically to extract base URL, UUID, and token.

### `SecondaryManifestUrl`
**Type:** string · **Default:** empty

Optional backup AIOStreams manifest URL. Used when the primary instance is unreachable (TCP refused, timeout, or 5xx).

### `EnableBackupAioStreams`
**Type:** bool · **Default:** `false`

When `true`, falls back to `SecondaryManifestUrl` if primary manifest parsing fails. When `false`, secondary URL is ignored.

---

## Libraries Tab

### `SyncPathMovies`
**Type:** string · **Default:** `/media/infinitedrive/movies`

Absolute path where movie `.strm` and `.nfo` files are written. Add this folder as an **Emby Movies library**.

### `SyncPathShows`
**Type:** string · **Default:** `/media/infinitedrive/shows`

Absolute path where TV series `.strm` and `.nfo` files are written. Add this folder as an **Emby TV Shows library**.

### `SyncPathAnime`
**Type:** string · **Default:** `/media/infinitedrive/anime`

Absolute path for anime `.strm` files when `EnableAnimeLibrary = true`. Requires the Emby Anime Plugin.

### `LibraryNameMovies`
**Type:** string · **Default:** `Streamed Movies`

Display name for the movies library.

### `LibraryNameSeries`
**Type:** string · **Default:** `Streamed Series`

Display name for the series library.

### `LibraryNameAnime`
**Type:** string · **Default:** `Streamed Anime`

Display name for the anime library.

### `EnableAnimeLibrary`
**Type:** bool · **Default:** `false`

When `true`, `type: "anime"` items from AIOStreams are routed to the anime library. Requires Emby Anime Plugin.

### `EmbyBaseUrl`
**Type:** string · **Default:** `http://127.0.0.1:8096`

The loopback URL written into every `.strm` file. Emby clients request this URL when the user presses Play.

> **Important:** After changing this, trigger a full catalog sync to regenerate all `.strm` files with the new URL.

### `EmbyApiKey`
**Type:** string · **Default:** empty

Emby API key for `.strm` file authentication. Required.

Get it from: **Emby Dashboard → Settings → API Keys → Add → Copy the key**

### `MetadataLanguage`
**Type:** string · **Default:** `en`

Preferred metadata language for Emby libraries.

### `MetadataCountryCode`
**Type:** string · **Default:** `US`

Country code for metadata (TMDB scraper).

### `ImageLanguage`
**Type:** string · **Default:** `en`

Preferred image/artwork language.

### `SubtitleDownloadLanguages`
**Type:** string · **Default:** `en`

Comma-separated subtitle language preferences.

### `SkipFutureEpisodes`
**Type:** bool · **Default:** `true`

Skip episodes that haven't aired yet.

### `FutureEpisodeBufferDays`
**Type:** int · **Range:** 0–30 · **Default:** `2`

Buffer days to consider future episodes as aired.

### `DefaultSeriesSeasons`
**Type:** int · **Range:** 1–50 · **Default:** `1`

Number of seasons to write when series metadata is unavailable (Stremio returns 404).

### `DefaultSeriesEpisodesPerSeason`
**Type:** int · **Range:** 1–100 · **Default:** `10`

Episodes per season when series metadata is unavailable.

### `EnableNfoHints`
**Type:** bool · **Default:** `true`

When `true`, a minimal `.nfo` file is written alongside every `.strm` file containing only IMDB/TMDB ID hints. Improves Emby metadata matching.

---

## Sources Tab

### `EnableAioStreamsCatalog`
**Type:** bool · **Default:** `true`

Enables syncing catalogs from the AIOStreams manifest's `catalogs[]` array.

### `AioStreamsCatalogIds`
**Type:** string · **Default:** empty (= all catalogs)

Comma-separated list of specific catalog IDs to sync. Leave empty for all catalogs.

Common IDs: `aiostreams`, `gdrive`, `library`, `torbox-search`

### `AioStreamsAcceptedStreamTypes`
**Type:** string · **Default:** `debrid`

Comma-separated stream types to accept: `debrid`, `torrent`, `usenet`, `http`, `live`. Set to empty to accept all.

### `CatalogItemCap`
**Type:** int · **Range:** 1–50,000 · **Default:** `500`

Maximum items fetched per catalog source per sync run.

### `CatalogItemLimitsJson`
**Type:** string · **Default:** empty

Per-catalog item limit overrides as JSON: `{"aio:movie:gdrive":200,"aio:series:nfx":50}`

Built automatically by the UI.

### `CatalogSyncIntervalHours`
**Type:** int · **Range:** 1–24 · **Default:** `1`

Minimum hours between successful syncs of the same source. Error-state sources always retry immediately.

### `ProxyMode`
**Type:** string · **Default:** `auto`

| Mode | Behaviour |
|------|-----------|
| `auto` | Redirect first; learn per-client; fall back to proxy for clients that fail |
| `redirect` | Always HTTP 302 to CDN URL |
| `proxy` | Always passthrough (needed for Samsung/LG TVs, CGNAT) |

### `MaxConcurrentProxyStreams`
**Type:** int · **Range:** 1–20 · **Default:** `5`

Max simultaneous proxied streams. Each uses ~256 KB RAM.

### `MaxConcurrentResolutions`
**Type:** int · **Range:** 1–20 · **Default:** `3`

Max parallel AIOStreams HTTP calls during background pre-resolution.

### `CacheLifetimeMinutes`
**Type:** int · **Range:** 30–1,440 · **Default:** `360`

Resolved stream URL cache TTL. Proactive range-probe at 70% TTL to catch silent URL expiry.

### `ApiDailyBudget`
**Type:** int · **Range:** 1–100,000 · **Default:** `2000`

Max AIOStreams API calls per UTC day. On-demand playback (cache miss) is exempt.

### `CandidatesPerProvider`
**Type:** int · **Range:** 1–10 · **Default:** `3`

Stream candidates stored per debrid provider per item.

### `SyncResolveTimeoutSeconds`
**Type:** int · **Range:** 5–300 · **Default:** `30`

On-demand resolution timeout. Auto-scales up if AIOStreams manifest advertises `behaviorHints.requestTimeout`.

### `ProviderPriorityOrder`
**Type:** string · **Default:** `realdebrid,torbox,alldebrid,debridlink,premiumize,stremthru,usenet,http`

Provider ranking within the same quality tier. Quality tier always takes precedence.

### `EnableCinemetaDefault`
**Type:** bool · **Default:** `true`

Auto-injects Cinemeta as a catalog source when AIOStreams is stream-only or unconfigured.

---

## Security Tab

### `PluginSecret`
**Type:** string · **Default:** (auto-generated)

HMAC-SHA256 signing secret. Auto-generated as 32 random bytes (base64) on first plugin load.

> **Warning:** Rotating this invalidates ALL existing `.strm` files. Trigger a full catalog sync after rotation.

### `SignatureValidityDays`
**Type:** int · **Range:** 1–3,650 · **Default:** `365`

How many days HMAC-signed `.strm` URLs remain valid.

---

## Parental Controls Tab

### `TmdbApiKey`
**Type:** string · **Default:** empty

TMDB API key for fetching MPAA/TV ratings. Free at themoviedb.org → Settings → API. Required for parental filtering.

### `BlockUnratedForRestricted`
**Type:** bool · **Default:** `true`

When enabled, users with max parental rating below 999 will NOT see content without known ratings.

---

## Versioned Playback

### `DefaultSlotKey`
**Type:** string · **Default:** `hd_broad`

Default quality slot for playback. Must match an enabled slot in the `version_slots` table.

Available slots: `hd_broad` (1080p H.264 DD+), `best_available`, `4k_hdr`, `4k_dv`, `4k_sdr`, `hd_efficient`, `compact`

### `CandidateTtlHours`
**Type:** int · **Range:** 1–168 · **Default:** `6`

Hours before a normalized stream candidate is considered expired and cleaned up.

### `NextUpLookaheadEpisodes`
**Type:** int · **Range:** 0–10 · **Default:** `2`

Number of subsequent episodes pre-resolved when an episode finishes playing. Set to `0` to disable.

---

## Schedule

### `SyncScheduleHour`
**Type:** int · **Range:** -1 or 0–23 · **Default:** `3`

Hour of day (UTC) for daily catalog sync. Set to `-1` to disable auto-sync.

---

## Library Re-adoption

### `DeleteStrmOnReadoption`
**Type:** bool · **Default:** `true`

When `true`, `.strm` files are deleted when real media files are detected for the same item.

---

## Metadata Enrichment

### `AioMetadataBaseUrl`
**Type:** string · **Default:** empty

AIOMetadata API base URL for enrichment. Format: `https://host/meta/{type}/{id}.json`

---

## First-Run

### `IsFirstRunComplete`
**Type:** bool · **Default:** `false`

Internal flag. Set to `true` after the first-run wizard completes. While `false`, the dashboard shows the setup wizard.

---

## Auto-Discovered Fields (read-only)

| Field | Source | Purpose |
|-------|--------|---------|
| `AioStreamsDiscoveredTimeoutSeconds` | manifest `behaviorHints.requestTimeout` | Auto-scaled resolve timeout |
| `AioStreamsDiscoveredName` | manifest `name` | Display name in Health tab |
| `AioStreamsDiscoveredVersion` | manifest `version` | Version in Health tab |
| `AioStreamsIsStreamOnly` | manifest catalog count | Triggers Cinemeta default |
| `AioStreamsStreamIdPrefixes` | manifest stream resources | ID format validation |
| `ResolvedInstanceType` | manifest URL host detection | `Private` vs `Shared` |

---

## Value Ranges

| Field | Min | Max |
|-------|-----|-----|
| `CacheLifetimeMinutes` | 30 | 1,440 |
| `ApiDailyBudget` | 1 | 100,000 |
| `MaxConcurrentResolutions` | 1 | 20 |
| `CatalogItemCap` | 1 | 50,000 |
| `CatalogSyncIntervalHours` | 1 | 24 |
| `MaxConcurrentProxyStreams` | 1 | 20 |
| `SyncResolveTimeoutSeconds` | 5 | 300 |
| `NextUpLookaheadEpisodes` | 0 | 10 |
| `SyncScheduleHour` | -1 or 0 | 23 |
| `CandidatesPerProvider` | 1 | 10 |
| `CandidateTtlHours` | 1 | 168 |
| `SignatureValidityDays` | 1 | 3,650 |
| `FutureEpisodeBufferDays` | 0 | 30 |
