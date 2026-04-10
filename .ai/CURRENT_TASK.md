---
status: completed
task: Sprint 156 — Webhook Retirement & Unified Write Path
phase: Complete
last_updated: 2026-04-10

## Sprint 156 — Webhook Retirement & Unified Write Path

### Phase 156A: StrmWriterService
- [x] Created `Services/StrmWriterService.cs` with WriteAsync method
- [x] Registered as singleton on `Plugin.Instance`
- [x] Constructor takes ILogManager, DatabaseManager

### Phase 156B: Migrate Callers
- [x] CatalogSyncTask: migrated to StrmWriterService.WriteAsync
- [x] FileResurrectionTask: migrated to StrmWriterService.WriteAsync
- [x] Deleted WriteStrmFileForItemPublicAsync from CatalogSyncTask
- [x] DiscoverService.AddToLibrary: migrated to StrmWriterService.WriteAsync

### Phase 156C: Attribution Column
- [x] Added first_added_by_user_id column to catalog_items table (V22→V23 migration)
- [x] Added FirstAddedByUserId property to CatalogItem model
- [x] Added SetFirstAddedByUserIdIfNotSetAsync method to DatabaseManager
- [x] StrmWriterService.WriteAsync calls SetFirstAddedByUserIdIfNotSetAsync (first-writer-wins)
- [x] Updated SELECT queries to include first_added_by_user_id column

### Phase 156D: Delete WebhookService
- [x] Removed WebhookSecret from PluginConfiguration.cs
- [x] Removed WebhookSecret from configurationpage.js
- [x] Deleted Services/WebhookService.cs

### Phase 156E: Clean Up Bypass Logic
- [x] Updated ItemPipelineService TODO comment to be explicit about user-added bypass
- [x] DigitalReleaseGateService bypass already explicit - no changes needed

### Phase 156F: Build & Verification
- [x] dotnet build -c Release — 0 errors
- [x] Grep checklist: verify all deleted symbols are gone
  - WebhookSecret: 0 references in .cs or .js (only docs/dumps)
  - WebhookService/IWebhookService: 0 code references (only comment in TriggerService.cs)
  - WriteStrmFileForItemPublicAsync: 0 references in .cs
- [x] Smoke test: full sync
  - Schema at V24 (includes first_added_by_user_id)
  - Catalog sync successful: 50 AIOStreams catalogs discovered
  - No errors in logs

### Phase 156G: Documentation
- [x] Updated REPO_MAP.md: Removed WebhookService, added StrmWriterService
- [x] Updated BACKLOG.md: Added Sprint 156 entry

## Bugs Fixed During Sprint 156
- UpsertCatalogItemAsync: Removed stray `strm_token_expires_at` column (belongs in materialized_versions)
- Multiple SELECT statements: Removed `strm_token_expires_at` from catalog_items queries
- GetItemsWithExpiringTokensAsync: Stubbed to use materialized_versions (TODO for future refactor)
- V22→V23 migration: Added ColumnExists check to avoid duplicate column error
- V23→V24 migration: Fixed INSERT/SELECT to include `first_added_by_user_id`

## Summary
Sprint 156 complete. All phases finished, docs updated, ready to commit.

