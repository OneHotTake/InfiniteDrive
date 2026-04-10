---
status: complete
task: Sprint 154 — Security Hardening & Dead Config Cleanup
phase: Complete
last_updated: 2026-04-10

## Sprint 154 — Security Hardening & Dead Config Cleanup

### Task 1: FIX-154A — Admin auth for SetupService
- [x] Added IRequiresRequest interface
- [x] Injected IAuthorizationContext via constructor
- [x] AdminGuard.RequireAdmin() on both POST endpoints (CreateDirectories, RotateApiKey)

### Task 2: FIX-154B — Rehydration auto-drain
- [x] Changed GetDefaultTriggers() from empty to 24-hour interval (2h max runtime)
- [x] Pending rehydration operations now auto-drain within 24h

### Task 3: FIX-154C — Remove MaxFallbacksToStore
- [x] Removed from PluginConfiguration.cs (property + Validate)
- [x] Removed from configurationpage.js (load + save)
- [x] Removed from configurationpage.html (input element)

### Task 4: FIX-154D — Build verification
- [x] dotnet build -c Release — 0 errors, 1 pre-existing warning

---

## Sprint 154 Summary
- SetupService now requires admin auth (closes P0 security gap)
- RehydrationTask auto-drains pending operations daily (closes P2 reliability gap)
- MaxFallbacksToStore dead config removed from all layers (P3 cleanup)
- 9 of 14 original audit findings were already fixed or incorrect

---

## Audit Verification (for reference)
- TriggerService: already secured (AdminGuard on all endpoints)
- WebhookService: intentionally uses shared-secret auth for external integrations
- MetadataFallbackTask: already has 500ms delay + 50-item cap
- ProxyMode, SignatureValidityDays, AioStreamsStreamIdPrefixes: all active
- summonMarvin: already wired to EmbyStreamsDeepClean
- es-sync-sources-list: already loaded via loadContentMgmtSources()
- Controllers/: deleted in Sprint 152

---

## Progress

### Sprint 148: 7/7 phases complete
### Sprint 152: 5/5 tasks complete
### Sprint 154: 4/4 tasks complete
