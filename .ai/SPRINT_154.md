# Sprint 154 — Security Hardening & Dead Config Cleanup

**Version:** v4.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 152

---

## Overview

Hardening and cleanup sprint derived from a full codebase audit (hydra-tion, decoration, gestation flows). The audit verified every finding against current source (post-Sprint 152). Most original findings were already fixed or incorrect — only 3 items remain actionable.

### Why This Exists

A comprehensive flow audit evaluated all queue sizes, throttles, delays, dead code, missing triggers, and security across the entire task pipeline. After verifying each finding against the actual source:

- **9 of 14 original findings were already fixed or based on incorrect assumptions**
- **3 items remain** — one security gap, one missing auto-drain, one dead config field

### Audit Verification Summary (Already Fixed / Incorrect)

| Finding | Why Invalid |
|---|---|
| MT-2: summonMarvin is a no-op | Already wired to EmbyStreamsDeepClean via POST to ScheduledTasks/Running |
| MT-6: es-sync-sources-list has no loader | loadContentMgmtSources() already populates it |
| DC-2/3/4/5: Dead controllers/services | Controllers/ deleted in Sprint 152 |
| DC-7: AioStreamsStreamIdPrefixes dead | Active: written during catalog sync, exposed via status API |
| DC-8: ProxyMode dead | Active: used in PlaybackEntry, DatabaseManager, UI |
| DC-9: SignatureValidityDays dead | Active: used in HousekeepingService, SetupService, SeriesPreExpansionService |
| D-5: MetadataFallbackTask no delay | Has 500ms delay + 50-item cap |
| H-8/G-9: ApiCallDelayMs wraps disk writes | Sits between iterations (API + disk), not exclusively around disk writes |
| DC-1/H-9: WriteStrm methods dead | Shared utilities still called by FileResurrectionTask and WebhookService |
| MT-10: DiscoverService unauthenticated | All 6 endpoints have AdminGuard.RequireAdmin |
| MT-10: TriggerService unauthenticated | Already has AdminGuard on all endpoints (documented in Sprint 100A-09) |
| MT-10: WebhookService unauthenticated | Intentionally uses shared-secret auth (WebhookSecret) for external integrations |

---

## Phase 154A — Security: Admin Auth for SetupService

### FIX-154A-01: Add admin authentication to SetupService

**File:** `Services/SetupService.cs` (modify)

**What:**
1. Add `IRequiresRequest` interface to class declaration
2. Add `using MediaBrowser.Controller.Net;` and `using MediaBrowser.Model.Services;`
3. Inject `IAuthorizationContext` via constructor
4. Add `IRequest Request { get; set; }` property
5. Add `AdminGuard.RequireAdmin(_authCtx, Request)` as first statement in both POST methods:
   - `Post(CreateDirectoriesRequest)` — creates filesystem directories
   - `Post(RotateApiKeyRequest)` — rotates API key and rewrites all .strm files

**Before:**
```csharp
public class SetupService : IService
{
    private readonly ILogger<SetupService> _logger;

    public SetupService(ILogManager logManager)
    {
        _logger = new EmbyLoggerAdapter<SetupService>(logManager.GetLogger("EmbyStreams"));
    }
```

**After:**
```csharp
public class SetupService : IService, IRequiresRequest
{
    private readonly ILogger<SetupService> _logger;
    private readonly IAuthorizationContext _authCtx;

    public IRequest Request { get; set; } = null!;

    public SetupService(ILogManager logManager, IAuthorizationContext authCtx)
    {
        _logger = new EmbyLoggerAdapter<SetupService>(logManager.GetLogger("EmbyStreams"));
        _authCtx = authCtx;
    }
```

And at the start of each POST method:
```csharp
var deny = AdminGuard.RequireAdmin(_authCtx, Request);
if (deny != null) return deny;
```

**Reference:** Identical pattern to `TriggerService.cs` (lines 95-146).

**Depends on:** Nothing

---

## Phase 154B — Rehydration Auto-Drain

### FIX-154B-01: Add daily default trigger to RehydrationTask

**File:** `Tasks/RehydrationTask.cs` (modify — `GetDefaultTriggers` method)

**What:**
1. Replace `Array.Empty<TaskTriggerInfo>()` with a daily interval trigger:

**Before:**
```csharp
public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    => Array.Empty<TaskTriggerInfo>(); // Admin-initiated only
```

**After:**
```csharp
public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
{
    return new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerInterval,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(2).Ticks
        }
    };
}
```

2. Update class doc comment: remove "Admin-initiated only", add "Runs daily; also triggerable on-demand via Content Mgmt tab or POST /EmbyStreams/Trigger?task=embystreams_rehydration"

**Rationale:** `PendingRehydrationOperations` is enqueued by the setup wizard when users add/remove quality tiers. Without a default trigger, these operations sit indefinitely until manually triggered. A 24-hour interval with a 2-hour max runtime auto-drains the queue within one day. The task is already a no-op when the queue is empty (returns immediately at line 83-87).

**Depends on:** Nothing

---

## Phase 154C — Dead Config Cleanup

### FIX-154C-01: Remove MaxFallbacksToStore

**Files:** `PluginConfiguration.cs`, `Configuration/configurationpage.html`, `Configuration/configurationpage.js` (modify)

**What:**
1. **PluginConfiguration.cs** — Remove:
   - Property declaration (line ~417): `public int MaxFallbacksToStore { get; set; } = 5;`
   - XML doc comment above it (lines ~411-416)
   - Validate() line (line ~631): `MaxFallbacksToStore = Clamp(MaxFallbacksToStore, 1, 50);`

2. **configurationpage.html** — Remove the input container (lines ~1145-1149):
```html
<!-- REMOVE -->
<div class="inputContainer">
  <label class="inputLabel inputLabelUnfocused" for="cfg-max-fallbacks">Max fallback candidates (legacy)</label>
  <input id="cfg-max-fallbacks" type="number" is="emby-input" min="1" max="20" placeholder="5" />
  <div class="es-hint">Legacy total cap — ignored when Candidates per provider is set. Default: 5.</div>
</div>
```

3. **configurationpage.js** — Remove:
   - Load line (~796): `set('cfg-max-fallbacks', cfg.MaxFallbacksToStore);`
   - Save line (~1031): `MaxFallbacksToStore: esInt(view, 'cfg-max-fallbacks', 5),`

**Why safe:** `MaxFallbacksToStore` is explicitly documented as "Legacy total candidate cap... ignored when CandidatesPerProvider > 0". `CandidatesPerProvider` defaults to 3 and is the active control. The UI already labels this "legacy". Emby's XML deserializer silently ignores missing properties, so existing configs with this field will simply have it ignored on next load.

**Depends on:** Nothing

---

## Phase 154D — Build Verification

### FIX-154D-01: Build + smoke test

**What:**
1. `dotnet build -c Release` — 0 errors, 0 new warnings
2. Verify SetupService endpoints reject non-admin requests
3. Verify RehydrationTask shows up with daily interval in Emby Scheduled Tasks
4. Verify Settings page renders without the removed MaxFallbacksToStore input
5. Update `.ai/CURRENT_TASK.md` with sprint summary

**Depends on:** FIX-154A-01, FIX-154B-01, FIX-154C-01

---

## Sprint 154 Dependencies

- **Previous Sprint:** 152 (Emby-Native Alignment)
- **Depends on:** Sprint 152 (EmbyStreamsInitializationService must exist for context)
- **Blocks:** None (cleanup sprint)

---

## Sprint 154 Completion Criteria

- [ ] SetupService requires admin authentication on both POST endpoints
- [ ] RehydrationTask has a 24-hour default trigger (auto-drains pending operations)
- [ ] MaxFallbacksToStore removed from PluginConfiguration, HTML, and JS
- [ ] Build succeeds with 0 errors, 0 new warnings
- [ ] No other config fields or functionality affected

---

## Sprint 154 Notes

**Files modified:** 4 (`Services/SetupService.cs`, `Tasks/RehydrationTask.cs`, `PluginConfiguration.cs`, `Configuration/configurationpage.html`, `Configuration/configurationpage.js`)

**Risk assessment:** LOW. All changes are additive (auth guard, trigger registration) or subtractive (dead config removal). No schema changes, no database migrations, no behavioral changes to active code paths.

**Security impact:** SetupService's `RotateApiKey` endpoint currently allows any LAN user to rotate the API key and rewrite all .strm files. Adding admin auth closes this gap. WebhookService is intentionally unauthenticated for admin — it uses shared-secret validation (WebhookSecret) which is the correct pattern for external integrations (Radarr, Sonarr, Jellyseerr).
