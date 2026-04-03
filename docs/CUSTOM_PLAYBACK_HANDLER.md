# Custom Playback Handlers in Emby: Can You Skip FFmpeg?

**Date:** 2026-03-29
**Subject:** Whether Emby allows replacing or bypassing ffmpeg with custom playback handlers
**Findings:** Yes, but with significant caveats

---

## TL;DR

**Short Answer:** Yes, you can intercept playback, but you're not replacing ffmpeg — you're intercepting at the **media source level**, before ffmpeg ever gets involved.

**Two Approaches:**

1. **Gelato's Method (Jellyfin):** Decorate `IMediaSourceManager` to return custom `MediaSourceInfo` objects with HTTP URLs → Jellyfin/ffmpeg handles the URL
2. **EmbyStreams' Method (Emby):** Create .strm files with URLs → Emby calls `/EmbyStreams/Play` endpoint → Endpoint returns stream → Client/ffmpeg handles it

**Can Emby do what Gelato does?** Probably YES with `IMediaSourceManager` decorator (Emby also uses ASP.NET Core dependency injection as of 4.8+).

---

## How Gelato Intercepts Playback (No Custom Player Needed)

### The Decorator Pattern

Gelato doesn't replace ffmpeg. Instead, it **intercepts the media source resolution** process:

```csharp
// File: ServiceRegistrator.cs, line 47
services.DecorateSingle<IMediaSourceManager, MediaSourceManagerDecorator>();
```

This replaces Jellyfin's `IMediaSourceManager` with a wrapper:

```csharp
public sealed class MediaSourceManagerDecorator : IMediaSourceManager {
    private readonly IMediaSourceManager _inner;  // Original implementation

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User user = null
    ) {
        // 1. Check if this is a Stremio/Gelato item
        var uri = StremioUri.FromBaseItem(item);

        // 2. If yes, sync available streams from Stremio
        if (uri is not null && !manager.HasStreamSync(cacheKey)) {
            var count = await manager.SyncStreams(item, userId, ct);
        }

        // 3. Get the original media sources (from database/file)
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

        // 4. Filter and reorder to show only Gelato streams
        var filteredSources = sources
            .Where(x => manager.IsGelato(m))
            .OrderBy(x => x.Index)
            .ToList();

        // 5. Return to Jellyfin
        return filteredSources;
    }
}
```

**Key Insight:** You're returning `MediaSourceInfo` objects with HTTP URLs in the `Path` property. Jellyfin then passes these URLs to ffmpeg. You're not replacing ffmpeg — you're just changing what URL ffmpeg gets.

---

## The Media Source Resolution Pipeline

```
User clicks Play in Jellyfin
    ↓
Jellyfin calls IMediaSourceManager.GetStaticMediaSources(item)
    ↓
MediaSourceManagerDecorator intercepts
    ├─ Checks if item is Gelato item
    ├─ Syncs available streams if needed
    ├─ Returns filtered MediaSourceInfo[] with HTTP URLs
    │
Jellyfin UI shows available streams
    │
User selects a stream
    ↓
Jellyfin calls ffmpeg with the Path from MediaSourceInfo
    │  Path: "http://127.0.0.1:8096/gelato/stream?ih=abc..."
    │
ffmpeg starts HTTP request to that URL
    ↓
DownloadFilter (or server) streams content back
    ↓
ffmpeg transcodes if needed (or direct passthrough)
    ↓
Client plays video
```

---

## What You Can Customize

### ✅ Yes: Replace Media Source Resolution
```csharp
// Intercept: What URLs are available for this item?
IMediaSourceManager.GetStaticMediaSources()

// You can:
- Return custom URLs
- Filter out certain sources
- Reorder sources (show best option first)
- Add new sources from external APIs
- Sync data from external services
```

### ✅ Yes: Intercept Playback Info Requests
```csharp
// Jellyfin/Emby asks: "What transcoding options are available?"
// Filters can inspect and modify:
IAsyncActionFilter (on any HTTP request)
```

### ✅ Yes: Return Custom Stream URLs
```csharp
// Instead of:
// /path/to/local/file.mp4

// Return:
// http://myserver:8096/custom/stream?id=123
// https://cdn.example.com/movie.mp4
// http://127.0.0.1:9000/torrent/stream?ih=abc
```

### ❌ No: Completely Replace FFmpeg
FFmpeg is hardcoded into Jellyfin/Emby for:
- Transcoding
- Format conversion
- Codec compatibility
- Stream analysis

You can't replace it, but you can:
- Bypass it (direct stream)
- Configure its parameters (probe size, duration, etc.)

### ❌ No: Custom Video Decoder
There's no plugin interface for custom video decoders. The client (browser, app) handles decoding.

---

## Does Emby Support IMediaSourceManager Decorator?

### Likely YES

**Evidence:**

1. **Emby 4.8+ uses ASP.NET Core** (like modern Jellyfin)
2. **Dependency Injection** is a core feature of ASP.NET Core
3. **ServiceStack** (Emby's REST framework) integrates with DI
4. **EmbyStreams** already uses `IServerEntryPoint` and custom services

**Reasoning:**

```csharp
// Emby 4.8+ likely has this in ServiceRegistration:
services.AddSingleton<IMediaSourceManager, DefaultMediaSourceManager>();

// So you could decorate it:
services.DecorateSingle<IMediaSourceManager, YourDecorator>();

// Using Gelato's pattern:
public class YourDecorator : IMediaSourceManager {
    private readonly IMediaSourceManager _inner;

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(...) {
        // Intercept and modify sources
        var sources = _inner.GetStaticMediaSources(...);
        // ... your logic ...
        return sources;
    }
}
```

**But:** This would require:
- Emby's `IMediaSourceManager` interface to be public
- `IPluginServiceRegistrator` to support service decoration (not just addition)
- .NET 8.0 dependency injection patterns

---

## Current EmbyStreams vs Potential IMediaSourceManager Approach

### Current: REST Endpoint + .strm Files

```
.strm file: <Link>http://localhost:8096/EmbyStreams/Play?imdb=tt...</Link>
    ↓
Emby loads URL
    ↓
Emby calls /Play endpoint
    ↓
PlaybackService resolves stream
    ↓
Returns URL or proxies content
    ↓
ffmpeg gets final URL
```

### Potential: IMediaSourceManager Decorator

```
Emby asks: GetStaticMediaSources(item)
    ↓
MediaSourceManagerDecorator intercepts
    ↓
Returns MediaSourceInfo[] with resolved stream URLs
    ↓
Emby UI shows streams
    ↓
User clicks Play
    ↓
ffmpeg gets URL directly (no /Play endpoint needed)
    ↓
Playback starts
```

**Advantages of decorator approach:**
- Skip .strm file creation entirely
- Direct integration with Emby's media source system
- Multiple streams shown as UI-level alternatives (like Gelato)
- Better user experience (no separate REST endpoint)

**Disadvantages:**
- Requires deeper Emby API access
- More complex (must implement full IMediaSourceManager interface)
- Tightly coupled to Emby's internal structure
- Breaking changes if Emby updates API

---

## How to Verify Emby Support

### Step 1: Check Available Interfaces

```bash
# In your Emby plugin project:
# Look in MediaBrowser.Server.Core.dll (DLL at runtime)
# See if these interfaces exist:

- IMediaSourceManager
- IPluginServiceRegistrator
- Emby.Server.Core.Services extension methods
```

### Step 2: Test Decorator Pattern

Create a test decorator:

```csharp
// File: Decorators/MediaSourceManagerDecorator.cs
public class MediaSourceManagerDecorator : IMediaSourceManager {
    private readonly IMediaSourceManager _inner;

    public MediaSourceManagerDecorator(IMediaSourceManager inner) {
        _inner = inner;
    }

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User user = null
    ) {
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

        // Log to verify this method is being called
        Console.WriteLine($"MediaSourceManagerDecorator.GetStaticMediaSources called for {item.Name}");

        return sources;
    }

    // Implement other IMediaSourceManager methods...
    // Delegate most to _inner
}

// File: Plugin.cs (in GetPages or somewhere in setup):
public override void RegisterServices(IServiceCollection services) {
    services.DecorateSingle<IMediaSourceManager, MediaSourceManagerDecorator>();
}
```

### Step 3: Register and Test

If Emby's `services.DecorateSingle()` doesn't exist, you'll need to:

```csharp
// Manual decoration
var original = services.FirstOrDefault(s => s.ServiceType == typeof(IMediaSourceManager));
if (original != null) {
    services.Remove(original);
    services.AddSingleton<IMediaSourceManager>(sp => {
        var inner = (IMediaSourceManager)Activator.CreateInstance(original.ImplementationType!);
        return new MediaSourceManagerDecorator(inner);
    });
}
```

---

## Comparison: Approaches to Custom Playback

| Approach | Complexity | ffmpeg Required | User Experience | Current Support |
|---|---|---|---|---|
| **REST Endpoint** (EmbyStreams) | Low | Yes | Separate endpoint, good UX | ✅ Working |
| **IMediaSourceManager Decorator** (Gelato) | Medium | Yes | Native UI, Emby integrated | ❓ Likely possible |
| **IAsyncActionFilter** | Low | Yes | HTTP request interception | ✅ Possible (untested) |
| **Custom Transcoder** | Very High | No | Replaces ffmpeg entirely | ❌ Not supported |

---

## Recommendations for EmbyStreams

### If You Want Deeper Integration Without Changing ffmpeg:

**Option 1: Try IMediaSourceManager Decorator**
```csharp
// Pros:
- No .strm files needed
- Direct Emby integration
- Multiple streams in UI

// Cons:
- May not be supported in Emby (unknown API stability)
- More complex code
- Breaking changes possible

// Risk: Medium (test thoroughly before committing)
```

**Option 2: Custom IAsyncActionFilter**
```csharp
// Intercept playback info requests
// Modify MediaSourceInfo before Emby uses it

// Pros:
- Less risky than decorator
- Still integrates with Emby's playback

// Cons:
- Still requires .strm files as entry point
// Risk: Low
```

**Option 3: Keep Current Approach**
```csharp
// .strm files + /Play endpoint

// Pros:
- Proven to work
- Compatible with all Emby versions
- Well-documented

// Cons:
- File system dependency
- Separate endpoint needed

// Risk: None (current implementation)
```

---

## Deep Dive: What IMediaSourceManager Does

### Methods You'd Need to Implement

```csharp
public interface IMediaSourceManager {
    // 1. Get available sources for an item
    IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User user = null);

    // 2. Get sources with transcoding info
    async Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, ...);

    // 3. Open live streams (for TV/streaming sources)
    async Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, ...);

    // 4. Validate media sources
    async Task<bool> ValidatePlayback(string itemId, string mediaSourceId, ...);

    // 5. Add media source providers
    void AddParts(IEnumerable<IMediaSourceProvider> providers);
}
```

### The Key Method: GetStaticMediaSources

```csharp
public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
    BaseItem item,
    bool enablePathSubstitution,
    User user = null
) {
    // This is called when:
    // 1. User clicks Play
    // 2. Emby needs to show available quality options
    // 3. Transcoding setup is initiated

    // You return:
    var sources = new List<MediaSourceInfo> {
        new MediaSourceInfo {
            Id = "primary-source",
            Path = "http://your-server/stream/movie.mp4",  // ← Your custom URL!
            Protocol = MediaProtocol.Http,  // ← Tells Emby it's HTTP
            Name = "High Quality",
            Container = "mp4",
            VideoProfiles = new[] { "h264" },
            // ... other metadata
        },
        new MediaSourceInfo {
            Id = "backup-source",
            Path = "http://backup-server/stream/movie.mp4",  // ← Alternate source
            Name = "Backup",
        }
    };

    return sources;
}
```

**This is how you customize playback without touching ffmpeg.**

---

## References

- **Gelato Implementation:**
  - `Decorators/MediaSourceManagerDecorator.cs` — Full implementation
  - `ServiceRegistrator.cs` (line 47) — Service registration
  - Pattern: Decorator with `DecorateSingle<TService, TDecorator>()`

- **Jellyfin/Emby APIs:**
  - `IMediaSourceManager` — Media source resolution
  - `MediaSourceInfo` — Return type (has Path property)
  - `IAsyncActionFilter` — HTTP request interception (Jellyfin/Emby 4.8+)
  - `IPluginServiceRegistrator` — Service registration hook

- **EmbyStreams Approach:**
  - `.strm` file format (XML with Link)
  - `/EmbyStreams/Play` endpoint (custom REST handler)
  - `PlaybackService.cs` — Stream resolution logic

---

## Conclusion

**Can you use a custom play handler instead of ffmpeg?**

- ✅ Yes, you can intercept and provide custom stream URLs
- ❌ No, you can't replace ffmpeg itself
- ❓ Maybe, Emby supports IMediaSourceManager decorator (untested)

**Best approach for EmbyStreams:**

1. **Short term:** Keep current approach (proven, stable)
2. **Medium term:** Test IMediaSourceManager decorator (better UX, higher risk)
3. **Long term:** Consider Jellyfin/Emby unification if both support same API

The key insight: **You're not replacing the player — you're just changing what URL it gets to play.**

---

**Analysis Completed:** 2026-03-29
