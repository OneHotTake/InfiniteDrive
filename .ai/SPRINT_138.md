# Sprint 138 — Integration Testing

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 137

---

## Overview

End-to-end integration testing against a live Emby server. Validates the complete unified design spec flow from .strm generation through playback, including token authentication and HEVC/AVC codec handling.

---

## Phase 138A — Build Verification

### FIX-138A-01: Clean build check

**What:**
1. `dotnet build -c Release` → 0 errors, 0 new warnings
2. Verify single `EmbyStreams.dll` in output (no satellite DLLs)
3. File size check — should not grow significantly from Sprint 131 baseline

**Depends on:** Sprint 137

---

## Phase 138B — E2E Test Scenarios

### FIX-138B-01: Fresh install → single tier → sync → play

**What:**
1. `./emby-reset.sh` → clean start
2. Configure with AIOStreams URL
3. Run wizard → HD Broad only (default)
4. Sync catalog → verify .strm files written with resolve token URLs
5. Verify .strm contains `/EmbyStreams/resolve?token={resolve_token}&quality=hd_broad&id=tt...&idType=imdb`
6. Verify resolve token is NOT raw PluginSecret (check format: base64url.hex_hmac)
7. Play item → verify m3u8 manifest returned by resolve endpoint
8. Verify m3u8 entries contain stream tokens (NOT raw PluginSecret)
9. Verify stream proxy returns video data

**Depends on:** FIX-138A-01

### FIX-138B-02: Multi-tier rehydration

**What:**
1. Enable 4K HDR tier via settings
2. Confirm rehydration warning
3. Verify multi-tier .strm/.nfo files written for all catalog items
4. Verify default .strm still unsuffixed, 4K HDR .strm has suffix
5. Verify each .strm has its own resolve token with correct quality param

**Depends on:** FIX-138B-01

### FIX-138B-03: Resolve endpoint downgrade tiers

**What:**
1. Request `quality=4k_hdr` for an item with only 1080p streams
2. Verify m3u8 contains `hd_broad` variant (downgrade)
3. Verify NAME attribute shows correct quality label + codec (e.g. "1080p H.264 – Source A")
4. Verify HEVC stream appears before AVC stream in same tier

**Depends on:** FIX-138B-01

### FIX-138B-04: Safari Range compliance

**What:**
1. Send `Range: bytes=0-1` to `/EmbyStreams/stream`
2. Verify `206 Partial Content` response
3. Verify exactly 2 bytes returned
4. Verify `Content-Range: bytes 0-1/{total}` header
5. Verify `Content-Length: 2` header

**Depends on:** FIX-138B-01

### FIX-138B-05: Token authentication

**What:**
1. **Stream endpoint:**
   - Invalid stream token → verify 401
   - Expired stream token → verify 401
   - Missing token → verify 401
   - Valid stream token → verify stream data returned
2. **Resolve endpoint:**
   - Invalid resolve token → verify 401
   - Expired resolve token → verify 401
   - Tampered quality param (token says hd_broad, request says 4k_hdr) → verify 401
   - Valid resolve token → verify m3u8 returned
3. **PluginSecret never in URLs:** grep .strm files for raw PluginSecret → should not appear

**Depends on:** FIX-138B-01

### FIX-138B-06: AIOStreams failure handling

**What:**
1. Configure invalid AIOStreams URL
2. Verify resolve returns 503 + `Retry-After: 30`
3. Verify cached manifest served when available (memory cache)

**Depends on:** FIX-138B-01

### FIX-138B-07: HEVC/AVC detection and display

**What:**
1. Find a movie with both HEVC and AVC streams available
2. Verify m3u8 shows HEVC variant before AVC variant in same tier
3. Verify NAME labels: "1080p H.265 – Source" vs "1080p H.264 – Source"
4. Verify CODECS attribute: `hvc1.*` for HEVC, `avc1.*` for AVC
5. Verify display labels: hd_broad shows "1080p", sd_broad shows "720p"

**Depends on:** FIX-138B-01

### FIX-138B-08: Doctor reconciliation

**What:**
1. Trigger Doctor run
2. Verify per-tier add/delete operations
3. Verify doctor log includes tier column
4. Verify tier-aware health check

**Depends on:** FIX-138B-02

### FIX-138B-09: Improbability Drive

**What:**
1. Navigate to Improbability Drive tab
2. Verify status indicator shows correct color
3. Press Summon Marvin → verify spinner and completion
4. Verify status refreshes after Marvin completes

**Depends on:** Sprint 136

### FIX-138B-10: Clean server start

**What:**
1. `./emby-reset.sh` → server starts cleanly
2. Plugin loads without errors in log
3. No assembly loading failures
4. All endpoints accessible
5. No references to deleted services in log

**Depends on:** Sprint 137

---

## Phase 138C — Final Verification

### FIX-138C-01: Final build + output check

**What:**
1. `dotnet build -c Release` → 0 errors, 0 new warnings
2. Single DLL output confirmed
3. No deleted service types referenced anywhere (grep for PlaybackService, SignedStreamService, StreamProxyService, ProxySessionStore, VersionPlaybackService, VersionSlotController)
4. No `StreamUrlSigner` references remain (all renamed to `PlaybackTokenService`)
5. `./emby-reset.sh` → clean start

**Depends on:** All test scenarios pass

### FIX-138C-02: Update documentation

**What:**
1. Update `.ai/REPO_MAP.md` — add/remove entries for all Sprint 132-138 changes
2. Update `BACKLOG.md` — mark completed sprints
3. Update `.ai/CURRENT_TASK.md` — final status

**Depends on:** FIX-138C-01

---

## Sprint 138 Dependencies

- **Previous Sprint:** 137 (Deprecated Removal)
- **Blocked By:** Sprint 137
- **Blocks:** None (final sprint)

---

## Sprint 138 Completion Criteria

- [ ] Build: 0 errors, 0 new warnings
- [ ] Single DLL output (no satellite DLLs)
- [ ] Fresh install → sync → play works end-to-end
- [ ] Multi-tier rehydration works
- [ ] Resolve returns m3u8 with downgrade tiers
- [ ] HEVC streams sorted before AVC in m3u8
- [ ] Display labels: "1080p" and "720p" (not "1080p HD" / "720p SD")
- [ ] Safari Range: bytes=0-1 returns 206 with 2 bytes
- [ ] Invalid/expired stream token returns 401
- [ ] Invalid/expired resolve token returns 401
- [ ] Tampered resolve token params returns 401
- [ ] PluginSecret never appears in .strm files or m3u8
- [ ] AIOStreams down returns 503 + Retry-After
- [ ] Doctor reconciliation per-tier
- [ ] Improbability Drive status + Summon Marvin
- [ ] `./emby-reset.sh` clean start
- [ ] No deleted service types referenced
- [ ] No `StreamUrlSigner` references (all renamed)
- [ ] REPO_MAP.md updated
- [ ] BACKLOG.md updated
- [ ] CURRENT_TASK.md updated

---

## Sprint 138 Notes

**Files modified:** ~0-2 (documentation only)

**Risk assessment:** MEDIUM. Integration testing reveals issues that unit tests miss. Token validation, HEVC detection, and multi-tier interactions are the highest-risk areas.

**Test infrastructure:** All tests use `./emby-reset.sh` for clean state. Manual verification against live Emby server. No automated test framework needed for this sprint.
