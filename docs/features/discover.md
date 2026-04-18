# Discover Feature - Complete Implementation Guide

## Overview

The Discover feature allows Emby users to:
- **Browse** a catalog of available streaming content from AIOStreams
- **Search** by title to find specific movies/shows
- **Add to Library** one-click to create .strm files and integrate into their library
- **Play** content directly from the sidebar

All with **zero additional configuration** beyond entering the AIOStreams URL during first setup.

## Architecture

### Plugin Registration (Automatic)

The following components are automatically discovered and registered by Emby via reflection:

| Component | Type | Purpose |
|-----------|------|---------|
| `DiscoverChannel` | `IChannel` | Sidebar integration for browsing/searching |
| `DiscoverService` | `IService` | REST API endpoints for Discover operations |
| `CatalogDiscoverTask` | `IScheduledTask` | Scheduled sync from AIOStreams (daily 4 AM) |
| `DiscoverInitializationService` | `IServerEntryPoint` | Auto-trigger on startup |

**No registration code needed** — Emby's plugin loader finds them automatically.

### Database Schema

**discover_catalog table** (SQLite):
```sql
CREATE TABLE discover_catalog (
    id                  TEXT PRIMARY KEY,      -- aio:{type}:{imdbid}
    imdb_id             TEXT NOT NULL,         -- tt1234567
    title               TEXT NOT NULL,
    year                INTEGER,
    media_type          TEXT NOT NULL,         -- 'movie' or 'series'
    poster_url          TEXT,
    backdrop_url        TEXT,
    overview            TEXT,
    catalog_source      TEXT NOT NULL,         -- Which AIOStreams catalog
    is_in_user_library  INTEGER NOT NULL DEFAULT 0,
    added_at            TEXT NOT NULL,         -- ISO 8601 timestamp
    updated_at          TEXT NOT NULL
);
```

This is **separate** from `catalog_items` (which tracks user-added items with .strm paths).

### Data Flow

```
AIOStreams API
    ↓ (fetches catalog)
CatalogDiscoverService
    ↓ (parses metadata)
discover_catalog table
    ↓ (queries)
DiscoverChannel (sidebar)  OR  DiscoverService (REST API)
    ↓ (converts to UI format)
ChannelItemInfo / DiscoverItem
    ↓ (displays)
Emby Sidebar / REST Response
```

## User Experience Flow

### Initial Setup (First Run)

1. **User enters AIOStreams URL** in plugin settings
2. **Clicks Save**
3. **Server starts/restarts**
4. `DiscoverInitializationService.Run()` executes:
   - Detects empty catalog + configured URL
   - Auto-triggers `CatalogDiscoverService.SyncDiscoverCatalogAsync()`
   - Runs in background (non-blocking)
5. **Discover appears in sidebar** with populated content
6. **User sees catalog** without any additional action

### Browsing/Searching (Normal Use)

1. User clicks **Discover** in sidebar
2. Emby calls `DiscoverChannel.GetChannelItems()`
3. Channel queries `discover_catalog` table
4. Results converted to `ChannelItemInfo`
5. User sees Netflix-like grid of content

### Adding to Library

1. User clicks **"Add to Library"** on an item
2. Request sent to `POST /EmbyStreams/Discover/AddToLibrary`
3. `DiscoverService.Post()` executes:
   - Validates request (ImdbId, Type, Title required)
   - Creates .strm file in `SyncPathMovies` or `SyncPathShows`
   - Creates `CatalogItem` database entry
   - Updates `discover_catalog` to mark `is_in_user_library = true`
   - **Auto-triggers library refresh** (fire-and-forget)
4. After brief delay, Emby indexes the file
5. Item appears in **Movies** or **TV Shows** library
6. User can click **Play** like any other item

### Playing Content

When user clicks Play on an added item:
1. Emby loads the .strm file
2. Opens URL: `/EmbyStreams/Play?imdb={imdbId}`
3. PlaybackService resolves the stream
4. User watches via Real-Debrid

## REST API Endpoints

All endpoints return JSON responses.

### Browse Available Catalog

```
GET /EmbyStreams/Discover/Browse?limit=20&offset=0
```

**Response:**
```json
{
  "items": [
    {
      "imdbId": "tt0371746",
      "title": "Iron Man",
      "year": 2008,
      "mediaType": "movie",
      "posterUrl": "https://...",
      "backdropUrl": "https://...",
      "overview": "...",
      "inLibrary": false,
      "catalogSource": "aiostreams",
      "audioLanguages": null
    }
  ],
  "total": 5243,
  "offset": 0
}
```

The `audioLanguages` field is populated from `stream_candidates.languages` for items that have been previously resolved/played. It contains comma-separated ISO 639-1 codes (e.g. `"ja,en"`). Items never played will have `null`.

### Search Catalog

```
GET /EmbyStreams/Discover/Search?q=batman&type=movie
```

Returns same `DiscoverItem` format, up to 50 results.

### Get Item Details

```
GET /EmbyStreams/Discover/Detail?imdbId=tt0371746
```

**Response:**
```json
{
  "item": {
    "imdbId": "tt0371746",
    "title": "Iron Man",
    "year": 2008,
    "mediaType": "movie",
    "posterUrl": "https://...",
    "backdropUrl": "https://...",
    "overview": "...",
    "inLibrary": false,
    "catalogSource": "aiostreams",
    "audioLanguages": "en"
  }
}
```

### Add to Library

```
POST /EmbyStreams/Discover/AddToLibrary?imdbId=tt0371746&type=movie&title=Iron%20Man&year=2008
```

**Response:**
```json
{
  "ok": true,
  "strmPath": "/media/embystreams/movies/Iron Man (2008).strm",
  "error": null
}
```

Or on error:
```json
{
  "ok": false,
  "strmPath": null,
  "error": "Item is already in library"
}
```

## Manual Trigger Support

User can manually trigger sync via dashboard:

```
POST /EmbyStreams/Trigger?task=catalog_discover
```

Response:
```json
{
  "status": "ok",
  "task": "catalog_discover"
}
```

Check logs at `~/emby-dev-data/logs/embyserver.txt` for sync progress.

## Configuration (Minimal)

Users **only need to configure**:

1. **AIOStreams URL** (in plugin settings)
2. **SyncPathMovies** (directory for movie .strm files)
3. **SyncPathShows** (directory for TV show .strm files)

Everything else is automatic:
- ✅ Catalog sync (on startup if empty)
- ✅ Library refresh (when items added)
- ✅ Scheduled daily sync (4 AM)
- ✅ Channel registration
- ✅ Search indexing

## Implementation Details

### Auto-Sync Logic (DiscoverInitializationService)

```csharp
// On server startup:
1. Checks if AIOStreams URL is configured
2. Checks if discover_catalog is empty
3. If both true: triggers CatalogDiscoverService.SyncDiscoverCatalogAsync()
4. Runs in background (non-blocking)
5. If catalog already populated: skips (idempotent)
```

### Auto-Library-Refresh Logic (DiscoverService)

```csharp
// After creating .strm file:
1. Writes file to disk
2. Creates CatalogItem database entry
3. Updates discover_catalog is_in_user_library flag
4. Queues background task:
   - Waits 100ms (for file system to settle)
   - Calls _libraryManager.ValidateMediaLibrary()
   - Returns immediately (user doesn't wait)
5. Emby detects new file and indexes it
```

### Sync Process (CatalogDiscoverService)

```csharp
1. Creates AioStreamsClient(config.AioStreamsUrl)
2. Fetches manifest:
   GET {url}/manifest.json
3. Iterates through catalogs in manifest
4. For each catalog (e.g., "movies"):
   - Clears previous entries by source
   - Fetches catalog items:
     GET {url}/catalog/movie/{catalogId}.json
   - Converts AioStreamsMeta to DiscoverCatalogEntry
   - Parses year from ReleaseInfo
   - Checks if in user library via catalog_items table
   - Upserts to discover_catalog
5. Logs total items synced
```

## Testing Guide

### Prerequisites

```bash
cd /home/geoff/embyStreams
./start-dev-server.sh
```

Server runs on http://localhost:9100

### Test 1: Initial Auto-Sync

1. Verify `discover_catalog` is empty:
   ```sql
   SELECT COUNT(*) FROM discover_catalog;
   -- Should return 0
   ```

2. Check logs for auto-sync:
   ```bash
   tail -f ~/emby-dev-data/logs/embyserver.txt | grep Discover
   ```

3. Wait 30 seconds, re-check count:
   ```sql
   SELECT COUNT(*) FROM discover_catalog;
   -- Should be > 0
   ```

**Expected**: Catalog populated automatically on startup.

### Test 2: Browse Endpoint

```bash
curl "http://localhost:9100/EmbyStreams/Discover/Browse?limit=10"
```

**Expected**: JSON with 10 items, total count, and offset.

### Test 3: Search Endpoint

```bash
curl "http://localhost:9100/EmbyStreams/Discover/Search?q=batman"
```

**Expected**: JSON with matching items (title contains "batman").

### Test 4: Add to Library

```bash
# First, find an IMDB ID from browse/search results
IMDB_ID="tt0371746"

curl -X POST \
  "http://localhost:9100/EmbyStreams/Discover/AddToLibrary?imdbId=${IMDB_ID}&type=movie&title=Iron%20Man&year=2008"
```

**Expected**:
- Response: `{"ok":true,"strmPath":"..."}`
- File created: `/media/embystreams/movies/Iron Man (2008).strm`
- Content: `/EmbyStreams/Play?imdb=tt0371746`

### Test 5: Auto-Library-Refresh

1. After adding item (Test 4), wait 1 second
2. Check Emby movies library in web UI
3. Item should appear automatically

**Expected**: Item shows in Movies library without manual refresh.

### Test 6: Manual Sync Trigger

```bash
curl -X POST "http://localhost:9100/EmbyStreams/Trigger?task=catalog_discover"
```

**Expected**:
- Response: `{"status":"ok","task":"catalog_discover"}`
- Logs show sync in progress

### Test 7: Sidebar Channel

1. Go to http://localhost:9100
2. Look for **Discover** in left sidebar
3. Click it
4. Should see grid of content (if auto-sync ran)
5. Search bar should work
6. Click item → should show details

**Expected**: Full Netflix-like browsing experience.

## Troubleshooting

### Catalog Not Populating

**Issue**: `discover_catalog` stays empty after restart

**Debug**:
```bash
# Check if AIOStreams URL configured
grep -A 5 "AioStreamsUrl" ~/emby-dev-data/config/plugins/configurations/EmbyStreams.xml

# Check logs for errors
grep "Discover" ~/emby-dev-data/logs/embyserver.txt | grep -i error
```

**Solution**: Ensure AIOStreams URL is set and reachable.

### Items Not Appearing in Library

**Issue**: Added item doesn't show in Movies/TV Shows after adding

**Debug**:
```bash
# Check if file was created
ls -la /media/embystreams/movies/

# Check logs for library refresh errors
grep "library refresh" ~/emby-dev-data/logs/embyserver.txt
```

**Solution**: Verify `SyncPathMovies`/`SyncPathShows` configured correctly.

### Discover Channel Missing

**Issue**: Sidebar doesn't show Discover

**Debug**:
```bash
# Check if DiscoverChannel is loaded
grep "DiscoverChannel" ~/emby-dev-data/logs/embyserver.txt
```

**Solution**: Restart Emby (server might need full restart for IChannel registration).

## Version Info

**Version**: 0.19.0.0
**Feature Added**: Sprint 18 - Discover
**Components**: 7 new files, ~1500 lines of code
**Database Migration**: V12 → V13

## Files Changed

| File | Changes |
|------|---------|
| `Models/DiscoverCatalogEntry.cs` | NEW - Catalog entry model |
| `Services/DiscoverService.cs` | NEW - REST API (Browse, Search, Detail, Add) |
| `Services/DiscoverChannel.cs` | NEW - IChannel sidebar integration |
| `Services/CatalogDiscoverService.cs` | NEW - AIOStreams sync logic |
| `Services/DiscoverInitializationService.cs` | NEW - Auto-initialization |
| `Tasks/CatalogDiscoverTask.cs` | NEW - Scheduled task runner |
| `Data/DatabaseManager.cs` | MODIFIED - Schema V13, discover_catalog methods |
| `Services/TriggerService.cs` | MODIFIED - Added catalog_discover trigger |
| `EmbyStreams.csproj` | MODIFIED - Version 0.19.0.0 |
| `plugin.json` | MODIFIED - Version 0.19.0.0 |

## Next Steps (Optional Enhancements)

- [ ] Rate limiting for AIOStreams API calls
- [ ] Caching of metadata (avoid re-fetching unchanged)
- [ ] Filter by genre/rating in browse
- [ ] Recommended items section
- [ ] Watch history integration
- [ ] Custom sort/filter options
