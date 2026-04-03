# EmbyStreams — Troubleshooting Guide

This guide covers every operational issue you might encounter — from installation through day-to-day maintenance, including complete clean-slate procedures and database management.

---

## File System Layout

Understanding where files live is the first step in diagnosing problems.

### Plugin files

| File | Default location | Purpose |
|------|-----------------|---------|
| `EmbyStreams.dll` | `{PluginsDir}/EmbyStreams/EmbyStreams.dll` | Plugin binary |
| `plugin.json` | `{PluginsDir}/EmbyStreams/plugin.json` | Plugin metadata (required by Emby 4.8+) |
| Support DLLs | `{PluginsDir}/EmbyStreams/` | `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*.dll`, `Newtonsoft.Json.dll`, `System.*.dll` |

**Linux default:** `/var/lib/emby/plugins/EmbyStreams/`
**Windows default:** `C:\ProgramData\Emby-Server\plugins\EmbyStreams\`

### Configuration

| File | Default location |
|------|-----------------|
| `EmbyStreams.xml` | `{DataPath}/plugins/configurations/EmbyStreams.xml` |

**Linux default:** `/var/lib/emby/data/plugins/configurations/EmbyStreams.xml`
**Windows default:** `C:\ProgramData\Emby-Server\data\plugins\configurations\EmbyStreams.xml`

### Database

| File | Default location |
|------|-----------------|
| `embystreams.db` | `{DataPath}/EmbyStreams/embystreams.db` |
| `embystreams.db-shm` | same folder (SQLite WAL shared memory — temporary) |
| `embystreams.db-wal` | same folder (SQLite WAL log — temporary) |

**Linux default:** `/var/lib/emby/data/EmbyStreams/embystreams.db`
**Windows default:** `C:\ProgramData\Emby-Server\data\EmbyStreams\embystreams.db`

### .strm and .nfo files

Written to the paths you configure in `SyncPathMovies` and `SyncPathShows`.

**Defaults:**
- Movies: `/media/embystreams/movies`
- Shows: `/media/embystreams/shows`

**Movies structure:**
```
/media/embystreams/movies/
└── Dune (2021)/
    ├── Dune (2021).strm
    └── Dune (2021).nfo
```

**Shows structure:**
```
/media/embystreams/shows/
└── The Bear (2022)/
    ├── tvshow.nfo
    ├── Season 1/
    │   ├── The Bear - S01E01.strm
    │   ├── The Bear - S01E01.nfo
    │   ├── The Bear - S01E02.strm
    │   └── ...
    └── Season 2/
        └── ...
```

### Emby logs

**Linux:** `/var/log/emby/` (look for `embyserver.txt` or `embyserver-YYYYMMDD.txt`)
**Windows:** `C:\ProgramData\Emby-Server\logs\`

EmbyStreams log lines are prefixed with `[EmbyStreams]`.

```bash
# Tail EmbyStreams log lines in real time
journalctl -u emby-server -f | grep EmbyStreams

# Or from log file
tail -f /var/log/emby/embyserver.txt | grep EmbyStreams
```

---

## Installation Problems

### Plugin doesn't appear in Dashboard → Plugins

**Check 1: Is `plugin.json` present?**
```bash
ls /var/lib/emby/plugins/EmbyStreams/plugin.json
```
Emby 4.8+ requires `plugin.json` to recognise a plugin. If it's missing, the plugin folder is ignored entirely.

**Check 2: Is the DLL in a subfolder?**
The entire `EmbyStreams/` folder must be inside the plugins directory. Placing `EmbyStreams.dll` directly in the plugins root will not work.

**Check 3: Emby was restarted?**
```bash
systemctl restart emby-server
```

**Check 4: Windows DLL blocked?**
On Windows, downloaded DLLs may be blocked by the OS. Right-click each DLL → Properties → Unblock.

**Check 5: Check Emby startup logs**
```bash
journalctl -u emby-server | grep -i "embystreams\|plugin\|error"
```

---

### Plugin loads but throws on startup

**Symptom:** Plugin appears briefly then crashes, or Health Dashboard shows errors immediately.

**Check: SQLite library present?**
```bash
apt-get install -y libsqlite3-0   # Debian/Ubuntu
```

**Check: All support DLLs present?**
The publish directory must contain not just `EmbyStreams.dll` but also:
- `Microsoft.Data.Sqlite.dll`
- `SQLitePCLRaw.core.dll`
- `SQLitePCLRaw.nativelibrary.dll`
- `SQLitePCLRaw.provider.dynamic_cdecl.dll`
- `Newtonsoft.Json.dll`

Check logs for `DllNotFoundException` or `FileNotFoundException`.

---

## Empty Library

### No items appear after first sync

**Step 1: Open Health Dashboard**
Dashboard → Plugins → EmbyStreams → Health Dashboard tab. Check:
- AIOStreams connection status (green = connected)
- Last sync time and item count
- Any error messages

**Step 2: Verify AIOStreams connection**
```bash
curl -s "http://your-aiostreams-host:7860/stremio/manifest.json"
```
Should return JSON. If it times out, AIOStreams is not reachable from the Emby host.

**Step 3: Verify the Emby library path**
The `SyncPathMovies` and `SyncPathShows` directories must exist **and** be configured as Emby library paths. Creating the directories is not enough — you must add them as libraries in Emby.

**Step 4: Force a sync**
In the Health Dashboard, click **Force Sync**, or:
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=force_sync" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=catalog_sync" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

**Step 5: Check for .strm files on disk**
```bash
find /media/embystreams/movies -name "*.strm" | head -5
```
If .strm files exist but Emby doesn't show them, trigger a library scan in Emby Dashboard → Libraries.

---

### Items in library but wrong metadata / missing posters

**Cause:** Emby's scraper couldn't match the filename to a TMDB entry.

**Fix 1: Verify .nfo files exist**
```bash
ls /media/embystreams/movies/Dune\ \(2021\)/
# Should show both Dune (2021).strm and Dune (2021).nfo
```

**Fix 2: Inspect the .nfo content**
```bash
cat "/media/embystreams/movies/Dune (2021)/Dune (2021).nfo"
```
Should contain `<uniqueid type="imdb">tt1160419</uniqueid>`. If the file is empty or missing the IMDB tag, enable `EnableNfoHints = true` in settings and re-sync.

**Fix 3: Trigger metadata refresh in Emby**
In Emby, right-click the item → Refresh Metadata → Replace All Metadata.

**Fix 4: Wait for MetadataFallbackTask**
If `EnableMetadataFallback = true`, the daily background task will fetch full metadata (including poster URL) from Cinemeta for any item that Emby's scraper couldn't match. Check the Health Dashboard for last run time.

---

## Playback Problems

### "No stream available" or Don't Panic error page

**Step 1: Check Health Dashboard**
Look at "AIOStreams Status" and the recent errors list.

**Step 2: Test AIOStreams directly**
```bash
# Replace with your actual imdb ID and AIOStreams URL
curl -s "http://your-aiostreams-host:7860/stremio/UUID/TOKEN/stream/movie/tt1160419.json"
```
If this returns `{"streams":[]}`, AIOStreams has no streams for this item — typically because the torrent isn't in your debrid service's cache.

**Step 3: Check your debrid subscription**
Log into your debrid service directly and verify the subscription is active and not expired.

**Step 4: Inspect a specific item**
```bash
curl -s "http://localhost:8096/EmbyStreams/Inspect?imdb=tt1160419" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```
Shows cached URL, expiry, quality, all ranked candidates, and last resolution time.

**Step 5: Force re-resolve**
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Invalidate" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""  \
     -H "Content-Type: application/json" \
     -d '{"imdb":"tt1160419"}'
```
Then press Play again — this forces a fresh AIOStreams call.

---

### Video stutters or buffers

**Cause A: Client cannot follow HTTP 302 redirects to external hosts**
Samsung TV, LG webOS, and some Android TV devices cannot follow redirects to external CDN URLs.

**Fix:** Change `ProxyMode` to `proxy` in plugin settings. The stream will be routed through the Emby server, which adds overhead but works for all clients.

**Cause B: Bitrate exceeds network capacity**
4K HDR streams can be 40–80 Mbps. If your internet uplink or LAN is too slow for the CDN download rate, the stream will stutter.

**Fix:** Use AIOStreams quality filters to cap resolution, or switch the debrid service to a CDN region closer to your server.

**Cause C: CDN URL expired mid-stream**
The debrid CDN URL expired while the stream was playing (typically > 4h after generation).

**Fix:** Stop and restart playback — this triggers a fresh URL resolution. The client-compat learning system will remember the safe bitrate for next time.

---

### Video plays but seeking fails

**Cause:** The stream source does not support `Accept-Ranges: bytes` (uncommon for debrid streams, but possible for some usenet/http sources).

**Diagnosis:** Check the `stream_url` field in the Inspect endpoint. If the URL is from Easynews or a direct HTTP source, seeking may not be supported.

**Fix:** There is no plugin-side fix — this is a limitation of the source. Switch to a different quality tier or provider in AIOStreams.

---

## .nfo File Management

### What are .nfo files?

`.nfo` files are Kodi-format metadata hints written by EmbyStreams. They contain XML with IMDB/TMDB IDs that tell Emby's built-in scraper exactly which database entry to use.

There are two types:

**1. Minimal ID hint** (written by `EnableNfoHints` at sync time):
```xml
<?xml version="1.0" encoding="utf-8"?>
<movie>
  <uniqueid type="imdb" default="true">tt1160419</uniqueid>
  <uniqueid type="tmdb">438631</uniqueid>
</movie>
```

**2. Rich .nfo** (written by `MetadataFallbackTask` for items Emby can't match):
```xml
<?xml version="1.0" encoding="utf-8"?>
<movie>
  <title>Dune: Part One</title>
  <year>2021</year>
  <plot>Paul Atreides, a brilliant and gifted young man...</plot>
  <uniqueid type="imdb" default="true">tt1160419</uniqueid>
  <thumb>https://image.tmdb.org/t/p/w500/d5NXSklXo0qyIYkgV94XAgMIckY.jpg</thumb>
  <genre>Science Fiction</genre>
  <genre>Adventure</genre>
</movie>
```

### Regenerating .nfo files

If `.nfo` files are missing or corrupted:

1. Enable `EnableNfoHints = true` in settings
2. Run a catalog sync — sync writes `.nfo` alongside new `.strm` files but does NOT overwrite existing `.nfo` files by default
3. To regenerate **all** `.nfo` files: run a Purge Catalog (see below) which wipes both `.strm` and `.nfo` files, then re-sync

### .nfo files are not being written

**Check:** Is `EnableNfoHints = true` in plugin settings?

**Check:** Does the Emby process have write permission to `SyncPathMovies` and `SyncPathShows`?
```bash
sudo -u emby touch /media/embystreams/movies/test_write
```

### Emby ignores .nfo files

**Check Emby setting:** Emby → Dashboard → Libraries → Edit library → NFO/Kodi settings.
Emby must have "Kodi nfo" readers enabled for the library type.

**Check file name:** Movie `.nfo` must have the same base name as the `.strm` file. Series `.nfo` must be named `tvshow.nfo` in the series root folder.

---

## Database Management

### Viewing database statistics

Open the Health Dashboard in the EmbyStreams plugin page. The **DB Stats** card shows:
- Total catalog items
- Active items (not soft-deleted)
- Resolution cache entries
- Stream candidates stored
- Database file size
- Last VACUUM time

Via API:
```bash
curl -s "http://localhost:8096/EmbyStreams/Status" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

### Clearing the resolution cache

Clears all cached stream URLs and their ranked candidates. Items will still appear in your library but will need to re-resolve at play time.

**Via UI:** Health Dashboard → Settings tab → **Clear Resolution Cache** button

**Via API:**
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=clear_cache" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

This also runs SQLite VACUUM automatically.

**When to use:**
- You've changed your AIOStreams quality filters and want all items to resolve with the new preferences
- You suspect stale URLs are causing widespread playback failures
- After changing `CandidatesPerProvider` and wanting fresh candidates

---

### Clearing the client compatibility profiles

Removes all learned per-device proxy/redirect preferences. Each device will re-learn on the next play.

**Via API:**
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=clear_client_profiles" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

**When to use:** After changing clients (new TV, new device), or if `ProxyMode = auto` is making wrong decisions for a device.

---

### Purging the catalog (keep resolution cache)

Removes all catalog items and sync state from the database, then deletes all `.strm` and `.nfo` files from disk. The resolution cache is preserved — if you re-sync the same catalog, existing cached URLs are reused.

**Via API:**
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=purge_catalog" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

After completion, run a catalog sync to repopulate:
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=catalog_sync" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

**When to use:**
- You changed `SyncPathMovies` or `SyncPathShows` — old `.strm` files reference the wrong path
- You changed `EmbyBaseUrl` — all `.strm` files contain the wrong playback URL
- The catalog is in a stale or inconsistent state and you want to start fresh
- After moving Emby to a new server with different paths

---

### Full clean-slate reset (nuclear option)

Removes **everything**: catalog items, resolution cache, stream candidates, playback log, client profiles, sync state, and all `.strm`/`.nfo` files on disk. The database structure is preserved (schema is not dropped).

**Via API:**
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=reset_all" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

This automatically runs SQLite VACUUM at the end.

Then re-run the wizard if needed:
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=reset_wizard" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

Then re-sync:
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=catalog_sync" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

**When to use:**
- Migrating to a completely new configuration
- Persistent database issues that survive a cache clear
- Starting over after changing debrid providers entirely

---

### Manually deleting the database

If the plugin won't start due to database corruption and the auto-recovery didn't work:

```bash
# Stop Emby first
systemctl stop emby-server

# Delete the database (plugin will recreate on next startup)
rm /var/lib/emby/data/EmbyStreams/embystreams.db
rm -f /var/lib/emby/data/EmbyStreams/embystreams.db-shm
rm -f /var/lib/emby/data/EmbyStreams/embystreams.db-wal

# Start Emby — plugin will initialise a fresh database automatically
systemctl start emby-server
```

> The plugin's `DatabaseManager.Initialise()` automatically runs `PRAGMA integrity_check` on startup and deletes + recreates the DB if corruption is detected. Manual deletion is usually not needed.

---

### Re-running the Setup Wizard

To show the wizard again (e.g. after changing the AIOStreams instance):

```bash
curl -X POST "http://localhost:8096/EmbyStreams/Trigger?task=reset_wizard" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

Or manually: open `EmbyStreams.xml` and set `<IsFirstRunComplete>false</IsFirstRunComplete>`, then restart Emby (or reload the plugin config page).

---

## API Budget Management

### Budget exceeded — resolver has stopped

**Symptom:** Health Dashboard shows "Daily API budget exceeded". New items don't resolve overnight.

**Fix 1 (immediate):** Increase `ApiDailyBudget` in plugin settings. Default is 2000; for large catalogs 5000–10000 is reasonable.

**Fix 2 (reduce consumption):**
- Reduce the number of AIOStreams catalogs synced (set specific `AioStreamsCatalogIds`)
- Reduce `CatalogItemCap` to limit items per catalog
- Set `NextUpLookaheadEpisodes = 0` to disable episode pre-warming

**Note:** On-demand playback (cache miss at play time) is **never** subject to the budget limit — it always resolves.

---

## High Availability / Failover

### Simulate failover dry-run

Test all three layers without affecting any cached data:

1. Open the High Availability tab in the plugin settings
2. Enter an IMDB ID in the "Simulate Failover" input (e.g. `tt0111161`)
3. Click **Simulate Failover**

The results table shows which layers would succeed and why.

Via API:
```bash
curl -s "http://localhost:8096/EmbyStreams/TestFailover?imdb=tt0111161" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"YOUR_API_KEY\""
```

---

### AIOStreams is down — Layer 2 not configured

**Symptom:** AIOStreams goes down; all playback fails immediately.

**Fix:** Add a fallback AIOStreams URL in Settings → AIOStreams Connection → Fallback URLs.
The fastest option: use DuckKota to create a second manifest (takes 2 minutes) and paste it as the fallback.

---

### Layer 3 fails — "not instantly available"

**Symptom:** AIOStreams is down, fallback manifest also unreachable, debrid API keys configured, but Layer 3 still fails.

**Cause:** The torrent for the requested item is not in your debrid service's cache (has never been downloaded by any debrid user recently). Layer 3 is instant-only — it will never trigger a download.

**Fix:** None at runtime. Wait for AIOStreams to recover. Alternatively, add the item to your debrid provider's cache manually (via the debrid web UI).

---

## Webhook Integration

### Jellyseerr/Overseerr not triggering sync

**Check 1:** Is the webhook URL correct?
```
POST http://your-emby-host:8096/EmbyStreams/Webhook/Sync
```

**Check 2:** Is `WebhookSecret` configured?
If a secret is set, Jellyseerr/Overseerr must send it as `Authorization: Bearer <secret>` or via `X-Api-Key`.

**Check 3:** Test the webhook manually:
```bash
curl -X POST "http://localhost:8096/EmbyStreams/Webhook/Sync" \
     -H "Content-Type: application/json" \
     -d '{"notification_type":"MEDIA_APPROVED","media":{"imdbId":"tt15299712"}}'
```

---

## Diagnostic API Endpoints

All require admin authentication (`X-Emby-Authorization: MediaBrowser Token="YOUR_KEY"`).

| Endpoint | What it shows |
|----------|--------------|
| `GET /EmbyStreams/Status` | Full health snapshot: AIOStreams status, DB stats, recent errors, version |
| `GET /EmbyStreams/Inspect?imdb=tt1160419` | Cached URL, expiry, all candidates, last play, play count for one item |
| `GET /EmbyStreams/RawStreams?imdb=tt1160419` | Raw AIOStreams JSON response for an item — useful to check what streams AIOStreams is returning |
| `GET /EmbyStreams/Search?q=dune` | Search catalog items by title |
| `GET /EmbyStreams/Catalogs` | All AIOStreams catalogs found in the manifest |
| `GET /EmbyStreams/UnhealthyItems` | Items in permanent failed-resolution state |
| `GET /EmbyStreams/TestFailover?imdb=tt1160419` | Dry-run all three failover layers |

---

## Complete Task Reference

All available trigger keys for `POST /EmbyStreams/Trigger?task={key}`:

| Task key | What it does |
|----------|-------------|
| `catalog_sync` | Run catalog sync immediately (all sources) |
| `link_resolver` | Run background URL pre-resolution pass |
| `file_resurrection` | Write `.strm` files for DB items that are missing them |
| `library_readoption` | Detect real media files and optionally remove redundant `.strm` files |
| `episode_expand` | Expand next-episode queue for active series |
| `force_sync` | Reset sync interval guard — next catalog sync fetches all sources regardless of `CatalogSyncIntervalHours` |
| `clear_cache` | Delete all resolution cache entries + VACUUM |
| `dead_link_scan` | Range-probe all valid cached URLs; mark stale those returning 4xx |
| `clear_client_profiles` | Delete all learned per-device proxy/redirect preferences |
| `purge_catalog` | ⚠️ Delete all catalog items from DB + delete all `.strm`/`.nfo` files from disk |
| `reset_all` | ⚠️ Delete everything (catalog + cache + logs) + delete all `.strm`/`.nfo` files from disk + VACUUM |
| `reset_wizard` | Reset `IsFirstRunComplete` to show Setup Wizard again |

> **⚠️ Warning:** `purge_catalog` and `reset_all` are irreversible. The database rows are hard-deleted; disk files are permanently removed. Run `catalog_sync` after either to repopulate.

---

## Getting an API Token for Curl

To use the diagnostic/trigger endpoints from the command line, you need an Emby admin API token.

**Option 1:** Use the Emby Dashboard
Dashboard → Advanced → API Keys → New API Key → copy the token.

**Option 2:** From `EmbyStreams.xml`
The config page uses the Emby session token when you're logged in. The API key from Dashboard → API Keys is easier for scripting.

**Using the token:**
```bash
curl -s "http://localhost:8096/EmbyStreams/Status" \
     -H "X-Emby-Authorization: MediaBrowser Token=\"abc123yourtokenhere\""
```
