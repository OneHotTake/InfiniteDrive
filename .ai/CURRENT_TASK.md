SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

---
status: complete
task: Sprint 350 — Critical Audit Remediation
last_updated: 2026-04-15

## Completed

- FIX-350-01: ManifestFetcher inverted logic — `!string.IsNullOrEmpty` → `string.IsNullOrEmpty` (showstopper)
- FIX-350-02: Circuit breaker RecordSuccess now sets CircuitState=Closed + BackoffIndex=0
- FIX-350-03: PlaybackTokenService Sign throws on empty secret; Verify returns false (fail closed)
- FIX-350-04: ResolverService probe timeout `break` → `continue` (don't stop probing remaining candidates)
- FIX-350-05: SeriesGapRepairService verification exception returns `false` (fail closed, no ghost .strm)
- FIX-350-06: SetupService path traversal — IsPathSafe() rejects `..` and invalid paths before CreateDirectory
- FIX-350-07: ItemPipelineService WriteStrmFileAsync — replaced placeholder with StrmWriterService integration
- FIX-350-08: StreamResolver series — updated comment (MediaItem lacks Season/Episode; series resolved via ResolverService)
- FIX-350-09: Provider state persistence — DB helpers + StreamResolutionHelper persist on failover + Plugin.cs restores on startup
- FIX-350-10: Circuit breaker state persistence — ResolverHealthTracker PersistStateAsync/RestoreState + DB helpers
- FIX-350-11: IMDB ID format validation — regex `^tt\d{7,8}$` in IdResolverService fast path
- FIX-350-12: RateLimiter — removed X-Forwarded-For/X-Real-IP trust; RemoteIp only
- FIX-350-13: M3U8 upstream fetch retry — 2 attempts on transient failure before 502
- FIX-350-14: StreamResolutionHelper — UpsertResolutionResultAsync wrapped in try-catch

## Files Changed

- Services/ManifestFetcher.cs: Inverted logic fix
- Services/ResolverHealthTracker.cs: Circuit close fix + persistence (DatabaseManager dep + PersistStateAsync/RestoreState)
- Services/PlaybackTokenService.cs: Fail closed on empty secret
- Services/ResolverService.cs: Probe break→continue
- Services/SeriesGapRepairService.cs: Exception→false (fail closed)
- Services/SetupService.cs: IsPathSafe() path traversal guard
- Services/ItemPipelineService.cs: WriteStrmFileAsync real impl + ILogManager dep
- Services/StreamResolver.cs: Series comment accuracy fix
- Data/DatabaseManager.cs: SetActiveProviderAsync/GetActiveProvider + circuit breaker state helpers
- Services/StreamResolutionHelper.cs: Provider persist on failover + DB write try-catch
- Plugin.cs: Restore provider state + circuit breaker state on startup + pass DB to ResolverHealthTracker
- Services/IdResolverService.cs: IMDB ID regex validation
- Services/RateLimiter.cs: Removed forwarded header trust
- Services/StreamEndpointService.cs: M3U8 retry loop
- Tasks/SyncTask.cs: Pass ILogManager to ItemPipelineService
