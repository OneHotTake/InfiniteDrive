# InfiniteDrive

> *"The ships hung in the sky in much the same way that bricks don't."* — Douglas Adams, *The Hitchhiker's Guide to the Galaxy*

An Emby plugin that discovers streaming catalogs from [AIOStreams](https://github.com/aiostreams), writes `.strm` files, and resolves debrid URLs on demand.

---

## What It Does

1. **Catalog Sync** — pulls movie/series/anime catalogs from AIOStreams, writes `.strm` files into Emby libraries
2. **Stream Resolution** — resolves `.strm` playback against AIOStreams in real time via `/InfiniteDrive/Resolve`
3. **Stream Probing** — checks if candidate streams respond before serving to players (`StreamProbeService`)
4. **ID Normalization** — resolves IMDb/TMDB/TVDB IDs via source addon's `/meta` endpoint (`IdResolverService`)
5. **NFO Decoration** — writes Emby-native `.nfo` files with scanner hints (`NfoWriterService`)
6. **Versioned Playback** — multiple quality slots per title (HD Broad floor, 4K HDR/DV opt-in)

---

## Requirements

- **Emby Server** 4.10.0.6+ (check with `Help → About`)
- **AIOStreams** manifest URL (self-hosted or via [DuckKota's wizard](https://duckkota.gitlab.io/stremio-tools/quickstart/))
- **Debrid subscription** configured in AIOStreams (Real-Debrid, AllDebrid, TorBox, Premiumize, etc.)
- **.NET 8.0** (bundled with Emby)

---

## Installation

1. Build: `dotnet publish -c Release`
2. Copy `bin/Release/net8.0/publish/InfiniteDrive.dll` + `plugin.json` to:
   ```
   /var/lib/emby/plugins/InfiniteDrive/   (Linux)
   C:\ProgramData\Emby-Server\plugins\InfiniteDrive\  (Windows)
   ```
3. Restart Emby: `systemctl restart emby-server`
4. Verify: **Dashboard → Plugins** → InfiniteDrive appears

Dev scripts:
```bash
./emby-reset.sh   # full reset (wipes data)
./emby-start.sh   # build + deploy + start (no data wipe)
```

---

## Configuration

**Config file:** `{DataPath}/plugins/configurations/InfiniteDrive.xml`

Open the config page:
```
http://localhost:8096/web/configurationpage?name=InfiniteDrive
```

### Required Settings

| Setting | Description |
|---------|-------------|
| **PrimaryManifestUrl** | Full AIOStreams manifest URL from DuckKota wizard or self-hosted |
| **EmbyBaseUrl** | Emby loopback URL for .strm files (default: `http://127.0.0.1:8096`) |
| **EmbyApiKey** | Emby API key for .strm authentication (Dashboard → API Keys → Add) |
| **SyncPathMovies** | Folder for movie `.strm` files (add as Emby Movies library) |
| **SyncPathShows** | Folder for series `.strm` files (add as Emby TV Shows library) |

### Optional: Failover

Add a **SecondaryManifestUrl** for backup AIOStreams. When the primary is unreachable, the plugin pivots automatically.

---

## User Interface

### Settings Tabs (7 tabs)

| Tab | Purpose |
|-----|---------|
| Health | Live dashboard: connection status, API budget, coverage stats |
| Providers | AIOStreams manifest URLs, connection test |
| Libraries | Storage paths, metadata language, library names |
| Sources | Catalog sync settings, cache tuning, proxy mode |
| Security | PluginSecret rotation, signature validity |
| Parental Controls | TMDB API key, unrated content filter |
| Repair | Diagnostic triggers, destructive actions (with confirmation) |

### Discover UI

Netflix-style browsing at:
```
http://localhost:8096/web/configurationpage?name=InfiniteDiscover
```
- Browse catalog with posters and ratings
- Search by title (auto-debounced)
- Add items to library with one click
- My Picks / My Lists tabs
- Server-side parental filtering

---

## Architecture

```
AIOStreams API
     │
     ├── CatalogSyncTask / TriggerService  (scheduled: discovers catalogs)
     ├── CatalogDiscoverService           (resolves items, normalizes IDs)
     ├── IdResolverService                (tt/tmdb/tvdb resolution chain)
     ├── NfoWriterService                 (writes .nfo files: seed + enriched)
     ├── StrmWriterService                 (writes .strm files)
     └── MarvinTask                        (trickle hydration, background)

Emby Player → /InfiniteDrive/Resolve      (real-time: picks streams, probes URLs)
                   └── /InfiniteDrive/Stream  (HLS manifest proxy or CDN redirect)
```

### Key Components

| Component | Responsibility |
|-----------|----------------|
| `Plugin` | Entry point, singleton state, config persistence |
| `InfiniteDriveInitializationService` | IServerEntryPoint: DB init, PluginSecret generation |
| `ResolverService` | `GET /InfiniteDrive/Resolve`: AIOStreams resolution with circuit breaker |
| `StreamEndpointService` | `GET /InfiniteDrive/Stream`: HLS manifest proxy + CDN redirect |
| `PlaybackTokenService` | HMAC-SHA256 stream token generation + validation |
| `DatabaseManager` | SQLite persistence (WAL mode, self-healing, schema v30) |
| `ResolverHealthTracker` | Circuit breaker: trips on 5 consecutive failures |
| `CooldownGate` | Profile-aware HTTP throttling (replaces scattered delays) |
| `StreamProbeService` | HEAD → GET-range: 500ms/probe, 1.5s total budget |
| `StrmWriterService` | Unified .strm write path (Sprint 156) |
| `NfoWriterService` | Centralized NFO authority (Sprint 356) |
| `SyncLock` | SemaphoreSlim: only one catalog sync or Marvin task at a time |

### Key Design Decisions

- **No cross-service ID translation at browse time** — IDs pass through to source addon's `/meta` endpoint. Cross-resolution happens lazily at sync time.
- **Dead streams sink, not drop** — probing reorders M3U8 so working streams come first; dead URLs remain as player fallback.
- **Emby does its own metadata** — NFO files contain only ID hints; Emby's scraper handles the rest.
- **HMAC over static keys** — all `.strm` URLs carry `PlaybackTokenService` signatures with configurable expiry.
- **Fail-closed security** — if `PluginSecret` is missing, streaming endpoints return 503 rather than unsigned URLs.

---

## Development

```bash
dotnet build -c Release
tail -f ~/emby-dev-data/logs/embyserver.txt
./emby-reset.sh
```

See `CLAUDE.md` for AI-assisted development workflow, `REPO_MAP.md` for codebase map.

---

## Version

**0.40.0.0** — "almost 0.42"

*(The answer is 42. We're still working on what the question is.)*

---

## License

MIT. See LICENSE file.
