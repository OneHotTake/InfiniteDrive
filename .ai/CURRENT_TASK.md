SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution
COMPLETE: Resilient Deletion Policy — absent_syncs column + IncrementAbsentSyncsAsync (two-phase, pin/block-safe) + PruneSourceAsync threshold gate + UpdateLastVerifiedAtAsync reset + DeleteWithVersions (version-variant cleanup) + AbsentSyncsThreshold config (default 3). Build: 0 errors.
COMPLETE: Settings UX refinement — wording pass across 8 UI files (StatusUI, StatusTabView, ConnectUI, SetupUI, ContentControlsUI, ContentControlsTabView, SyncAndMarvinUI+TabView, AdvancedUI). Build: 0 errors.
COMPLETE: Discover search — manifest-routed catalog IDs, Cinemeta supplement, local catalog + Emby library blend, aiostreamserror filter, primary→secondary failover. Build: 0 errors.
COMPLETE: Discover rails — DB-first (Recently Added + In Your Library from catalog_items), Cinemeta background cache (6h TTL, interlocked refresh). Rails return in <50ms. Build: 0 errors.
COMPLETE: StrmFileManager.WriteOrReplaceStrmFilesAsync now calls ReportFileSystemChanged so Emby auto-detects new .strm files. Build: 0 errors.
COMPLETE: Discover "In Library" dual-source check — GetCatalogItemAioIdsWithStrmPath in DatabaseManager.Catalog.cs + fallback in BatchLibraryLookup (DiscoverService.cs) + search endpoint library status fix. Root cause: search endpoint hardcoded InLibrary=false. Build: 0 errors.
COMPLETE: PrioritizeExtendedEditions toggle — StoredVersion.Edition, SerializeVersions Edition field, MakeVersion edition prefix, split bucket logic + IsExtended helper, PluginConfiguration 2 new props, 4 call sites updated. Build: 0 errors.
## AIOStreams Ranking Improvements (2026-05-07)
COMPLETE: T1-A SeaDex +8/+4 bonus + [SeaDex] label; T1-B VisualTags/Encode scoring + DV/HDR label in version; T1-C Library +10 + [Library] label; T2-B eac3/ac3 aliases; T2-C ParsedFile.Quality direct; T2-D Edition field. Build: 0 errors.

---
status: complete
task: Discover Page Overhaul — Apple TV-style poster grid UI
last_updated: 2026-05-07

## Summary
- Replaced GenericEdit Discover page with custom IHasWebPages embedded HTML+JS
- Apple TV-style poster card grids, horizontal rails, search-as-you-type, detail modal
- Reuses existing REST API (DiscoverService) — no backend changes
- Deleted: DiscoverController.cs, DiscoverPageView.cs, DiscoverUI.cs, discoverpage.css
- Created: Configuration/discoverpage.html (~150 lines), Configuration/discoverpage.js (~220 lines)
- Modified: Plugin.cs (added IHasWebPages + GetPages()), InfiniteDrive.csproj (embedded resources)
- Build: 0 errors, 0 warnings
last_updated: 2026-05-06

## Summary
- Branch: feature/multi-version-strm-prewrite
- CDN URL failover: primary→secondary→fresh-resolve pipeline in OpenMediaSource
- Self-healing: Marvin stream list comparison promotes secondary on dead primary
- SecondaryUrl on StoredVersion/SelectedVersion, assigned by AssignSecondaryUrls
- Versioned GetMediaSources: serves direct-play sources from stored versions with failover tokens
- StrmFileManager.RewriteSingleStrmFile for atomic single-file URL replacement
- DatabaseManager.UpdateStoredVersionUrlAsync for targeted JSON column updates
- Build: `dotnet publish -c Release` — 0 errors, 0 warnings

## Summary
- Fixed default page (was Advanced, now Providers via CreateDefaultPageView)
- Removed duplicate Providers tab (main page IS Providers)
- Moved Language & Ratings from Advanced → Libraries
- Added TypeOptions to LibraryProvisioningService (auto-select metadata downloaders/image fetchers)
- Fixed STRM creation: version_slots seeded with best_available enabled; TriggerBackgroundSync now runs populate phase after sync
- Removed CatalogItemCap; kept CatalogSyncIntervalHours (throttles Marvin sync phase)
- Quality Tiers now toggleable via ToggleButtonItem in Playback tab
- Catalogs now toggleable via ToggleButtonItem
- Libraries now include language/country/subtitle settings

## Files Modified
- UI/Settings/SettingsController.cs — tab order, load/save for libraries+language
- UI/Settings/LibrariesUI.cs — added language fields
- UI/Settings/AdvancedUI.cs — removed language fields
- UI/Settings/AdvancedTabView.cs — removed language saves
- UI/Settings/CatalogsUI.cs — removed CatalogItemCap, added ToggleCatalogCommand
- UI/Settings/CatalogsTabView.cs — ToggleButtonItem for catalog enable/disable
- UI/Settings/PlaybackUI.cs — added ToggleTierCommand, TierStatus
- UI/Settings/PlaybackTabView.cs — full rewrite with toggle support
- Services/LibraryProvisioningService.cs — BuildTypeOptions with metadata/image fetchers
- Data/DatabaseManager.cs — best_available as default enabled slot
- Plugin.cs — TriggerBackgroundSync now runs populate phase

## Build
`dotnet build -c Release` — 0 warnings, 0 errors

## Sprint 500 — Dead Code Deletion (2026-04-29)
COMPLETE: Deleted 10 task files + 5 service files; removed EnableAnimeLibrary, EnableNfoHints, EnableStreamPrefetch, PrefetchBatchDelayMs from config; stripped IScheduledTask from CatalogSyncTask/RefreshTask/PreCacheAioStreamsTask (now internal helpers, invisible to Emby UI). Only MarvinTask appears in Emby scheduled tasks. Build: 0 errors, 0 warnings.

## Sprint 501 — New Documentation (2026-04-29)
COMPLETE: Created ARCHITECTURE.md, SETTINGS_DESIGN.md, MARVIN_STATE_MACHINE.md. Updated README.md with docs index.

## Sprint 502 — Backend Plumbing (2026-04-29)
COMPLETE: PluginConfiguration 14 new properties added (MoviesLibraryName/Path, SeriesLibraryName/Path, AnimeLibraryName/Path, CertificationCountry, DefaultSubtitleLanguage, PreferredQualityTiers, DefaultQualityTier, HideUnratedContent, MaxListsPerUser, MarvinProcessIntervalMinutes, StreamResolutionBatchSize, MarvinActionsPerHour, PluginLogLevel, CacheRefreshIntervalDays); Validate() extended; AdvancedTabView now calls TriggerBackgroundSync after save; StreamCacheService comment added; ApplyParentalFilter updated with HideUnratedContent global filter; build: 0 errors, 0 warnings.

## Sprint 506 — Tab 4: Sync & Marvin (2026-04-29)
COMPLETE: Created SyncAndMarvinUI.cs + SyncAndMarvinTabView.cs; registered as Tab 4 in SettingsController.cs (Setup → Catalogs & Lists → Content Controls → Sync & Marvin → Libraries → Playback → Health → Advanced); MarvinProcessIntervalMinutes, StreamResolutionBatchSize, MarvinActionsPerHour number fields; RespectPlaylistsWhenPruning + AutoDeduplicatePhysicalMedia toggles added to PluginConfiguration.cs; Run Marvin Now button; read-only pruning summary; Marvin-on-save wired; SETTINGS_DESIGN.md + ARCHITECTURE.md updated. Build: 0 errors, 0 warnings.

## Sprint 507 — Tab 5: Advanced (2026-04-29)
COMPLETE: Rewrote AdvancedUI.cs + AdvancedTabView.cs as minimal final tab; ShowAdvanced toggle at top; PluginLogLevel dropdown, ClearCache button, CacheRefreshIntervalDays field, ResetAllData/RebuildLibraries/ResetFactoryDefaults buttons with confirmation dialogs; tab order consolidated to 5 tabs (Setup → Catalogs & Lists → Content Controls → Sync & Marvin → Advanced); Libraries/Playback/Health tabs removed from SettingsController.cs; SETTINGS_DESIGN.md + ARCHITECTURE.md updated. Build: 0 errors, 0 warnings.

## Sprint 508 — Final Cleanup & Polish (2026-04-29)
COMPLETE: Locked final 5-tab order in SettingsController.cs; removed dead Load/Save methods (LoadProviders, LoadLibraries, LoadCatalogs, LoadPlayback, LoadHealth, SaveProviders, SaveLibraries, SaveCatalogs, SavePlayback); deleted 12 legacy files (CatalogsUI/TabView, PlaybackUI/TabView, LibrariesUI/TabView, HealthUI/TabView, ProvidersUI/TabView, MetadataUI, SecurityUI); added "Don't Panic" footer LabelItem to all 5 UI classes; SETTINGS_DESIGN.md rewritten with clean 5-tab structure; ARCHITECTURE.md updated with Sprint 508 completion note. Build: 0 errors, 0 warnings.

## Playback Pipeline Hardening (2026-05-03)
COMPLETE: 6-step playback pipeline hardening across AioMediaSourceProvider + StreamCacheService. (1) Pre-cache sources now use Path="" + RequiresOpening + OpenToken to prevent ffprobe storm. (2) OpenMediaSource skips HEAD on rank-0 (saves 0-5s happy path). (3) Blocking ProbeAndSetStreamsAsync replaced with BuildFallbackStreamsFromFilename + fire-and-forget ProbeAndCacheAsync in all 3 OpenMediaSource paths. (4) Fresh-resolve fallback via TryFreshResolveAsync when all candidates fail; MediaType added to OpenTokenData. (5) TryRefreshCandidateUrlAsync matches FileIdx to prevent wrong-file URLs from multi-file torrents. (6) ApplyProbes fixed sync-over-async. Build: 0 errors, 0 warnings.

## Comprehensive Logging (2026-05-03)
COMPLETE: Added detailed timing logging (start/end/duration) to CdnProber.cs, AioStreamsClient.cs (GetMovieStreamsAsync, GetSeriesStreamsAsync, GetRawStringAsync). Logs now track HTTP GET time, content read time, and total elapsed time for all external calls. Build: 0 errors, 0 warnings.

## Phase 1 — Cache Consolidation + aio_id Fix (2026-05-04)
COMPLETE: Consolidated resolution_cache + stream_candidates + cached_streams into single stream_resolution_cache table. Deleted 13 files (models, repositories, services for version-slot system). Replaced StreamScoringService with inline ranking in AioMediaSourceProvider, ResolverService, PreCacheAioStreamsTask. Added aio_id TEXT NOT NULL as primary lookup key (stores AIOStreams top-level id — supports IMDB, KITSU, MAL, etc.). imdb_id now nullable (only real tt-prefixed IDs). tmdb_key kept as nullable secondary column. UNIQUE constraint on (aio_id, COALESCE(season,-1), COALESCE(episode,-1), rank). Migration with idempotency check via cache_migrated_v2 flag in plugin_metadata. v1→v2 upgrade path for databases that already ran the first migration. All public method signatures unchanged. Build: 0 errors, 0 warnings.

## Technical Debt Plan (2026-05-04)
Compiled from 3 audits (Claude, Qwen, Grok) → 40 items across HIGH/MED/LOW.
6 sprints + 1 cross-cutting track. See BACKLOG.md for full details.

Attack order:
  511 (quick wins + bug fixes) → 512 (factory) → 513 (DB decomposition) → 514 (service extraction)
                                ↘ 515 (model consolidation) → 516 (god class internals)

Sprint 511 (LOW):  DRY ranking, RateLimiter leak, PluginSecret race, 5 quick kills
Sprint 512 (MED):  AioStreamsClient factory — 22 `new` sites → 1 factory
Sprint 513 (HIGH): DatabaseManager decomposition — extract repos, fix .Result, target <3500 lines
Sprint 514 (MED):  Service extraction — OpenMediaSourceHandler, ResolverService split, CatalogSyncTask phases
Sprint 515 (HIGH): Model consolidation — merge 3 stream DTOs, 2 state machines, fix dual ICatalogRepository
Sprint 516 (MED):  God class internals — DiagnosticsEndpoints split, StreamHelpers split, StrmWriter dedup, AioStreamsClient cleanup

## Technical Debt Reduction Post-516 (2026-05-04)
COMPLETE: BUG-01 (ResolverService rate-limit return null→continue), H-01 (MarvinTask SELECT * → explicit columns), H-03 (11 dead DatabaseManager methods deleted), H-04 (5 dead AioMediaSourceProvider members deleted), H-05 (3 dead CatalogSyncTask methods deleted), M-01 (GetNeedsEnrichCountAsync/GetBlockedCountAsync added to DB; StatusService+MarvinTask use them), M-03 (RefreshTask GetEmbyBaseUrl dupe deleted; inlined), M-04 (TriggerService lazy HousekeepingService singleton), M-06 (UseRequiresOpening hardcode removed from Validate(); danger comment added), M-09 (ParsePort dead copy in RawStreamsService deleted), M-10 (duplicate HDR check fixed), L-01 (22 silent catch blocks fixed with LogDebug), L-02 (no-op static constructor deleted from Plugin.cs), L-03 (IsSaved/IsBlocked passthrough aliases deleted from MediaItem), L-04 (LEGACY label removed from PluginConfiguration), L-06 (redundant stub assignments removed from HealthService). Build: 0 errors, 0 warnings.

## Metadata Parsing Fixes (2026-05-07)
COMPLETE: AioMetaResponse — imdb_id snake_case fix, director→Directors List<string> + StringOrArrayConverter, Released/AppExtras/AioAppExtras/AioCastMember/AioLink/Links fields, AioBehaviorHints.DefaultVideoId. AioMetadataProvider — ParseRuntimeMinutes (handles 1h49min/Xh/Xmin/plain), app_extras rich cast fallback, IMDB from meta.ImdbId/behaviorHints.DefaultVideoId, PremiereDate from Released, collection tag from Links, GetSearchResults implemented for Movie+Series. Build: 0 errors, 0 warnings.

## Stream Cache Polish + Subtitle Extraction (2026-05-05)
COMPLETE: Part 1 — 6 cache pipeline bugs fixed. Bug 1+2: GetUncachedItemsAsync NOT EXISTS now checks expires_at, expired/stale entries picked up for re-resolution. Bug 3+4: Removed dead PreCacheIntervalHours and CacheRefreshIntervalDays from config+UI. Bug 5: Batch order randomized for jitter. Bug 6: Post-loop dead-link probe on 5 recent entries. Part 2 — Full subtitle support via ISubtitleProvider. AioStreamsSubtitle enhanced with title/langCode/fromTrusted/aiTranslated. FetchSubtitlesAsync added to AioStreamsClient with provider iteration. PreCache fetches + scores subtitles via Jaccard matching. AioSubtitleProvider implements ISubtitleProvider (cache-first, live fallback). Live resolve path also decorates subtitles. New: Services/AioSubtitleProvider.cs, GetRecentCachedEntries/GetCachedSubtitlesAsync in DatabaseManager.StreamCache.cs. Modified: 11 files. Build: 0 errors, 0 warnings.

## AIOStreams Onboarding + SDK Upgrade (2026-06-28)
COMPLETE: Phase 0 SDK upgrade 4.10.0.8 → 4.10.0.16-Beta. Swapped 6 libs/ DLLs from emby-beta/opt/emby-server/system (deb 4.10.0.16). One API break fixed: IMediaSourceProvider.OpenMediaSource now (string openToken, string consumerId, List<ILiveStream>, CancellationToken) in AioMediaSourceProvider.cs. dotnet publish -c Release: 0 errors. Smoke-test (Emby load) pending.
COMPLETE: Phase 1 parser hardening (CandidateNormalizer.cs) — NFKC unicode fold in ParseQualityString (math-bold/superscript cosmetic formatters → ASCII; no-op for plain filenames); demoted parsedFile to opportunistic in docs (filename is real primary, parsedFile null on real v2.30 instances). Build: 0 errors. Verified NFKC mapping against captured live unicode.
COMPLETE: Phase 2 content-readiness state engine (StatusService ContentReadiness green/yellow/red over SystemStateService + AioStreams.Ok + hasCatalogs||hasLists; StatusUI headline traffic light). Build: 0 errors.
COMPLETE: Phase 3 read-only manifest probe (ManifestUrlParser census: catalog count/browsable/search-only + stream resource; ProbeStreamSignalsAsync filename/videoSize/parsedFile tiers; ConnectTabView Test button shows full diagnostic). Build: 0 errors.
COMPLETE: Phase 4 opt-in formatter+sort config write (AioStreamsConfigClient GET/PUT /api/v1/user Basic auth, outbound client only; touches ONLY formatter+sortCriteria; diff preview; ConnectUI password + Preview/Apply buttons; PrimaryManifestPassword config). Verified PUT contract {config,password} + old manifest URLs survive writes. Build: 0 errors.
COMPLETE: Unit tests (40 pass) — CandidateNormalizer NFKC/filename, AioStreamsConfigClient safety (catalogs/services untouched, idempotent). Fixed real bug: ExtractResolution missed attached-p (1080p/2160p). Removed 2 dead test files (StreamScoringService, PlaybackTokenService — types deleted), fixed stale assertions.
COMPLETE: E2E (live, dev Emby 4.10.0.16, real AIOStreams). configure→sync(11802 items)→strm(38887 files)→playback(HTTP 206 video/mp4) PASS. Phase 2 ContentReadiness=green live. Phase 3 probe live. Phase 4 ApplyRecommended tested on BOTH instances (formatter+sort changed, catalogs/presets/services preserved, idempotent, manifest URL survives, restored). Search/resolution PASS. Failover fault injection PASS (primary dead→secondary serves, readiness stays green). Harness: scripts/e2e-playback-test.sh + LiveConfigIntegrationTests.cs.
FOUND+FIXED(dev DB): collection_membership stale schema (missing aio_id) broke AddToLibrary → recreated table per alpha policy. Code CREATE is correct; old DBs need reset. RemoveFromLibrary "not found" is correct per-user-save semantics.
COMPLETE: User (non-admin) flows verified + fixed (live, idtester user). mdblist user-list add → "Top Watched Movies Of The Week" Emby playlist (50 items); discover search+add → "My InfiniteDrive" playlist (The Matrix). 6 bugs fixed: (1) list-add SQLite crash (source clobber vs ON CONFLICT(aio_id,source)); (2) AddToLibrary short-circuit skipped per-user surfacing; (3) PlaylistService.CreatePlaylistAsync Guid.TryParse on numeric internal id → null → never populated (re-find by name); (4) PlaylistService had no IUserManager to create playlists (wired through); (5) list items never surfaced per-user (added membership + immediate playlist); (6) usability: list name was raw URL (FriendlyNameFromUrl slug→Title). Error handling verified robust (non-https/unsupported/404/empty/missing-fields/unauth all clean). 44/44 unit tests pass.
COMPLETE: Deletion/prune semantics (live-verified). List removal = soft-delete (memberships/playlist persist by design). User deletion: NEW EmbyEventHandler UserDeleted hook → DeleteAllUserDataAsync (wipes user_catalogs/collection_membership/user_item_saves) — verified E2E (53 memberships→0). Prune: GetAbsentPruneCandidatesAsync (threshold + not in membership + not in playback_log) → HasBeenPlayedByAnyUser Emby IsPlayed filter → SoftDeleteCatalogItemsAsync. Ever-watched protection proven (membership/playback/Emby-IsPlayed). Revived dead playback_log write in OnPlaybackStopped. Files: DatabaseManager(.cs/.Catalog.cs), CatalogSyncTask, MarvinTask, Plugin, InfiniteDriveInitializationService, PlaylistService, EmbyEventHandler. 44/44 tests pass. NOTE: dev DB missing catalog_items.global_absent_syncs (stale schema) → prune silently no-op'd there; code CREATE correct, reset fixes.
COMPLETE: User-flow gap testing + admin settings audit (live, Playwright + API). USER: rails/browse/search/detail/add/remove/list-add/list-remove/list-limit/user-deletion all verified. Fixed 2 bugs: RemoveFromLibrary required media_items row (now removes via membership/playlist; add↔remove round-trip verified); Discover/Detail hung ~30s on non-catalog items (8s timeout + graceful fallback). Isolation "leak" = by-design (strm in shared Emby library → shared playlist), per user. ADMIN: all 8 GenericEdit tabs (Overview/Libraries/Quality/Providers/Sources/Restrictions/Marvin/Advanced) render cleanly, save round-trips (verified REMUX toggle persist+revert), actions present. Phase 2 readiness light + Phase 3 manifest test + Phase 4 password/Apply-formatter UI all render correctly. Discover page (custom HTML) beautiful + functional (poster rails, badges, search-as-you-type autocomplete). 44/44 unit tests pass.
COMPLETE: Found + tested Trakt/TMDB keys (live). Keys were in .ai/test-secrets.json (traktUserId=Trakt Client ID, tmdbApiKey=v3). Both validated against provider APIs + set via Emby plugin-config API (XML edits get overwritten by Emby on shutdown — gotcha). End-to-end as user idtester: Trakt list (250 fetched), TMDB list (9 fetched). FIXED BUG: ListFetcher._http had no User-Agent → Trakt Cloudflare HTML 403 (added default UA). POLISH: TMDB list DisplayName now uses real list name (was numeric slug "2"). All 4 providers (mdblist/anilist/trakt/tmdb) enabled+working. 44/44 tests pass.
COMPLETE: Settings UX audit (all 8 tabs, live browser + stored creds). Fixed: (1) Providers manifest Test timeout 10s→30s + reworded misleading "check your network" msg — was false-failing on slow-but-working 12s manifests; now shows "OK — Duck Streams v2.30.4 · 107 browsable/6 search-only"; (2) Providers copy removed nonexistent "Emby server address" field ref; (3) Overview: removed no-op Save button (ShowSave=false, read-only page); (4) Overview terminology "Version Strategy"→"Quality"; (5) empty "—" status cards → meaningful text (Block List/Sync/System Lists); (6) Marvin "Respect user playlists when pruning" toggle WIRED into prune (was saved-but-never-read/dead) + copy mentions ever-watched protection. Verified-good: all destructive Advanced actions have ConfirmationPrompt; Libraries/Quality/Sources/Restrictions/Marvin well-designed. 44/44 tests pass.
COMPLETE: Verified all 4 Advanced destructive actions actually WORK (live, with confirmation dialogs): Clear Cache (stream_resolution_cache 193→0), Force Full Rebuild (triggers Marvin sync), Wipe Library Data (catalog_items 14722→0, user lists/settings preserved), Reset to Factory Defaults (config cleared, IsFirstRunComplete=false). FOUND+FIXED BUG: Wipe Library Data deleted DB rows but left ~54k .strm files on disk (claimed "N paths cleaned" but never deleted them) → AdvancedTabView.ResetAllDataAsync now deletes each via StrmFileManager.DeleteWithVersions + reports actual count. Restored dev config + cleaned orphans + re-synced. 44/44 tests pass.
COMPLETE: Clean setup + E2E test (post-fixes). Validated fixed Wipe deletes .strm (log: 51 deleted). Full E2E PASSED: configure→sync(catalog repopulating)→strm(418 files)→playback HTTP 206 video/mp4 (2.88GB file). Fixed E2E harness to follow redirects (-L): meteor/comet providers 302-redirect to CDN; Emby follows natively, harness now does too (was a false-fail). Dev instance clean + configured + re-syncing.
