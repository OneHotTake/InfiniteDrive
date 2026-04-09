# Sprint 137 — Deprecated Removal + Cleanup

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 136

---

## Overview

Remove old playback infrastructure that has been replaced by the new resolver + stream architecture. Delete deprecated services, rename `StreamUrlSigner` to `PlaybackTokenService`, migrate remaining callers, and remove dead configuration settings.

---

## Phase 137A — Delete Deprecated Services

### FIX-137A-01: Delete `Services/PlaybackService.cs`

**File:** `Services/PlaybackService.cs` (delete)

**What:** The `/EmbyStreams/Play` endpoint is replaced by `/EmbyStreams/resolve` + `/EmbyStreams/stream`. Delete the entire file.

**Depends on:** Sprint 133

### FIX-137A-02: Delete `Services/SignedStreamService.cs`

**File:** `Services/SignedStreamService.cs` (delete)

**What:** HMAC-signed `/EmbyStreams/Stream` endpoint replaced by `/EmbyStreams/stream` with stream tokens. Delete the entire file.

**Depends on:** Sprint 132

### FIX-137A-03: Delete `Services/StreamProxyService.cs`

**File:** `Services/StreamProxyService.cs` (delete)

**What:** Proxy session-based streaming replaced by `/EmbyStreams/stream`. Delete the entire file.

**Depends on:** Sprint 132

### FIX-137A-04: Delete `Services/ProxySessionStore.cs`

**File:** `Services/ProxySessionStore.cs` (delete)

**What:** Short-lived proxy session tokens no longer needed. Delete the entire file.

**Depends on:** FIX-137A-03

### FIX-137A-05: Delete `Services/VersionPlaybackService.cs`

**File:** `Services/VersionPlaybackService.cs` (delete)

**What:** `/EmbyStreams/VersionedPlay` replaced by `/EmbyStreams/resolve`. Delete the entire file.

**Depends on:** Sprint 133

### FIX-137A-06: Delete `test-signed-stream.sh`

**File:** `test-signed-stream.sh` (delete)

**What:** Test script references old HMAC endpoint. No longer applicable.

**Depends on:** FIX-137A-02

### FIX-137A-07: Delete `Controllers/VersionSlotController.cs`

**File:** `Controllers/VersionSlotController.cs` (delete)

**What:** Zero callers confirmed (no JS references, no C# callers). All functionality available via Wizard + `VersionSlotRepository` directly. Exposes `GET /Versions`, `POST /Versions`, `POST /Versions/Rehydrate` — all superseded by wizard/config UI.

**Depends on:** Sprint 134

---

## Phase 137B — Rename StreamUrlSigner → PlaybackTokenService

### FIX-137B-01: Rename and clean up `StreamUrlSigner.cs`

**File:** `Services/StreamUrlSigner.cs` → `Services/PlaybackTokenService.cs` (rename)

**What:**
1. Rename file and class from `StreamUrlSigner` to `PlaybackTokenService`
2. Remove old methods no longer used after caller migration:
   - `GenerateSignedUrl()` — replaced by `GenerateResolveToken()`
   - `ValidateSignature()` — replaced by `ValidateResolveToken()`
   - `Sign()` — replaced by `GenerateStreamToken()`
   - `Verify()` — replaced by `ValidateStreamToken()`
   - `ComputeHmac()` — old HMAC format, no longer needed
   - `ComputeHmacSimple()` — old simple format, no longer needed
3. Keep: `GenerateSecret()`, `GenerateResolveToken()`, `ValidateResolveToken()`, `GenerateStreamToken()`, `ValidateStreamToken()`, `ComputeHmacString()`, `ConstantTimeEquals()`, `Base64UrlEncode()`, `Base64UrlDecode()`
4. Update all `using` and references across codebase

**Depends on:** Phase 137C (all callers migrated first)

---

## Phase 137C — Migrate Remaining Callers

### FIX-137C-01: Migrate `SetupService.cs`

**File:** `Services/SetupService.cs` (modify, lines 241-242)

**What:**
1. Replace `StreamUrlSigner.GenerateSignedUrl(...)` with `GenerateResolveToken()` + resolve URL format
2. Key rotation rewrites all .strm files — must use new resolve URL format

**Depends on:** Sprint 134

### FIX-137C-02: Migrate `StreamResolutionService.cs`

**File:** `Services/StreamResolutionService.cs` (modify, lines 53, 61, 100)

**What:**
1. Replace `StreamUrlSigner.Sign(url, secret)` with `GenerateStreamToken(secret)` approach
2. Evaluate whether this service is still needed post-resolver — may be dead code

**Depends on:** Sprint 133

### FIX-137C-03: Clean up `Plugin.cs` references

**File:** `Plugin.cs` (modify)

**What:**
1. Remove any explicit registration of deleted services (if present)
2. Remove references to deleted endpoints in status URLs or health checks

**Depends on:** Phase 137A

### FIX-137C-04: Clean up `StreamResolver.cs`

**File:** `Services/StreamResolver.cs` (modify)

**What:**
1. Remove `ResolveToProxyTokenAsync()` — proxy tokens no longer exist
2. Keep `GetDirectStreamUrlAsync()` if still used (check callers)

**Depends on:** FIX-137A-04

---

## Phase 137D — Remove Dead Configuration

### FIX-137D-01: Remove dead config settings from `PluginConfiguration.cs`

**File:** `PluginConfiguration.cs` (modify)

**What:**
1. Remove `SignatureValidityDays` — no more old-style HMAC signing
2. Remove `MaxConcurrentProxyStreams` — no longer relevant
3. Clean up `Validate()` method to remove validation for deleted fields

**Depends on:** Phase 137C

### FIX-137D-02: Build verification

**What:**
1. `dotnet build -c Release` → 0 errors, 0 new warnings
2. Verify no references to deleted types remain
3. Verify no references to `StreamUrlSigner` class name remain (all should be `PlaybackTokenService`)
4. Verify single DLL output

**Depends on:** All previous fixes

---

## Post-v3 Items (NOT in this sprint)

These files reference `StreamUrlSigner`/old playback paths but don't break and are deferred:
- `Tasks/LinkResolverTask.cs` — pre-resolves URLs into resolution_cache; will align with new resolver model post-v3
- `Tasks/EpisodeExpandTask.cs` — writes episode .strm files; `SeriesPreExpansionService` handles the primary path now

---

## Sprint 137 Dependencies

- **Previous Sprint:** 136 (Improbability Drive)
- **Blocked By:** Sprint 136, Sprint 132, Sprint 133, Sprint 134
- **Blocks:** Sprint 138 (Integration Testing needs clean codebase)

---

## Sprint 137 Completion Criteria

- [ ] `PlaybackService.cs` deleted
- [ ] `SignedStreamService.cs` deleted
- [ ] `StreamProxyService.cs` deleted
- [ ] `ProxySessionStore.cs` deleted
- [ ] `VersionPlaybackService.cs` deleted
- [ ] `VersionSlotController.cs` deleted
- [ ] `test-signed-stream.sh` deleted
- [ ] `StreamUrlSigner.cs` renamed to `PlaybackTokenService.cs`
- [ ] Old methods removed from `PlaybackTokenService` (Sign, Verify, GenerateSignedUrl, ValidateSignature, ComputeHmac, ComputeHmacSimple)
- [ ] `SetupService.cs` migrated to resolve tokens
- [ ] `StreamResolutionService.cs` migrated or removed
- [ ] Dead registrations removed from `Plugin.cs`
- [ ] Dead config settings removed from `PluginConfiguration.cs`
- [ ] `SignatureValidityDays` removed
- [ ] No `StreamUrlSigner` references remain (all renamed to `PlaybackTokenService`)
- [ ] Build succeeds with 0 errors, 0 new warnings
- [ ] Single DLL output confirmed

---

## Sprint 137 Notes

**Files deleted:** 7 (PlaybackService, SignedStreamService, StreamProxyService, ProxySessionStore, VersionPlaybackService, VersionSlotController, test-signed-stream.sh)
**Files renamed:** 1 (StreamUrlSigner → PlaybackTokenService)
**Files modified:** ~5 (`Plugin.cs`, `PluginConfiguration.cs`, `SetupService.cs`, `StreamResolutionService.cs`, `StreamResolver.cs`)

**Risk assessment:** MEDIUM. Deleting services removes entire HTTP endpoints. Renaming StreamUrlSigner affects all callers. The new endpoints must be fully functional before this sprint.

**Migration order:** Migrate callers first (Phase 137C), then rename (Phase 137B). This ensures the rename grep finds zero references to old name.
