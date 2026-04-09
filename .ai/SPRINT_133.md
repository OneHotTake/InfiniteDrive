# Sprint 133 — Resolver Service + M3U8 Builder

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 132

---

## Overview

Create \`Services/ResolverService.cs\` and \`Services/M3u8Builder.cs\` for /EmbyStreams/Resolve endpoint. These provide:
1. AIOStreams stream resolution with tier selection
2. M3U8 variant playlist generation for Emby clients

### Why This Exists

The \`LinkResolverTask\` exists but is a task-based scheduled approach. We need:
1. Service layer for real-time resolution (Sprint 122 AIOStreams API integration)
2. M3U8 variant generation for HLS clients (Emby, Roku, Apple TV)

---

## Phase 133A — Resolver Service

### FIX-133A-01: Create ResolverService

**File:** \`Services/ResolverService.cs\` (create)

**What:**
1. Create \`ResolverService\` class implementing \`IService\`:
\`\`csharp
public class ResolverService : IService, IRequiresRequest
{
    private readonly ILogger<ResolverService> _logger;
    private readonly ILogManager _logManager;
    private readonly AioStreamsClient _aioClient;
    private readonly DatabaseManager _db;
    private readonly M3u8Builder _m3u8Builder;

    public ResolverService(
        ILogManager logManager,
        AioStreamsClient aioClient,
        DatabaseManager db,
        M3u8Builder m3u8Builder)
    {
        _logger = new EmbyLoggerAdapter<ResolverService>(logManager.GetLogger("EmbyStreams"));
        _logManager = logManager;
        _aioClient = aioClient;
        _db = db;
        _m3u8Builder = m3u8Builder;
    }

    public async Task<object> Get(ResolverRequest req)
    {
        // Resolve stream for given imdbId, quality, mediaType
        // Return M3U8 manifest with signed stream URLs
    }
}
\`\`

2. Implement Resolve logic:
\`\`csharp
private async Task<object> ResolveAsync(string imdbId, string quality, string mediaType)
{
    // 1. Query AIOStreams API for streams
    // 2. Filter by quality tier (4k_hdr, 4k_sdr, hd_broad, sd_broad)
    // 3. Select top tier per source
    // 4. Generate M3U8 playlist with signed URLs
    // 5. Return manifest
}
\`\`

**Depends on:** FIX-133B-01

---

## Phase 133B — M3U8 Builder

### FIX-133B-01: Create M3U8 Builder

**File:** \`Services/M3u8Builder.cs\` (create)

**What:**
1. Create \`M3u8Builder\` class:
\`\`csharp
public class M3u8Builder
{
    public const string MimeType = "application/vnd.apple.mpegurl";
    public const string Version = "6";

    // Quality tier metadata
    public static readonly Dictionary<string, TierMetadata> TierMetadata = new()
    {
        ["4k_hdr"] = new TierMetadata { DisplayName = "4K HDR", Resolution = "2160p", Is4K = true, IsHDR = true },
        ["4k_sdr"] = new TierMetadata { DisplayName = "4K SDR", Resolution = "2160p", Is4K = true, IsHDR = false },
        ["hd_broad"] = new TierMetadata { DisplayName = "1080p Broad", Resolution = "1080p", Is4K = false, IsHDR = false },
        ["sd_broad"] = new TierMetadata { DisplayName = "SD Broad", Resolution = "480p-720p", Is4K = false, IsHDR = false },
    };

    // Stream quality detection helpers
    public static string MapStreamToTier(AioStreamsStream stream) { ... }
    public static string GetSourceName(AioStreamsStream stream) { ... }
    public static bool IsHevcStream(AioStreamsStream stream) { ... }

    // M3U8 manifest generation
    public string CreateVariantPlaylist(
        string baseUrl,
        string quality,
        List<M3U8Variant> variants) { ... }
}
\`\`

2. Add quality tier metadata:
\`\`csharp
public class TierMetadata
{
    public string DisplayName { get; set; }
    public string Resolution { get; set; }
    public bool Is4K { get; set; }
    public bool IsHDR { get; set; }
}
\`\`

**Depends on:** FIX-133A-01

---

## Phase 133C — Request Models

### FIX-133C-01: Resolver Request Model

**File:** \`Models/ResolverRequest.cs\` (create)

**What:**
\`\`csharp
public class ResolverRequest : IReturn<object>
{
    public string Id { get; set; }
    public string Quality { get; set; }
    public string IdType { get; set; }  // "movie" or "series"
}
\`\`

---

## Phase 133D — API Endpoint

### FIX-133D-01: Register Resolve Endpoint

**File:** \`Plugin.cs\` (modify)

**What:**
Add to service registration:
\`\`csharp
AddSingleton<ResolverService>();
\`\`

---

## Phase 133E — Build Verification

### FIX-133E-01: Build + Test

**What:**
1. \`dotnet build -c Release\` — 0 errors, 0 warnings
2. Manual test \`/EmbyStreams/Resolve?id=tt0000000&quality=hd_broad\`
3. Verify M3U8 format and signed URLs

---

## Sprint 133 Completion Criteria

- [ ] \`Services/ResolverService.cs\` created
- [ ] \`Services/M3u8Builder.cs\` created
- [ ] \`Models/ResolverRequest.cs\` created
- [ ] Resolver endpoint registered in Plugin.cs
- [ ] Build succeeds with 0 errors
- [ ] Manual \`/EmbyStreams/Resolve\` endpoint test

---

## Notes

**Files created:** 3
**Files modified:** 1 (\`Plugin.cs\`)

**Risk:** LOW
