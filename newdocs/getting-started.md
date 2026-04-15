# Getting Started with InfiniteDrive

---

## Prerequisites

- **Emby Server** 4.10.0.6+ (check with **Help → About**)
- **AIOStreams instance** (self-hosted or via [DuckKota's wizard](https://duckkota.gitlab.io/stremio-tools/quickstart/))
- **Active debrid subscription** (Real-Debrid, TorBox, AllDebrid, Premiumize, etc.) configured in AIOStreams

---

## Step 1: Get Your AIOStreams Manifest URL

### Option A: DuckKota's Hosted Wizard (fastest)

1. Open [duckkota.gitlab.io/stremio-tools/quickstart/](https://duckkota.gitlab.io/stremio-tools/quickstart/)
2. Enter your debrid API key
3. Choose quality preferences (or accept defaults)
4. Copy the manifest URL — format: `https://host/stremio/uuid/token/manifest.json`

### Option B: Self-Host AIOStreams

1. Install AIOStreams via Docker or binary: [github.com/Viren070/AIOStreams](https://github.com/Viren070/AIOStreams)
2. Configure your debrid service and addon preferences in AIOStreams web UI
3. Copy your manifest URL from: **AIOStreams UI → Settings → Manifest**

---

## Step 2: Install the Plugin

**Plugin folder:** `InfiniteDrive/` (NOT `EmbyStreams/`)

1. Build: `dotnet publish -c Release`
2. Create the plugin directory:
   ```bash
   # Linux
   mkdir -p /var/lib/emby/plugins/InfiniteDrive

   # Windows
   mkdir "C:\ProgramData\Emby-Server\plugins\InfiniteDrive"
   ```
3. Copy from `bin/Release/net8.0/publish/`:
   - `InfiniteDrive.dll`
   - `plugin.json`
   - All `.dll` dependencies (from publish output)

4. Restart Emby Server:
   ```bash
   systemctl restart emby-server
   ```

5. Verify: **Dashboard → Plugins** → InfiniteDrive appears

---

## Step 3: Configure Emby API Key

1. In Emby Dashboard: **Settings → API Keys → Add**
2. Name it "InfiniteDrive" and copy the key
3. In InfiniteDrive settings: paste this key as **EmbyApiKey**

---

## Step 4: Initial Configuration

Open the config page:
```
http://localhost:8096/web/configurationpage?name=InfiniteDrive
```

1. **Providers tab:** Paste your AIOStreams manifest URL
   - The plugin auto-extracts base URL, UUID, and token
2. **Libraries tab:** Set storage paths (create folders first; add as Emby libraries)
   - Movies: `/media/infinitedrive/movies`
   - TV Shows: `/media/infinitedrive/shows`
3. **Run catalog sync:** Health tab → Force Sync

---

## Step 5: Verify Playback

1. Open Emby and browse to your Movies or TV Shows library
2. Click Play on any title
3. The stream resolves via `/InfiniteDrive/Resolve` and plays from your debrid CDN

### What happens in the background

| Time | Event |
|------|-------|
| ~30 s | First `.strm` files appear in Emby library |
| ~5 min | Popular titles pre-cached for instant playback |
| ~1 hour | Full catalog cached; all titles play instantly |

---

## Next Steps

### Enable Discover UI (optional)
Browse and search at:
```
http://localhost:8096/web/configurationpage?name=InfiniteDiscover
```

### Configure Failover (optional)
Add a **SecondaryManifestUrl** in the Providers tab for redundancy.

### Adjust Sync Schedule (optional)
Default: 3 AM UTC. Change in the Sources tab or set `SyncScheduleHour` to `-1` to disable auto-sync.

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Library is empty | Health tab → Force Sync |
| Play gives error | Verify AIOStreams is reachable; check debrid subscription is active |
| Stream starts then dies | CDN URLs expire ~6h; cache auto-refreshes |
| Plugin not visible | Verify `InfiniteDrive.dll` + `plugin.json` are in `plugins/InfiniteDrive/` folder |
| "PluginSecret empty" warning | Navigate to config page once to trigger auto-generation |

For more help, see **[Troubleshooting](./troubleshooting.md)**.

---

## Configuration Reference

### Required Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `PrimaryManifestUrl` | (empty) | Full AIOStreams manifest URL |
| `EmbyBaseUrl` | `http://127.0.0.1:8096` | URL Emby listens on (written in `.strm` files) |
| `EmbyApiKey` | (empty) | Emby API key for `.strm` authentication |
| `SyncPathMovies` | `/media/infinitedrive/movies` | Movie `.strm` storage |
| `SyncPathShows` | `/media/infinitedrive/shows` | Series `.strm` storage |

### Optional Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `SecondaryManifestUrl` | (empty) | Backup AIOStreams for failover |
| `EnableBackupAioStreams` | `false` | Enable secondary failover |
| `ProxyMode` | `auto` | `auto` / `redirect` / `proxy` |
| `CacheLifetimeMinutes` | `360` | Stream URL cache TTL (30–1440) |
| `CatalogSyncIntervalHours` | `1` | Min hours between sync runs (1–24) |
| `ProviderPriorityOrder` | `realdebrid,torbox,...` | Provider ranking |
| `DefaultSlotKey` | `hd_broad` | Default quality slot for playback |

See **[Configuration Guide](./configuration.md)** for the full reference.
