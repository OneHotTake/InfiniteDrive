# Sprint 155 — CooldownGate: Good-Citizen Throttling for AIOStreams

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 154

---

## Overview

Replace scattered `Task.Delay(config.ApiCallDelayMs, ct)` calls with a single
`CooldownGate` service that distinguishes LOCAL operations (aggressive) from
HTTP operations (polite). Auto-detect the AIOStreams instance type from the
configured manifest URL and apply the correct cooldown profile without
exposing any new UI.

### Why This Exists

Today every HTTP call site bakes its own `ApiCallDelayMs` delay and there is
no coordinated response to `429 Too Many Requests`. If a public AIOStreams
instance throttles us, we keep hammering it and eventually get blocked. We
also slow down local disk/DB operations for no reason because the same delay
field is reused across layers.

The goal is a single gate class, automatic instance-type detection, and a
global 429 backoff that keeps us off every operator's block list — while
**reducing** user-facing configuration surface, not expanding it.

Design spec: `docs/COOLDOWN.md`.

---

## Phase 155A — CooldownGate Service

### FIX-155A-01: Create CooldownGate + CooldownProfile

**File:** `Services/CooldownGate.cs` (create)

**What:**

1. Create `InstanceType` enum (`Shared`, `Private`).
2. Create `CooldownKind` enum (`CatalogFetch`, `StreamResolve`, `Enrichment`, `Cinemeta`).
3. Create `CooldownProfile` with compiled-in constants per instance type:

```
                              SHARED     PRIVATE
HTTP base delay (ms)             1000         200
HTTP jitter (+/- ms)              300          80
HTTP timeout (s)                    8          12
CatalogSourcesPerRun                2           6
EnrichmentPerRun                   42         150
RehydrationPerRun                 500        2000
CinemetaDelayMs                   700         200
GlobalCooldownSeconds             900         120
```

4. Create `CooldownGate` as a singleton `IService`-free POCO registered with
   Emby DI (same pattern as `AioStreamsClient`). It exposes:
   - `Task WaitAsync(CooldownKind kind, CancellationToken ct)` — sleeps
     `base + rand(-jitter, +jitter)` and respects the global cooldown window.
   - `void Tripped(TimeSpan? retryAfter = null)` — sets
     `_globalCooldownUntil = UtcNow + (retryAfter ?? profile.GlobalCooldownSeconds)`.
   - `InstanceType Instance { get; }` / `CooldownProfile Profile { get; }`.

5. Unit-testable: inject `Func<int, int> jitterSource` so tests can pin it.

**Depends on:** FIX-155B-01 (reads `ResolvedInstanceType` from config).

---

### FIX-155A-02: Register CooldownGate in Plugin.cs

**File:** `Plugin.cs` (modify)

**What:**
Add to existing service registration block:

```csharp
serviceCollection.AddSingleton<CooldownGate>();
```

Ensure construction order: `CooldownGate` must be constructible before
`AioStreamsClient`, `AioMetadataClient`, and `CinemetaClient` (they take it
as a constructor dependency).

---

## Phase 155B — Instance Type Detection

### FIX-155B-01: Add ResolvedInstanceType to PluginConfiguration

**File:** `PluginConfiguration.cs` (modify)

**What:**

1. Add `public InstanceType ResolvedInstanceType { get; set; } = InstanceType.Shared;`
   (default `Shared` — safer fallback).
2. Add static helper `InstanceType DetectInstanceType(string manifestUrl)`:
   - Return `Private` if host is `localhost`, `127.0.0.1`, or matches
     RFC1918 ranges (`10.*`, `192.168.*`, `172.16-31.*`).
   - Return `Shared` if host matches the known public-instance allowlist
     (maintained list in code — start with `elfhosted.com`,
     `aiostreams.elfhosted.com`).
   - Return `Shared` for everything else (safer default).
3. In `Validate()`, recompute `ResolvedInstanceType` from
   `PrimaryManifestUrl` on every save so it stays in sync.

**No UI changes.** This field is persisted in `EmbyStreams.xml` only.

---

### FIX-155B-02: Retire ApiCallDelayMs

**Files:** `PluginConfiguration.cs`, `Configuration/configurationpage.html`,
`Configuration/configurationpage.js` (modify)

**What:**

1. Remove `ApiCallDelayMs` property from `PluginConfiguration.cs`
   (and its `Clamp` line in `Validate()`).
2. Remove the `#es-api-call-delay` input element and its label from
   `configurationpage.html`.
3. Remove all `_save`/`_load` references to `ApiCallDelayMs` in
   `configurationpage.js`.
4. Grep the repo for any other reader of `ApiCallDelayMs` and migrate them
   in Phase 155C.

**Net user-facing change:** one fewer field on the configuration page.

---

## Phase 155C — Wire the Gate Into HTTP Call Sites

### FIX-155C-01: AioStreamsClient

**File:** `Services/AioStreamsClient.cs` (modify)

**What:**

1. Inject `CooldownGate` via constructor.
2. Replace the existing `await Task.Delay(delayMs, cancellationToken)` at
   lines ~1116 and ~1135 with:
   ```csharp
   await _cooldown.WaitAsync(CooldownKind.StreamResolve, cancellationToken);
   ```
   (or `CatalogFetch` in the catalog path — inspect the callsite).
3. After every HTTP response, if `statusCode == 429` call
   `_cooldown.Tripped(ParseRetryAfter(response))` and return the empty result
   gracefully (same as current failure path).
4. Helper: `static TimeSpan? ParseRetryAfter(HttpResponseMessage resp)` that
   reads the `Retry-After` header if present.

---

### FIX-155C-02: AioMetadataClient

**File:** `Services/AioMetadataClient.cs` (modify — inspect first for exact
class name; may be named differently)

**What:**

1. Inject `CooldownGate`.
2. Wrap the enrichment fetch with
   `await _cooldown.WaitAsync(CooldownKind.Enrichment, ct);`.
3. Handle 429 identically to FIX-155C-01.

---

### FIX-155C-03: CinemetaClient / MetadataFallbackTask

**Files:** `Tasks/MetadataFallbackTask.cs` (modify), relevant Cinemeta client
if separate (modify).

**What:**

1. Replace the hardcoded `Task.Delay(DelayMs, cancellationToken)` at
   `MetadataFallbackTask.cs:197` with
   `await _cooldown.WaitAsync(CooldownKind.Cinemeta, ct);`.
2. Leave the initial `Task.Delay(Random.Shared.Next(0, 120_000), ct)` at the
   top of the task — that's the task-spread jitter, not per-call throttling.

---

### FIX-155C-04: LinkResolverTask

**File:** `Tasks/LinkResolverTask.cs` (modify)

**What:**

1. Inject `CooldownGate`.
2. Replace `await Task.Delay(config.ApiCallDelayMs, cancellationToken)` at
   line ~193 with
   `await _cooldown.WaitAsync(CooldownKind.StreamResolve, ct);`.
3. Leave the existing exponential backoff at line ~245 — it's a separate
   retry loop, orthogonal to the gate.
4. Update XML comment at line ~29 to reference `CooldownGate` instead of
   `ApiCallDelayMs`.

---

## Phase 155D — Batch Caps From Profile

### FIX-155D-01: Read caps from CooldownProfile

**Files:** `Tasks/DeepCleanTask.cs`, `Services/RehydrationService.cs`,
`Tasks/CatalogSyncTask.cs`, `Tasks/RefreshTask.cs` (modify)

**What:**

For each task that has a batch cap, replace the hardcoded constant with
`_cooldown.Profile.EnrichmentPerRun` (or `CatalogSourcesPerRun` /
`RehydrationPerRun` as appropriate).

Tasks keep their own iteration logic — they just ask the gate "how many is
too many for this instance type."

Do not remove the task's own runtime-cap guards (e.g. "stop if cancellation
token fires" or "stop after 2h wall-clock"). Those are orthogonal.

---

## Phase 155E — Observability

### FIX-155E-01: Quiet 429 dashboard badge

**File:** `Services/ProgressStreamer.cs` (modify)

**What:**

1. Add a new progress event type: `EventType = "upstream_cooldown"` with
   payload `{ until: ISO8601, reason: "shared_instance_rate_limit" }`.
2. Have `CooldownGate.Tripped()` call
   `_progressStreamer.Emit("upstream_cooldown", ...)` (soft dependency — may
   be `null` in tests).
3. `configurationpage.js`: when the dashboard sees an `upstream_cooldown`
   event, show a small muted badge:
   > *"Upstream busy — pausing briefly to stay a good neighbour."*
4. Clear the badge automatically when `until` passes.

**Explicit non-goal:** no popup, no toast, no error modal. A single quiet
line of text in the existing dashboard. No new UI surface.

---

### FIX-155E-02: "Three strikes" private-instance suggestion

**File:** `Services/CooldownGate.cs` (modify), dashboard view (modify).

**What:**

1. `CooldownGate` keeps a rolling counter of `Tripped()` calls in the last
   hour (simple in-memory `Queue<DateTimeOffset>`).
2. If count >= 3 **and** `Instance == Shared`, set a one-shot flag
   `SuggestPrivateInstance = true` visible on the admin dashboard.
3. Dashboard shows one quiet line:
   > *"Want faster syncs? Consider a private AIOStreams instance. [Learn more]"*
   Linking to `docs/COOLDOWN.md` on GitHub.

This is the only time the user ever sees the words "rate limit" in the UI.

---

## Phase 155F — Build & Verification

### FIX-155F-01: Build verification

**What:**

1. `dotnet build -c Release` — 0 errors, 0 net-new warnings.
2. Grep: `ApiCallDelayMs` must return zero results in `.cs`, `.html`, `.js`.
3. Grep: every `HttpClient`/`GetAsync`/`PostAsync` call targeting the
   AIOStreams or Cinemeta host must be preceded by a `CooldownGate.WaitAsync`
   call on the same code path.

---

### FIX-155F-02: Synthetic 429 smoke test

**File:** `Services/AioStreamsClient.cs` (modify, `#if DEBUG`-guarded).

**What:**

1. Add a `DEBUG`-only hook `ForceNext429 = true` on `AioStreamsClient` that
   makes the next HTTP call return a fake 429.
2. Add a manual test step to `./test-signed-stream.sh` (or a new
   `./test-cooldown.sh`) that:
   - Sets `ForceNext429 = true`.
   - Calls `/EmbyStreams/Refresh`.
   - Asserts the dashboard shows the `upstream_cooldown` badge.
   - Asserts a second call within 60s returns cached data (does not hit HTTP).
3. Release builds strip the hook entirely.

---

### FIX-155F-03: Local-path regression check

**What:**

Before/after benchmark of `.strm` write throughput: run Refresh on a
synthetic 1000-item catalog twice (before CooldownGate / after) and confirm
LOCAL operations are within ±5%. If they're measurably slower, a gate got
wired into a LOCAL path by mistake — fix it.

---

## Sprint 155 Completion Criteria

- [ ] `Services/CooldownGate.cs` created with `InstanceType`, `CooldownKind`,
      `CooldownProfile`, and `CooldownGate` types
- [ ] `CooldownGate` registered as singleton in `Plugin.cs`
- [ ] `PluginConfiguration.ResolvedInstanceType` added, auto-populated in
      `Validate()` from manifest URL
- [ ] `PluginConfiguration.ApiCallDelayMs` removed from C#, HTML, and JS
- [ ] `AioStreamsClient`, `AioMetadataClient`, `CinemetaClient`,
      `LinkResolverTask`, `MetadataFallbackTask` all use
      `CooldownGate.WaitAsync` instead of raw `Task.Delay(ApiCallDelayMs)`
- [ ] `DeepCleanTask`, `RehydrationService`, `CatalogSyncTask`, `RefreshTask`
      read batch caps from `CooldownGate.Profile`
- [ ] 429 responses call `CooldownGate.Tripped()` and emit a single
      `upstream_cooldown` progress event
- [ ] Dashboard shows quiet cooldown badge when active
- [ ] Three-strikes "suggest private instance" line appears after
      3 × 429 in an hour on SHARED instances
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Grep for `ApiCallDelayMs` returns 0 matches
- [ ] Synthetic 429 smoke test passes
- [ ] Local I/O throughput within ±5% of pre-sprint baseline
- [ ] `docs/COOLDOWN.md` referenced from `README.md` under "Good Citizen"

---

## Notes

**Files created:** 1 (`Services/CooldownGate.cs`)
**Files modified:** ~10 (`Plugin.cs`, `PluginConfiguration.cs`,
`configurationpage.html`, `configurationpage.js`, `AioStreamsClient.cs`,
`AioMetadataClient.cs`, `CinemetaClient.cs` (if separate), `LinkResolverTask.cs`,
`MetadataFallbackTask.cs`, `DeepCleanTask.cs`, `RehydrationService.cs`,
`CatalogSyncTask.cs`, `RefreshTask.cs`, `ProgressStreamer.cs`)
**Files deleted:** 0
**Config fields added (user-visible):** 0
**Config fields removed (user-visible):** 1 (`ApiCallDelayMs`)

**Risk:** MEDIUM — touches every HTTP call site. Mitigated by:
1. `CooldownGate.WaitAsync` is a drop-in replacement for the existing
   `Task.Delay(ApiCallDelayMs, ct)` — same signature, same semantics.
2. Phase ordering ensures the gate exists and is registered before any
   call site is migrated.
3. Phase 155F catches the most common mistake (gating a LOCAL path).

**Elegance invariant:** at the end of this sprint the configuration page has
*fewer* fields than it started with. All new complexity lives in one
compiled-in profile table and one 60-line service class.

**Reference:** `docs/COOLDOWN.md` — full design spec including rationale,
published AIOStreams rate limits, and the complete state machine for
`_globalCooldownUntil`.
