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
