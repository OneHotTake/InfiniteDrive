# EmbyStreams — Developer Guide

Full reference for developers. Read on demand — do not load at session start.

---

## What Is This?

**EmbyStreams** integrates [AIOStreams](https://github.com/Viren070/AIOStreams) into Emby Server as a `.dll` plugin:

1. Syncs a catalog from AIOStreams (movies, TV, season packs) into Emby as `.strm` files
2. Pre-resolves stream URLs to a local SQLite cache for sub-100ms playback startup
3. Provides Netflix-style Discover — browsing, searching, one-click "Add to Library"
4. Handles four-layer failover when streams are unavailable
5. Rotates playback API keys for security

**Key principle:** Everything runs inside the Emby process. No Docker, no sidecar services, no extra databases.

---

## Documentation Map

| Document | Purpose |
|----------|---------|
| `README.md` | Product overview, getting started (users) |
| `docs/RUNBOOK.md` | Dev server setup, troubleshooting (developers) |
| `docs/HISTORY.md` | Sprint history, release notes (archival) |
| `docs/SECURITY.md` | Threat model, API key rotation |
| `docs/getting-started.md` | First-time user setup |
| `docs/configuration.md` | Full config reference |
| `docs/troubleshooting.md` | Common issues |
| `docs/failure-scenarios.md` | Ops reference |
| `docs/features/discover.md` | Discover feature internals |

---

## Folder Structure

```
Services/
├── PlaybackService.cs          # Resolve .strm → debrid CDN URLs
├── CatalogSyncService.cs       # Sync catalogs from AIOStreams/Trakt/MDBList
├── CatalogDiscoverService.cs   # Sync catalogs for Discover feature
├── DiscoverService.cs          # REST API for Discover (browse/search/play)
└── ...
Data/
├── DatabaseManager.cs          # SQLite schema V15, migrations, queries
└── ...
Models/
├── CatalogItem.cs              # .strm file + metadata
├── DiscoverEntry.cs            # Discover catalog entry
└── ...
Configuration/
├── configurationpage.html      # Settings UI
├── configurationpage.js        # Settings logic
└── ...
```

---

## Architecture: Core Flows

**Adding a movie to library:**
1. User clicks "Add to Library" in Discover
2. `DiscoverService.AddToLibraryAsync()` → creates `.strm` file on disk
3. Updates database entry with metadata
4. Triggers targeted Emby library scan (not full rescan)
5. `.strm` file appears in Movies library

**Playing a `.strm` file:**
1. User clicks Play in Emby
2. Emby loads `.strm` file (contains HMAC-signed URL)
3. `PlaybackService` resolves URL → real debrid CDN URL from cache
4. Cache miss → queries AIOStreams, caches result (TTL 6h)
5. Returns URL to Emby client (proxy or redirect by client type)

**Discovering content:**
1. User opens Discover tab
2. `DiscoverService.Browse()` queries database
3. `DiscoverService.Search()` queries database + live AIOStreams API
4. Results merged and ranked (library items first)

---

## Key Concepts

### .strm Files

Plain text file containing a single HMAC-signed URL:
```
https://emby.example.com/EmbyStreams/Stream?id=tt0111161&sig=<hmac>&exp=<unix_ts>
```
`sig` = HMAC-SHA256 signature. `exp` = Unix expiry timestamp. Together they prove the request originated from Emby and has not expired.

### Database Schema (V15)

| Table | Purpose |
|-------|---------|
| `catalog_items` | `.strm` files synced from AIOStreams |
| `discover_catalog` | Discover catalog entries |
| `stream_candidates` | Ranked stream candidates with `stream_key`, `binge_group` |
| `stream_cache` | Pre-resolved debrid URLs (TTL 6h) |
| `config` | Plugin settings |

To reset: `rm ~/emby-dev-data/EmbyStreams/embystreams.db` and restart (runs all migrations).

### Configuration Sources

1. Plugin config UI → stored in Emby database
2. Environment variables (Docker deployments)
3. Sensible defaults for everything

---

## Common Tasks

### Add a New API Endpoint

1. Add method to `Services/DiscoverService.cs` (or relevant service)
2. Mark with `[HttpGet]`/`[HttpPost]` and route attribute
3. Register in `PluginServiceRegistration.cs` if needed
4. Test: `curl http://localhost:9100/EmbyStreams/MyEndpoint`

### Add a Database Migration

1. Increment schema version constant in `DatabaseManager.cs`
2. Add migration block in `ApplyMigrationsAsync()`
3. Add table/column definitions
4. Test by deleting the db file and restarting

### Debug a Playback Issue

```bash
ls -la /media/embystreams/movies/                          # strm file exists?
cat /media/embystreams/movies/SomeMovie.strm               # correct URL?
curl -v "http://localhost:9100/EmbyStreams/Stream?..."     # endpoint responds?
grep -i "hmac\|expired\|unauthorized" ~/emby-dev-data/logs/embyserver.txt
```

### Test Discover Feature

```bash
# Force catalog sync
curl -s -X POST http://localhost:9100/EmbyStreams/Trigger?task=catalog_discover

# Verify DB has entries
sqlite3 ~/emby-dev-data/EmbyStreams/embystreams.db \
  "SELECT COUNT(*) FROM discover_catalog;"

# Test search
curl -s "http://localhost:9100/EmbyStreams/Discover/Search?q=shawshank" | jq .
```

### Watchdog Orchestrator

```bash
systemctl --user status embystreams-watchdog.service
systemctl --user start embystreams-watchdog.service
```

Reads tasks from `.ai/TASK_QUEUE.json` every 30s. Routes: free cloud → local Ollama → Claude.
Full docs: *(documentation pending)*

---

## Testing Checklist

- [ ] `dotnet build -c Release` — 0 errors, 0 warnings
- [ ] `./start-dev-server.sh` — server reachable at http://localhost:9100
- [ ] Config page loads: `/web/configurationpage?name=EmbyStreams`
- [ ] No errors: `tail -100 ~/emby-dev-data/logs/embyserver.txt | grep -i error`
- [ ] Feature smoke test:
  - Catalog sync change → verify `.strm` file created
  - Playback change → verify URL resolves
  - Discover change → verify search returns results

---

## Principles

1. **Self-contained** — no Docker, no sidecars, everything in the Emby process
2. **Secure by default** — HMAC signatures, SSRF guards, timing-safe auth, token redaction
3. **Resilient** — four-layer failover, automatic retries, cache fallbacks
4. **Simple config** — wizard does the heavy lifting
5. **Transparent** — health dashboard, debug inspector, full logs

---

## Getting Help

- Plugin won't load → `docs/RUNBOOK.md` "Common Issues"
- Test data → use hosted AIOStreams in the wizard
- Emby API questions → [Emby plugin docs](https://github.com/MediaBrowser/Emby)

