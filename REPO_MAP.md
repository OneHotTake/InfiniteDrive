# REPO_MAP.md (ultra-lean — updated only at sprint end)

Root
- Plugin.cs                  : entry point + registration
- PluginConfiguration.cs     : all settings + validation
- CLAUDE.md                  : token rules (this file)

Configuration/               : UI HTML/JS only (never read in backend)
Data/                        : Schema + DatabaseManager (SQLite)
Services/                    : all business logic
Tasks/                       : background tasks
Models/                      : POCO only

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
