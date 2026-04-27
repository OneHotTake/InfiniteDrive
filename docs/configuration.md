# EmbyStreams — Configuration Reference

Every configurable option is documented here, grouped by the tab or section where it appears in the plugin UI.

**Config file location:** `{EmbyDataPath}/plugins/configurations/EmbyStreams.xml`
Typical paths: `/var/lib/emby/data/plugins/configurations/EmbyStreams.xml` (Linux), `C:\ProgramData\Emby-Server\data\plugins\configurations\EmbyStreams.xml` (Windows).

> All numeric fields are clamped to safe ranges automatically after save. You cannot break the plugin with an out-of-range value — it will be silently corrected to the nearest valid bound.

---

## AIOStreams Connection

These settings control how the plugin reaches your AIOStreams instance.

### `AioStreamsManifestUrl`
**Type:** string · **Default:** empty · **UI label:** "Manifest URL (paste to auto-fill)"

Paste your full AIOStreams manifest URL here. The plugin parses it automatically to extract `AioStreamsUrl`, `AioStreamsUuid`, and `AioStreamsToken`. This field is a convenience input — it is not persisted after parsing.

Format: `https://your-host/stremio/{uuid}/{token}/manifest.json`

> Get yours from the DuckKota wizard at [duckkota.gitlab.io/stremio-tools/quickstart/](https://duckkota.gitlab.io/stremio-tools/quickstart/) or from AIOStreams web UI → Settings → Manifest.

### `AioStreamsUrl`
**Type:** string · **Default:** empty

Base URL of your AIOStreams server. No trailing slash, no `/stremio` path.

Examples: `http://192.168.1.100:7860`, `https://my-aiostreams.example.com`

### `AioStreamsUuid`
**Type:** string · **Default:** empty

The UUID segment from your manifest URL. Leave empty only if your AIOStreams instance has authentication disabled.

### `AioStreamsToken`
**Type:** string · **Default:** empty

The encrypted-token segment from your manifest URL. Leave empty only if your AIOStreams instance has authentication disabled.

> **Security note:** These values are stored in `EmbyStreams.xml` as plaintext. For a home server this is acceptable; for a shared or cloud host, be aware that local filesystem access grants full access to your debrid credentials through AIOStreams.

### `AioStreamsFallbackUrls`
**Type:** string · **Default:** empty

Optional additional AIOStreams manifest URLs used as Layer 2 failover. Comma or newline separated.

When the primary `AioStreamsUrl` is unreachable (TCP refused, timeout, or 5xx), the plugin tries each fallback URL in order. Fallbacks are used for **stream resolution only** — catalog sync always uses the primary instance.

Example: two manifests from DuckKota (one on your NAS, one on an ElfHosted VPS):
```
http://nas:7860/stremio/uuid-a/token-a/manifest.json
https://backup.elfhosted.com/stremio/uuid-b/token-b/manifest.json
```

### `AioStreamsAcceptedStreamTypes`
**Type:** string · **Default:** `debrid`

Comma-separated list of stream type codes to accept from AIOStreams. Types not in this list are silently filtered out before ranking.

| Code | Covers |
|------|--------|
| `debrid` | Real-Debrid, TorBox, AllDebrid, Premiumize, DebridLink, StremThru cached links |
| `usenet` | Easynews, NZBDav, AltMount |
| `torrent` | Direct torrent streams (rarely useful without a debrid) |
| `http` | Direct HTTP sources |
| `live` | Live stream URLs |

Set to empty string to accept everything AIOStreams returns.

---

## Catalog Sources

EmbyStreams can populate your library from several sources. All active sources are merged; the same IMDB ID from two sources appears once in your library.

### AIOStreams Catalogs

#### `EnableAioStreamsCatalog`
**Type:** bool · **Default:** `true`

Enables syncing catalogs discovered from the AIOStreams manifest. The manifest's `catalogs[]` array lists every catalog provided by your configured addons (Torrentio, MediaFusion, Comet, GDrive, etc.).

Disable only if your AIOStreams instance is stream-only and you rely on the Cinemeta default catalog for content discovery.

#### `AioStreamsCatalogIds`
**Type:** string · **Default:** empty (= all catalogs)

Optional comma-separated list of catalog IDs to sync. Leave empty to sync every catalog the manifest advertises.

Use this to restrict syncing to one or two catalogs when the manifest contains many (e.g. only sync your Google Drive library and not the Torrentio trending catalogs).

Common AIOStreams catalog IDs:
- `aiostreams` — default AIOStreams catalog
- `gdrive` — Google Drive integration
- `library` — Library addon
- `torbox-search` — TorBox catalog

#### `FilterAdultCatalogs`
**Type:** bool · **Default:** `true`

When enabled, catalogs that declare `behaviorHints.adult = true` in the manifest are silently skipped. Disable only if you intentionally want adult content.

### Cinemeta Auto-Default

#### `EnableCinemetaDefault`
**Type:** bool · **Default:** `true`

When enabled, the plugin automatically adds Cinemeta as a catalog source if **no other catalog source is configured and active**. This ensures new users always see library content even before they configure Trakt, MDBList, or a custom addon.

Cinemeta is only injected when:
- `EnableCatalogAddon`, `EnableTraktSource`, and `EnableMdbListSource` are all disabled (or have missing credentials), AND
- AIOStreams is either unconfigured or is stream-only

Disable if you intentionally want an empty library on first run.

> **AIOMetadata alternative:** The metadata source field accepts any Stremio-compatible addon manifest, not just Cinemeta. [AIOMetadata](https://github.com/cedya77/aiometadata) is a self-hostable metadata addon that aggregates TMDB, TVDB, MAL, AniList, AniDB, Kitsu, Fanart.tv, IMDb, and MDBList into a single manifest. It is a strict superset of Cinemeta for non-anime content and adds full anime catalog support with richer artwork (logos, backdrops).
>
> To use AIOMetadata as your catalog source, paste your AIOMetadata manifest URL wherever Cinemeta would go. The URL format is: `https://{host}/stremio/{userUUID}/{compressedConfig}/manifest.json`
>
> **Requirements:** AIOMetadata requires TMDB + TVDB API keys and a user UUID — it cannot be a zero-config fallback like Cinemeta. Get started at the public ElfHosted instance (`aiometadata.elfhosted.com`) or self-host via Docker (`ghcr.io/cedya77/aiometadata`).

---

## File Storage Paths

### `SyncPathMovies`
**Type:** string · **Default:** `/media/embystreams/movies`

Absolute path where movie `.strm` and `.nfo` files are written.

Emby must have a **Movies** library pointed at this folder (or a parent of it).

### `SyncPathShows`
**Type:** string · **Default:** `/media/embystreams/shows`

Absolute path where TV show `.strm` and `.nfo` files are written.

File structure: `{SyncPathShows}/{ShowTitle} ({Year})/Season {N}/{ShowTitle} - S{NN}E{NN}.strm`

Emby must have a **TV Shows** library pointed at this folder.

### `EnableNfoHints`
**Type:** bool · **Default:** `true`

When enabled, a minimal `.nfo` file is written alongside every `.strm` file.

The `.nfo` contains only `<uniqueid>` tags for IMDB and TMDB IDs — no plot, poster, or cast. Emby reads these IDs to find the exact right metadata entry instead of relying on filename matching alone.

This improves reliability for:
- Movies whose titles differ between AIOStreams and Emby's TMDB scraper
- Foreign films and anime with transliteration differences
- Any title where Emby would otherwise pick the wrong metadata entry

Disable only if another tool (e.g. a Kodi database manager) manages your `.nfo` files and you do not want EmbyStreams to overwrite them.

### `EmbyBaseUrl`
**Type:** string · **Default:** `http://127.0.0.1:8096`

The loopback URL written into every `.strm` file. Emby clients request this URL when the user presses Play; the plugin intercepts it.

This value is also written into `.strm` files as the playback target, so it must be reachable from every Emby client on your network.

> **Important:** If you change this after `.strm` files already exist, you must run a full catalog sync to regenerate all `.strm` files with the new URL. Use the **Purge Catalog** trigger (Settings → Debug tab) to wipe and re-sync.

---

## Cache & Resolution

### `CacheLifetimeMinutes`
**Type:** int · **Range:** 30–1440 · **Default:** `360` (6 hours)

How long a resolved stream URL is considered fresh. After this time, the entry is marked stale and re-resolved on the next play or background resolver run.

Real-Debrid CDN URLs typically expire server-side at 4–6 hours. The plugin adds a proactive range-probe at **70% of this TTL** (≈ 252 minutes for the 360-minute default) to detect silent URL expiry before the cache expires.

> Setting this lower than 60 minutes will cause very frequent re-resolution and high API usage.

### `ApiDailyBudget`
**Type:** int · **Range:** 1–100,000 · **Default:** `2000`

Maximum number of AIOStreams API calls per UTC calendar day. When this limit is reached, the background resolver pauses until the next day. On-demand playback (cache miss at play time) is **not subject** to this budget — it always proceeds.

Each call to AIOStreams may trigger multiple upstream addon requests internally; this budget counts the calls EmbyStreams makes to AIOStreams, not what AIOStreams makes downstream.

### `MaxConcurrentResolutions`
**Type:** int · **Range:** 1–20 · **Default:** `3`

Number of parallel AIOStreams calls during background pre-resolution (LinkResolverTask). Higher values speed up cache warming but increase API load.

### `ApiCallDelayMs`
**Type:** int · **Range:** 0–5000 · **Default:** `500`

Minimum milliseconds between successive AIOStreams API calls in the background resolver. Provides a natural rate-limiting floor. 0 is valid — use with caution on hosted instances.

### `SyncResolveTimeoutSeconds`
**Type:** int · **Range:** 5–300 · **Default:** `30`

Timeout for on-demand (synchronous) AIOStreams resolution at play time. If AIOStreams doesn't respond within this many seconds, the play request falls through to Layer 2/3 fallback.

The plugin also reads `behaviorHints.requestTimeout` from the AIOStreams manifest and uses whichever is larger (`max(configured, discovered)`). This means the timeout automatically grows when you add slow addons to AIOStreams.

### `CatalogItemCap`
**Type:** int · **Range:** 1–50,000 · **Default:** `500`

Maximum items fetched per catalog source per sync run. Prevents unlimited catalog growth and protects API quota.

### `CatalogItemLimitsJson`
**Type:** string · **Default:** empty

Per-catalog item limit overrides as a JSON object. The plugin config page builds this automatically from the per-row inputs in the Catalog panel.

Example: `{"aio:movie:gdrive":200,"aio:series:nfx":50}`

### `CatalogSyncIntervalHours`
**Type:** int · **Range:** 1–168 · **Default:** `24`

How many hours must pass since a catalog source's last successful sync before it can be re-fetched. Sources in an error state bypass this interval and are always retried.

This is an internal throttle — the Emby scheduled task still runs on its own schedule; this prevents hammering catalog endpoints on every task invocation.

### `CandidatesPerProvider`
**Type:** int · **Range:** 1–10 · **Default:** `3`

Number of stream candidates stored per debrid provider per item. With 3 candidates × 3 providers = 9 stored URLs. PlaybackService tries them in quality order before falling back to a fresh AIOStreams call.

Higher values (5) improve resilience against CDN URL expiry; lower values (1) save database space.

### `ProviderPriorityOrder`
**Type:** string · **Default:** `realdebrid,torbox,alldebrid,debridlink,premiumize,stremthru,usenet,http`

Comma-separated provider priority order. Within the same quality tier, EmbyStreams picks the provider that appears earliest in this list.

Quality tier **always** overrides provider priority: a 4K TorBox stream beats a 1080p Real-Debrid stream regardless of this setting.

---

## Stream Pre-Cache

The pre-cache system proactively resolves stream metadata for library items before users browse them, making the version picker appear instantly instead of requiring a 20-40s live resolve.

### `EnablePreCache`
**Type:** bool · **Default:** `true`

Enables the background `PreCacheAioStreamsTask` that resolves AIO streams for uncached library items and stores them in the `cached_streams` table.

When disabled, all items require live resolution when browsed (20-40s delay for first browse). Existing cached entries remain usable but are not refreshed.

### `PreCacheBatchSize`
**Type:** int · **Range:** 1–500 · **Default:** `42`

Maximum number of items to resolve per pre-cache task run. Each item may trigger multiple AIO provider calls (one per configured provider until a hit is found).

Lower values reduce API consumption; higher values warm the cache faster. 42 is a good balance for most catalogs.

### `PreCacheIntervalHours`
**Type:** int · **Range:** 1–48 · **Default:** `6`

Hours between automatic pre-cache task runs. The task runs as an Emby scheduled task.

For large catalogs (>5000 items), consider 4-6 hours to keep the cache warm. For small catalogs, 12-24 hours is sufficient.

### `PreCacheTTLDays`
**Type:** int · **Range:** 1–90 · **Default:** `14`

Days after which a cached stream entry is considered expired. Expired entries are re-resolved on the next pre-cache run.

Shorter TTLs (7 days) ensure fresher stream data but increase API usage. Longer TTLs (30 days) reduce API load but may serve stale provider info.

---

## Streaming & Proxy

### `ProxyMode`
**Type:** string · **Default:** `auto`

Controls how resolved stream URLs are delivered to Emby clients.

| Mode | Behaviour |
|------|-----------|
| `auto` | Tries HTTP 302 redirect first; records per-device result; falls back to proxy for clients that fail |
| `redirect` | Always HTTP 302 to the CDN URL — lowest server load; requires the client to follow redirects to external hosts |
| `proxy` | Always passes the stream through the Emby server — required for Samsung/LG TV clients and devices behind CGNAT |

`auto` is recommended. After the first successful play, the client's preference is remembered in the `client_compat` table and used on all subsequent plays.

### `MaxConcurrentProxyStreams`
**Type:** int · **Range:** 1–20 · **Default:** `5`

How many streams can be simultaneously proxied before new requests fall back to redirect. Each proxy stream holds ~256 KB of RAM buffer.

---

## Next-Up Pre-warm

### `NextUpLookaheadEpisodes`
**Type:** int · **Range:** 0–10 · **Default:** `2`

Number of subsequent episodes queued for background pre-resolution when an episode finishes playing. Ensures the next episodes in a binge-watch are already resolved and play instantly.

Season boundaries are crossed automatically (last episode of S01 queues S02E01).

Set to `0` to disable pre-warming entirely.

---

## Metadata Fallback

### `EnableMetadataFallback`
**Type:** bool · **Default:** `true`

Enables a daily background task (`MetadataFallbackTask`) that checks for items whose `.nfo` file lacks a poster thumb URL and fetches rich metadata from Cinemeta.

For each qualifying item, it writes a full Kodi-format `.nfo` (title, plot, poster URL, genres, cast, director) from the Cinemeta v3 API. This runs after Emby's own scraper has had a chance to process items.

Disable if you never want EmbyStreams to write `.nfo` files beyond the minimal ID hints written by `EnableNfoHints`.

---

## Library Re-adoption

### `DeleteStrmOnReadoption`
**Type:** bool · **Default:** `true`

When `LibraryReadoptionTask` detects that a real media file now exists for an item currently managed as a `.strm`, this setting controls whether the `.strm` (and its parent folder if empty) is deleted.

- `true` (default): removes the redundant `.strm` to prevent Emby showing duplicates
- `false`: keeps the `.strm` as a fallback — Emby may show the same title twice

---

## Sync Schedule

### `SyncScheduleHour`
**Type:** int · **Range:** -1 or 0–23 · **Default:** `3`

Hour of day (0–23 UTC) at which the daily catalog sync trigger fires automatically.
Set to `-1` to disable the automatic daily trigger and rely solely on the Emby Scheduled Tasks page.

---

## Webhook Security

### `WebhookSecret`
**Type:** string · **Default:** empty

Optional shared secret for `POST /EmbyStreams/Webhook/Sync`. When set, callers must include `Authorization: Bearer <secret>` or `X-Api-Key: <secret>`.

Leave empty only on fully trusted private networks. For Jellyseerr/Overseerr integrations on a home LAN, leaving this empty is fine.

---


## Miscellaneous

### `DontPanic`
**Type:** bool · **Default:** `false`

When enabled, error states in the Health Dashboard are displayed as a friendly green **DON'T PANIC** banner rather than alarming red alerts. Inspired by *The Hitchhiker's Guide to the Galaxy*.

### `IsFirstRunComplete`
**Type:** bool · **Default:** `false`

Internal flag. Set to `true` by the wizard after initial setup is complete. While `false`, the plugin shows the Setup Wizard on load.

To re-run the wizard: use `POST /EmbyStreams/Trigger?task=reset_wizard` or manually set this to `false` in `EmbyStreams.xml`.

---

## Auto-Discovered Fields (read-only)

These fields are populated automatically by the plugin and should not be edited manually.

| Field | Source | Purpose |
|-------|--------|---------|
| `AioStreamsDiscoveredTimeoutSeconds` | manifest `behaviorHints.requestTimeout` | Auto-scaled sync resolve timeout |
| `AioStreamsDiscoveredName` | manifest `name` | Display name in Health Dashboard |
| `AioStreamsDiscoveredVersion` | manifest `version` | Display version in Health Dashboard |
| `AioStreamsIsStreamOnly` | manifest catalog count | Triggers Cinemeta default catalog |
| `AioStreamsStreamIdPrefixes` | manifest stream resources | IMDB ID format validation |

---

## Value Ranges (Clamping Reference)

| Field | Min | Max |
|-------|-----|-----|
| `CacheLifetimeMinutes` | 30 | 1440 |
| `ApiDailyBudget` | 1 | 100000 |
| `MaxConcurrentResolutions` | 1 | 20 |
| `ApiCallDelayMs` | 0 | 5000 |
| `CatalogItemCap` | 1 | 50000 |
| `CatalogSyncIntervalHours` | 1 | 168 |
| `MaxConcurrentProxyStreams` | 1 | 20 |
| `SyncResolveTimeoutSeconds` | 5 | 300 |
| `NextUpLookaheadEpisodes` | 0 | 10 |
| `SyncScheduleHour` | -1 or 0 | 23 |
| `CandidatesPerProvider` | 1 | 10 |
| `PreCacheBatchSize` | 1 | 500 |
| `PreCacheIntervalHours` | 1 | 48 |
| `PreCacheTTLDays` | 1 | 90 |
