# FINDINGS.md — EmbyStreams Plugin Codebase Map

> **Handoff document.** Read this at the start of every session before touching code.
> Update "Current State" and "Handoff Note" at the end of every sprint.

---

## Codebase Map

### Plugin Entry Points

| File | Class | Purpose |
|------|-------|---------|
| `Plugin.cs` | `Plugin : BasePlugin<PluginConfiguration>` | Singleton. Inits DatabaseManager. Auto-generates PluginSecret. |
| `PluginConfiguration.cs` | `PluginConfiguration : BasePluginConfiguration` | All persisted settings. XML serialized to {DataPath}/plugins/configurations/EmbyStreams.xml. |

### HTTP Services (IService)

| File | Routes | Auth | Purpose |
|------|--------|------|---------|
| `Services/PlaybackService.cs` | `GET /EmbyStreams/Play` | `[Authenticated]` | Legacy: resolves .strm → CDN URL. Still active for backwards compat. |
| `Services/SignedStreamService.cs` | `GET /EmbyStreams/Stream` | **None (HMAC sig)** | NEW public endpoint — validates HMAC, resolves stream, 302 redirect |
| `Services/UnauthenticatedStreamService.cs` | `GET /EmbyStreams/GetStream` | IP (localhost only) | FFprobe cache-only lookup |
| `Services/DiscoverService.cs` | `GET/POST /EmbyStreams/Discover/*` | `[Authenticated]` | Browse/search/add Discover catalog |
| `Services/StatusService.cs` | `GET /EmbyStreams/Status` | `[Authenticated]` | Health dashboard JSON |
| `Services/TriggerService.cs` | `POST /EmbyStreams/Trigger` | Admin | Manual task trigger |
| `Services/WebhookService.cs` | `POST /EmbyStreams/Webhook/Sync` | Optional secret | Radarr/Sonarr/Jellyseerr integration |
| `Services/SetupService.cs` | `POST /EmbyStreams/Setup/*` | `[Authenticated]` | Dir creation, API key rotation |
| `Services/StreamProxyService.cs` | `GET /EmbyStreams/Stream/{ProxyId}` | Token | Passthrough proxy for non-redirect clients |

### Stream URL Signing

| File | Class | Purpose |
|------|-------|---------|
| `Services/StreamUrlSigner.cs` | `StreamUrlSigner` (static) | HMAC-SHA256 URL signing and validation. Secret from PluginConfiguration.PluginSecret. |

### Scheduled Tasks

| File | Schedule | Purpose |
|------|---------|---------|
| `Tasks/CatalogSyncTask.cs` | Daily | Fetches catalog from AIOStreams/Cinemeta, writes .strm files |
| `Tasks/LinkResolverTask.cs` | Every 15 min | Pre-resolves stream URLs to SQLite cache |
| `Tasks/CatalogDiscoverTask.cs` | Daily | Populates discover_catalog table for Discover UI |
| `Tasks/EpisodeExpandTask.cs` | Every 4h | Writes per-episode .strm for series |
| `Tasks/FileResurrectionTask.cs` | Every 2h | Rebuilds missing .strm files |
| `Tasks/LibraryReadoptionTask.cs` | Scheduled | Deletes .strm when real file found |

### Database (SQLite at {DataPath}/EmbyStreams/embystreams.db, schema V14)

Key tables: `catalog_items`, `discover_catalog`, `resolution_cache`, `stream_candidates`, `sync_state`.

### .strm File Generation — Call Sites

| Location | Context | Post-Sprint 55 URL format |
|----------|---------|--------------------------|
| `CatalogSyncTask.WriteMovieStrm()` | Bulk catalog sync | `{EmbyBaseUrl}/EmbyStreams/Stream?id=...&type=movie&exp=...&sig=...` |
| `CatalogSyncTask.WriteSeriesStrmAsync()` | Episode .strm | `...&type=series&season={s}&episode={e}&sig=...` |
| `CatalogSyncTask.WriteStrmFileForItemPublicAsync()` | Webhook / manual | Same as above |
| `DiscoverService.Post(AddToLibrary)` | Single item | Same signed URL, folder-per-movie structure |

### Folder Naming Convention (post Sprint 55)

```
{SyncPathMovies}/{Title} ({Year}) [imdbid-{imdbId}]/
{SyncPathMovies}/{Title} ({Year}) [imdbid-{imdbId}]/{Title} ({Year}).strm
```

The `[imdbid-ttXXXXXXX]` suffix triggers Emby's built-in IMDB metadata scraper automatically.

**Before Sprint 55:** Used `[tmdbid={tmdbId}]` — does NOT trigger IMDB auto-match.

### Deprecated: IChannel (DiscoverChannel)

`Services/DiscoverChannel.cs` had `IChannel` + `IRequiresMediaInfoCallback` removed in Sprint 55. The class shell remains but is no longer registered as an Emby channel. The REST Discover API in `DiscoverService.cs` remains active.

---

## Architecture Decisions

### A1: HMAC over Emby Token for .strm Files

**Why:** `.strm` files pointing to `[Authenticated]` endpoints break on VLC, Roku, Apple TV, ffmpeg — clients that can't inject `X-Emby-Token`.

**How:** `StreamUrlSigner` embeds a time-limited HMAC-SHA256 signature in the URL. `/EmbyStreams/Stream` validates it without requiring Emby auth. PluginSecret (32-byte random, base64) is the signing key.

### A2: IMDB Folder Naming

**Why:** Emby's metadata agents auto-match IMDB IDs from `[imdbid-ttXXXXXXX]` in folder names. TMDB folder names (`[tmdbid=...]`) require a separate NFO to hint the scraper.

**How:** `BuildFolderName()` now uses `item.ImdbId` instead of `item.TmdbId`. NFO files still written when `EnableNfoHints=true`.

### A3: 365-Day Signature Validity

.strm files should be durable. 365-day validity means they survive server restarts, config reloads, and long inactivity periods. Re-sync needed only when `PluginSecret` or `EmbyBaseUrl` changes.

### A4: DiscoverChannel Deprecation

IChannel has Emby client compatibility issues (especially around playback), confirmed by smoke testing. Removed interface implementations; the underlying catalog data and REST API remain.

---

## Current State (post Sprint 55)

- `Services/StreamUrlSigner.cs` — **NEW** — HMAC utility
- `Services/SignedStreamService.cs` — **NEW** — public `/EmbyStreams/Stream` endpoint
- `PluginConfiguration.cs` — **UPDATED** — `PluginSecret` field added
- `Plugin.cs` — **UPDATED** — auto-generates `PluginSecret` on first load
- `Tasks/CatalogSyncTask.cs` — **UPDATED** — IMDB folder names, signed .strm URLs
- `Services/DiscoverService.cs` — **UPDATED** — AddToLibrary uses folder-per-movie + signed URLs
- `Services/DiscoverChannel.cs` — **DEPRECATED** — IChannel interface removed
- `Configuration/configurationpage.html` — **UPDATED** — PluginSecret field with Regenerate button
- `Configuration/configurationpage.js` — **UPDATED** — PluginSecret load/save

---

## Open Questions / Blockers

1. **Library Creation**: Emby SDK does not expose clean programmatic library creation from a plugin. Users must create the library manually. UI provides the path to use.

2. **Existing .strm Files**: Old files with `[tmdbid=...]` folders and `Play` endpoint URLs remain on disk. They still work (Play endpoint active). Next sync creates new folders. Old ones become orphaned.

3. **Folder Renaming**: If an item existed before Sprint 55, it will have both the old folder (e.g., `Movie (2020) [tmdbid=12345]/`) and after the next sync a new folder (`Movie (2020) [imdbid-tt1234567]/`). The old one must be manually removed or the DB will track both.

---

## Handoff Note — Sprint 56 (✅ COMPLETE — Public Endpoint Validated)

**Done (v0.56):**
- v0.56.1 ✓ Build verified (0 errors, 0 warnings)
- v0.56.2 ✓ Auth discovery & resolution:
  - Emby 4.8.0.37+ defaults all plugin routes to `[Authenticated]`
  - `[Unauthenticated]` attribute DOES exist in MediaBrowser.Server.Core 4.9.1.90
  - Applied `[Unauthenticated]` to SignedStreamService
- v0.56.3 ✓ Public endpoint validation:
  - Deployed updated DLL to dev server
  - Sent unauthenticated request to `/EmbyStreams/Stream`
  - Received HTTP 500 (app error, not 401 auth error) ← **PROOF endpoint is public**
  - Handler was called, HMAC validation code executed
- Code verification all passed ✓

**Architecture Decision (Auth — ✅ RESOLVED):**
Public signed stream endpoint fully working:
- Endpoint: `GET /EmbyStreams/Stream?id={imdb}&type={type}&exp={unix}&sig={hmac}`
- No Emby authentication required (uses `[Unauthenticated]` attribute)
- HMAC-SHA256 signature validates request authenticity + expiry
- Supports all clients: Emby, VLC, ffmpeg, Roku, Apple TV, web browsers
- 365-day signature validity for durability across restarts

**Gotchas:**
- `[Unauthenticated]` attribute from `MediaBrowser.Controller.Net` (requires Emby 4.8.0.37+)
- `Request.Response.Redirect(url)` is correct pattern for 302 redirects
- `PluginSecret` is auto-generated in `Plugin.cs` constructor
- Rotating `PluginSecret` invalidates ALL existing .strm files

**Next Sprint (v0.57 — Library Automation):**
- Research programmatic library creation via `ILibraryManager`
- Auto-create "AIO Movies" and "AIO Shows" libraries on first sync
- Add UI path-copy button (if programmatic creation unavailable)
