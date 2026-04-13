# Sprint 131 — Remove Polly Dependency (Single-DLL Shipping)

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 130

---

## Overview

Remove the Polly NuGet dependency so the plugin ships as a single DLL with no satellite assemblies. Polly is currently used only for retry, circuit breaker, and timeout around AIOStreams HTTP calls. All three features are replaceable with straightforward inline C# and the existing `HttpClient.Timeout`.

**Why now:** Polly v8 requires `Polly.Core.dll` alongside `Polly.dll`, causing assembly-loading failures in Emby's plugin host. The current workaround (static `AssemblyLoadContext.Resolving` handler + `libs/` subfolder) is fragile. Removing Polly entirely eliminates the class of problem.

---

## Phase 131A — Remove Polly Policy and Resilience Folder

### FIX-131A-01: Delete `Resilience/AIOStreamsResiliencePolicy.cs`

**What:** Delete the entire file. It is the only consumer of `Polly` namespaces beyond `AioStreamsClient.cs`.

**Depends on:** None
**Must not break:** Build must succeed after Phase 131B completes.

### FIX-131A-02: Remove Polly `PackageReference` from `EmbyStreams.csproj`

**File:** `EmbyStreams.csproj` (modify)

**What:** Delete lines 77-81 (the `<!-- Polly resilience patterns -->` comment and `<PackageReference Include="Polly" .../>`).

**Depends on:** FIX-131A-01

---

## Phase 131B — Inline Resilience in AioStreamsClient

### FIX-131B-01: Replace Polly with simple circuit breaker class

**File:** `Services/AioStreamsClient.cs` (modify)

**What:**
1. Remove `using Polly;`
2. Remove `_resiliencePolicy` field and `CreateResiliencePolicy()` method
3. Remove `_resiliencePolicy.ExecuteAsync(...)` wrapper from the 3 HTTP call sites (`GetAsync<T>`, `GetJsonElementAsync`, `GetJsonAsync`) — call `_sharedHttp.GetAsync()` directly
4. Add a private `SimpleCircuitBreaker` inner class (~25 lines):
   - `_failureCount` int, `_openUntil` DateTime
   - `bool IsOpen` — true if `_failureCount >= 5 && DateTime.UtcNow < _openUntil`
   - `void RecordFailure()` — increment counter, set `_openUntil = now + 30s` on threshold
   - `void RecordSuccess()` — reset counter
5. Wire it into the existing retry loop in `GetAsync<T>`:
   - Before each attempt: check `IsOpen`, return null if open
   - On success: `RecordSuccess()`
   - On `HttpRequestException` or 5xx: `RecordFailure()`
6. `HttpClient.Timeout` already covers the timeout Polly provided (set at line 579)

**Behavioral contract preserved:**
- 3 retries with exponential backoff (already exists inline)
- Circuit breaker opens after 5 consecutive failures, resets after 30s
- 15s timeout via `HttpClient.Timeout`

**Depends on:** FIX-131A-01, FIX-131A-02

---

## Phase 131C — Remove Polly Assembly-Loading Workaround

### FIX-131C-01: Remove `AssemblyLoadContext.Resolving` handler from `Plugin.cs`

**File:** `Plugin.cs` (modify)

**What:**
1. Remove the entire static constructor (lines 45-62) — the `libs/` folder and assembly resolve handler exist solely for Polly satellite DLLs
2. No other code references `libs/` or `AssemblyLoadContext`

**Depends on:** FIX-131B-01

---

## Phase 131D — Build Verification

### FIX-131D-01: Clean build + deploy test

**What:**
1. `dotnet build -c Release` — 0 errors, 0 new warnings
2. Verify output directory contains only `EmbyStreams.dll` (no Polly DLLs)
3. `./emby-reset.sh` — server starts, plugin loads, no assembly errors in log
4. `./test-signed-stream.sh` — playback still works

**Depends on:** FIX-131C-01

---

## Sprint 131 Dependencies

- **Previous Sprint:** 130 (Integration Testing)
- **Blocked By:** Sprint 130
- **Blocks:** None

---

## Sprint 131 Completion Criteria

- [ ] `Resilience/AIOStreamsResiliencePolicy.cs` deleted
- [ ] Polly `PackageReference` removed from `.csproj`
- [ ] `using Polly;` removed from all `.cs` files
- [ ] `AssemblyLoadContext.Resolving` handler removed from `Plugin.cs`
- [ ] Inline circuit breaker replaces Polly circuit breaker (same thresholds)
- [ ] Build succeeds with 0 errors, 0 new warnings
- [ ] Build output is a single `EmbyStreams.dll` (no satellite DLLs)
- [ ] Server starts cleanly with no assembly-load errors
- [ ] Signed stream test passes (`./test-signed-stream.sh`)

---

## Sprint 131 Notes

**Files changed:** 3 files modified (`EmbyStreams.csproj`, `Services/AioStreamsClient.cs`, `Plugin.cs`), 1 file deleted (`Resilience/AIOStreamsResiliencePolicy.cs`)

**Risk assessment:** LOW. Polly is a thin wrapper over basic HTTP patterns. The inline retry already exists in `AioStreamsClient.GetAsync<T>`. The circuit breaker is a counter + timestamp. No other code references Polly.

**Regression risk:** The existing inline retry loop in `GetAsync<T>` was already doing the real work; Polly's retry was wrapping an already-retrying method (double retry). Removing Polly's outer retry actually *simplifies* behavior.
