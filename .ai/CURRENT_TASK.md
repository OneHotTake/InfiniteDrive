---
status: completed
task: Sprint 158 + 160B/C
phase: Complete
last_updated: 2026-04-10

## Sprint 158 — User Catalogs: Public Trakt + MDBList via RSS

### Phase 158A: Kill Phantom SourceType + Schema
- [x] Removed SourceType.Trakt and SourceType.MdbList from enum
- [x] Added SourceType.UserRss with correct extension methods
- [x] Fixed FileResurrectionTask.cs to use UserRss instead of Trakt/MdbList
- [x] Fixed Tests/SyncPipelineTests.cs to use UserRss
- [x] Updated CurrentSchemaVersion to 25
- [x] Added V25 migration: tvdb_id/raw_meta_json/catalog_type on catalog_items (with ColumnExists guards)
- [x] Added V25 migration: user_catalogs table
- [x] Added V25 migration: user_catalog_id on source_memberships
- [x] Updated sources table CHECK constraint in safeguard block: removed 'Trakt','MdbList', added 'UserRss'
- [x] Added migration to convert legacy trakt/mdblist source rows to user_rss

### Phase 158B: RssFeedParser
- [x] Created Services/RssFeedParser.cs (parse-only, no HTTP)
- [x] RssItem record, 1000-item cap, IMDb regex extraction
- [x] DetectService static helper (trakt.tv or mdblist.com)

### Phase 158C: UserCatalogSyncService
- [x] Created Services/UserCatalogSyncService.cs with SyncOneAsync/SyncAllForOwnerAsync
- [x] CooldownGate integration, HTTP fetch, RssFeedParser call
- [x] Created Models/UserCatalog.cs POCO
- [x] Added DatabaseManager CRUD: CreateUserCatalogAsync, GetUserCatalogsByOwnerAsync,
      GetAllActiveUserCatalogsAsync, GetUserCatalogByIdAsync, SetUserCatalogActiveAsync,
      UpdateUserCatalogSyncStatusAsync, CountActiveClaimsAsync, UpsertSourceMembershipWithCatalogAsync
- [x] CatalogSyncTask: iterates user catalogs in 6-hour backstop pass

### Phase 158D: User Endpoints
- [x] Created Services/UserCatalogsService.cs with GET/POST endpoints
- [x] GET /EmbyStreams/User/Catalogs (returns active catalogs)
- [x] POST /User/Catalogs/Add (validates URL, fetches feed, inserts, eager syncs)
- [x] POST /User/Catalogs/Remove (soft-delete, ownership check)
- [x] POST /User/Catalogs/Refresh (synchronous, single or all)

### Phase 158F: UI
- [x] Added "My Lists" tab button to configurationpage.html
- [x] Added My Lists tab content (HTML)
- [x] Added loadMyLists, renderMyLists, wireMyListsButtons JS functions
- [x] esAlert helper added

## Sprint 160B/C — IdResolverService + CatalogDiscoverService Integration

### Phase 160B: IdResolverService
- [x] Created Services/IdResolverService.cs
- [x] Parses all prefix formats: tt, tmdb_, tmdb:, tvdb_, tvdb:, kitsu:, mal:, imdb:
- [x] Fast path for tt IDs (no network)
- [x] Source addon /meta/{type}/{id}.json call with 1.5s timeout
- [x] AIOMetadata fallback for tmdb/kitsu/mal IDs
- [x] CanonicalId: tt > tmdb_ > tvdb_ > native
- [x] ResolvedIds record
- [x] Registered in Plugin.cs

### Phase 160C: CatalogDiscoverService Integration
- [x] Replaced naive `meta.ImdbId ?? meta.Id ?? ""` with manifestId + IdResolverService
- [x] Fast path for tt IDs
- [x] addonBaseUrl computed from client.ManifestUrl
- [x] IdResolver injected via Plugin.Instance.IdResolverService

## Build
- [x] dotnet build -c Release — 0 errors, 1 pre-existing warning

## Summary
Sprint 158 and 160B/C complete. All phases implemented and build clean.
