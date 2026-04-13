# Sprint 140 — Improbability Drive Validation

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 139

---

## Overview

Full cross-cutting validation of the entire Sprint 131–139 change order.
This sprint does not write new features. It audits what was actually built
against the unified design spec (v3.1), corrects any drift introduced
during implementation, and produces a clean, verified baseline.

The name is apt: after eight sprints of parallel construction, this is
where we find out if the Infinite Improbability Drive actually works —
or if it has turned into a bowl of petunias.

**Scope:** Code audit + correction + doc update only.
**Must not:** Add new features, change architecture, or touch
catalog/content management (🔕 out of scope throughout).

---

## Phase 140A — Security Audit (Highest Priority)

The most critical delta introduced during our design iteration.
Verify these before anything else.

### FIX-140A-01: Verify PlaybackTokenService exists and is correct

**File:** `Services/PlaybackTokenService.cs` (audit)

**What:**
1. Confirm `StreamUrlSigner.cs` was renamed to `PlaybackTokenService.cs`
   and NOT deleted
2. Confirm the following methods exist and are correctly implemented:

   ```csharp
   // 365-day scoped resolve token
   string GenerateResolveToken(string quality, string id, string idType)
   // payload: $"resolve:{quality}:{id}:{idType}:{expiry_unix}"
   // token:   base64url(payload) + "." + hex_hmac

   // 4-hour scoped stream token
   string GenerateStreamToken(string encodedUrl)
   // payload: $"stream:{encodedUrl}:{expiry_unix}"
   // token:   base64url(payload) + "." + hex_hmac

   bool ValidateResolveToken(string token, string quality, string id, string idType)
   bool ValidateStreamToken(string token, string encodedUrl)
   ```

3. Confirm HMAC algorithm is HMAC-SHA256 keyed on PluginSecret
4. Confirm both Validate methods return `false` (never throw) on:
   - Malformed token
   - Expired token
   - Signature mismatch
   - Scope mismatch (wrong quality/id/url)
5. Confirm tokens are logged truncated to first 6 chars only

**Fix if wrong:** Implement or correct `PlaybackTokenService.cs` per above.

**Acceptance:** `PlaybackTokenService.cs` exists, compiles, passes manual inspection of all four methods.

### FIX-140A-02: Verify StreamEndpointService uses HMAC token validation

**File:** `Services/StreamEndpointService.cs` (audit)

**What:**
1. Find the token validation line — it must NOT be:
   ```csharp
   token == PluginSecret   // ← WRONG — raw equality, security flaw
   ```
   It must be:
   ```csharp
   PlaybackTokenService.ValidateStreamToken(token, encodedUrl)
   ```
2. Confirm returns 401 on validation failure — never 403
3. Confirm `PluginSecret` does not appear anywhere in response headers, error bodies, or log lines

**Fix if wrong:** Replace raw equality check with `ValidateStreamToken()` call. Return 401 on false.

**Acceptance:** No `== PluginSecret` comparison in `StreamEndpointService.cs`.

### FIX-140A-03: Verify ResolverService uses HMAC token validation

**File:** `Services/ResolverService.cs` (audit)

**What:**
1. Confirm incoming token validated via:
   ```csharp
   PlaybackTokenService.ValidateResolveToken(token, quality, id, idType)
   ```
   Not raw equality.
2. Confirm each `/EmbyStreams/stream` URL in the generated m3u8 uses a freshly minted 4-hour stream token per entry:
   ```csharp
   var streamToken = PlaybackTokenService.GenerateStreamToken(encodedUrl);
   // → /EmbyStreams/stream?token={streamToken}&url={encodedUrl}
   ```
   Not the incoming resolve token recycled into the manifest.
3. Confirm the raw debrid URL never appears in a log line

**Fix if wrong:** Replace validation and/or manifest URL generation per above.

**Acceptance:** Resolve token validated by scope; each m3u8 entry has its own fresh stream token.

### FIX-140A-04: Verify HLS segment rewriting mints fresh stream tokens

**File:** `Services/StreamEndpointService.cs` (audit)

**What:** When `StreamEndpointService` rewrites relative segment URLs in an upstream HLS playlist into absolute `/EmbyStreams/stream` URLs, each rewritten URL must carry a newly generated stream token for that specific segment URL — not a copy of the request's token.

```csharp
// For each rewritten segment:
var segToken = PlaybackTokenService.GenerateStreamToken(
    Uri.EscapeDataString(absoluteSegmentUrl));
rewritten = $"{base}/EmbyStreams/stream?token={segToken}" +
            $"&url={Uri.EscapeDataString(absoluteSegmentUrl)}";
```

**Fix if wrong:** Update the HLS rewriting loop to call `GenerateStreamToken()` per segment.

**Acceptance:** Rewritten segment URLs each carry a unique token scoped to that segment URL.

### FIX-140A-05: Verify VersionMaterializer generates HMAC resolve tokens

**File:** `Services/VersionMaterializer.cs` (audit)

**What:**
1. `BuildStrmUrl()` must call:
   ```csharp
   PlaybackTokenService.GenerateResolveToken(quality, id, idType)
   ```
2. The raw `PluginSecret` must NOT appear in any generated `.strm` URL
3. Token expiry must be 365 days from generation time

**Fix if wrong:** Replace `PluginSecret` in URL with `GenerateResolveToken()` output.

**Acceptance:** Open any `.strm` file written after Sprint 134 — the `token=` param is a base64url string, not the raw HMAC-SHA256 key.

### FIX-140A-06: Verify DiscoverService generates HMAC resolve tokens

**File:** `Services/DiscoverService.cs` (audit)

**What:** Same check as FIX-140A-05 for the Discover "Add to Library" path. The `.strm` written by `DiscoverService.Post()` must use `PlaybackTokenService.GenerateResolveToken()`, not raw `PluginSecret`.

**Fix if wrong:** Update `DiscoverService.Post()` per Sprint 139 override notes.

**Acceptance:** No `PluginSecret` in `.strm` URL written by Discover.

---

## Phase 140B — Quality Tier Audit

### FIX-140B-01: Verify tier display labels throughout

**Files:** `Services/M3u8Builder.cs`, `Data/VersionSlotRepository.cs`, `Configuration/configurationpage.html`, `Configuration/configurationpage.js` (audit all)

**What:** Confirm display labels (not internal keys) read as follows everywhere they appear in UI, m3u8 NAME attributes, and seed data:

| Internal key | Required display label |
|--------------|----------------------|
| `4k_hdr`     | 4K HDR               |
| `4k_sdr`     | 4K SDR               |
| `hd_broad`   | 1080p                |
| `sd_broad`   | 720p                 |

Search for "HD Broad" and "SD Broad" across the entire repo. Any occurrence is a bug.

**Fix if wrong:** Replace all "HD Broad" → "1080p" and "SD Broad" → "720p" in display strings. Internal keys (`hd_broad`, `sd_broad`) are unchanged.

**Acceptance:** `grep -r "HD Broad" .` returns zero results. `grep -r "SD Broad" .` returns zero results.

### FIX-140B-02: Verify HEVC sort order in M3u8Builder

**File:** `Services/M3u8Builder.cs` (audit)

**What:** Within each quality tier, confirm variants are sorted:
1. HEVC/H.265 streams descending by bandwidth
2. AVC/H.264 streams descending by bandwidth
3. Unknown codec treated as AVC (graceful fallback, no manifest break)
4. Max 3 variants per tier total (HEVC + AVC combined)
5. `NAME` attribute includes codec suffix: "1080p H.265", "1080p H.264", "4K HDR H.265", etc.
6. `CODECS` attribute uses `hvc1.x.x` for HEVC, `avc1.x.x` for AVC

**Fix if wrong:** Update sort logic and `NAME`/`CODECS` generation per above.

**Acceptance:** Generate a test manifest for a title with both HEVC and AVC streams at the same tier — HEVC entry appears first.

### FIX-140B-03: Verify removed slots are gone

**File:** `Data/VersionSlotRepository.cs` (audit)

**What:** Confirm seed data contains exactly 4 tiers and no others: `4k_hdr`, `4k_sdr`, `hd_broad`, `sd_broad`

Search for `4k_dv`, `1080p` (as a slot key, not label), `480p`. Any occurrence in seed data is a bug.

**Fix if wrong:** Remove orphaned slot definitions from seed data.

**Acceptance:** `VersionSlotRepository` seeds exactly 4 rows.

---

## Phase 140C — Deprecated Code Audit

### FIX-140C-01: Verify deleted services are gone

**What:** Confirm the following files do not exist anywhere in the repo:

```
Services/PlaybackService.cs
Services/SignedStreamService.cs
Services/StreamProxyService.cs
Services/ProxySessionStore.cs
Services/VersionPlaybackService.cs
Controllers/VersionSlotController.cs
test-signed-stream.sh
```

**Fix if wrong:** Delete any survivors. Check for references to their types before deleting — fix callers first.

**Acceptance:** `find . -name "PlaybackService.cs"` → no results. Repeat for each file above.

### FIX-140C-02: Verify removed config properties are gone

**File:** `PluginConfiguration.cs` (audit)

**What:** Confirm the following properties do not exist:

```csharp
ProxyMode           // removed Sprint 134
SignatureValidityDays  // removed Sprint 134
MaxConcurrentProxyStreams  // removed Sprint 134
```

**Fix if wrong:** Remove property and clean up `Validate()` method.

**Acceptance:** `grep -r "ProxyMode" .` → zero results in `.cs` files.

### FIX-140C-03: Verify no raw PluginSecret in any URL

**What:** This is the single most important security invariant. Run across the entire repo:

```bash
grep -r "PluginSecret" . --include="*.cs" \
  | grep -v "//.*PluginSecret" \
  | grep -v "PlaybackTokenService" \
  | grep -v "Configuration\." \
  | grep -v "Plugin\.cs"
```

Any result that shows `PluginSecret` being interpolated into a URL string is a critical bug.

**Fix if wrong:** Replace with appropriate `GenerateResolveToken()` or `GenerateStreamToken()` call.

**Acceptance:** Zero hits on the grep above outside of configuration/generation contexts.

---

## Phase 140D — Endpoint Behaviour Audit

### FIX-140D-01: Safari Range compliance spot-check

**What:**
```bash
curl -v -H "Range: bytes=0-1" \
  "http://localhost:8096/EmbyStreams/stream?token=<valid>&url=<encoded>"
```

Verify:
1. Response status: `206 Partial Content`
2. `Content-Range: bytes 0-1/<total>` present
3. `Content-Length: 2` present
4. `Accept-Ranges: bytes` present
5. Body: exactly 2 bytes
6. `Content-Encoding` header: absent

**Fix if wrong:** Debug `StreamEndpointService` Safari probe handler. This is the most common Apple client failure mode.

**Acceptance:** All six conditions verified.

### FIX-140D-02: Token expiry spot-check

**What:** Manually construct an expired resolve token (set expiry to 1 second in the past) and call `/EmbyStreams/resolve` with it.

Verify:
1. Response: `401 Unauthorized`
2. No indication in response body of why it failed (don't reveal whether expired vs invalid vs wrong scope)

Manually construct an expired stream token and call `/EmbyStreams/stream` with it.

Verify: `401 Unauthorized`

**Fix if wrong:** Ensure expiry check in `ValidateResolveToken()` and `ValidateStreamToken()` fires before HMAC check (fast fail on expired).

**Acceptance:** Both expired token tests return 401 with no diagnostic detail in response body.

### FIX-140D-03: Downgrade ladder spot-check

**What:** Call `/EmbyStreams/resolve` with `quality=4k_hdr` for a title where only 1080p streams exist in AIOStreams.

Verify the returned m3u8:
1. Contains at least one `hd_broad` (1080p) variant
2. Does NOT contain any `4k_hdr` entries (none available)
3. `NAME` attribute shows "1080p" or "1080p H.265" / "1080p H.264" (not "HD Broad")
4. At least one entry present (never empty manifest when downgrade exists)

**Fix if wrong:** Debug `ResolverService` downgrade ladder logic.

**Acceptance:** Non-empty manifest with correct labels returned.

### FIX-140D-04: Improbability Drive status indicator spot-check

**What:**
1. Navigate to Emby Dashboard → Plugins → EmbyStreams → Improbability Drive tab
2. Confirm DON'T PANIC header renders in large, friendly letters
3. Confirm status indicator shows one of: 🟢 🟡 🔴 with correct label
4. Press Summon Marvin:
   - Button label changes to "Marvin is grumbling…"
   - Spinner appears
   - Button returns to "Summon Marvin" on completion
   - Status indicator refreshes
5. Confirm no logs, no technical details, no quality breakdowns visible
6. Confirm `GET /EmbyStreams/Marvin` still returns a Marvin quote (Easter egg preserved)

**Fix if wrong:** Debug `StatusService.cs` and `configurationpage.js` `summonMarvin()` / `refreshImprobabilityStatus()`.

**Acceptance:** All six steps verified.

---

## Phase 140E — Final Build + Docs

### FIX-140E-01: Clean build verification

**What:**
```bash
dotnet build -c Release
```
1. 0 errors
2. 0 new warnings vs Sprint 131 baseline
3. Single `EmbyStreams.dll` in output — no satellite DLLs

**Acceptance:** Build output is one file.

### FIX-140E-02: Update documentation

**Files:** `.ai/REPO_MAP.md`, `BACKLOG.md`, `.ai/CURRENT_TASK.md`

**What:**
1. Add `Services/PlaybackTokenService.cs` to REPO_MAP
2. Remove all deleted services from REPO_MAP
3. Mark Sprints 131–139 complete in BACKLOG
4. Update CURRENT_TASK with final status
5. Remove any remaining references to `ProxyMode`, `SignedStream`, `HD Broad`, `SD Broad`, or `/EmbyStreams/Play` in docs

**Acceptance:** Docs reflect actual current state of repo.

---

## Sprint 140 Dependencies

- **Previous Sprint:** 139 (Discover Alignment)
- **Blocked By:** Sprints 131–139 all complete
- **Blocks:** Nothing — this is the validation gate

---

## Sprint 140 Completion Criteria

**Security (P0 — must all pass before anything else ships):**

- [ ] `PlaybackTokenService.cs` exists with all four methods correct
- [ ] `StreamEndpointService` uses `ValidateStreamToken()` — no raw equality
- [ ] `ResolverService` uses `ValidateResolveToken()` — no raw equality
- [ ] Each m3u8 stream entry has its own fresh 4-hour stream token
- [ ] HLS segment rewriting mints per-segment stream tokens
- [ ] `VersionMaterializer` uses `GenerateResolveToken()` — no raw secret in URLs
- [ ] `DiscoverService` uses `GenerateResolveToken()` — no raw secret in URLs
- [ ] `grep` for raw `PluginSecret` in URLs returns zero results

**Quality Tiers:**

- [ ] `grep -r "HD Broad"` returns zero results
- [ ] `grep -r "SD Broad"` returns zero results
- [ ] `VersionSlotRepository` seeds exactly 4 tiers
- [ ] HEVC sorts before AVC within each tier in m3u8

**Deprecated Removal:**

- [ ] All 7 deleted files confirmed absent
- [ ] `ProxyMode`, `SignatureValidityDays`, `MaxConcurrentProxyStreams` absent from config

**Endpoint Behaviour:**

- [ ] Safari `Range: bytes=0-1` returns 206 with exactly 2 bytes
- [ ] Expired token returns 401 with no diagnostic detail
- [ ] Downgrade ladder returns correct labels (not "HD Broad")
- [ ] Improbability Drive renders, button works, Easter eggs preserved

**Build + Docs:**

- [ ] `dotnet build -c Release` → 0 errors, 0 warnings, single DLL
- [ ] REPO_MAP, BACKLOG, CURRENT_TASK updated

---

## Sprint 140 Notes

**Files modified:** 0–6 (corrections only — if all sprints landed correctly, this sprint is pure verification with doc updates)

**Risk assessment:** LOW if sprints 131–139 followed the delta brief exactly. MEDIUM if Claude Code deviated on the security overrides (the raw token equality check is the most likely regression).

The P0 security checks must gate everything. If FIX-140A-01 through FIX-140A-06 have any failures, fix them before running the endpoint behaviour tests — a system with raw `PluginSecret` in URLs should not be tested against a live debrid account.

**Design decisions locked in this sprint:**

- Resolve token expiry: 365 days (long-lived file credential)
- Stream token expiry: 4 hours (single session; fresh on every `/resolve`)
- Binge pre-cache warms SQLite debrid lookups only — tokens are always minted fresh at resolve time, so no expiry risk for binge watching
- VersionSlotController: deleted (confirmed dead code)
- StreamUrlSigner: renamed to PlaybackTokenService (not deleted)

Don't Panic. The probability of all eight sprints landing perfectly is approximately one in one followed by a very large number of zeros. That's what this sprint is for.
