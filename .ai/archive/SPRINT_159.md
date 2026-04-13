# Sprint 159 — Stream Availability Probe & Fallback

**Version:** v3.3 | **Status:** Under Review — not yet approved | **Risk:** LOW | **Depends:** Sprint 156

---

## Overview

Before serving the top-ranked stream to a user, do a lightweight HTTP
probe to confirm the URL actually responds. Fall back through ranked
candidates until one works, or serve the best available with a Warn log.

Today `StreamResolutionService` picks rank-0 and serves it blind. Dead
CDN URLs silently fail the user with a spinner or error. This sprint
adds a minimal availability check — no codec inspection, no quality
gating, no ffprobe — just "does this URL return bytes?"

### Why This Exists

Ranked streams are ranked by metadata quality (resolution, codec hints,
cache status), not by liveness. A highly-ranked stream can be behind a
dead CDN link. The gap costs users playback failures that a 200ms HTTP
probe would have caught and routed around.

### Non-Goals

- ❌ ffprobe / codec / HDR / resolution validation
- ❌ Minimum quality settings or quality-based hard gating
- ❌ Per-provider failure counters or Status tab surfacing
- ❌ Sync-time pre-probing (future sprint)
- ❌ Any changes to ranking logic

---

## Phase 159A — StreamProbeService

### FIX-159A-01: Create StreamProbeService

**File:** `Services/StreamProbeService.cs` (create)

**What:**

1. New singleton class `StreamProbeService`.
2. Dependencies: `ILogManager`, `IHttpClient`.
3. Public record:
   ```csharp
   public sealed record ProbeResult(
       bool Ok,
       int? StatusCode,
       string Reason);   // "ok" | "timeout" | "http_{code}" | "error"
   ```
4. Method:
   ```csharp
   Task<ProbeResult> ProbeAsync(string url, CancellationToken ct);
   ```
5. Implementation:
   1. Send `HEAD {url}` with a 500ms timeout.
   2. If response is 2xx or 206 → return `ProbeResult(Ok: true, ...)`.
   3. If HEAD returns 405 (Method Not Allowed) → retry with
      `GET {url}` and `Range: bytes=0-1023`, 500ms timeout.
      If 206 or 200 → return Ok.
   4. Any other status code → return `ProbeResult(Ok: false, StatusCode, "http_{code}")`.
   5. `TaskCanceledException` / timeout → return `ProbeResult(Ok: false, null, "timeout")`.
   6. Any other exception → return `ProbeResult(Ok: false, null, "error")`.
6. No retry logic inside `ProbeAsync` — callers handle fallback.
7. Log at Debug for each probe attempt and result.

---

## Phase 159B — Wire into StreamResolutionService

### FIX-159B-01: Probe top 3 candidates before serving

**File:** `Services/StreamResolutionService.cs` (modify)

**What:**

1. Inject `StreamProbeService` into `StreamResolutionService`.
2. After `_resolver.ResolveStreamsAsync` returns the ranked list, and
   before picking rank-0, run the probe loop:
   ```
   totalBudget = 1500ms CancellationTokenSource
   for candidate in ranked.Take(3):
       result = await _probe.ProbeAsync(candidate.Url, linkedCt)
       if result.Ok → serve this candidate immediately
   ```
3. Short-circuit on first OK — do not probe remaining candidates.
4. The 1500ms budget is shared across all probes via a linked
   `CancellationTokenSource`. When budget expires, exit the loop.
5. If the loop exhausts all 3 without an OK (timeout or all fail):
   - Serve `ranked[0]` (rank-0, best-effort).
   - Log at Warn:
     ```
     [StreamResolutionService] Best-effort fallback for {MediaId}:
     all 3 probes failed. Serving rank-0 ({url}). Failures: {reasons}
     ```
6. If the ranked list has fewer than 3 candidates, probe only what
   exists — do not pad.
7. The probe loop only runs on live resolution (step 4 in the existing
   ranked fallback hierarchy). Cache hits (steps 1–3) are served
   immediately without probing — they were already validated by a
   previous live resolution.

---

## Phase 159C — Registration

### FIX-159C-01: Register StreamProbeService

**File:** `Plugin.cs` (modify)

**What:**

Register `StreamProbeService` as a singleton alongside the other
service registrations. Single line — no other changes to `Plugin.cs`.

---

## Phase 159D — Build & Verification

### FIX-159D-01: Build

`dotnet build -c Release` — 0 errors, 0 net-new warnings.

---

### FIX-159D-02: Grep checklist

| Pattern | Expected |
|---|---|
| `StreamProbeService` | ≥ 3 (create, ctor inject, Plugin.cs) |
| `ProbeAsync` | ≥ 2 (definition + caller) |
| `ffprobe` | 0 |
| `best-effort` or `BestEffort` | ≥ 1 (Warn log) |

---

### FIX-159D-03: Manual smoke test

1. Find a known-dead stream URL (or mock one by temporarily poisoning
   a URL in the DB).
2. Trigger playback for that item.
3. Assert: playback resolves to a different candidate (not the dead URL)
   and the dead URL does **not** reach the user's player.
4. Assert: a Warn log line appears with the failure reason.
5. Assert: total resolution time is under 2 seconds wall-clock.

---

## Sprint 159 Completion Criteria

- [ ] `Services/StreamProbeService.cs` created
- [ ] `ProbeAsync` does HEAD → GET-range fallback, 500ms per attempt
- [ ] 1.5s total budget across all probes, shared via linked CTS
- [ ] Probe loop in `StreamResolutionService` — max 3 candidates, short-circuit on first OK
- [ ] Best-effort fallback logs at Warn with candidate URL and failure reasons
- [ ] Cache hits bypass the probe loop entirely
- [ ] `StreamProbeService` registered as singleton in `Plugin.cs`
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Grep checklist clean
- [ ] Smoke test: dead URL is skipped, fallback candidate served

---

## Notes

**Files created:** 1 (`Services/StreamProbeService.cs`)

**Files modified:** 2 (`Services/StreamResolutionService.cs`, `Plugin.cs`)

**Files deleted:** 0

**Config fields added:** 0

**Risk: LOW** — purely additive. The probe loop has a hard wall-clock
cap; if probing fails or times out, behavior degrades gracefully to
the existing rank-0 pick. Cache hits are unaffected.
