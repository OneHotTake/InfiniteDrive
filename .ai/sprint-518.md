# Sprint 518 — Remaining Tech Debt (Post-517)

**Status:** Active | **Risk:** LOW | **Depends:** Sprint 517 | **Target:** continuing

## Why (2 sentences max)
Sprint 517 covered all CRITICAL/HIGH items. Five MEDIUM items remain: sync-as-async methods, CooldownKind enum collapse, grace period duplication, StatusService helper extraction, and AioStreamsClient factory routing.

## Non-Goals
- H-02 (Plugin.Instance singleton — multi-sprint scope)
- Any behavior changes — pure refactoring only

## Tasks

### FIX-518-01: M-07 — Sync-wrapped-as-async methods (DatabaseManager)
**Files:** Data/DatabaseManager.cs (modify)
**Effort:** S
**What:** Refactor GetCatalogItemCountByLocalSourceAsync, GetReadoptedCountAsync, GetTotalResurrectionCountAsync to use QueryScalarIntAsync instead of sync-open + Task.FromResult

### FIX-518-02: M-08 — Collapse CooldownKind enum (CooldownGate + callers)
**Files:** Services/CooldownGate.cs, Services/ResolverService.cs, Services/StreamResolutionHelper.cs, Tasks/CatalogProviders.cs, Tasks/MarvinTask.cs, Services/MetadataEnrichmentService.cs
**Effort:** M
**What:** Collapse CatalogFetch/StreamResolve/Enrichment → Default (all had HttpBaseDelayMs=0). Keep SeriesMeta separate. Update all callers.

### FIX-518-03: M-02 — GracePeriodPolicy extraction
**Files:** Services/RemovalPipeline.cs, Services/RemovalService.cs, Models/GracePeriodPolicy.cs (new)
**Effort:** S
**What:** Extract static class with Duration constant + IsProtected method. Both services reference it.

### FIX-518-04: M-01b — TestProviderAsync helper in StatusService
**Files:** Services/StatusService.cs
**Effort:** S
**What:** Extract private TestProviderAsync(string manifestUrl, CancellationToken ct) for two connection-test blocks.

### FIX-518-05: M-05 — AioStreamsClientFactory.CreateForProvider
**Files:** Services/AioStreamsClientFactory.cs, 8 call sites
**Effort:** M
**What:** Add CreateForProvider(ProviderConfig p, ILogger l) overload. Route 8 external-provider sites through it. Leave AioStreamsClient.cs internals alone.

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] All callers compile after CooldownKind collapse
- [ ] Grace period still 7 days (behavior unchanged)

## Completion
- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated
- [ ] git commit -m "chore: end sprint 518"
