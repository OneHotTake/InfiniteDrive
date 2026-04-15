# Sprint 302 — Reliability & Resilience

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 301

## Overview

Improve system behavior under degraded conditions. Circuit breakers, smarter rate limiting, and proactive validation.

## Task 302-01: Per-Resolver Circuit Breaker

**Problem:** Dead resolver retried on every request, eating 15s timeout each time

**Files:** `Services/AioStreamsClient.cs`, `Services/ResolverHealthTracker.cs` (new)

**Changes:**
- Create `ResolverHealthTracker` singleton tracking per-resolver state
- Track: consecutive failures, last failure time, circuit open until
- Circuit opens after 3 consecutive failures
- Circuit stays open for: 30s → 60s → 120s → 300s (exponential with cap)
- Add ±10% jitter to prevent thundering herd
- Circuit half-opens after timeout (one request allowed to test)
- Success resets failure count and closes circuit

**Acceptance Criteria:**
- [ ] Dead resolver skipped after 3 failures (circuit open)
- [ ] Circuit auto-recovers after timeout
- [ ] Jitter prevents all clients retrying simultaneously
- [ ] Health state persists across requests (singleton)
- [ ] Resolver coming back online detected within 5 minutes

**Effort:** M

---

## Task 302-02: Bursty Rate Limiting (Stremio-Style)

**Problem:** Current cooldown is time-based; real usage is bursty (browse → many requests → idle)

**Files:** `Services/CooldownGate.cs`

**Changes:**
- Replace fixed delay with burst-aware limiting
- Track `_lastCallTime` per operation type
- If last call was <100ms ago, add small delay (50-100ms)
- If last call was >2s ago, no delay needed (new burst starting)
- On 429 response, set per-kind cooldown (not global)
- Cooldown duration: use `Retry-After` header if present, else 60s default

**Acceptance Criteria:**
- [ ] Rapid sequential calls get small delays (not blocking)
- [ ] Fresh burst after idle has no delay
- [ ] 429 on catalog sync doesn't freeze playback resolution
- [ ] Respects `Retry-After` header from AIOStreams

**Effort:** M

---

## Task 302-03: CooldownGate Thread Safety

**Problem:** Race condition allows concurrent requests to bypass cooldown

**Files:** `Services/CooldownGate.cs`

**Changes:**
- Add `lock` or `SemaphoreSlim` around cooldown state access
- Ensure `_lastCallTime` updates are atomic
- Ensure cooldown check + wait is atomic operation

**Acceptance Criteria:**
- [ ] 100 concurrent requests don't all bypass cooldown
- [ ] No deadlocks under load
- [ ] Performance impact negligible (<1ms added latency)

**Effort:** S

---

## Task 302-04: StreamProbeService Implementation

**Problem:** Dead CDN URLs served blind, user sees spinner then failure

**Files:** `Services/StreamProbeService.cs` (new), `Services/StreamResolutionService.cs`

**Changes:**
- Create `StreamProbeService` with `ProbeAsync(url)` method
- Probe method: HEAD request with 2s timeout
- If HEAD returns 405, retry with GET + `Range: bytes=0-1023`
- Return `ProbeResult`: OK, Timeout, HttpError (with status code)
- In resolution: probe top 3 candidates, serve first OK
- Total probe budget: 5s (not per-probe, allows slow CDNs)
- If all probes fail: log at Warn, serve rank-0 anyway (best effort)

**Acceptance Criteria:**
- [ ] Dead URL detected before serving (most of the time)
- [ ] Slow but working CDN not marked dead (2s per-probe timeout)
- [ ] Probe failure doesn't block playback (falls back to best effort)
- [ ] Total probe time bounded (5s max regardless of candidate count)

**Effort:** M

---

## Task 302-05: Public Endpoint Rate Limiting

**Problem:** Attacker floods `/resolve` → triggers AIOStreams 429 → system freezes

**Files:** `Services/ResolverService.cs`, `Services/StreamEndpointService.cs`, `Services/RateLimiter.cs` (new)

**Changes:**
- Create simple in-memory rate limiter (sliding window per IP)
- Limits: 30 resolve/minute, 120 stream/minute per IP
- Return 429 with `Retry-After: 60` when exceeded
- Exempt localhost / configured trusted IPs
- Log rate limit hits at Warn (potential attack or misconfigured client)

**Acceptance Criteria:**
- [ ] Normal usage (1 user browsing) never hits limit
- [ ] Flood from single IP blocked after threshold
- [ ] Rate limit doesn't affect other IPs
- [ ] Legitimate high-usage (family sharing) can be whitelisted

**Effort:** M

---

## Task 302-06: Marvin Sync Safety

**Problem:** If one resolver unavailable during sync, items might be incorrectly removed

**Files:** `Tasks/MarvinTask.cs` (or equivalent sync task)

**Changes:**
- Before removing any item, verify it's absent from BOTH resolvers
- If one resolver is down (circuit open or timeout), skip removal for that sync cycle
- Log at Info: "Skipping removals, resolver {name} unavailable"
- Add `last_verified_at` column to track when item was confirmed in catalog
- Only remove items not verified in >7 days AND absent from available resolver

**Acceptance Criteria:**
- [ ] Resolver down → no items removed during that sync
- [ ] Item removed only when confirmed absent from both resolvers
- [ ] Grace period prevents premature removal on transient failures
- [ ] Orphaned items eventually cleaned (after grace period)

**Effort:** M

---

## Sprint 302 Completion Criteria

- [ ] All 6 tasks implemented
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Circuit breaker: dead resolver skipped within 3 failures
- [ ] Rate limiting: bursty usage works, floods blocked
- [ ] Thread safety: concurrent requests don't bypass cooldown
- [ ] Stream probe: dead CDN URLs caught before serve
- [ ] Sync safety: resolver down doesn't trigger mass removal

---

