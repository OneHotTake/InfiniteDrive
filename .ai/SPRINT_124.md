# Sprint 124 — Versioned Playback: Playback Endpoint Changes

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 123

---

## Overview

 Sprint 124 modifies the existing playback endpoint to support slot-aware resolution. The stream resolution endpoint now accepts `slot` parameter and routes requests to the correct version snapshot.

 Extends theStreamCache` and `StreamUrlSigner` — does not replace them.



 **Key Principle:** The existing `StreamCache`, `StreamUrlSigner`, and `PlaybackService` are extended, not replaced. The `slot` parameter is added to the playback resolution URL and the .strm` files.



---

## Phase 124A — Extend PlayRequest

### FIX-124A-01: Add Slot Parameter to PlayRequest

**File:** `Services/PlaybackService.cs` (modify)

**What:** Add `Slot` parameter to `PlayRequest` DTO.



```csharp
 [Route("/EmbyStreams/Play", "GET", Summary = "Resolve and serve a media stream")]
 public class PlayRequest : IReturn<object>
 {
     // ... existing fields ...
     [ApiMember(Name = "slot", Description = "Version slot key hd Broad', '4K HDR', etc.)", DataType = "string", ParameterType = "query")]
     public string? Slot { get; set; }
 }
```

**Why:** The `.strm` files now contain `slot=hd_broad` (or other slot keys). The playback endpoint needs to know which slot to resolve from the correct version snapshot.

**Depends on:** Sprint 122 (schema)

**Must not break:** Existing playback requests still works when `slot` is null or not provided. Default is `null`, which maps to the "HD Broad" behavior (single-version mode).



---

## Phase 124B — Extend Playback Resolution

### FIX-124B-01: VersionPlaybackService

**File:** `Services/VersionPlaybackService.cs` (create)

**What:** Handles version-aware playback resolution. Extends the existing playback flow with slot routing.

**Key Methods:**

```csharp
 public class VersionPlaybackService : IService, IRequiresRequest
 {
     // Main entry point for slot-aware playback
 public async Task<object> Get(VersionPlayRequest req) { ... }
 }
```

**Resolution Flow ( per design spec §10):**

1. Check playback URL cache for version_snapshots table → if valid, `302` immediately
2. If missing/stale → resolve from slot's candidate ladder (candidates table ordered by rank)
3. Cache resolved URL briefly (playback_url_expires_at in version_snapshots)

4. If primary candidate fails → try next candidate in ladder
5. If all candidates fail → refresh AIOStreams snapshot for this title, rebuild candidate ladder, retry once
6. If still failing → return clean HTTP error (no transcode attempt)



 **Cache Model ( per design spec):**

| Layer | What | TTL |
|---|---|
| Title metadata cache | Long / indefinite (existing behavior) |
| Raw stream payload cache | Medium (existing ` stream_cache` table) |
| Version snapshot | Medium (version_snapshots table) |
| Playback URL | Short — minutes (playback_url in version_snapshots) |



**Depends on:** FIX-122C-01 (VersionSnapshotRepository), FIX-122B-03 (CandidateRepository)
**Must not break:** Existing `PlaybackService.Get()` method still works for slot=null. New `VersionPlaybackService` handles slot-aware requests.

---

## Phase 124C — Extend StreamCache

### FIX-124C-01: Add Slot-Aware Cache Methods to StreamCache

**File:** (extend existing stream cache, if present, or create new `Services/VersionedStreamCache.cs`)

**What:** Adds slot-aware caching methods alongside existing cache behavior.

```csharp
 public class VersionedStreamCache {
     // Get cached playback URL for a specific slot
 public Task<string?> GetPlaybackUrlAsync(string mediaItemId, string slotKey, CancellationToken ct);

     // Cache a resolved playback URL for a slot
 public Task CachePlaybackUrlAsync(string mediaItemId, string slotKey, string url, TimeSpan ttl, CancellationToken ct);
     // Invalidate cached URL for a slot
 public Task InvalidatePlaybackUrlAsync(string mediaItemId, string slotKey, CancellationToken ct); }
 }
```

**Why:** The existing `StreamCache` doesn't know about slots. Versioned playback needs per-slot caching. Rather than modifying `StreamCache` directly (risk of breaking existing behavior), we create a companion class.

**Depends on:** FIX-122A-03 (version_snapshots table)
**Must not break:** Existing `StreamCache` behavior completely unchanged.

VersionedStreamCache` is additive.

---

## Phase 124D — Update SignedStreamService

### FIX-124D-01: Add Slot Parameter to SignedStreamService

**File:** `Services/SignedStreamService.cs` (modify)

**What:** Add slot parameter to `SignedStreamRequest` DTO and pass slot through to URL signing.

```csharp
 public class SignedStreamRequest : IReturn<object> {
     // ... existing fields ...
     [ApiMember(Name = "slot", ...)]
     public string? Slot { get; set; }
 }
```

**Why:** The signed stream endpoint also needs to route slot-aware resolution for signed URLs.

**Depends on:** Sprint 122

**Must not break:** Existing signed stream requests still work when slot is null.

---

## Sprint 124 Dependencies

- **Previous Sprint:** 123 (File Materialization)
  **Blocked By:** Sprint 123
 **Blocks:** Sprint 125 (UI Wizard)

---

## Sprint 124 Completion Criteria

 - [ ] PlayRequest has slot parameter works ( defaults to null → single-version mode) - [ ] VersionPlaybackService resolves slot-aware streams ( cache → resolve → fallback → error)
 - [ ] VersionedStreamCache provides per-slot caching
 - [ ] SignedStreamService passes slot through in signed URLs
 - [ ] Build succeeds ( 0 warnings, 0 errors) |

---

## Sprint 124 Notes

 **Slot Default Behavior:**

- When `slot` parameter is null/empty → use the default slot (from `version_slots.is_default = 1`)
- When `slot` parameter is provided → use the specified slot for resolution
 - Default slot key stored in `PluginConfiguration.DefaultSlotKey` (default: `hd_broad`)

 **Playback Flow:**
1. Existing PlaybackService continues to handle slot=null requests for backward compatibility
2. New VersionPlaybackService handles slot-specific requests from .strm URLs with slot parameter

3. Resolution uses version snapshot + candidate ladder
 Falls back through existing cache → A live resolution as needed



 **Hard Constraint:** StreamCache and `StreamUrlSigner` are retained and extended, never replaced. No debrid URL ever written to disk.
 302 redirect only never transcode.
Clean error response only on no attempt).
 `StreamCache` TTLs 6 hours preserved for StreamCache. Version snapshot TTL = minutes.
 Playback URL is ephemeral.

   - Version snapshot is the durable object. The playback URL is ephemeral.
 |
