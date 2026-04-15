# Sprint 310 — Critical Path Unification & Security Hardening

**Status:** Draft | **Risk:** HIGH | **Depends:** none | **Target:** v0.41

## Why
The main playback endpoint (`/resolve`) ignores the secondary provider entirely, causing total playback failure when primary is down even with a healthy secondary configured. Additionally, three security gaps allow unsigned URL leakage and an open proxy window during initialization.

## Non-Goals
- Parallel provider attempts (future sprint)
- Pre-warming or cache improvements
- Metadata cross-validation
- State machine consolidation

---

## Tasks

### FIX-310-01: Unify resolution path — ResolverService delegates to StreamResolutionHelper
**Files:** `Services/ResolverService.cs` (modify), `Services/StreamResolutionHelper.cs` (modify if needed)
**Effort:** M
**What:** Replace `ResolverService.ResolveStreamsAsync` body to call `StreamResolutionHelper.SyncResolveViaProvidersAsync` instead of instantiating its own single-provider `AioStreamsClient`. Adapt response mapping to match existing return types. **Gotcha:** Ensure CancellationToken flows through; verify timeout config is respected.

### FIX-310-02: Make ResolverHealthTracker a shared singleton
**Files:** `Plugin.cs` (modify), `Services/ResolverService.cs` (modify), `Services/StreamResolutionHelper.cs` (modify), `Services/ResolverHealthTracker.cs` (modify if needed)
**Effort:** S
**What:** Register `ResolverHealthTracker` as singleton property in `Plugin.cs` (same pattern as `CooldownGate`). Inject shared instance into `ResolverService` and `StreamResolutionHelper` constructors. Remove per-instance instantiation. **Gotcha:** Verify thread-safety of health tracker state.

### FIX-310-03: Integrate health tracker into StreamResolutionHelper
**Files:** `Services/StreamResolutionHelper.cs` (modify)
**Effort:** S
**What:** Before attempting a provider in `SyncResolveViaProvidersAsync`, check `_healthTracker.IsCircuitOpen(providerKey)`. Skip open circuits. After success/failure, call `RecordSuccess`/`RecordFailure`. **Gotcha:** Provider key must be consistent (use manifest URL or configured name).

### FIX-310-04: Delete DirectStreamUrl endpoint
**Files:** `Services/DiscoverService.cs` (modify)
**Effort:** S
**What:** Remove `Get(DiscoverDirectStreamRequest)` method (~lines 1150-1231) and the `DiscoverDirectStreamRequest` route class. Grep for any callers in JS/HTML and remove. **Gotcha:** Verify Discover UI doesn't break — search for `DirectStreamUrl` in `configurationpage.js` and `discoverpage.js`.

### FIX-310-05: Guard StreamEndpointService against empty PluginSecret
**Files:** `Services/StreamEndpointService.cs` (modify)
**Effort:** S
**What:** At top of `HandleAsync` (or equivalent entry point), add check: if `Plugin.Instance.Configuration.PluginSecret` is null/empty, return HTTP 503 with body `{"error": "plugin_not_initialized"}`. Do not proceed to URL proxying. **Gotcha:** Ensure this doesn't break first-run wizard flow — secret should be generated before any playback is possible.

### FIX-310-06: Verify HMAC uses timing-safe comparison
**Files:** `Services/PlaybackTokenService.cs` (modify if needed), `Services/StreamUrlSigner.cs` (modify if needed)
**Effort:** S
**What:** Grep for `==` comparison of signature strings. Replace any `signature == expectedSignature` with `CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signature), Encoding.UTF8.GetBytes(expectedSignature))`. Add `using System.Security.Cryptography;` if missing. **Gotcha:** Both `Verify()` methods in both files must be checked.

---

## Verification

- [ ] `dotnet build -c Release` — 0 errors, 0 warnings
- [ ] `./emby-reset.sh` succeeds + Discover UI loads without JS errors
- [ ] Manual test: Configure two providers. Kill primary (or set invalid URL). Press Play. **Expected:** Playback succeeds via secondary within 15s.
- [ ] Manual test: Call `/Discover/DirectStreamUrl` — **Expected:** 404 Not Found
- [ ] Manual test: Clear `PluginSecret` from config XML, restart Emby, press Play — **Expected:** 503 "plugin_not_initialized", not proxied request
- [ ] Manual test: Playback works normally with valid config

---

## Completion

- [ ] All tasks done
- [ ] BACKLOG.md updated (move C-01 through C-05 to Done)
- [ ] REPO_MAP.md updated (remove DirectStreamUrl, note singleton HealthTracker)
- [ ] git commit -m "chore: end sprint 310 — critical path unification"
