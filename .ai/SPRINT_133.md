# Sprint 133 — Resolver Endpoint (`/EmbyStreams/resolve`) + M3u8 Builder

**Version:** v3.3 | **Status:** Code Written (token update pending) | **Risk:** HIGH | **Depends:** Sprint 132

---

## Overview

New `ResolverService.cs` and `M3u8Builder.cs` implementing the resolver + m3u8 model. The resolver collects all available streams at the requested quality tier and all lower tiers, returning an HLS master playlist (m3u8) with quality downgrade options.

**Endpoint:** `GET /EmbyStreams/resolve?token=<resolve_token>&quality=<tier>&id=<id>&idType=<type>[&season=&episode=]`

**Token model (updated):**
- Resolve tokens are long-lived HMAC-signed tokens (365d expiry), embedded in .strm files
- Validation: `StreamUrlSigner.ValidateResolveToken(token, secret, quality, id, idType)` — HMAC + expiry + param match
- Each m3u8 entry gets a fresh short-lived stream token via `GenerateStreamToken(secret)`
- HEVC streams sorted before AVC within each tier; NAME reflects codec (e.g. "1080p H.265 – RealDebrid")

**Key capabilities:**
- Cache hierarchy: in-memory (10 min TTL, 1000 entry cap) → SQLite `resolution_cache` → live AIOStreams query
- Collect ALL streams at requested quality + all lower tiers
- Sort: exact match → HEVC before AVC → cached first → bandwidth descending
- Wrap each debrid URL as `/EmbyStreams/stream?token={stream_token}&url=...` with fresh token
- Return HLS master playlist (m3u8) with `#EXT-X-STREAM-INF` per entry
- `NAME` attribute includes codec: "4K HDR H.265 – Source A" or "1080p H.264 – Source B"
- Always include at least one downgrade option when available
- Failure states: no streams → 503 + `Retry-After: 30`, invalid token → 401

---

## Phase 133A — M3u8 Builder

### FIX-133A-01: Create `Services/M3u8Builder.cs`

**File:** `Services/M3u8Builder.cs` (new)

**What:**
1. `M3u8Variant` model: Bandwidth, Resolution, Codecs, Name, Url
2. `TierMetadata` dictionary: 4 tiers with resolution, HEVC codec, AVC codec, bitrate, display label
3. Display labels: `hd_broad` → "1080p", `sd_broad` → "720p" (internal keys unchanged)
4. `TierOrder` array: highest to lowest quality ordering
5. `BuildMasterPlaylist(List<M3u8Variant>)` → `#EXTM3U` + `#EXT-X-VERSION:3` + per-variant `#EXT-X-STREAM-INF`
6. `CreateVariant(tier, url, sourceLabel, bandwidthOverride?, isHevc?)` → codec-aware variant builder
7. `GetDowngradeTiers(requestedTier)` → all tiers at or below requested

**HEVC/AVC codec strings per tier:**
- 4K HDR: HEVC `hvc1.21600000,ec-3` / AVC `avc1.640033,ec-3`
- 4K SDR: HEVC `hvc1.21600000,ac-3` / AVC `avc1.640033,ac-3`
- 1080p: HEVC `hvc1.640028,mp4a.40.2` / AVC `avc1.640028,mp4a.40.2`
- 720p: HEVC `hvc1.64001f,mp4a.40.2` / AVC `avc1.64001f,mp4a.40.2`

**Depends on:** Sprint 132 (needs stream token generation)

### FIX-133A-02: Define 4 quality tiers

**What:** Collapse existing 6 slots to spec's 4 tiers:
- `4k_hdr` → 4K HDR (3840×2160 HDR10/DV, Atmos/TrueHD) — 30 Mbps
- `4k_sdr` → 4K SDR (3840×2160 SDR, DD+/DTS) — 20 Mbps
- `hd_broad` → 1080p (1920×1080, DD+) — 8 Mbps — DEFAULT
- `sd_broad` → 720p (1280×720, Stereo) — 4 Mbps

**Remove:** `4k_dv`, `1080p` (redundant with hd_broad), `480p`

**Depends on:** None (data model only)

---

## Phase 133B — Resolver Service

### FIX-133B-01: Create `Services/ResolverService.cs`

**File:** `Services/ResolverService.cs` (new)

**What:**
1. `ResolverRequest` DTO with `[Route("/EmbyStreams/resolve", "GET")]` and `[Unauthenticated]`
2. Token validation via `StreamUrlSigner.ValidateResolveToken(token, secret, quality, id, idType)`
3. In-memory manifest cache with 10-minute TTL and 1000-entry eviction
4. SQLite `resolution_cache` lookup as second-tier cache
5. Live AIOStreams query fallback: `GetMovieStreamsAsync` / `GetSeriesStreamsAsync`
6. Stream-to-tier mapping using `StreamHelpers.ParseQualityTier` + HDR detection
7. Per-tier sorting: cached first → HEVC before AVC → by codec score → max 3 variants per tier
8. Per-variant stream token: `StreamUrlSigner.GenerateStreamToken(secret)` — fresh token per m3u8 entry
9. HEVC detection: `IsHevcStream(stream)` — checks codec field and filename
10. Proxy URL wrapping: `{embyBase}/EmbyStreams/stream?token={streamToken}&url=...`
11. Quality mismatch logging: `ts | id | idType | requested | served | token[6]`
12. Empty manifest logging: `ts | id | idType | requested | result=empty`

**Depends on:** FIX-133A-01

---

## Phase 133C — Registration + Build

### FIX-133C-01: Verify service auto-discovery

**What:** Both services use `[Route]` and `[Unauthenticated]` attributes — Emby auto-discovers them. No manual registration in Plugin.cs needed.

### FIX-133C-02: Build verification

**What:** `dotnet build -c Release` → 0 errors, 0 new warnings

**Depends on:** FIX-133B-01

---

## Sprint 133 Dependencies

- **Previous Sprint:** 132 (Stream Endpoint + Token Methods)
- **Blocked By:** Sprint 132
- **Blocks:** Sprint 134 (Hydration Pipeline needs resolve URL format)

---

## Sprint 133 Completion Criteria

- [x] `Services/M3u8Builder.cs` created
- [x] `Services/ResolverService.cs` created
- [x] HEVC/AVC codec awareness in tier metadata
- [x] Display labels updated: hd_broad → "1080p", sd_broad → "720p"
- [x] IsHevcStream detection from codec field + filename
- [x] HEVC sorted before AVC within each tier
- [x] NAME attribute includes codec: "1080p H.265 – Source A"
- [ ] Build succeeds with 0 errors
- [ ] Resolve endpoint returns valid m3u8 with stream tokens
- [ ] Quality tier downgrade ordering correct
- [ ] In-memory cache hits on repeated requests
- [ ] Invalid/expired resolve token returns 401
- [ ] No streams returns 503 + Retry-After: 30
- [ ] Quality mismatch logged

---

## Sprint 133 Notes

**Files created:** 2 (`Services/M3u8Builder.cs`, `Services/ResolverService.cs`)

**Risk assessment:** HIGH. The resolver orchestrates AIOStreams queries, tier mapping, caching, and m3u8 generation. Edge cases around stream-to-tier mapping, HEVC detection, and the downgrade ladder require testing.

**Design decisions:**
- 4 tiers replace 6 slots — simpler mental model, matches spec
- In-memory cache before DB cache — fast repeated playback
- Max 3 variants per tier — keeps manifest small for Emby
- HEVC/AVC detection from both codec field and filename — covers AIOStreams variations
- Stream tokens generated per-variant — each m3u8 entry has independent token
- DB cache stores a marker (`m3u8:{tier}:{count}`) not full manifest — memory cache is authoritative
- Resolve token validates quality/id/idType params — prevents URL tampering
