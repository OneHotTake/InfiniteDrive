# EmbyStreams Development Backlog

Versioning: `v0.{SPRINT}.{TASK}`

**Current Status**: Sprint 104 Complete (Beta Software Migration) |
                    Sprints 105-108 Superseded (v3.3 Breaking Change) |
                    Sprints 109-121 Planned (Full v3.3 Implementation) |
                    Sprint 80 Complete |
                    Sprint 79 Complete |
                    Sprint 76 Complete |
                    Sprint 75 Complete |
                    Sprint 74 Complete |
                    Sprint 73 Complete |
                    Sprint 72 Complete |
                    Sprint 71 Build Fix Complete |
                    Sprint 70 Complete |
                    Sprint 69 Complete |
                    Sprint 68 Complete |
                    Sprint 67 Complete |

---

## Sprint 72 — Backlog Audit & Correction

**Status:** Complete — Documentation reconciled with git history

| Audit Item | Status | Result |
|-----------|--------|--------|
| Verify Sprint 67 entry | ✅ COMPLETE | Added to BACKLOG.md |
| Verify Sprint 68 entry | ✅ COMPLETE | Added to BACKLOG.md |
| Verify Sprint 70 FIX 5 status | ✅ COMPLETE | Documented as reverted in Sprint 71 |
| Verify Sprint 69 collection sync status | ✅ COMPLETE | Documented as removed in Sprint 71 |
| Remove duplicate headings | ✅ COMPLETE | All duplicates removed |
| Update REPO_MAP.md | ✅ COMPLETE | Removed stale CollectionSyncService references |

### Changes Made

**Modified BACKLOG.md**:
- Removed duplicate Sprint 71, 70, 69 headings
- Added Sprint 68 — Infrastructure & Bug Fixes
- Added Sprint 67 — Fix Version Badge + deployment improvements
- Updated Sprint 70 FIX 5 to reflect reversion in Sprint 71
- Updated Sprint 69 to reflect collection sync removal in Sprint 71

**Modified .ai/REPO_MAP.md**:
- Removed Services/CollectionSyncService.cs entry (deleted in Sprint 71)
- Removed DatabaseManager GetFetchedItemIdsAsync and GetCatalogItemCountBySourceAsync entries

### Root Cause

Sprint 69 and 70 made changes incompatible with Emby SDK:
- Sprint 69: Collection sync using Jellyfin SDK APIs (not in Emby SDK)
- Sprint 70 FIX 5: Episode query using ParentId property (not in Emby SDK)

Both were reverted in Sprint 71 Build Fix.

### Commit

`[pending]` — Sprint 72 commit

---

## Sprint 71 — Build Fix: Remove Jellyfin Collection Sync Code

**Status:** Complete — All build errors resolved

**Root Cause:** Sprint 69 implemented collection sync using Jellyfin SDK APIs (`CreateCollectionAsync`, `AddToCollectionAsync`, `RemoveFromCollectionAsync`) which do not exist in Emby SDK.

**Note:** Also reverted Sprint 70 FIX 5 (Episode query scope) because Emby SDK lacks `InternalItemsQuery.ParentId` property.

| Fix | Status | Result |
|-----|--------|--------|
| Delete CollectionSyncService.cs | ✅ COMPLETE | Jellyfin-specific code removed |
| Remove EnableCollectionSync | ✅ COMPLETE | Config field removed |
| Remove collection sync call | ✅ COMPLETE | CatalogSyncTask.cs cleaned up |
| Fix GetMetaString nullable access | ✅ COMPLETE | Use .Value for JsonElement? |
| Add using System.Collections.Generic | ✅ COMPLETE | EmbyEventHandler.cs fixed |
| Revert Sprint 70 FIX 5 (episode query) | ✅ COMPLETE | Restored original approach (Emby SDK lacks ParentId) |

### Changes Made

**Deleted:** Services/CollectionSyncService.cs (145 lines)
**Modified:** PluginConfiguration.cs — removed `EnableCollectionSync` field
**Modified:** Tasks/CatalogSyncTask.cs — removed collection sync call (lines 803-818), fixed GetMetaString nullable access
**Modified:** Services/EmbyEventHandler.cs — added using directive, reverted to original episode query

### Build Result

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Commits

`129f048` — fix: Remove Jellyfin-specific collection sync code + fix build errors
`18ff0ae` — chore: Update docs for Sprint 71 Build Fix

---

## Sprint 71 — Validate getStream() Dead Code

**Status:** Complete — Method confirmed non-existent in codebase

| Block | Status | Result |
|-------|--------|--------|
| v0.71.1 — Validate getStream() | ✅ COMPLETE | Method does not exist in codebase |

### Findings

**Result:** `getStream()` method does NOT exist in codebase.

**Evidence:**
- grep shows 0 method definitions for `getStream()`
- grep shows 0 occurrences of reflection/dynamic invocation patterns
- StatusService.cs uses `GetStream` only in string literals (status URLs) — not method calls

**Conclusion:** The method may have been:
1. Already removed in a previous sprint (commit `28929e4`)
2. Never existed (flag may have been a false positive)

**Verification Steps Completed:**
- ✅ Step A: Search for method definitions → 0 results
- ✅ Step B: Check interface contracts → None found
- ✅ Step C: Check reflection/dynamic invocation patterns → 0 results
- ✅ Step D: Zero external references confirmed

### Action Taken

No code changes required. Sprint 71 validated as complete since:
- Method does not exist in current codebase
- No interface contract requires it
- No reflection/dynamic invocation patterns detected

### Commit

`90342af` — docs: Sprint 71 — validate getStream() non-existent

---

## Sprint 70 — Security Hardening, Resource Leak Elimination & NFO Enrichment

**Status:** Complete — 5 of 6 fixes implemented (FIX 5 reverted in Sprint 71)

**Commit:** 3ae4e89

| Fix | Status | Result |
|------|--------|--------|
| FIX 1 — X-Forwarded-For removal | ✅ COMPLETE | SSRF guard added to UnauthenticatedStreamService |
| FIX 2 — AdminGuard on ClearSyncStates | ✅ COMPLETE | Admin-only check added |
| FIX 3 — HttpClient lifetime | ✅ COMPLETE | Static shared instances in AioStreamsClient + StremioMetadataProvider |
| FIX 4 — NFO enrichment | ✅ COMPLETE | Provider ID fields + tags added |
| FIX 5 — Episode query scope | ⚠️ REVERTED | Attempted ParentId scoping — not in Emby SDK, reverted in Sprint 71 |
| FIX 6 — Error message + converter comment | ✅ COMPLETE | catalog_discover added, no-op documented |

### Implementation Summary

**Modified Services/UnauthenticatedStreamService.cs**
- Removed X-Forwarded-For header trust (SSRF vulnerability)
- GetClientIp() now only uses Request.RemoteIp
- Added null IP guard returning 403 instead of rate-limit path

**Modified Services/TriggerService.cs**
- Added AdminGuard.RequireAdmin to Post(ClearSyncStatesRequest) as first statement
- Added catalog_discover to default error message task key list

**Modified Services/AioStreamsClient.cs**
- Replaced per-instance _http field with static _sharedHttp
- Added static constructor to set User-Agent on shared instance
- Removed _http initialization from all three constructors
- Updated Dispose() to NOT dispose _sharedHttp

**Modified Services/StremioMetadataProvider.cs**
- Implemented IDisposable interface
- Replaced per-instance _httpClient with static _sharedHttp
- Added provider ID fields: TmdbId, TmdbAlt, AniListId, KitsuId, MalId
- Added GetTmdbId() helper to coalesce both TMDB field names

**Modified Services/AioStreamsClient.cs (ResourceListConverter)**
- Added explanatory comment on no-op behavior
- Added null/empty string guard for JsonTokenType.String case

**Modified Tasks/CatalogSyncTask.cs**
- Added GetMetaString() helper for safe JSON extraction
- Added provider ID tags to WriteUniqueIds():
  - <tmdbid> (from tmdb_id or tmdb)
  - <anilistid> (from anilist_id)
  - <kitsuid> (from kitsu_id)
  - <malid> (from mal_id)

**Modified Services/EmbyEventHandler.cs**
- Attempted two-step scoped query (HAS ANYPROVIDER ID + ParentId)
- REVERTED in Sprint 71: Emby SDK InternalItemsQuery lacks ParentId property
- Restored to original unbounded query with in-memory filtering

---

## Sprint 69 — NFO Enrichment + Collection Auto-Creation

**Status:** Complete — Collection Auto-Creation feature implemented (REMOVED in Sprint 71)

**Commit:** 76ec20c

| Block | Status | Result |
|-------|--------|--------|
| v0.69.1 — Audit NFO Writer | ✅ COMPLETE | No code changes needed |
| v0.69.2 — Enrich NFO Output | ⚠️ Skipped | Upstream provides uniqueids in meta |
| v0.69.3 — SDK Capability Spike | ❌ BLOCKED | Emby SDK lacks ICollectionManager APIs (Jellyfin-only) |
| v0.69.4 — Config Gate | ❌ REMOVED | EnableCollectionSync removed in Sprint 71 |
| v0.69.5 — CollectionSyncService | ❌ REMOVED | File deleted in Sprint 71 (Jellyfin-only) |
| v0.69.6 — CatalogSyncTask Wire | ❌ REMOVED | Wiring removed in Sprint 71 |

### Implementation Summary

**Created Services/CollectionSyncService.cs** (DELETED in Sprint 71)
- Constructor injection: `ICollectionManager`, `ILibraryManager`, `ILogger`
- `SyncCollectionsAsync()`: Creates collections (≥5 items), adds items, prunes orphans
- Only manages collections whose Name exactly matches a catalog source (never user-created)

**Modified PluginConfiguration.cs** (REMOVED in Sprint 71)
- Added `EnableCollectionSync` config field (default: false)

**Modified Tasks/CatalogSyncTask.cs** (REMOVED in Sprint 71)
- Wired collection sync after .strm files written
- Added `BuildCatalogSources()` helper: maps SourceKeys to display names + item counts

**Note:** Catalog UI checkbox was never implemented — feature removed before implementation.

### Additional Commits

`9892216` — chore: end-of-sprint-69 update
`99b9e02` — chore: sprint report — architecture review
`cafbe90` — chore: architecture review vs gelato/aiostreams

---

## Sprint 68 — Infrastructure & Bug Fixes

**Status:** Complete

**Commit:** 074209b

### Changes Made

**Created Tasks/DoctorTask.cs**
- New unified catalog reconciliation engine with 5-phase item state machine
- Phase 1: Fetch & Diff (catalog vs disk)
- Phase 2: Write (.strm creation with preflight directory checks)
- Phase 3: Adopt (retire .strm when real file detected)
- Phase 4: Health Check (validate cached URLs)
- Phase 5: Report (persist stats to doctor_last_run.json)

**Modified Data/DatabaseManager.cs**
- Added EnsureMediaDirectoriesExist() — Creates /media/embystreams/{movies,shows,anime} with 755 permissions
- WAL mode verification and logging (journal_mode=WAL confirmed)
- PluginSecret initialization deferred until ApplicationPaths ready

**UI Navigation Bug Fixes**
- Fixed tab switching issues
- Fixed wizard navigation state

---

## Sprint 67 — Fix Version Badge + Deployment Improvements

**Status:** Complete

**Commit:** 2896654

### Changes Made

**Modified Plugin.cs**
- Added PluginVersion property that parses plugin.json at runtime
- Replaces broken assembly version fallback (0.0.0.0) due to GenerateAssemblyInfo=false

**Modified StatusService.cs**
- Use Plugin.Instance?.PluginVersion instead of assembly version
- Fixes version badge showing v0.0.0 in UI

**Modified emby-start.sh**
- Deploy plugin.json alongside DLL
- Unify port references to 8096
- Remove duplicate DLL copy line

**Modified emby-reset.sh**
- Deploy plugin.json alongside DLL
- Add LD_LIBRARY_PATH export

---

## Sprint 64 — Universal NFO Generation + Anime Pipeline Normalization

**Status:** Complete

| Task | Status | Result |
|------|--------|--------|
| v0.64.1 — Remove hard anime plugin requirement | ✅ COMPLETE |
| v0.64.2 — Remove anime-specific skip guard for non-tt IDs | ✅ COMPLETE |
| v0.64.3 — Create NFO writer for all synced items | ✅ COMPLETE |
| v0.64.4 — Create episode NFO writer | ✅ COMPLETE |
| v0.64.5 — Write all upstream uniqueid tags to NFO | ✅ COMPLETE |
| v0.64.6 — Extend unresolved item logging | ✅ COMPLETE |
| v0.64.7 — Add sync summary with per-type counters | ✅ COMPLETE |
| v0.64.8 — Clean up config fields | ✅ COMPLETE |
| v0.64.9 — Update anime plugin messaging | ✅ COMPLETE |
| v0.64.10 — Integration tests | ✅ COMPLETE |
| v0.64.11 — Update .ai/REPO_MAP.md | ✅ COMPLETE |

### Key Accomplishments

1. **Unified Content Pipeline**: All content types (movies, series, anime) flow through same code path
2. **Enhanced NFO Generation**: Complete NFO files with all upstream metadata
3. **Flexible ID Handling**: Supports all ID formats from upstream providers
4. **Improved Logging**: Detailed sync summaries with per-type counters
5. **No Plugin Dependencies**: Anime plugin is optional

### Files Modified

- Tasks/CatalogSyncTask.cs — Enhanced NFO writing, per-type counters
- Tasks/EpisodeExpandTask.cs — Added episode NFO writing
- Services/SeriesPreExpansionService.cs — Added episode NFO writing
- PluginConfiguration.cs — Removed AnimeLibraryId
- Configuration/*.html, *.js — Updated anime plugin messaging
- docs/anime-library-setup.md — Updated documentation

---

## Sprint 73 — Fix Episode Query Performance (Emby SDK Native)

**Status:** Complete

**Commit:** 8f8ffa6

### Performance Issue

GetEpisodeCountForSeason() was using unbounded library query:
- Queried ALL episodes with given season number (library-wide)
- Filtered by IMDB ID in-memory via LINQ
- For 500+ series libraries, "season 1" returned 10,000+ episodes
- Performance: O(library) when it should be O(series)

### SDK Investigation

Decompiled MediaBrowser.Controller.dll and confirmed:
- AncestorIds — Int64[] (requires internal ID, not Guid)
- AnySeriesProviderIdEquals — ICollection<KeyValuePair<string, string>> ✓
- SeriesIds — Int64[]

### Solution Implemented

**Chosen Approach:** AnySeriesProviderIdEquals (single query)

- Filters episodes by series provider ID directly
- No need for two-step series lookup
- O(series) instead of O(library)
- Plus 6-hour cache to prevent repeated queries during binge sessions

### Changes Made

**Modified Services/EmbyEventHandler.cs**:
- Added `using System.Collections.Concurrent;`
- Added `_episodeCountCache` static ConcurrentDictionary
- Replaced unbounded GetItemList query with AnySeriesProviderIdEquals
- Removed in-memory LINQ filtering (episodes.Count())
- Added cache check before query
- Added cache update after query (6-hour TTL)

### Result

- Build: 0 warnings, 0 errors
- Performance: O(library) → O(series)
- Cache prevents repeated queries during binge sessions

---

## Sprint 74 — Delete UnauthenticatedStreamService.cs (Dead Code Removal)

**Status:** Complete

**Commit:** cbdcfaf

### Background

Sprint 70 FIX 1 added an SSRF guard to UnauthenticatedStreamService.cs
but did not delete the file as originally specified. The file provided
GET /EmbyStreams/GetStream endpoint designed for FFprobe/local tools,
but was never called by any client.

### Verification

**Step A: Confirm no external callers**
- grep confirmed 0 external callers
- Only references are string URL literals in StatusService.cs (not method calls)
- No type references to UnauthenticatedStreamService or GetStreamRequest

### Changes Made

**Deleted:** Services/UnauthenticatedStreamService.cs (11,787 bytes)

**Modified .ai/REPO_MAP.md**
- Removed UnauthenticatedStreamService.cs entry

**Modified .ai/FAILURES.ndjson**
- Appended failure record documenting dead code removal

### Build Result

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Note

FFprobe use case is covered by the signed Stream endpoint
(GET /EmbyStreams/Stream with HMAC signature). The unauthenticated
endpoint was designed for convenience but never integrated into any client.

---

---

## Sprint 77 — Multi-Provider ID Support (P0)

**Status:** ✅ COMPLETE
**Commit:** e07efd8

Based on Sprint 76 research findings:
- FIX 1: UniqueIdsJson column in catalog_items ✅
- FIX 2: Multi-provider episode count fallback chain ✅

**Changes:**
- Schema migration: Added unique_ids_json column
- CatalogItem model: UniqueIds property + GetProviderId() helper
- CatalogSyncTask: BuildUniqueIdsJson() + ExtractProviderIds() from AIOStreams meta
- DatabaseManager: GetCatalogItemByProviderIdAsync() + ParseUniqueIdsJson() helper
- EmbyEventHandler: GetEpisodeCountForSeason queries all providers
- Fallback chain: IMDB → AniList → Kitsu → MAL → TMDB → 30

**Result:** Anime without IMDB IDs now supported for episode counting

---

## Sprint 76 — Stremio-Kai Research (AIOStreams Robustness)

**Status:** Complete — Research sprint, no code changes
**Commit:** 043066b

Research-only sprint analyzing Stremio-Kai browser extension patterns
to identify improvements EmbyStreams should adopt for multi-provider ID
support, anime detection, and stream resolution robustness.

### Deliverables

**Created:**
- `.ai/research/stremio-kai-analysis.md` — Repository structure overview
- `.ai/research/stremio-kai-provider-ids.md` — Provider ID handling documentation
- `.ai/research/stremio-kai-anime.md` — Anime-specific handling documentation
- `.ai/research/stremio-kai-recommendations.md` — Prioritized improvements list

### Key Findings

**Stremio-Kai vs EmbyStreams:**

| Area | Stremio-Kai | EmbyStreams | Gap |
|------|-------------|-------------|-----|
| Provider IDs | 6 providers (IMDb/TMDB/TVDB/MAL/AniList/Kitsu) | IMDb + TMDB only | **Critical** |
| ID Conversion | Haglund API + LRU cache (24h TTL) | None | **Critical** |
| Anime Detection | 3-tier (demographics → Japan+animation → anime DB IDs) | Catalog type field only | **Significant** |
| Episode Count | Multi-provider fallback chain | IMDB-only Emby query | **Critical** |
| Rate Limiting | Per-API queue + token bucket | None | Moderate |
| Retry Logic | Exponential backoff (3 retries) | Simple try/catch | Moderate |
| Metadata Storage | IndexedDB with multi-entry indexes | SQLite with fixed columns | Moderate |
| NFO Writing | All provider IDs | IMDB + TMDB + metadata IDs | Moderate |
| Cache | 2-level (memory + localStorage) | SQLite only | Minor |

### Priority Recommendations

| Priority | Item | Target Sprint |
|----------|------|----------------|
| P0 | UniqueIdsJson storage + multi-provider episode count | Sprint 77 |
| P1 | Haglund ID converter + basic anime detection | Sprint 78 |
| P2 | API rate limiting + retry with backoff | Sprint 79 |
| P3 | Jikan integration + full 3-tier detection | Future |

### Architectural Insight

Stremio-Kai is a **browser extension overlay** (not server-side addon).
Key patterns requiring adaptation:
- `window.MetadataModules` → C# DI container
- IndexedDB → SQLite with JSON columns
- `Promise.allSettled` → `Task.WhenAll`
- LRU cache → SQLite cache table with TTL
- DOM events → C# events / `IEventConsumer`

---

## Sprint 75 — Anime Provider ID Investigation (Build Fixed)

**Status:** Complete — Build restored, implementation deferred
**Commit:** 694ff1f

Investigation into multi-provider anime ID support (UniqueIdsJson approach).
Code changes were reverted due to build errors caused by removing
`Title` property from `CatalogItem` without updating all references.

### Root Cause

Build failure was caused by incomplete refactoring:
- Removed `Title` property from `CatalogItem` model
- But `item.Title` was still referenced in 30+ places across codebase
- `ResolveImdbId` in `CatalogSyncTask.cs` returned empty for non-IMDB IDs

### Resolution

- `git reset --hard HEAD~1` restored working state
- Build now compiles successfully with 0 errors
- `UniqueIdsJson` implementation deferred to Sprint 77 (guided by Sprint 76 research)

### Note

This sprint's architecture intent (flexible `UniqueIdsJson` storage) was correct
per user feedback. Sprint 76 research confirmed this approach and provided
detailed implementation guidance.

---

## Sprint 77 — Multi-Provider ID Support (P0)

**Status:** 🔜 PLANNED

Based on Sprint 76 research findings (P0 priority items):

**Task 1:** UniqueIdsJson storage in `CatalogItem`
- Add `UniqueIdsJson` property to model
- SQLite schema migration: `ALTER TABLE catalog_items ADD COLUMN unique_ids_json TEXT`
- Store provider IDs as JSON array: `[{"provider":"kitsu","id":"48363"}]`

**Task 2:** Multi-provider episode count fallback
- Modify `GetEpisodeCountForSeason` in `EmbyEventHandler.cs`
- Fallback chain: IMDB → Kitsu → AniList → MAL
- Use provider IDs from `UniqueIdsJson` for fallback lookups

**Related:** Sprint 76 research documents in `.ai/research/`

---

## Sprint 78 — Adult Content Investigation

**Status:** Complete — Research sprint, no code changes
**Commit:** e7a82b7

Research-only sprint investigating adult content flow through AIOStreams.

### Key Findings

**AIOStreams Adult Catalogs Use AniList ID Format:**
- Catalog IDs: `bc2e3b0.anidb_popular`, `bc2e3b0.kitsu_top_airing`, etc.
- ID Prefix: `bc2e3b0.` (AniList database, NOT IMDB)
- Example ID: `bc2e3b0.37510` (not `tt` format)

**Current EmbyStreams Gaps:**
1. `ResolveImdbId` discards non-IMDB IDs → AniList IDs return empty
2. `BuildUniqueIdsJson` only stores IMDB + TMDB → No anime provider IDs
3. No adult path routing → No `SyncPathAdult` configuration
4. No genre-based adult detection → No hentai/ecchi/yaoi filtering

### Deliverables

- `.ai/research/adult-content-investigation.md` — Full investigation with recommendations

### Note

Sprint 79 (Haglund API implementation) was reverted. This sprint's findings
recommend a different approach: defer adult content support and focus on UX improvements.

---

## Sprint 79 — Haglund API Client + Retry/Backoff + Rate Limiting

**Status:** REVERTED — Commit dfc3cbb
**Original Commit:** 3496075

**Reversion Reason:** User decided against Haglund ID converter. Not needed because:
- AIOStreams returns IMDB IDs for most content
- Anime providers (AniList/Kitsu/MAL) work with Sprint 77 UniqueIdsJson
- ID conversion adds complexity without clear benefit
- Emby is source of truth — we query Emby's ProviderIds

**Original Implementation:**
- `Services/HaglundIdConverter.cs` — Haglund API client for ID conversion
- `Helpers/RetryHelper.cs` — Retry with exponential backoff
- `Services/ApiRateLimiter.cs` — Per-API rate limiting

**Replaced by:** Sprint 79 (Manifest Management UX + Adult Content Cleanup)

---

## Sprint 79 — Manifest Management UX + Adult Content Cleanup

**Status:** Complete
**Note:** Replaces reverted Sprint 79 (Haglund API) and planned Sprint 80

### Part A: Edit Manifest Button
- `ManifestUrlParser.cs` — parses AIOStreams URL, generates configure link
- `StatusResponse.ManifestConfigureUrl` for UI consumption
- UI button appears when valid manifest configured
- Content filtering info box explaining philosophy

### Part B: Adult Content Removal
- Removed `FilterAdultCatalogs` config field
- Removed adult checkbox and path input from UI
- Removed adult routing logic from `CatalogSyncTask.cs`

### Philosophy

EmbyStreams syncs ALL manifest content.
Content filtering is user's responsibility in AIOStreams.
"Edit Manifest" button provides easy access to AIOStreams config.

---

## Won't Do

### Adult Content Detection/Routing

**Removed in Sprint 79**

AIOStreams metadata doesn't reliably include adult indicators:
- `adult` flag: null
- `ageRating`: null
- `certification`: null
- Genre-based detection: unreliable

**Philosophy:** EmbyStreams syncs ALL content from the manifest.
Users control content filtering in AIOStreams via:
- Catalog selection (enable/disable catalogs)
- Content filters (nudity, certification levels)
- Genre exclusions

If users need adult content separated, they should:
1. Create separate AIOStreams manifest (adult-only)
2. Run separate EmbyStreams instance pointing to that manifest
3. Configure separate Emby library

### Haglund ID Converter

**Decided against in Sprint 76 discussion**

Not needed because:
- AIOStreams returns IMDB IDs for most content
- Anime providers (AniList/Kitsu/MAL) work with Sprint 77 UniqueIdsJson
- ID conversion adds complexity without clear benefit
- Emby is source of truth — we query Emby's ProviderIds

Sprint 79 (Haglund implementation) was reverted.

---

## Sprint 80 — Configuration UI Overhaul + Library Auto-Creation

**Status:** Complete — Fixed Health/Settings tabs, consolidated wizard library config

### Overview

Major UX overhaul to fix configuration UI issues:
1. Consolidated library configuration into wizard Step 2 (Preferences)
2. Fixed non-functional Health and Settings tabs
3. Verified catalog loading logic

### Changes Made

**Phase 2: Fix Wizard Library Configuration**
- Moved base path input from Step 1 to Step 2 (Preferences)
- Moved library name inputs (Movies, Series, Anime) to Step 2
- Removed Step 1 library configuration card
- Added `LibraryNameAnime` property to `PluginConfiguration.cs`
- Updated wizard data collection in JavaScript
- Fixed derived paths display logic

**Phase 4 & 5: Fix Health/Settings Tabs**
- Renamed `es-tab-content-sources` → `es-tab-content-health`
- Renamed `es-tab-content-advanced` → `es-tab-content-settings`
- Updated `showTab()` JavaScript function to handle 'health' and 'settings' tabs
- Updated dashboard polling and navigation references
- Updated "Go to Health Dashboard" button to "Go to Settings"

### Phase 3: Auto-Library Creation (SKIPPED)

**Finding:** Emby SDK does not expose clean programmatic library creation from plugins.

The existing JavaScript implementation uses the Emby REST API (`/Libraries/VirtualFolders`) which works correctly. Creating a server-side endpoint would require using ILibraryManager, but the SDK capabilities for this are limited (see docs/FINDINGS.md line 118).

Library creation continues to work via the JavaScript implementation.

### Files Modified

- `Configuration/configurationpage.html` — Renamed tab content panels, moved wizard inputs
- `Configuration/configurationpage.js` — Updated showTab(), loadCatalogs(), wizard handlers
- `PluginConfiguration.cs` — Added `LibraryNameAnime` property
- `.ai/CURRENT_TASK.md` — Updated with sprint progress
- `BACKLOG.md` — Added Sprint 80 section

### Testing

- Build: ✅ Success (0 warnings, 0 errors)
- Health tab: ✅ Functional (shows sources table, doctor status)
- Settings tab: ✅ Functional (shows all configuration options)
- Wizard flow: ✅ Library config consolidated to Step 2

---

## Sprint 105-108 — Superseded

**Status:** Superseded by Sprint 109

Sprints 105-108 were originally planned as an extension of the existing v20 architecture. After review, it was determined that v3.3 requires a fundamental architectural change with a full wipe migration. Sprints 105-108 have been superseded by Sprint 109.

**See:** `.ai/SPRINT_109.md` — v3.3 Foundation & Migration

---

## Sprint 109 — Foundation & Migration (v3.3 Breaking Change)

**Status:** Complete ✓ | **Risk:** HIGH | **Depends:** Sprint 104

**File:** `.ai/SPRINT_109.md`

### Overview

Implements the foundational architecture for v3.3, a breaking change that requires full database reset.

### Phases Completed

**Phase 109A — New Database Schema:** Complete ✓
- FIX-109A-01: Schema.cs with 9 tables ✓
- FIX-109A-02: home_section_tracking table ✓ (included in schema)
- FIX-109A-03: DatabaseInitializer ✓

**Phase 109B — Core Domain Models:** Complete ✓
- FIX-109B-01: MediaIdType enum ✓
- FIX-109B-02: MediaId value type ✓
- FIX-109B-03: ItemStatus enum ✓
- FIX-109B-04: FailureReason enum ✓
- FIX-109B-05: PipelineTrigger enum ✓
- FIX-109B-06: SaveReason enum ✓
- FIX-109B-07: SourceType enum ✓
- FIX-109B-08: MediaItem entity ✓
- FIX-109B-09: Source entity ✓
- FIX-109B-10: AioStreamsPrefixDefaults config ✓

**Phase 109D — Emby Library Provisioning on Install:** Complete ✓
- FIX-109D-01: Library provisioning service ✓

### Key Changes

- MediaId system replaces IMDB-only keys
- ItemStatus lifecycle machine replaces ItemState enum
- Sources model replaces Catalog model
- Saved/Blocked states replace PIN model
- Your Files detection via media_item_ids table
- 9 new database tables (including home_section_tracking)

### Files Created

- Data/Schema.cs - v3.3 schema definitions
- Data/DatabaseInitializer.cs - Schema initialization
- Models/MediaIdType.cs - Provider type enum
- Models/MediaId.cs - ID value type
- Models/ItemStatus.cs - Lifecycle state machine
- Models/FailureReason.cs - Failure enumeration
- Models/PipelineTrigger.cs - Trigger enumeration
- Models/SaveReason.cs - Save reason enumeration
- Models/SourceType.cs - Source type enumeration
- Models/MediaItem.cs - Core entity
- Models/Source.cs - Source entity
- Models/AioStreamsPrefixDefaults.cs - AIOStreams prefix mappings
- Services/LibraryProvisioning.cs - Library provisioning service

### Build Status

✓ SUCCESS (0 warnings, 0 errors)

### Findings and Guidance

**Database Schema:**
- All 9 tables created with proper indexes
- Foreign key constraints defined where appropriate
- WAL mode enabled for concurrency
- Schema version tracking implemented

**Domain Models:**
- All enums include extension methods for parsing and display
- MediaId type implements IEquatable with full equality semantics
- ItemStatus includes state transition validation
- MediaItem and Source entities include derived state properties

**Library Provisioning:**
- Service created with directory creation logic
- Placeholder for REST API library registration (requires further development)
- Placeholder for user policy updates (requires further SDK integration)
- Provisioning flag mechanism prevents re-application

**Notes for Future Sprints:**
- LibraryProvisioning requires REST API integration for actual Emby library creation
- User policy hiding mechanism needs proper SDK API exploration
- Library registration should be deferred to post-install configuration wizard

### Completion Criteria

- [x] All 9 database tables created
- [x] All core domain models implemented
- [x] Library provisioning service created
- [x] Build succeeds
- [ ] E2E: Fresh DB initialized (pending Sprint 121)
- [ ] E2E: v20 DB migrated (N/A - no migration path per spec §17)

---

## Sprint 110 — Services Layer (v3.3)

**Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 109

**File:** `.ai/SPRINT_110.md`

### Overview

Implements core services that drive the v3.3 architecture.

### Services Created

- ItemPipelineService — Item lifecycle orchestration
- StreamResolver — AIOStreams stream resolution and ranking
- MetadataHydrator — Cinemeta/AIOMetadata fetch
- YourFilesReconciler — Your Files detection and matching
- SourcesService — Source enable/disable management
- CollectionsService — Emby BoxSet management
- SavedService — Save/Unsave/Block actions

### Completion Criteria

- [ ] All services implemented
- [ ] Coalition rule respected
- [ ] Multi-provider ID matching works
- [ ] Build succeeds
- [ ] Unit tests pass

---

## Sprint 111 — Sync Pipeline (v3.3)

**Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 110

**File:** `.ai/SPRINT_111.md`

### Overview

Implements the sync pipeline: fetch → filter → diff → process → handle removed.

### Key Components

- ManifestFetcher — Fetches from AIOStreams with TTL check
- ManifestFilter — Filters blocked/duplicate/over-cap items
- ManifestDiff — Compares manifest vs database
- SyncTask — Orchestrates full pipeline

### Completion Criteria

- [ ] Manifest fetched and cached
- [ ] Entries filtered correctly
- [ ] Diff accurate
- [ ] Pipeline phases execute
- [ ] Progress reporting works
- [ ] Build succeeds

---

## Sprint 112 — Stream Resolution and Playback (v3.3)

**Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 111

**File:** `.ai/SPRINT_112.md`

### Overview

Implements stream resolution for playback with cache management and URL signing.

### Key Components

- PlaybackService — Cache-first resolution
- StreamCache — TTL-based caching
- StreamUrlSigner — HMAC-SHA256 signing
- ProgressStreamer — SSE progress events

### Completion Criteria

- [ ] Cache-first resolution works
- [ ] URL signing works
- [ ] SSE streams progress
- [ ] Build succeeds

---

## Sprint 113 — Saved/Blocked User Actions (v3.3)

**Status:** Planning | **Risk:** LOW | **Depends:** Sprint 112

**File:** `.ai/SPRINT_113.md`

### Overview

Implements user-triggered Save/Unsave/Block/Unblock actions.

### Key Components

- SavedRepository — Persist saved/blocked state
- SavedActionService — Action logic with Coalition rule
- SavedController — Admin API
- Saved UI — Config page UI

### Completion Criteria

- [ ] All actions work
- [ ] Coalition rule respected
- [ ] UI lists items correctly
- [ ] Build succeeds

---

## Sprint 114 — Your Files Detection (v3.3)

**Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 113

**File:** `.ai/SPRINT_114.md`

### Overview

Implements Your Files detection and conflict resolution.

### Key Components

- YourFilesScanner — Scans library for user files
- YourFilesMatcher — Multi-provider ID matching
- YourFilesConflictResolver — Conflict resolution
- YourFilesTask — Scheduled reconciliation

### Completion Criteria

- [ ] Your Files detected
- [ ] Multi-provider matching works
- [ ] Conflicts resolved correctly
- [ ] Build succeeds

---

## Sprint 115 — Removal Pipeline (v3.3)

**Status:** Planning | **Risk:** LOW | **Depends:** Sprint 114

**File:** `.ai/SPRINT_115.md`

### Overview

Implements removal pipeline for items no longer in manifest.

### Key Components

- RemovalService — Mark and remove items
- RemovalPipeline — Process removed items
- RemovalTask — Scheduled cleanup
- RemovalController — Admin API

### Completion Criteria

- [ ] Items marked for removal
- [ ] Coalition rule double-checked
- [ ] .strm files deleted
- [ ] Emby items removed
- [ ] Build succeeds

---

## Sprint 116 — Collection Management (v3.3)

**Status:** Planning | **Risk:** LOW | **Depends:** Sprint 115

**File:** `.ai/SPRINT_116.md`

### Overview

Implements collection management via Emby BoxSet API.

### Key Components

- BoxSetRepository — Persist BoxSet metadata
- BoxSetService — Emby BoxSet API wrapper
- CollectionSyncService — Sync sources to BoxSets
- CollectionTask — Scheduled sync

### Completion Criteria

- [ ] BoxSets created
- [ ] Items synced to BoxSets
- [ ] Orphans pruned
- [ ] Build succeeds

---

## Sprint 117 — Admin UI (v3.3)

**Status:** Planning | **Risk:** LOW | **Depends:** Sprint 116

**File:** `.ai/SPRINT_117.md`

### Overview

Implements Admin UI for v3.3 configuration.

### Key Components

- Config Page HTML — Tabbed layout
- Config Page JavaScript — UI logic
- Config Page CSS — Styling
- Config Controller — API endpoints

### Tabs

- Sources — Enable/disable sources
- Collections — View/sync collections
- Saved — Saved items
- Blocked — Blocked items
- Actions — Manual actions
- Logs — Pipeline logs

### Completion Criteria

- [ ] All tabs work
- [ ] All actions trigger correctly
- [ ] Toast notifications work
- [ ] Confirmations work for dangerous actions
- [ ] Build succeeds

---

## Sprint 118 — Home Screen Rails (v3.3)

**Status:** Planning | **Risk:** LOW | **Depends:** Sprint 117

**File:** `.ai/SPRINT_118.md`

### Overview

Implements home screen rails for easy content access.

### Rail Types

- Saved — User-saved items
- New — Recently added
- Collections — Emby BoxSets
- RecentlyResolved — Fresh streams

### Completion Criteria

- [ ] Rails display correctly
- [ ] Items link correctly
- [ ] Lazy loading works
- [ ] Build succeeds

---

## Sprint 119 — API Endpoints (v3.3)

**Status:** Planning | **Risk:** LOW | **Depends:** Sprint 118

**File:** `.ai/SPRINT_119.md`

### Overview

Implements comprehensive API endpoints.

### Controllers

- StatusController — Plugin status
- SourcesController — Source management
- CollectionsController — Collection management
- ItemsController — Item queries
- ActionsController — Manual actions
- LogsController — Log retrieval

### Completion Criteria

- [ ] All endpoints work
- [ ] JSON responses correct
- [ ] Admin guards work
- [ ] Pagination works
- [ ] Build succeeds

---

## Sprint 120 — Logging (v3.3)

**Status:** Planning | **Risk:** LOW | **Depends:** Sprint 119

**File:** `.ai/SPRINT_120.md`

### Overview

Implements comprehensive logging.

### Key Components

- PipelineLogger — Item lifecycle events
- ResolutionLogger — Stream resolution events
- LogRepository — Persist logs
- LogRetentionService — Cleanup old logs
- LogRetentionTask — Scheduled cleanup

### Retention

- Pipeline logs: 30 days
- Resolution logs: 7 days

### Completion Criteria

- [ ] All events logged
- [ ] Logs queryable
- [ ] Old logs pruned
- [ ] Build succeeds

---

## Sprint 122 — Versioned Playback (Schema, Data, Data Models, Candidate Normalizer, Slot Matcher, Playback, Rehydration, UI)

 Startup Detection)

 Build + Test) | **Status:** Planning | **Risk:** HIGH | **Depends:** Sprint 121

**Status:** Planning | **Risk:** LOW | **Depends:** Sprint 120

**File:** `.ai/SPRINT_121.md`

### Overview

Implements comprehensive E2E testing.

### Test Categories

- Migration Tests — v20 → v3
- Sync Pipeline Tests — Full flow
- Playback Tests — Resolution and signing
- User Action Tests — Save/Block
- Your Files Tests — Detection
- E2E Test Plan — Manual scenarios

### Completion Criteria

- [ ] Test infrastructure created
- [ ] All tests pass
- [ ] E2E scenarios documented
- [ ] Build succeeds

---

## v3.3 Summary

**Sprints:** 109-121 (13 sprints)
**Status:** Planning complete
**Release Target:** v3.3.0
**Breaking Change:** Full database reset required

**Key Features:**
- MediaId system with multi-provider support
- ItemStatus lifecycle machine
- Sources model with ShowAsCollection
- Saved/Blocked states
- Your Files detection
- Emby BoxSet integration
- Comprehensive logging
- Full Admin UI
- Home screen rails

**See Also:**
- `.ai/SPRINT_109.md` — Sprint 109 details
- `.ai/SPRINT_110.md` — Sprint 110 details
- `.ai/SPRINT_111.md` — Sprint 111 details
- `.ai/SPRINT_112.md` — Sprint 112 details
- `.ai/SPRINT_113.md` — Sprint 113 details
- `.ai/SPRINT_114.md` — Sprint 114 details
- `.ai/SPRINT_115.md` — Sprint 115 details
- `.ai/SPRINT_116.md` — Sprint 116 details
- `.ai/SPRINT_117.md` — Sprint 117 details
- `.ai/SPRINT_118.md` — Sprint 118 details
- `.ai/SPRINT_119.md` — Sprint 119 details
- `.ai/SPRINT_120.md` — Sprint 120 details
- `.ai/SPRINT_121.md` — Sprint 121 details


---

## Sprint 150 — Spec Drift & UX Fixes (2026-04-10)
**Status:** [x] Complete

### Completed
- [x] M-6: Added RefreshHealth/DeepCleanHealth to StatusResponse DTO; computed in Get() with 2×/3× interval thresholds
- [x] H-4: Per-user InLibrary in DiscoverService — added TryGetCurrentUserId(), GetUserPinnedImdbIdsAsync(), updated Browse/Search/Detail to overlay per-user pins
- [x] H-5: Added composite index idx_user_item_pins_user_source_pinned on DatabaseManager schema
- [x] MISSING-1: Added GetBlockedItemsAsync/UnblockItemAsync to DatabaseManager; created Services/AdminService.cs; added Blocked Items tab to configurationpage.html/js (admin-only)
- [x] MISSING-2: Created Services/UserService.cs (GetUserPinsRequest, RemovePinsRequest); added My Picks tab to configurationpage.html/js (user-facing)
- [x] MISSING-3: Added Content Mgmt admin tab to configurationpage.html; admin tab visibility toggled by ApiClient.getCurrentUser() in loadConfig()
- [x] H-1 (Sprint 151): Added CatalogItem.Blocked computed property

### Guidance
- AdminService/UserService follow same IService + IRequiresRequest pattern as StatusService
- Per-user InLibrary reads from user_item_pins joined to catalog_items — not discover_catalog.is_in_user_library
- Admin tabs hidden by default (display:none), shown via JS for IsAdministrator users

---

## Sprint 151 — God Class Refactor (Safe Items) (2026-04-10)
**Status:** [x] Complete

### Completed
- [x] H-1: CatalogItem.Blocked computed property (done under Sprint 150)
- [x] M-1: Deleted dead private STRM methods from CatalogSyncTask.cs (WriteStrmFilesAsync, WriteStrmFileForItemAsync, WriteSeriesStrmAsync, WriteEpisodesFromSeasonsJsonAsync, WriteNfoFileAsync) — ~648 lines removed; kept WriteStrmFile helper and WriteStrmFileForItemPublicAsync
- [x] M-3: AioMetadataClient._httpClient promoted from per-instance to static
- [x] M-4: EnrichStepAsync exception handling improved — OperationCanceledException rethrown, 429 breaks loop, IOException breaks loop, JsonException/other continue
- [x] L-2: NeverRetryUnixSeconds constant added to RefreshTask; replaces DateTimeOffset(2100,...) magic literal
- [x] L-4: Comments added to 42 magic numbers in RefreshTask and DeepCleanTask

### Skipped
- H-3: CatalogRepository extraction — Emby SQLite API (SQLite3.Open) incompatibility risk
- R-1: Repository decoupling from DatabaseManager — risky; deferred to Sprint 155+
