# Gelato Playback Architecture Analysis

**Date:** 2026-03-29
**Subject:** How Gelato plays .strm files (no builtin player)
**Conclusion:** Gelato leverages Jellyfin's native player by intercepting HTTP requests

---

## TL;DR

**Gelato does NOT have a builtin player.** Instead, it:

1. Creates **virtual items** in Jellyfin's database
2. Sets each item's `Path` property to an **HTTP URL**
3. Intercepts Jellyfin's `GetDownload` endpoint via `DownloadFilter`
4. Streams the content (HTTP proxy or torrent) back to Jellyfin's native player

Result: Items play through Jellyfin's built-in media player seamlessly.

---

## Architecture: Database Injection vs .strm Files

### EmbyStreams (File-Based)
```
Create .strm file on disk
    ↓
Emby scans library folder
    ↓
Emby discovers .strm files
    ↓
User clicks Play
    ↓
Emby loads .strm XML → extracts URL
    ↓
/EmbyStreams/Play?imdb=tt... endpoint resolves stream
    ↓
Client plays stream
```

### Gelato (Database Injection + HTTP Proxy)
```
Fetch streams from Stremio API
    ↓
Create Video items in Jellyfin database
    ↓
Set item.Path = "http://127.0.0.1:port/gelato/stream?ih=..."
    ↓
Mark items as Virtual (IsVirtualItem = true)
    ↓
User clicks Play
    ↓
Jellyfin calls GetDownload endpoint
    ↓
DownloadFilter intercepts request
    ↓
Makes HTTP request to item.Path (gelato/stream endpoint)
    ↓
Streams torrent/file content back to Jellyfin
    ↓
Jellyfin's native player displays stream
```

---

## Code Flow: How Playback Works

### Step 1: Creating Virtual Items (SyncStreams)

**File:** GelatoManager.cs, line 518-560

```csharp
// For each stream from Stremio API:
var path = s.IsFile()
    ? s.Url  // Direct file URL (e.g., https://debrid-cdn.example.com/file.mp4)
    : $"http://127.0.0.1:{httpPort}/gelato/stream?ih={s.InfoHash}"
        + (s.FileIdx is not null ? $"&idx={s.FileIdx}" : "")
        + (s.Sources is { Count: > 0 }
            ? $"&trackers={Uri.EscapeDataString(string.Join(',', s.Sources))}"
            : "");

// Create Video item
var target = new Video {
    Id = _library.GetNewItemId(path, typeof(Video)),
    Name = primary.Name,
    Path = path,  // ← Store URL as Path property
    IsVirtualItem = true,  // Mark as virtual (not a real file)
    ProviderIds = providerIds,
    // ... other metadata
};

// Save to database
_repo.SaveItems([target], ...);
```

### Step 2: User Clicks Play in Jellyfin

Jellyfin's playback flow:
```
User clicks Play
    ↓
Jellyfin UI calls PlaybackController.GetDownload()
    ↓
PlaybackInfoFilter (Order=1) runs first
    ↓
DownloadFilter (Order=3) runs second
```

### Step 3: DownloadFilter Intercepts (Filters/DownloadFilter.cs)

```csharp
public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
{
    if (ctx.GetActionName() != "GetDownload")  // Only intercept GetDownload
    {
        await next();
        return;
    }

    var guid = ctx.RouteData.Values["id"];  // Item GUID from URL
    var mediaSourceId = ctx.HttpContext.Items["MediaSourceId"];  // From PlaybackInfoFilter

    var item = _library.GetItemById<Video>(mediaSourceId ?? guid, user);

    if (_manager.IsStremio(item))  // Is this a Gelato/Stremio item?
    {
        var path = item.Path;  // Get the URL stored in Path property

        // Make HTTP request to the stored URL
        var resp = await client.GetAsync(
            path,  // e.g., "http://127.0.0.1:8096/gelato/stream?ih=abc123"
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None
        );

        resp.EnsureSuccessStatusCode();

        var stream = await resp.Content.ReadAsStreamAsync();

        // Return stream to Jellyfin client
        ctx.Result = new FileStreamResult(stream, contentType)
        {
            FileDownloadName = fileName,
            EnableRangeProcessing = true,  // ← Support seeking!
        };
        return;
    }

    // Not a Gelato item, pass to next filter
    await next();
}
```

### Step 4: GelatoApiController Handles Stream Request

**File:** Controllers/GelatoApiController.cs, line 80-200+

For **direct file URLs** (e.g., Real-Debrid CDN):
- The DownloadFilter makes an HTTP request directly to the CDN
- DownloadFilter streams the response back to Jellyfin

For **torrent streams** (magnet links):
```csharp
[HttpGet("stream")]
public async Task<IActionResult> TorrentStream(
    [FromQuery] string ih,      // infohash
    [FromQuery] int? idx,        // file index
    [FromQuery] string? filename, // optional filename filter
    [FromQuery] string? trackers  // DHT trackers
)
{
    // Security: Only localhost can access
    if (!IsLoopback(remoteIp))
        return Forbid();

    // Create torrent engine
    var settings = new EngineSettingsBuilder
    {
        MaximumConnections = 40,
        MaximumDownloadRate = Configuration.P2PDLSpeed,
        MaximumUploadRate = Configuration.P2PULSpeed,
    }.ToSettings();

    var engine = new ClientEngine(settings);

    // Add torrent for streaming
    var manager = await engine.AddStreamingAsync(magnet, downloadPath);
    await manager.StartAsync();

    // Wait for metadata
    while (!manager.HasMetadata && !ct.IsCancellationRequested)
        await Task.Delay(100);

    // Select file (by index or filename)
    var selected = idx >= 0
        ? manager.Files[idx]
        : manager.Files.FirstOrDefault(f => f.Path.EndsWith(filename))
            ?? PickHeuristic(manager);  // Pick largest video file

    // Stream the file
    var stream = await manager.StreamProvider.CreateStreamAsync(selected, ct);

    return File(
        stream,
        "application/octet-stream",
        selected.Path,
        enableRangeProcessing: true  // ← Support seeking!
    );
}
```

### Step 5: Jellyfin Native Player Displays Stream

The FileStreamResult (with EnableRangeProcessing=true) allows:
- **Seeking** — User can skip forward/backward
- **Bandwidth negotiation** — Client requests byte ranges
- **Multiple client support** — Web UI, mobile apps, etc.

All handled by Jellyfin's built-in player.

---

## Key Architectural Insights

### 1. No Custom Player Needed
- Gelato doesn't write a custom video player
- Leverages Jellyfin's built-in playback mechanism
- Items appear 100% native to Jellyfin users

### 2. Virtual Items with HTTP Paths
```csharp
// Item metadata is in database
item.Name = "Shawshank Redemption"
item.PremiereDate = 1994-10-14
item.IsVirtualItem = true

// But Path points to HTTP URL (not file)
item.Path = "http://127.0.0.1:8096/gelato/stream?ih=infohash&idx=0"
```

### 3. Filter-Based Request Interception
- Jellyfin calls `GetDownload` (standard playback endpoint)
- `DownloadFilter` intercepts before controller runs
- Makes HTTP request to `item.Path`
- Returns stream to client

### 4. Proxy Architecture
```
Client → Jellyfin GetDownload
           ↓
        DownloadFilter intercepts
           ↓
        Makes request to /gelato/stream or CDN
           ↓
        Streams content back to client
```

No direct connection between client and stream source.

### 5. Torrent Streaming (P2P)
- Uses MonoTorrent library for magnet link support
- Streams file while downloading (not buffering entire file)
- Supports seeking (important for seeking within a downloaded portion)
- Only localhost can access `/gelato/stream` (security)

### 6. Multiple Stream Versions
```csharp
// SyncStreams creates one Video item per stream source
primary = original user-added item (not playable)
alternate1 = Stream from provider A (isStream=true)
alternate2 = Stream from provider B (isStream=true)

// Jellyfin UI shows as "alternate versions" of the same movie
// User can switch between streams (providers) in playback UI
```

---

## Comparison: Gelato vs EmbyStreams Playback

| Aspect | Gelato | EmbyStreams |
|--------|--------|-------------|
| **Item Storage** | Jellyfin database (virtual items) | .strm files on disk |
| **Path Property** | HTTP URL (gelato/stream or CDN) | .strm file path (XML content) |
| **Discovery** | Database query | File system scan |
| **Playback Interception** | DownloadFilter (generic) | Custom /Play endpoint |
| **Player** | Jellyfin native | Jellyfin native (via .strm URL) |
| **Seeking** | Range requests (HTTP native) | Range requests (HTTP native) |
| **P2P Support** | Yes (MonoTorrent) | No (debrid-only) |
| **Per-User Config** | Yes (UserConfigs) | No (admin-only) |
| **Stream Selection** | UI shows alternate versions | Database + REST API |

---

## Technical Advantages of Gelato's Approach

### ✅ Strengths

1. **100% Native Jellyfin Integration**
   - Items appear as native Jellyfin items
   - No file system dependency
   - Database-backed, recoverable if files lost

2. **No File System Overhead**
   - No .strm file creation/deletion
   - No folder structure management
   - No disk I/O for discovery

3. **Elegant Alternate Versions**
   - Multiple streams shown as "alternate versions"
   - Users switch providers in playback UI (native Jellyfin feature)
   - No duplicate library entries

4. **P2P Streaming**
   - MonoTorrent handles magnet links natively
   - Streams torrent files while downloading
   - Reduces load on debrid providers

### ❌ Weaknesses

1. **Database-Only Caching**
   - No persistent cache after restart
   - Every playback hits Stremio API (unless cached)
   - Cold start penalty

2. **Jellyfin 10.11 Only**
   - Tight coupling to specific Jellyfin version
   - API changes break plugin

3. **Complex Filter Architecture**
   - Multiple filters (PlaybackInfoFilter, DownloadFilter, etc.)
   - Filter execution order critical
   - Hard to debug interaction issues

4. **Localhost-Only P2P**
   - `/gelato/stream` endpoint only accepts localhost
   - Can't use P2P from remote clients
   - Forces proxy through server

---

## Why EmbyStreams Uses .strm Files Instead

**Design decision:** File-based .strm over database injection

**Reasons:**

1. **Emby API Limitation** — Emby doesn't expose database item creation like Jellyfin does
2. **Compatibility** — .strm is Emby's standard format (native support)
3. **Simplicity** — File scanning is simpler than database injection
4. **Resilience** — .strm files are recoverable; human-readable XML

**Trade-off:** Adds file system dependency but gains compatibility and simplicity.

---

## Key Learnings for EmbyStreams

### 1. Could Emby Support Per-User Manifests?
Currently: Single admin-configured AIOStreams URL for all users

Gelato approach: `ConcurrentDictionary<Guid, PluginConfiguration>` per user

**Emby Challenge:** Does Emby's .strm format support user context? Would need:
- Per-user .strm files in separate folders, OR
- Metadata in .strm indicating which users can access, OR
- API key scoped to user (like current implementation)

**Feasibility:** Medium (would require file organization change)

### 2. Could EmbyStreams Pre-Cache Better?
Currently: Background resolver pre-caches for popular items

Gelato approach: In-memory caching (lost on restart)

**Better approach:**
- Keep EmbyStreams' SQLite persistence
- Add in-memory "hot" cache for frequently played items
- Hybrid model: Memory for hot, disk for cold

**Feasibility:** Low (straightforward addition)

### 3. Could EmbyStreams Support P2P?
Currently: Debrid CDN only (direct URLs)

Gelato approach: MonoTorrent + magnet link support

**Challenge:** Would require:
- Magnet link parsing (add MonoTorrent library)
- Streaming torrent file content
- Per-user bandwidth throttling

**Feasibility:** Medium (library is available, but needs integration)

---

## Conclusion

**Gelato's Brilliance:** Using HTTP paths + filter interception to make Stremio items appear native to Jellyfin without a custom player.

**Key Innovation:** `item.Path = "http://127.0.0.1:port/gelato/stream?..."`

When Jellyfin calls GetDownload, the DownloadFilter makes a request to that URL and streams it back. Jellyfin's native player handles the rest.

**Applicable to EmbyStreams?** Partially:
- Emby has different APIs (can't inject database items easily)
- .strm files are more compatible with Emby's architecture
- Pre-caching strategy is better than in-memory-only
- Per-user support would require significant refactoring

**Bottom Line:** Both approaches are valid. Gelato chose database injection (Jellyfin-native). EmbyStreams chose file-based (Emby-compatible). Each is optimal for its target server.

---

## References

- **Gelato Files Analyzed:**
  - `GelatoManager.cs` (line 518-560) — Virtual item creation
  - `Filters/DownloadFilter.cs` — Request interception
  - `Controllers/GelatoApiController.cs` (line 80+) — Stream endpoint
  - `Common.cs` — StremioUri format

- **Key Jellyfin Concepts:**
  - Virtual items (`IsVirtualItem = true`)
  - Alternate versions (LinkedAlternateVersions)
  - Filter pipeline (IAsyncActionFilter)
  - IMediaSourceManager

- **MonoTorrent:** https://github.com/amir0/monotorrent (torrent streaming library)
