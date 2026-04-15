# InfiniteDrive — Implementation Sprint Plan

## Sprint Structure

| Sprint | Focus | Type |
|--------|-------|------|
| **Sprint 301** | Breaking/Core Logic Changes | Code |
| **Sprint 302** | Reliability & Resilience | Code |
| **Sprint 303** | Cleanup & Dead Code Removal | Code |
| **Sprint 304** | Nice-to-Have Improvements | Code |
| **Sprint 305** | Automated Testing | Test |
| **Sprint 306** | Integration Validation | Validation |

---

# Sprint 301 — Core Logic Fixes

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** None

## Overview

Fix the core playback pipeline issues that cause dead-ends for users. These are breaking changes to fundamental resolution logic.

## Task 301-01: Quality Tier Fallback

**Problem:** User on 4K tier gets 404 when content only exists at 1080p

**Files:** `Services/ResolverService.cs`

**Changes:**
- Add ordered quality fallback chain: `4k_hdr` → `4k_sdr` → `1080p` → `720p` → `sd` → `any`
- When filter returns empty at requested tier, iterate down chain until streams found
- Log quality degradation at Info level when fallback used
- Return all streams as final fallback (let user get *something*)

**Acceptance Criteria:**
- [ ] 4K user can play 1080p-only content (with log noting degradation)
- [ ] Quality preference still respected when available
- [ ] No streams = no streams (don't invent them)

**Effort:** S

---

## Task 301-02: Series Episode Pre-Expansion

**Problem:** Series items have S01E01 placeholder, user plays S02E05 → wrong content

**Files:** `Services/SeriesPreExpansionService.cs`, `Tasks/CatalogSyncTask.cs`, `Services/StrmWriterService.cs`

**Changes:**
- Make episode expansion mandatory during catalog sync (not deferred)
- Block series from appearing in library until all episodes have `.strm` files
- Add `episodes_expanded` boolean column to `catalog_items`
- Expansion must complete atomically (all or nothing per series)

**Acceptance Criteria:**
- [ ] Series only visible to Emby after all episodes written
- [ ] Each episode has correct season/episode in `.strm` filename
- [ ] Interrupted expansion resumes cleanly on next sync
- [ ] S02E05 resolves to S02E05 (not S01E01)

**Effort:** M

---

## Task 301-03: Distinct Error Responses

**Problem:** All failures return generic "no streams" — user can't tell what's wrong

**Files:** `Services/ResolverService.cs`, `Models/ResolverError.cs` (new)

**Changes:**
- Create `ResolverError` enum: `NoStreamsExist`, `QualityMismatch`, `PrimaryResolverDown`, `AllResolversDown`, `RateLimited`, `InvalidToken`
- Return structured error response with code + human message
- Map to appropriate HTTP status: 404 (no content), 503 (service down), 429 (rate limited), 401 (token)

**Acceptance Criteria:**
- [ ] "No streams for this title" vs "Service temporarily unavailable" distinguishable
- [ ] Error code in response body for programmatic handling
- [ ] Human-readable message suitable for UI display

**Effort:** S

---

## Task 301-04: VideosJson Deletion Safety Guard

**Problem:** Corrupted VideosJson → parser returns empty → all episodes deleted

**Files:** `Services/EpisodeDiffService.cs`

**Changes:**
- Before applying diff, validate: if removing >50% of episodes AND old count ≥ 5, ABORT
- Log at Error level with details when guard triggers
- Set item to `NeedsReview` state instead of deleting
- Admin can manually clear after investigation

**Acceptance Criteria:**
- [ ] Corrupted JSON does not trigger mass deletion
- [ ] Guard triggers → no files deleted, item flagged
- [ ] Normal diff (add 2, remove 1) proceeds normally
- [ ] Edge case: series ends, final season removes many episodes → still works (removal < 50%)

**Effort:** S

---

## Task 301-05: Primary/Secondary Resolver Failover Clarity

**Problem:** Failover exists but behavior is unclear, timeouts may be too aggressive

**Files:** `Services/ResolverService.cs`, `Services/AioStreamsClient.cs`

**Changes:**
- Document and enforce: Primary → Secondary → Hard Fail flow
- Increase per-resolver timeout to 15s (AIOStreams can be slow)
- On Primary fail, log at Warn and immediately try Secondary
- On Secondary fail, log at Error with clear "TOTAL RESOLUTION FAILURE" message
- Remove any dead code suggesting additional fallback layers

**Acceptance Criteria:**
- [ ] Primary timeout → Secondary tried (no 15s+ delay before failover)
- [ ] Both fail → single clear error log (not scattered across methods)
- [ ] No false promise of Layer 3 / debrid direct fallback in code or logs

**Effort:** S

---

## Sprint 301 Completion Criteria

- [ ] All 5 tasks implemented
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Quality fallback: 4K user plays 1080p content successfully
- [ ] Series: S02E05 plays correct episode
- [ ] Error messages: "service down" distinct from "no streams"
- [ ] Deletion guard: corrupt JSON doesn't delete episodes
- [ ] Resolver failover: Primary→Secondary→Fail with clear logging

---

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

# Sprint 303 — Cleanup & Dead Code Removal

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 302

## Overview

Remove dead code, legacy patterns, and false promises from the codebase. Improve maintainability.

## Task 303-01: Remove Dead Debrid Fallback Code

**Problem:** Layer 3 direct debrid code exists but is non-functional, creates false confidence

**Files:** Multiple (audit needed)

**Changes:**
- Search for: `InfoHash`, `DirectDebrid`, `Layer3`, `DebridFallback`
- Remove all code paths that suggest direct debrid resolution
- Remove related configuration options if any
- Update any comments/docs that reference this fallback
- Keep debrid-related code only if actively used for primary resolution

**Acceptance Criteria:**
- [ ] No code suggests fallback beyond Secondary resolver
- [ ] No unused `InfoHash` storage or retrieval
- [ ] Configuration page doesn't show dead options
- [ ] Grep for "debrid" shows only active code paths

**Effort:** S

---

## Task 303-02: Remove Multi-Strm Remnants

**Problem:** Legacy code for multiple `.strm` versions per item clutters codebase

**Files:** `Services/StrmWriterService.cs`, potentially others

**Changes:**
- Search for: version arrays, quality-specific strm paths, multi-file write loops
- Remove any code that writes multiple `.strm` files per media item
- Consolidate to: one `.strm` per movie, one `.strm` per episode
- Clean up any database columns tracking multiple versions

**Acceptance Criteria:**
- [ ] Each movie has exactly one `.strm`
- [ ] Each episode has exactly one `.strm`
- [ ] No version/quality suffix in filenames
- [ ] Code is simpler and easier to follow

**Effort:** S

---

## Task 303-03: Consolidate Error Handling Patterns

**Problem:** Inconsistent try/catch patterns, some swallow errors silently

**Files:** Multiple services

**Changes:**
- Audit all `catch` blocks in resolution path
- Ensure all catches either: re-throw, return error result, or log at Warn+
- Remove empty catch blocks
- Standardize on: catch specific exceptions, not bare `Exception`
- Add context to log messages (mediaId, resolver name, etc.)

**Acceptance Criteria:**
- [ ] No silent error swallowing in playback path
- [ ] All errors logged with sufficient context
- [ ] Exception types are specific where possible
- [ ] Consistent pattern across services

**Effort:** M

---

## Task 303-04: Path Sanitization Hardening

**Problem:** `SanitisePath` doesn't block `..` traversal

**Files:** `Services/StrmWriterService.cs`

**Changes:**
- Add `..` to blocked character list
- Add validation that final path is within configured library root
- Use `Path.GetFullPath()` and verify starts with allowed prefix
- Log at Error if traversal attempt detected

**Acceptance Criteria:**
- [ ] `../` in title doesn't escape library directory
- [ ] Attempted traversal logged as potential attack
- [ ] Legitimate titles with `.` still work
- [ ] All write paths validated before write

**Effort:** S

---

## Task 303-05: Remove Unused Configuration Options

**Problem:** Dead config fields confuse users and developers

**Files:** `PluginConfiguration.cs`, `Configuration/configurationpage.html`

**Changes:**
- Audit each config field for actual usage
- Remove fields with no active code paths
- Remove corresponding UI elements
- Document remaining fields with clear descriptions

**Candidates to audit:**
- Anything debrid-related (if Layer 3 removed)
- Multi-version/quality-specific paths
- Legacy feature flags

**Acceptance Criteria:**
- [ ] Every config field has active code using it
- [ ] UI shows only functional options
- [ ] Config page is cleaner and less overwhelming

**Effort:** S

---

## Task 303-06: Logging Consistency Pass

**Problem:** Mix of log levels, some critical events at Debug, some noise at Info

**Files:** All services

**Changes:**
- Establish logging standards:
  - Error: System broken, needs attention
  - Warn: Degraded but functional, or user-facing issue
  - Info: Significant state changes, sync completions
  - Debug: Detailed flow, useful for troubleshooting
- Audit and adjust log levels across codebase
- Ensure structured logging (use `{Placeholder}` not string concat)
- Add correlation IDs where helpful (request ID through resolution chain)

**Acceptance Criteria:**
- [ ] Production logs (Info+) are meaningful signal, not noise
- [ ] Debug logs tell complete story for troubleshooting
- [ ] No PII in logs (user IDs OK, emails/IPs sparingly)
- [ ] Consistent format across services

**Effort:** M

---

## Sprint 303 Completion Criteria

- [ ] All 6 tasks implemented
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Grep "debrid" shows no dead code
- [ ] Grep "\.strm" shows single-file-per-item pattern
- [ ] No empty catch blocks in resolution path
- [ ] Path traversal blocked and logged
- [ ] Config page shows only active options
- [ ] Log output is clean and actionable

---

# Sprint 304 — Nice-to-Have Improvements

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 303

## Overview

Quality of life improvements, better UX, and proactive maintenance features.

## Task 304-01: Proactive Token Refresh

**Problem:** 365-day token expiry is a cliff — entire library dies with no warning

**Files:** `Tasks/TokenRefreshTask.cs` (new), `Services/StrmWriterService.cs`

**Changes:**
- Create scheduled task running weekly
- Query items with tokens expiring within 90 days
- Re-sign `.strm` files with fresh tokens
- Log summary: "Refreshed {count} tokens, {remaining} still valid"
- Add `token_expires_at` tracking if not present

**Acceptance Criteria:**
- [ ] Tokens refreshed before expiry (no manual intervention)
- [ ] Task runs reliably on schedule
- [ ] Large libraries handled in batches (cap 1000/run)
- [ ] No disruption to playback during refresh

**Effort:** M

---

## Task 304-02: Cache Pre-Warm on Detail View

**Problem:** First play of item hits AIOStreams; could pre-warm when user browses

**Files:** `Services/DiscoverService.cs` (or equivalent), `Services/CachePreWarmService.cs` (new)

**Changes:**
- When user views item detail page, trigger lightweight cache check
- If cached URL exists: probe it (HEAD request)
- If probe fails or no cache: queue background resolution
- Resolution happens async — user doesn't wait
- Play button works immediately if cache valid, or waits briefly for background resolve

**Acceptance Criteria:**
- [ ] Item detail view triggers pre-warm (non-blocking)
- [ ] Valid cache confirmed without full re-resolution
- [ ] Invalid cache triggers background refresh
- [ ] Play button never slower than without pre-warm

**Effort:** M

---

## Task 304-03: Anime Canonical ID Dedup

**Problem:** Anime without IMDB IDs bypass dedup, creates duplicate library entries

**Files:** `Services/IdResolverService.cs`, `Tasks/CatalogSyncTask.cs`

**Changes:**
- Dedup key: use canonical ID (IMDB preferred, then TMDB, then Kitsu)
- Store all known IDs for cross-reference
- When new item arrives, check if any ID matches existing item
- Merge rather than duplicate

**Acceptance Criteria:**
- [ ] Same anime from two catalogs doesn't duplicate
- [ ] Kitsu-only items can still be deduped by Kitsu ID
- [ ] IMDB match takes precedence if available
- [ ] Existing duplicates can be merged (or flagged for manual review)

**Effort:** M

---

## Task 304-04: Identity Verification Warning

**Problem:** ID resolution can return wrong content, user plays wrong movie silently

**Files:** `Services/IdResolverService.cs`

**Changes:**
- After resolving ID, compare returned metadata (title, year) with source
- If title similarity < 80% OR year differs by > 1: flag as "unverified"
- Store `identity_confidence` score on item
- Show warning badge in Discover UI for low-confidence items
- Log at Warn when confidence is low

**Acceptance Criteria:**
- [ ] Obvious mismatches flagged (different title entirely)
- [ ] Minor variations pass (punctuation, "The" prefix)
- [ ] User sees visual indicator of uncertainty
- [ ] False positives minimized (don't flag everything)

**Effort:** M

---

## Task 304-05: SingleFlight Result Caching

**Problem:** Requests 100ms apart both hit API (SingleFlight only collapses concurrent)

**Files:** `Services/SingleFlight.cs`

**Changes:**
- After factory completes, cache result for configurable TTL (default 5s)
- Subsequent requests for same key get cached result
- Cache entries auto-expire
- Memory-bounded (LRU eviction if > 1000 entries)

**Acceptance Criteria:**
- [ ] Rapid sequential requests don't all hit API
- [ ] Cache expires appropriately (stale data clears)
- [ ] Memory usage bounded
- [ ] No behavior change for long-gap requests

**Effort:** S

---

## Task 304-06: State Machine Consolidation (Design Only)

**Problem:** Two competing state enums (`ItemState`, `ItemStatus`) create confusion

**Files:** `Models/ItemState.cs`, `Models/ItemStatus.cs`

**Changes (Design Document Only — Implementation deferred):**
- Document all current states and their transitions
- Design unified state machine covering all use cases
- Map migration path from current states to new
- Identify code paths that need updating
- Estimate effort for full implementation

**Acceptance Criteria:**
- [ ] Design document produced (not code changes)
- [ ] All current states mapped to proposed unified model
- [ ] Migration path documented
- [ ] Effort estimate for implementation sprint

**Effort:** S (design only)

---

## Sprint 304 Completion Criteria

- [ ] All 6 tasks implemented (Task 304-06 is design doc only)
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Token refresh: scheduled task runs, tokens renewed
- [ ] Pre-warm: detail view triggers background resolution
- [ ] Anime dedup: Kitsu-only items don't duplicate
- [ ] Identity verification: mismatches flagged visually
- [ ] SingleFlight: short TTL caching works
- [ ] State machine: design document complete

---

# Sprint 305 — Automated Testing

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 304

## Overview

Create automated test coverage for all changes in Sprints 301-304.

## Task 305-01: Quality Fallback Unit Tests

**Files:** `Tests/ResolverServiceTests.cs`

**Test Cases:**
| Test | Input | Expected |
|------|-------|----------|
| Exact tier match | 4K streams available, 4K requested | Returns 4K streams |
| Fallback needed | 1080p only, 4K requested | Returns 1080p, logs degradation |
| Multiple fallbacks | SD only, 4K requested | Returns SD after full chain |
| No streams | Empty list | Returns empty (not invented) |
| Fallback to any | Mixed quality, exotic tier requested | Returns all streams |

**Effort:** S

---

## Task 305-02: Series Expansion Integration Tests

**Files:** `Tests/SeriesExpansionTests.cs`

**Test Cases:**
| Test | Scenario | Expected |
|------|----------|----------|
| Full expansion | Series with 24 episodes | 24 `.strm` files created |
| Atomic failure | Error at episode 15 | No partial state, rollback |
| Resume after failure | Previously failed series | Completes expansion |
| Correct episode mapping | S02E05 requested | S02E05 content returned |
| Library visibility | Incomplete expansion | Series not visible to Emby |

**Effort:** M

---

## Task 305-03: Circuit Breaker Tests

**Files:** `Tests/ResolverHealthTrackerTests.cs`

**Test Cases:**
| Test | Scenario | Expected |
|------|----------|----------|
| Opens after failures | 3 consecutive failures | Circuit opens |
| Stays open during timeout | Requests during open state | Skipped, not attempted |
| Half-opens after timeout | First request after timeout | One request allowed |
| Closes on success | Success in half-open state | Circuit closes |
| Exponential backoff | Repeated opens | 30s → 60s → 120s → 300s |
| Jitter | Multiple circuits opening | Times vary by ±10% |

**Effort:** M

---

## Task 305-04: Rate Limiter Tests

**Files:** `Tests/RateLimiterTests.cs`, `Tests/CooldownGateTests.cs`

**Test Cases:**
| Test | Scenario | Expected |
|------|----------|----------|
| Under limit | 29 requests/minute | All allowed |
| At limit | 30th request | Allowed |
| Over limit | 31st request | Blocked with 429 |
| Window slides | Wait 30s, request | Allowed (window shifted) |
| Per-IP isolation | Two IPs at limit | Each has own limit |
| Trusted IP bypass | Localhost at 100 req | All allowed |
| Bursty pattern | 10 rapid, pause, 10 rapid | Both bursts allowed |
| Cooldown thread safety | 100 concurrent requests | No race condition |

**Effort:** M

---

## Task 305-05: Stream Probe Tests

**Files:** `Tests/StreamProbeServiceTests.cs`

**Test Cases:**
| Test | Scenario | Expected |
|------|----------|----------|
| Healthy URL | HEAD returns 200 | ProbeResult.OK |
| HEAD not allowed | HEAD returns 405 | Retries with GET Range |
| Dead URL | Returns 404 | ProbeResult.HttpError(404) |
| Timeout | No response in 2s | ProbeResult.Timeout |
| Total budget | 3 slow URLs | Stops at 5s total |
| First OK wins | First probe succeeds | Remaining not probed |
| All fail | 3 dead URLs | Returns list of failures |

**Effort:** S

---

## Task 305-06: Deletion Safety Guard Tests

**Files:** `Tests/EpisodeDiffServiceTests.cs`

**Test Cases:**
| Test | Scenario | Expected |
|------|----------|----------|
| Normal diff | Add 2, remove 1 of 20 | Proceeds normally |
| Mass removal blocked | Remove 15 of 20 | Aborted, item flagged |
| Small collection exempt | Remove 3 of 4 | Proceeds (count < 5) |
| Corrupt JSON | Empty parse result | Aborted, item flagged |
| Series ending | Remove 12 (final season) | Proceeds if < 50% total |
| Edge: exactly 50% | Remove 10 of 20 | Proceeds (not > 50%) |

**Effort:** S

---

## Task 305-07: Error Response Tests

**Files:** `Tests/ResolverErrorTests.cs`

**Test Cases:**
| Test | Scenario | Expected |
|------|----------|----------|
| No streams | AIOStreams returns empty | 404 + `NoStreamsExist` |
| Quality mismatch | Streams exist, wrong tier | 404 + `QualityMismatch` |
| Primary down | Primary timeout | 503 + `PrimaryResolverDown` |
| Both down | Both timeout | 503 + `AllResolversDown` |
| Rate limited | 429 from AIOStreams | 503 + `RateLimited` |
| Invalid token | Token validation fails | 401 + `InvalidToken` |
| Correct HTTP codes | Each error type | Maps to correct status |

**Effort:** S

---

## Sprint 305 Completion Criteria

- [ ] All 7 test tasks implemented
- [ ] All tests pass: `dotnet test`
- [ ] Test coverage > 80% for modified code paths
- [ ] No flaky tests (run 3x, all pass)
- [ ] Tests run in < 60 seconds total
- [ ] Mocking used appropriately (no real API calls in unit tests)

---

# Sprint 306 — Integration Validation

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 305

## Overview

End-to-end validation of all changes against real Emby server and AIOStreams instance.

## Task 306-01: Playback Flow Validation

**Environment:** Dev Emby server with InfiniteDrive plugin

**Test Matrix:**

| ID | Scenario | Steps | Expected |
|----|----------|-------|----------|
| P-01 | Normal movie playback | Browse → Select → Play | Plays within 5s |
| P-02 | Quality fallback | Request 4K, only 1080p exists | Plays 1080p, log shows fallback |
| P-03 | Series episode | Browse series → S02E05 → Play | Correct episode plays |
| P-04 | Cache hit | Play same item again | Plays within 1s (cached) |
| P-05 | Stale cache | Wait for cache expiry → Play | Re-resolves, then plays |

**Effort:** M

---

## Task 306-02: Failure Mode Validation

**Environment:** Dev Emby with controlled network conditions

**Test Matrix:**

| ID | Scenario | Setup | Expected |
|----|----------|-------|----------|
| F-01 | Primary resolver down | Block primary URL | Failover to secondary, plays |
| F-02 | Both resolvers down | Block both URLs | Clear "service unavailable" message |
| F-03 | Slow resolver | Add 8s latency | Still plays (within 15s timeout) |
| F-04 | Rate limited | Trigger 429 | Playback paused, resumes after cooldown |
| F-05 | Dead CDN URL | Poison cache with dead URL | Probe detects, tries next candidate |

**Effort:** M

---

## Task 306-03: Circuit Breaker Validation

**Environment:** Dev Emby with simulated failures

**Test Matrix:**

| ID | Scenario | Steps | Expected |
|----|----------|-------|----------|
| C-01 | Circuit opens | Fail primary 3x | 4th request skips primary |
| C-02 | Circuit recovers | Wait 30s after open | Next request tries primary |
| C-03 | Exponential backoff | Open circuit repeatedly | 30s → 60s → 120s cooldown |
| C-04 | Success resets | Succeed after half-open | Circuit fully closes |

**Effort:** S

---

## Task 306-04: Sync Safety Validation

**Environment:** Dev Emby with catalog manipulation

**Test Matrix:**

| ID | Scenario | Steps | Expected |
|----|----------|-------|----------|
| S-01 | Normal sync | Run full sync | Items added/removed correctly |
| S-02 | Resolver down during sync | Block resolver, run sync | No removals, log notes skip |
| S-03 | Item missing from one resolver | Remove from primary only | Item NOT removed (still in secondary) |
| S-04 | Item missing from both | Remove from both | Item removed after grace period |
| S-05 | Corrupt VideosJson | Manually corrupt JSON | Episodes NOT deleted, item flagged |

**Effort:** M

---

## Task 306-05: Performance Validation

**Environment:** Dev Emby with production-like catalog size

**Benchmarks:**

| ID | Metric | Target | Method |
|----|--------|--------|--------|
| B-01 | Cold resolution time | < 10s | Time from click to play (cache miss) |
| B-02 | Warm resolution time | < 1s | Time from click to play (cache hit) |
| B-03 | Sync throughput | > 100 items/min | Time full catalog sync |
| B-04 | Memory usage | < 500MB | Monitor during sync |
| B-05 | Probe overhead | < 500ms added | Compare play time with/without probe |

**Effort:** S

---

## Task 306-06: Security Validation

**Environment:** Dev Emby with attack simulation

**Test Matrix:**

| ID | Scenario | Method | Expected |
|----|----------|--------|----------|
| X-01 | Rate limit enforcement | Flood /resolve (100 req/s) | Blocked after 30/min |
| X-02 | Path traversal | Title with `../../etc/passwd` | Sanitized, no escape |
| X-03 | Invalid token | Forge token with wrong
secret | 401 Unauthorized |
| X-04 | Expired token | Use 366-day-old token | 401 Unauthorized |
| X-05 | Trusted IP bypass | Localhost at high rate | Not blocked |

**Effort:** S

---

## Task 306-07: Regression Validation

**Environment:** Dev Emby with existing library

**Test Matrix:**

| ID | Scenario | Steps | Expected |
|----|----------|-------|----------|
| R-01 | Existing library intact | Upgrade plugin, check library | All items still present |
| R-02 | Existing cache valid | Play previously-cached item | Plays without re-resolution |
| R-03 | Existing tokens valid | Play item with old token | Still works (within 365 days) |
| R-04 | Config migration | Upgrade with old config | Settings preserved |
| R-05 | Database migration | Upgrade with old schema | Migration runs cleanly |

**Effort:** S

---

## Task 306-08: Documentation Update

**Files:** `README.md`, `docs/CONFIGURATION.md`, `docs/TROUBLESHOOTING.md`

**Changes:**
- Update architecture description (two resolvers, hard fail after both)
- Remove references to dead features (Layer 3 debrid, multi-strm)
- Document new error codes and what they mean
- Add troubleshooting guide for common failures
- Update configuration reference (remove dead options)

**Acceptance Criteria:**
- [ ] README reflects actual system behavior
- [ ] No documentation of non-existent features
- [ ] Error codes documented with user actions
- [ ] Troubleshooting covers all failure modes

**Effort:** M

---

## Sprint 306 Completion Criteria

- [ ] All 8 validation tasks completed
- [ ] Playback: all 5 scenarios pass
- [ ] Failure modes: all 5 scenarios handled gracefully
- [ ] Circuit breaker: all 4 scenarios work as designed
- [ ] Sync safety: all 5 scenarios protect data
- [ ] Performance: all 5 benchmarks met
- [ ] Security: all 5 attack scenarios blocked
- [ ] Regression: all 5 scenarios pass
- [ ] Documentation: updated and accurate

---

# Sprint Summary

## Implementation Order

```
Sprint 301 (Core Logic)
    ↓
Sprint 302 (Reliability)
    ↓
Sprint 303 (Cleanup)
    ↓
Sprint 304 (Nice-to-Have)
    ↓
Sprint 305 (Testing)
    ↓
Sprint 306 (Validation)
```

## Effort Estimates

| Sprint | Tasks | Total Effort | Estimated Days |
|--------|-------|--------------|----------------|
| 301 | 5 | 1S + 3S + 1M = ~3M | 3-4 days |
| 302 | 6 | 1S + 5M = ~5M | 5-6 days |
| 303 | 6 | 4S + 2M = ~3M | 3-4 days |
| 304 | 6 | 1S + 4M + 1S(doc) = ~4M | 4-5 days |
| 305 | 7 | 4S + 3M = ~4M | 3-4 days |
| 306 | 8 | 4S + 3M + 1M(doc) = ~4M | 4-5 days |
| **Total** | **38** | **~23M equivalent** | **22-28 days** |

## Risk Summary

| Sprint | Risk Level | Key Risks |
|--------|------------|-----------|
| 301 | MEDIUM | Series expansion changes file structure |
| 302 | MEDIUM | Rate limiting could affect legitimate use |
| 303 | LOW | Removing code could break unknown dependencies |
| 304 | LOW | New features, isolated from core |
| 305 | LOW | Tests only, no production code changes |
| 306 | LOW | Validation only, catches issues before release |

## Definition of Done

Each sprint complete when:
- [ ] All tasks implemented per specification
- [ ] `dotnet build -c Release` — 0 errors, 0 new warnings
- [ ] Code reviewed (self-review checklist for solo dev)
- [ ] Sprint-specific completion criteria met
- [ ] Changes committed with descriptive message
- [ ] CHANGELOG.md updated

## Version Bump Strategy

| Milestone | Version |
|-----------|---------|
| After Sprint 301 | v0.41.0-alpha.1 |
| After Sprint 302 | v0.41.0-alpha.2 |
| After Sprint 303 | v0.41.0-beta.1 |
| After Sprint 304 | v0.41.0-beta.2 |
| After Sprint 305 | v0.41.0-rc.1 |
| After Sprint 306 | v0.41.0 (release) |

---

## Quick Reference: What Each Sprint Fixes

| User Problem | Fixed In |
|--------------|----------|
| "No streams" when 1080p exists for 4K user | Sprint 301 |
| Wrong episode plays for series | Sprint 301 |
| Can't tell if service is down vs content missing | Sprint 301 |
| Episodes randomly disappear | Sprint 301 |
| Every request slow when resolver is down | Sprint 302 |
| System freezes after rate limit | Sprint 302 |
| Dead URLs cause playback failure | Sprint 302 |
| Attackers can freeze system | Sprint 302 |
| Items removed during resolver outage | Sprint 302 |
| Confusing dead code in codebase | Sprint 303 |
| Library dies after 1 year (tokens) | Sprint 304 |
| Slow first-play (no pre-warm) | Sprint 304 |
| Duplicate anime entries | Sprint 304 |
| Wrong movie plays (ID mismatch) | Sprint 304 |
