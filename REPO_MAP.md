# REPO_MAP.md (ultra-lean — updated only at sprint end)

Root
- Plugin.cs                  : entry point + registration
- PluginConfiguration.cs     : all settings + validation
- CLAUDE.md                  : token rules (this file)

Configuration/               : UI HTML/JS only (never read in backend)
Data/                        : Schema + DatabaseManager (SQLite)
Services/                    : all business logic
Services/Api/                : extracted endpoint classes (Catalog, Diagnostics, Health, Stats, etc.)
Tasks/                       : background tasks
Models/                      : POCO only + ResolutionResult + AioStreams DTOs

.ai/
- CURRENT_TASK.md            : active task only (read first)
- SESSION_SUMMARY.md         : token ledger
- SPRINT_TEMPLATE.md         : one-page only

Everything else archived. Max 3 files per subtask. Never re-read.

## Sprint 215 Complete (2026-04-13)
- Settings Redesign: wizard-based UI → flat 7-tab Apple-style layout
- New tabs: providers, libraries, sources, security, parental, health, repair
- All wizard code removed from configurationpage.js

## Sprint 216 Complete (2026-04-14)
- Anime catalog routing fix: accept kitsu: IDs, force anime mediaType for anime catalogs
- Research sprint: 41 silent drop paths inventoried, schema gaps documented, plugin comparison done
- See BACKLOG.md "Sprint 216" for full research findings and Sprint 217 recommendations

## Sprint 219 Complete (2026-04-14)
- IChannel SDK Reality Check (research): ISearchableChannel is dead marker interface, no search path exists
- Browse-only IChannel confirmed possible via FolderId routing
- See .ai/research/sprint-219-findings.md for full findings

## Sprint 220 Complete (2026-04-14)
- Series gap detection via Emby TV REST endpoints (GetSeasons/GetEpisodes/GetMissing)
- New: Services/EmbyTvApiClient.cs, Services/SeriesGapDetector.cs, Tasks/SeriesGapScanTask.cs
- Modified: DatabaseManager (GetIndexedSeriesAsync), TriggerService (series_gap_scan), StatusService (SeriesGapSummary), SeriesPreExpansionService (deferred gap scan)
- Adapted: queries media_items (has emby_item_id) not catalog_items as sprint assumed

## Sprint 221 Complete (2026-04-14)
- Series gap repair: writes missing .strm + .nfo files for detected gaps
- New: Services/SeriesGapRepairService.cs, Tasks/SeriesGapRepairTask.cs
- Modified: StrmWriterService (WriteEpisodeStrm + WriteEpisodeNfo), SeriesGapDetector (autoRepair hook), DatabaseManager (GetSeriesWithGapsAsync), TriggerService (series_gap_repair), StatusService (repair stats)
- Fixed: seasons_json now includes missingEpisodeNumbers from Sprint 220's detector

## Sprint 222 Complete (2026-04-14)
- Catalog-first episode sync: Videos[] stored in catalog_items, diff-based add/remove of episodes
- New: Services/EpisodeDiffService.cs, Services/EpisodeRemovalService.cs
- Modified: SeriesPreExpansionService (VideosJson storage + SyncSeriesEpisodesAsync diff), DatabaseManager (videos_json column), RemovalService (full series folder delete), StatusService (EpisodeSyncSummary), TriggerService (deprecation warnings)
- Deprecated: SeriesGapScanTask, SeriesGapRepairTask marked [Obsolete]

## Sprint 310 Complete (2026-04-15)
- Critical path unification: ResolverService now tries primary → secondary providers with circuit breaker
- ResolverHealthTracker is a shared singleton (Plugin.Instance.ResolverHealthTracker)
- Deleted DirectStreamUrl endpoint (unsigned URL leakage vector)
- StreamEndpointService returns 503 when PluginSecret missing
- All HMAC comparisons already use FixedTimeEquals (verified)

## Sprint 311 Complete (2026-04-15)
- Self-healing failover: ActiveProviderState tracks Primary/Secondary, auto-restore on Marvin cycle
- CatalogSyncIntervalHours default 24h→1h
- Gap repair verifies upstream streams before writing .strm (no ghost episodes)
- New: POST /InfiniteDrive/Admin/ClearSentinel, Models/ActiveProviderState.cs

## Sprint 312 Complete (2026-04-15)
- Deleted: Tasks/FileResurrectionTask.cs (superseded by MarvinTask)
- Removed dead LastKnownServerAddress config field
- Cleaned misleading debrid fallback comments (InfoHash is active AIOStreams code)
- Verification: zero matches for FileResurrection, DirectDebrid, DirectStreamUrl in source

## Sprint 350 Complete (2026-04-15)
- 14 critical audit fixes: ManifestFetcher showstopper, circuit breaker close/persist, PluginSecret fail-closed, probe continue, gap repair fail-closed, path traversal guard, ItemPipeline .strm write impl, provider+circuit state persistence, IMDB ID validation, rate limiter hardening, M3U8 retry, DB write resilience

## Sprints 354–357 Complete (2026-04-15)
- S354: NamingPolicyService — one BuildFolderName, one SanitisePath. Deleted 4 duplicate copies across StrmWriter/SeriesPreExpansion/EpisodeExpand/RefreshTask. Fixed RefreshTask `[tmdbid=X]` bug.
- S355: ResolutionResult — structured failure enum (Success/Throttled/ContentMissing/ProviderDown) replaces null returns in StreamResolutionHelper + ResolverService.
- S356: NfoWriterService — centralized NFO authority with Seed + Enriched quality levels. Deleted 10 private NFO writers, 3 manual XML escapers. All use SecurityElement.Escape. Skipped deletion of 4 active tasks (not obsolete).
- S357: StatusService decomposition — 2655-line file split into StatusService.cs (812) + Api/CatalogEndpoints.cs (520) + Api/DiagnosticsEndpoints.cs (1020) + Api/SearchEndpoints.cs (334).

## Sprints 360–362 Complete (2026-04-15)
- S360: ManifestState container — Plugin.Manifest replaces 3 statics + 4 static methods. Models/ManifestState.cs new.
- S361: PipelinePhaseTracker — Plugin.Pipeline shared in-memory snapshot. Models/PipelinePhaseTracker.cs new. RefreshTask wired.
- S362: Pipeline visibility — HealthResponse.ActivePipeline, MarvinTask + CatalogSyncTask wired. DiagnosticsEndpoints exposes current phase.

## Documentation Audit Complete (2026-04-15)
- Created architecture/ with 6 exhaustive technical docs (OVERVIEW, SERVICES, CONTROL_FLOW, STATE_MANAGEMENT, DTO_SCHEMAS, TASKS)
- Updated 6 newdocs/ files to fix drift from Sprints 354-362
- Audited 46 docs files total: 40 drift findings cataloged and resolved

## Sprint 370 Complete (2026-04-16)
- One-pass series episode sync from AIOStreams meta endpoint
- RefreshTask fetches Videos[] from AIOStreams directly, falls back to Stremio on failure
- StrmWriterService.WriteEpisodesFromVideosJsonAsync for one-pass episode writing

## Language & Localization Sprint Complete (2026-04-18)
- MediaStreams populated from AIOStreams audio languages + subtitles
- Language-aware version picker sorting, per-user candidate preference in ResolverService
- TMDB locale configurable (ListFetcher + CertificationResolver), schema V32
- DiscoverItem.AudioLanguages from stream_candidates

## Sprint 407 (2026-04-23) — NATIVE UI REVERTED
- Native UI migration attempted and reverted. All settings are HTML-based.
- Configuration/UI/ deleted. Plugin uses IHasWebPages only.

## Sprint 410 Complete (2026-04-23)
- Secure playback via RequiresOpening + OpenMediaSource: All playback now gated behind Emby auth
- New: Models/InfiniteDriveLiveStream.cs (ILiveStream wrapper)
- Modified: AioMediaSourceProvider (OpenMediaSource impl, RequiresOpening flag, UseRequiresOpening config)
- Modified: StrmWriterService (ILibraryMonitor notification after .strm write)
- Deprecated: ResolverService, StreamEndpointService, PlaybackTokenService token methods (follow-up)

## Sprint 420 Complete (2026-04-27)
- Stream pre-cache system: background task resolves AIO streams before users browse, version picker appears instantly
- New: Models/CachedStreamEntry.cs (CachedStreamEntry, StreamVariant, UncachedItem), Services/StreamCacheService.cs (IStreamCacheService), Tasks/PreCacheAioStreamsTask.cs
- Modified: Plugin.cs (StreamCacheService singleton), PluginConfiguration.cs (EnablePreCache, PreCacheBatchSize, PreCacheIntervalHours, PreCacheTTLDays)
- Modified: Data/DatabaseManager.cs (cached_streams table + 5 query methods), Services/AioMediaSourceProvider.cs (pre-cache check + write-through), Services/TriggerService.cs (precache trigger)

## Sprint 515 Complete (2026-05-04)
- Model consolidation + service decomposition: 3 stream DTOs merged into StreamCandidate, dual ICatalogRepository eliminated, state machines merged
- DatabaseManager decomposed into 6 partial classes: DatabaseManager.cs (2341), DatabaseManager.MediaItems.cs (1022), DatabaseManager.Catalog.cs (972), DatabaseManager.StreamCache.cs (403), DatabaseManager.Operations.cs (1028), DatabaseManager.Discover.cs (404)
- AioMediaSourceProvider split: AioMediaSourceProvider.cs (966), AioMediaSourceProvider.Open.cs (430), AioMediaSourceProvider.StreamBuilding.cs (389)
- ResolverService split: ResolverService.cs (349), ResolverService.Cache.cs (267)
- CatalogSyncTask extracted: CatalogSyncTask.cs (738), CatalogProviders.cs (859)
- Deleted: Models/Candidate.cs, Repositories/CatalogRepository.cs

## Sprint 516 Complete (2026-05-04)
- DiagnosticsEndpoints split into 10 individual service files; deleted DebugSeedMatrixService
- New: Services/Api/AnimePluginStatusService.cs, TestUrlService.cs, AnswerService.cs, MarvinService.cs, DbStatsService.cs, PanicService.cs, RecentErrorsService.cs, UnhealthyItemsService.cs, RawStreamsService.cs, HealthService.cs
- Services/Api/DiagnosticsEndpoints.cs — now 7 lines (comment only, services extracted)
- Models/AioStreams.cs — NEW (534 lines, 18 DTOs + exceptions extracted from AioStreamsClient)
- Services/AioStreamsClient.cs — 1482→882 lines (DTOs extracted, dead methods deleted, GetMovieStreamsAsync/GetSeriesStreamsAsync merged into GetStreamsCoreAsync)
- Services/StrmWriterService.cs — deleted duplicate WriteEpisodeStrm, extracted BuildEpisodePath helper
- Services/StreamHelpers.cs — deleted dead ExponentialBackoffMs
- Services/CandidateNormalizer.cs — removed dead NormalizeStreams/BuildCandidate and 5 helper methods

## Sprint 518 Complete (2026-05-04)
- M-07: 3 sync-as-async DB methods → QueryScalarIntAsync with parameter binding overload
- M-08: CooldownKind enum collapsed from 4 values → 2 (Default, SeriesMeta)
- M-02: GracePeriodPolicy static class extracted from RemovalPipeline + RemovalService
- M-01b: TestProviderAsync helper extracted in StatusService (2 duplicate blocks → 1 method)
- M-05: AioStreamsClientFactory.CreateForProvider + TryCreateForManifest; 9 call sites routed through factory
- AioImageProvider: deleted duplicate GetConfiguredProviders, now uses ProviderHelper.GetProviders
- Net: -34 lines across 17 files + 1 new file (Models/GracePeriodPolicy.cs)
