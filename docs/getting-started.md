# Getting Started with EmbyStreams

EmbyStreams syncs your AIOStreams catalog into Emby, enabling Netflix-style discovery and one-click playback from debrid services.

---

## Prerequisites

- **Emby Server** 4.8 or later
- **AIOStreams instance** (self-hosted or via [DuckKota's wizard](https://duckkota.gitlab.io/stremio-tools/quickstart/))
- **Active debrid subscription** (Real-Debrid, TorBox, Premiumize, or AllDebrid)

---

## Step 1: Get Your AIOStreams Manifest URL

### Option A: Use DuckKota's Hosted Wizard (fastest)

1. Open [duckkota.gitlab.io/stremio-tools/quickstart/](https://duckkota.gitlab.io/stremio-tools/quickstart/)
2. Enter your debrid API key
3. Choose quality preferences (or accept defaults)
4. Copy the manifest URL — it looks like: `https://aiostreams.example.com/stremio/uuid/token/manifest.json`

### Option B: Self-Host AIOStreams

1. Install AIOStreams via Docker or binary from [github.com/Viren070/AIOStreams](https://github.com/Viren070/AIOStreams)
2. Configure your debrid service and addon preferences in AIOStreams web UI
3. Copy your manifest URL from: **AIOStreams UI → Settings → Manifest**

---

## Step 2: Install the Plugin

1. Create the plugin folder:
   ```bash
   # Linux
   mkdir -p /var/lib/emby/plugins/EmbyStreams

   # Windows
   mkdir "C:\ProgramData\Emby-Server\plugins\EmbyStreams"
   ```

2. Copy all files from `bin/Release/net8.0/publish/`:
   - `EmbyStreams.dll`
   - `plugin.json`
   - All `.dll` dependencies (Microsoft.Data.Sqlite, SQLitePCLRaw, Newtonsoft.Json, System.* shims)

3. Ensure SQLite is available (Linux):
   ```bash
   apt-get install -y libsqlite3-0
   ```

4. Restart Emby Server:
   ```bash
   systemctl restart emby-server
   ```

5. Verify: Open **Emby Dashboard → Plugins** and confirm EmbyStreams appears.

---

## Step 3: Initial Setup via Wizard

1. Open **Emby Dashboard → Plugins → EmbyStreams** → **Settings**

2. Run the **Setup Wizard** (first tab):
   - **Step 1:** Paste your manifest URL from Step 1
     - The plugin auto-extracts base URL, UUID, and token
   - **Step 2:** Set your media folders (create them on disk first; add as Emby libraries)
     - Movies: `/media/embystreams/movies` (or your preference)
     - TV Shows: `/media/embystreams/shows` (or your preference)
   - **Step 3:** Review settings
   - **Step 4:** Click **Save & Start Sync**

3. Wait ~1 minute — `.strm` files will appear in your Emby library

---

## Step 4: Verify Playback

1. Open Emby and browse to your Movies or TV Shows library
2. Click Play on any title
3. The stream resolves in <100 ms and plays directly from your debrid CDN

### What happens in the background

| Time | Event |
|------|-------|
| ~30 s | First `.strm` files appear in Emby library |
| ~5 min | Popular titles pre-cached for instant playback |
| ~1 hour | Full catalog cached; all titles play instantly |

---

## Next Steps

### Enable Discover (optional)

The plugin includes a Discover feature for browsing and searching the catalog. This is accessed via REST API endpoints and can be integrated into third-party Emby clients.

### Configure Failover (optional)

For production setups, add a **Secondary AIOStreams instance** in the plugin settings for redundancy. This ensures playback continues if your primary instance goes down.

### Adjust Sync Schedule (optional)

By default, the plugin syncs catalogs at 3 AM UTC. Customize this in **Settings → Sync Schedule** if needed.

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Library is empty | Open Health Dashboard → click Force Sync |
| Play gives "Don't Panic" error | Verify AIOStreams is reachable; check debrid subscription is active |
| Stream starts then dies | Real-Debrid CDN URLs expire ~6h; cache will auto-refresh |
| No poster art | Wait for Emby's scraper to process; trigger library scan from Dashboard |
| Plugin not visible in Dashboard | Verify DLL and `plugin.json` are in correct subfolder; restart Emby |

For more detailed help, see **[Troubleshooting Guide](./troubleshooting.md)**.

---

## Configuration Reference

For a complete list of all settings, see **[Configuration Guide](./configuration.md)**.

### Key Settings

- **PrimaryManifestUrl** — Your AIOStreams manifest URL (configured in wizard)
- **SecondaryManifestUrl** — Optional backup AIOStreams for failover
- **SyncPathMovies / SyncPathShows** — Folders where `.strm` files are written (configured in wizard)
- **ProxyMode** — `auto` (recommended) / `redirect` / `proxy` for client compatibility
- **CacheLifetimeMinutes** — How long resolved URLs are cached (default: 6 hours)

---

## Next: Advanced Topics

- **[Discover Feature](./features/discover.md)** — Netflix-style browsing
- **[Failure Scenarios](./failure-scenarios.md)** — What happens when things break
- **[Security Model](../SECURITY.md)** — API key rotation, threat model
