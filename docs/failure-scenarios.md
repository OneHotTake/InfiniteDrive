# EmbyStreams — Failure Scenarios

This document describes every failure state the plugin can encounter and exactly what happens in each case. The goal is zero silent failures — every problem either resolves itself automatically or surfaces a clear error.

---

## Playback Failures

### How `GET /EmbyStreams/Play` works (normal path)

```
1. SQLite cache lookup
   ├─ MISS → go to step 4 (sync resolve)
   └─ HIT
       ├─ "failed" + empty URL + not expired → no_streams sentinel (step 5.a)
       ├─ "valid" + not expired + not aging → serve immediately (step 2)
       └─ expired / stale / aging → range probe (step 3)
           ├─ probe OK → serve + queue background refresh
           └─ all probes fail → go to step 4 (sync resolve)

4. Sync AIOStreams call
   ├─ success → cache + serve
   └─ AioStreamsUnreachableException
       └─ 4.5. Layer 3 direct debrid fallback
           ├─ success → cache 30 min + serve
           └─ no InfoHash / no API key / debrid not cached
               └─ go to step 5 (panic)

5. Total failure → HTTP 503 + redirect to /EmbyStreams/Panic
```

---

### F-01: Valid cache hit — normal fast path

**Trigger:** Cache entry exists, `status = 'valid'`, not expired, age < 70% of `CacheLifetimeMinutes`.

**Behaviour:** Stream URL served immediately via redirect or proxy. No external calls made. Playback begins in < 100 ms.

**Logging:** `[EmbyStreams] Cache HIT for {imdb}` (Debug)

---

### F-02: Aging cache — proactive range probe

**Trigger:** Cache entry is valid and not expired, but age > 70% of `CacheLifetimeMinutes` (≈ 252 min for the 360-min default).

**Why it happens:** Real-Debrid CDN URLs expire server-side at ~4–6 hours, independent of the 6-hour cache TTL. Proactive probing prevents silently serving a dead URL during hours 4–6.

**Behaviour:**
1. Makes a `GET Range: bytes=0-0` probe against the primary cached URL
2. If probe returns 2xx or 206: serves the URL, queues a background Tier 1 re-resolution
3. If probe returns 401/403/404/410: tries each ranked candidate URL in turn
4. If all candidates fail: falls through to sync AIOStreams call

**Logging:** `[EmbyStreams] Cache HIT but URL aging for {imdb} — proactive range probe` (Debug)

---

### F-03: Stale / expired cache

**Trigger:** Cache entry exists but `status != 'valid'` OR `expires_at` is in the past.

**Behaviour:** Same range-probe flow as F-02. If all probes pass, the cache entry is refreshed in the background.

**Logging:** `[EmbyStreams] Cache STALE for {imdb} — validating` (Debug)

---

### F-04: All cached URLs dead (401/403/404/410)

**Trigger:** Every stored URL (primary + all ranked candidates) fails the range probe.

**Behaviour:**
1. Falls through to synchronous AIOStreams call
2. If AIOStreams responds: new URL cached + served
3. If AIOStreams is unreachable: Layer 3 direct debrid fallback attempted

**Logging:** `[EmbyStreams] All cached URLs for {imdb} returned 4xx — sync resolving` (Warning)

**User impact:** Slight delay at play time (typically 2–20 s depending on AIOStreams addon count). No visible error if AIOStreams is up.

---

### F-05: Cache miss — first play of an item

**Trigger:** No cache entry exists for this IMDB ID + season/episode combination.

**Behaviour:**
1. Makes a synchronous AIOStreams stream resolution call
2. AIOStreams queries all configured addons in parallel
3. EmbyStreams ranks results (quality tier → codec score → provider priority) and stores top candidates
4. Serves the top-ranked URL, typically within 3–20 s

**Logging:** `[EmbyStreams] Cache MISS for {imdb} — sync resolving` (Info)

**Optimisation:** The background `LinkResolverTask` pre-resolves items before they are played. A cache miss at play time means the resolver hasn't reached this item yet, the item is new, or the cache was manually cleared.

---

### F-06: AIOStreams returns empty streams (`{"streams":[]}`)

**Trigger:** AIOStreams is reachable but returns an empty streams array for this item. This means none of the configured addons found a cached torrent for it — not a network error.

**Behaviour:**
1. A `failed` sentinel entry is written to `resolution_cache` with `stream_url = ''` and a **1-hour TTL** (shorter than the normal 6-hour TTL)
2. Any subsequent play within the 1-hour window immediately returns 503 without calling AIOStreams again
3. After 1 hour, the resolver retries automatically

**Logging:** `[EmbyStreams] no_streams sentinel hit for {imdb} (1h TTL not expired)` (Debug) when the sentinel is hit on replay.

**Why 1 hour TTL:** Prevents hammering AIOStreams for items that are genuinely unavailable. The short TTL ensures the plugin retries within the hour in case availability changes (torrent gets cached by a debrid user).

---

### F-07: AIOStreams unreachable (Layer 2 fallback triggers)

**Trigger:** Primary AIOStreams instance returns `HttpRequestException` or `TaskCanceledException` (TCP refused, DNS failure, or timeout).

**Behaviour:**
1. Each configured fallback URL in `AioStreamsFallbackUrls` is tried in order
2. First fallback to respond successfully is used
3. If all fallbacks are also unreachable: `AioStreamsUnreachableException` is thrown

**Logging:** `[EmbyStreams] All AIOStreams instances unreachable for {imdb} — attempting direct debrid fallback` (Warning)

---

### F-08: All AIOStreams instances down — Layer 3 direct debrid

**Trigger:** `AioStreamsUnreachableException` from Layer 2 exhaustion.

**Conditions required for Layer 3 to fire:**
- At least one `StreamCandidate` for this item has a non-null `InfoHash` stored in the database (means AIOStreams was available at least once previously and the torrent was cached)
- At least one of `RealDebridApiKey`, `TorBoxApiKey`, `PremiumizeApiKey`, or `AllDebridApiKey` is configured

**Behaviour:**
1. For each candidate with an InfoHash, tries providers in `ProviderPriorityOrder`
2. Per provider: calls the **instant availability** (cache check) API — never triggers a download
3. If the torrent is cached: generates a fresh direct-play URL
4. Caches the result with a **30-minute short TTL** (so the next play re-validates via AIOStreams once it recovers)

**Logging:** Per-provider results at Debug level

**User impact:** Playback succeeds despite AIOStreams being completely down. The stream URL is generated directly from the debrid provider's API.

---

### F-09: Layer 3 not available or torrent not cached

**Trigger:** AIOStreams is down, but either:
- No API keys are configured, OR
- The item has no stored InfoHash (was never cached by the debrid provider), OR
- All providers report the torrent is not in their cache

**Behaviour:** Emits 503 response with redirect to `/EmbyStreams/Panic`.

---

### F-10: Season 0 / specials (S00Exx)

**Trigger:** Play request with `season=0`.

**Behaviour:** Season/episode parameters are dropped and the request is treated as a movie lookup. AIOStreams has no concept of Season 0 episodes.

**Logging:** `[EmbyStreams] Season 0 (specials) requested for {imdb} — falling back to movie lookup` (Debug)

---

### F-11: AIOStreams rate limited (429)

**Trigger:** AIOStreams returns HTTP 429.

**Behaviour:** The 429 response is treated as a temporary failure. A `failed` sentinel is cached with a **5-minute backoff TTL** (much shorter than the empty-streams 1-hour TTL or the error TTL). The next play after 5 minutes will retry.

---

### F-12: Total failure — Panic page redirect

**Trigger:** All of the above paths exhausted with no stream URL.

**Behaviour:**
1. Returns HTTP 503
2. If `DontPanic = false`: standard JSON error response
3. If the Emby client follows the body as a stream: it will get a redirect to `GET /EmbyStreams/Panic` which returns a Hitchhiker's Guide–styled HTML error page

**Error codes returned:**
| Code | Meaning |
|------|---------|
| `no_streams` | AIOStreams explicitly returned empty streams |
| `stream_unavailable` | All resolution paths failed |
| `server_error` | Plugin not initialised (should never happen in practice) |
| `bad_request` | Missing or invalid IMDB ID in the `.strm` file |

---

## Catalog Sync Failures

### C-01: Catalog HTTP error or timeout

**Trigger:** AIOStreams catalog endpoint returns 4xx/5xx, or the request times out.

**Behaviour:**
1. The sync for that specific catalog is aborted; other catalogs continue
2. `sync_state.consecutive_failures` is incremented
3. After 3 consecutive failures, a warning is emitted in the Health Dashboard
4. The interval guard is NOT advanced — the catalog will be retried on the next sync run

**Recovery:** Automatic once AIOStreams recovers.

---

### C-02: Item has no IMDB ID (kitsu:, tmdb:, etc.)

**Trigger:** AIOStreams returns a catalog item with a non-IMDB ID (e.g. `kitsu:12345` for anime).

**Behaviour:** Item is silently skipped. Only items with `tt` IMDB IDs are written to the Emby library.

**Why:** EmbyStreams uses IMDB IDs as the universal key across catalog, cache, and `.strm` filenames. Non-IMDB IDs would require a separate mapping layer.

---

### C-03: `.strm` file write fails (permission error)

**Trigger:** The Emby process does not have write permission to `SyncPathMovies` or `SyncPathShows`.

**Behaviour:** The sync task logs an error for each affected item and continues. Items that cannot be written are recorded in the DB without a `strm_path`.

**Resolution:** Fix file permissions; then run `POST /EmbyStreams/Trigger?task=file_resurrection` to write the missing `.strm` files.

---

### C-04: Catalog pagination exceeds max pages

**Trigger:** A catalog returns >= 100 items per page and the plugin has fetched 200 pages.

**Behaviour:** Pagination stops at `AioCatalogMaxPages = 200`. This caps catalog sync at 20,000 items from a single catalog regardless of `CatalogItemCap`.

---

### C-05: Adult catalog skipped

**Trigger:** A catalog's manifest declares `behaviorHints.adult = true` and `FilterAdultCatalogs = true`.

**Behaviour:** The entire catalog is skipped silently.

---

### C-06: Catalog requires search (no browse)

**Trigger:** A catalog has `extra[].name = "search"` with `isRequired = true` — meaning it only supports searched queries, not browsable lists.

**Behaviour:** The catalog is skipped for catalog sync (you can't page through a search-only catalog). It remains usable for on-demand stream resolution.

---

## Database Failures

### D-01: Database corruption detected at startup

**Trigger:** The SQLite integrity check (`PRAGMA integrity_check`) returns anything other than `ok`.

**Behaviour:**
1. The corrupt database file is deleted
2. A new empty database is created and fully initialised
3. All catalog items and cached URLs are lost

**Recovery:** Run a full catalog sync and wait for the background resolver to re-warm the cache. This process typically takes 15–60 minutes for a large catalog.

**Logging:** `[EmbyStreams] Database integrity check failed — deleting and recreating {path}` (Warning)

---

### D-02: Schema migration fails

**Trigger:** A migration step throws an unhandled exception (e.g. disk full, filesystem error).

**Behaviour:** The exception bubbles up to the plugin initialisation code. Emby logs the error; the plugin may be unavailable until the underlying issue is resolved and Emby is restarted.

---

### D-03: Database file locked

**Trigger:** Another process (or a previous hung task) holds a write lock on `embystreams.db`.

**Behaviour:** SQLite WAL mode (`PRAGMA journal_mode=WAL`) allows concurrent reads but serialises writes. Write operations wait up to the SQLite default busy timeout. If the lock persists, writes fail with `SQLITE_BUSY`.

**Recovery:** Restart Emby to release any hung connections.

---

## Proxy Failures

### P-01: Proxy stream unexpectedly terminated

**Trigger:** The debrid CDN closes the connection mid-stream (common for 4K+ bitrate streams on unstable WAN links, or when the CDN URL expires mid-stream).

**Behaviour:** The Emby client receives a connection reset. Most clients (Infuse, Emby for Android) automatically retry from the last position. The plugin does not attempt to resume — a new `/EmbyStreams/Play` request will re-enter the resolution flow.

---

### P-02: Range request not supported by source

**Trigger:** A stream URL (typically from a usenet/HTTP source) does not return `Accept-Ranges: bytes`.

**Behaviour:** Seeking will not work for that stream. The video plays from the beginning. No error is shown.

---

### P-03: Too many concurrent proxy streams

**Trigger:** More than `MaxConcurrentProxyStreams` streams are being simultaneously proxied.

**Behaviour:** New proxy requests fall back to redirect mode for that session. If the client cannot follow redirects (e.g. Samsung TV), the stream may fail.

**Resolution:** Increase `MaxConcurrentProxyStreams` or switch affected clients to proxy mode explicitly.

---

## Webhook Failures

### W-01: Webhook authentication fails

**Trigger:** `WebhookSecret` is set and the request does not include a matching `Authorization: Bearer <secret>` or `X-Api-Key` header.

**Behaviour:** HTTP 401 is returned. The sync operation is not performed.

---

### W-02: Invalid IMDB ID in webhook body

**Trigger:** The webhook body contains `{"imdb":"not_a_tt_id"}`.

**Behaviour:** HTTP 400 is returned. IMDB IDs must match `tt` + 1–8 digits.

---

### W-03: Jellyseerr/Overseerr webhook not recognised

**Trigger:** The webhook body does not contain `notification_type: MEDIA_APPROVED` and does not contain an `imdb` field.

**Behaviour:** Silently ignored — no sync is triggered.

---

## Security Guard Failures

### S-01: Non-admin user accesses admin endpoint

**Trigger:** A user without Emby Admin role calls a protected endpoint (Status, Catalogs, Inspect, Search, Trigger, etc.).

**Behaviour:** HTTP 401 with `{"error":"admin_required"}`.

---

### S-02: SSRF attempt on TestUrl endpoint

**Trigger:** `POST /EmbyStreams/TestUrl` with a URL using a non-http/https scheme (e.g. `file://`, `ftp://`) or targeting an APIPA address (`169.254.x.x`).

**Behaviour:** Request is rejected with HTTP 400 before any network call is made.

---

## Metadata Failures

### M-01: Emby scraper cannot match item

**Trigger:** Emby's TMDB scraper cannot find a match for the `.strm` filename or `.nfo` ID hints.

**Behaviour:**
1. If `EnableNfoHints = true`: the IMDB ID in the `.nfo` should guide Emby to the correct match. If it still fails, Emby shows the item with a placeholder poster.
2. If `EnableMetadataFallback = true`: the daily `MetadataFallbackTask` detects items without poster thumbs and fetches full metadata from Cinemeta, writing a rich `.nfo` with poster URL.

---

### M-02: Cinemeta API unavailable

**Trigger:** `https://v3-cinemeta.strem.io` is unreachable during MetadataFallbackTask or catalog default injection.

**Behaviour:** The fallback task silently skips items it cannot enrich. The next daily run retries.

---

## Background Task Failures

### T-01: LinkResolverTask exceeds API daily budget

**Trigger:** `api_budget` table shows >= `ApiDailyBudget` calls today (UTC).

**Behaviour:** The resolver pauses for the rest of the day. On-demand playback resolution is not affected.

---

### T-02: EpisodeExpandTask cannot find next episode

**Trigger:** Playback stops on the last episode of a series (or the last episode in the DB).

**Behaviour:** The pre-warm queue simply has nothing to add. No error. No next-episode pre-resolution.

---

### T-03: FileResurrectionTask cannot write `.strm`

**Trigger:** Permission denied or path does not exist.

**Behaviour:** The item's `resurrection_count` is incremented. After repeated failures, the item is marked with a high resurrection count but is never deleted from the DB.

---

### T-04: Scheduled task timeout (30 min)

**Trigger:** Any task triggered via `POST /EmbyStreams/Trigger` runs for more than 30 minutes.

**Behaviour:** The task's `CancellationToken` is cancelled. Partial progress is preserved (already-written items remain in DB/disk). The task stops cleanly.

**Logging:** `[EmbyStreams] TriggerService: '{task}' timed out (30 min)` (Warning)

---

### T-05: PreCacheAioStreamsTask — API budget exhausted mid-batch

**Trigger:** `IsBudgetExhaustedAsync()` returns true while the pre-cache task is running.

**Behaviour:** The task logs how many items were resolved before exhaustion and stops. Remaining items are retried on the next scheduled run. No items are lost.

**Logging:** `[PreCache] Budget exhausted after {N} items — {M} remaining` (Warning)

---

### T-06: PreCacheAioStreamsTask — AIO rate limit (429)

**Trigger:** AIOStreams returns HTTP 429 during pre-cache resolution.

**Behaviour:** The task catches `AioStreamsRateLimitException`, applies exponential backoff (5s → 10s → 20s → 40s → 60s max with 0-2s jitter), and continues to the next item. The rate-limited item is not counted as failed — it will be retried on the next run. The `CooldownGate` is also notified for global 429 tracking.

**Logging:** `[PreCache] AIO rate limit hit — backing off {Seconds}s (consecutive: {Hits}) for {Imdb}` (Warning)

---

### T-07: PreCacheAioStreamsTask — all providers fail for an item

**Trigger:** Every configured provider returns no streams or throws an exception for a specific item.

**Behaviour:** The item is counted as `failed` in stats. No entry is written to `cached_streams`. The item will appear as uncached on the next run and be retried.

**Logging:** `[PreCache] Failed {Imdb}` (Debug)

---

### T-08: PreCacheAioStreamsTask — plugin not initialised

**Trigger:** The pre-cache task runs but `Plugin.Instance`, `DatabaseManager`, or `StreamCacheService` is null.

**Behaviour:** The task logs a warning and exits cleanly. No items are processed.

**Logging:** `[PreCache] Plugin not initialised — aborting` or `[PreCache] StreamCacheService not initialised — aborting` (Warning)
