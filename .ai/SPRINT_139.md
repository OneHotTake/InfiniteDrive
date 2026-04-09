# Sprint 139 — Discover "Add to Library" Alignment

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 137

---

## Overview

Align the Discover "Add to Library" flow with the new resolver + stream architecture. The current `DiscoverService.Post(DiscoverAddToLibraryRequest)` writes `.strm` files using `StreamUrlSigner.GenerateSignedUrl()` (deleted in Sprint 137) and falls back to `/EmbyStreams/Play` (also deleted). Both code paths will break after Sprint 137.

**Spec reference:** §10 (post-v3 item, but required because Sprint 137 removes the old endpoints this feature depends on).

---

## Phase 139A — Fix .strm URL Generation

### FIX-139A-01: Replace HMAC-signed URL with resolve token URL

**File:** `Services/DiscoverService.cs` (modify, lines 710-725)

**What:**
1. Replace `StreamUrlSigner.GenerateSignedUrl(...)` call with the new resolve token format:
   ```csharp
   var defaultSlot = Plugin.Instance?.Configuration?.DefaultSlotKey ?? "hd_broad";
   var resolveToken = StreamUrlSigner.GenerateResolveToken(
       defaultSlot, req.ImdbId, "imdb", secret);
   strmContent = $"{config.EmbyBaseUrl.TrimEnd('/')}/EmbyStreams/resolve" +
       $"?token={Uri.EscapeDataString(resolveToken)}" +
       $"&quality={Uri.EscapeDataString(defaultSlot)}" +
       $"&id={Uri.EscapeDataString(req.ImdbId)}" +
       $"&idType=imdb";
   ```
2. Remove the `/EmbyStreams/Play` fallback — use resolve URL always
3. Remove `using` for old `StreamUrlSigner.GenerateSignedUrl` pattern

**Before (current code):**
```csharp
if (!string.IsNullOrEmpty(secret))
{
    strmContent = StreamUrlSigner.GenerateSignedUrl(
        config.EmbyBaseUrl, req.ImdbId,
        req.Type.ToLowerInvariant() == "series" ? "series" : "movie",
        null, null, secret,
        TimeSpan.FromDays(config.SignatureValidityDays > 0 ? config.SignatureValidityDays : 365));
}
else
{
    strmContent = $"{config.EmbyBaseUrl.TrimEnd('/')}/EmbyStreams/Play?imdb={req.ImdbId}";
}
```

**After (new code):**
```csharp
var defaultSlot = Plugin.Instance?.Configuration?.DefaultSlotKey ?? "hd_broad";
var resolveToken = StreamUrlSigner.GenerateResolveToken(
    defaultSlot, req.ImdbId, "imdb", secret);
strmContent = $"{config.EmbyBaseUrl.TrimEnd('/')}/EmbyStreams/resolve" +
    $"?token={Uri.EscapeDataString(resolveToken)}" +
    $"&quality={Uri.EscapeDataString(defaultSlot)}" +
    $"&id={Uri.EscapeDataString(req.ImdbId)}" +
    $"&idType=imdb";
```

**Depends on:** Sprint 137 (StreamUrlSigner old methods removed, but `GenerateResolveToken` still available)

**Note:** After Sprint 137 renames `StreamUrlSigner` → `PlaybackTokenService`, this code should reference `PlaybackTokenService.GenerateResolveToken()`.

---

## Phase 139B — Series Episode Support

### FIX-139B-01: Add season/episode params for series items

**File:** `Services/DiscoverService.cs` (modify)

**What:**
1. The resolve endpoint supports `&season=&episode=` query params
2. Discover "Add to Library" for series currently writes a single `.strm` per item
3. For series, the `.strm` URL should NOT include season/episode — the resolver
   receives those at playback time from the Emby client
4. No change needed: series items use the base resolve URL without season/episode,
   and `SeriesPreExpansionService` writes per-episode `.strm` files during expansion

**Verification:** Confirm series items resolve correctly via the base URL.

**Depends on:** FIX-139A-01

---

## Phase 139C — NFO Alignment

### FIX-139C-01: Write .nfo alongside .strm

**File:** `Services/DiscoverService.cs` (modify)

**What:**
1. After writing `.strm`, also write a minimal `.nfo` with IMDB uniqueid
   (matches what `CatalogSyncTask.WriteNfoFileAsync()` does for synced items)
2. Ensures Emby metadata matching works for Discover-added items
3. Use the same NFO format as the catalog sync pipeline

**Depends on:** FIX-139A-01

---

## Phase 139D — Build Verification

### FIX-139D-01: Build + manual test

**What:**
1. `dotnet build -c Release` → 0 errors, 0 new warnings
2. Use Discover UI to add a movie → verify `.strm` contains resolve token URL
3. Verify resolve token is NOT raw PluginSecret
4. Play the added item → verify m3u8 manifest returned → verify stream plays

**Depends on:** FIX-139C-01

---

## Sprint 139 Dependencies

- **Previous Sprint:** 137 (Deprecated Removal — deletes old endpoints)
- **Blocked By:** Sprint 137, Sprint 132, Sprint 133
- **Blocks:** None (this is a cleanup sprint)

---

## Sprint 139 Completion Criteria

- [ ] `DiscoverService.Post()` uses resolve token URL format
- [ ] `StreamUrlSigner.GenerateSignedUrl` reference removed from `DiscoverService.cs`
- [ ] `/EmbyStreams/Play` fallback removed
- [ ] Resolve token is HMAC-signed (not raw PluginSecret)
- [ ] `.nfo` written alongside `.strm` for Discover-added items
- [ ] Series items resolve correctly
- [ ] Build succeeds with 0 errors
- [ ] Manual test: Discover → Add to Library → Play works end-to-end

---

## Sprint 139 Notes

**Files modified:** 1 (`Services/DiscoverService.cs`)

**Risk assessment:** LOW. Single-file change replacing URL generation logic. The new resolve token format is already proven in Sprint 134's hydration pipeline.

**Why this wasn't in Sprint 137:** Sprint 137 deletes the old services and renames StreamUrlSigner → PlaybackTokenService. This sprint aligns the Discover feature (a separate code path) to use the new token format. Separating them keeps Sprint 137 focused on removal and this sprint focused on the Discover-specific alignment.

**Note on default quality tier:** Discover-added items always use the default slot (hd_broad). If the admin enables additional tiers, the Doctor reconciliation (Sprint 135) will write the additional tier `.strm` files during its next run.
