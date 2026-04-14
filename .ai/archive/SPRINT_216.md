# Sprint 216 — Anime Catalog Routing, Silent Drop Elimination & Full Metadata Research (RESEARCH SPRINT)

**Status:** Draft | **Risk:** LOW | **Depends:** none | **Target:** v0.8.x (research only — no release)

## Why (2 sentences max)
Claude’s analysis revealed two critical silent-drop bugs in anime catalogs (kitsu: IDs + type="series/movie" overriding catalog.Type="anime"), causing ~81% of items from anilist_top-anime (and similar lists) to be discarded or mis-routed. This sprint puts the entire team into pure **research & fact-finding mode** to validate every drop condition, confirm consistency with our prior IMDB→generic-item-type refactor, surface ALL other silent drops, design a debug harness with full raw-JSON storage, compare against Emby’s 4 anime plugins + NFO decorator, and deliver precise recommended changes — before any code is touched.

## Non-Goals
- No code changes or PRs in this sprint (pure research/report).
- Do not implement fixes — only document them for Sprint 217.
- Do not touch production Emby instance.

## Tasks

### RESEARCH-216-01: Validate Claude’s Anime Logic + All Drop Conditions
**Files to analyze:** CatalogSyncTask.cs (especially lines 480-545), ResolveImdbId, GenerateDeterministicId, any NFO generation paths, and every place that calls `return null` or skips an item.  
**Effort:** M  
**What:**  
- Confirm: items may **only** be dropped if (1) user explicitly blocked it, (2) it is a #DUPE# entry, or (3) user already has it in another library.  
- Every other drop must be logged (with full item JSON + reason).  
- Verify NFO decoration hierarchy: AIOMetadata (if present) → AIOStreams Primary → AIOStreams Secondary → Cinameta.  
- Confirm we always prefer Emby-native resolution but **never fail** on missing IMDB/TMDB/etc.  
- Validate deduplication rule: if an item appears in both an anime list AND a movie/series list → it **must** land in the anime folder (period).  
- Confirm consistency with previous sprints’ move away from strict IMDB requirement to generic item-type handling.  
- Document how we currently track multiple sources per item (outside of anime) and whether that structure already exists everywhere.  
- Explicitly handle ALL anime ID formats (kitsu:, anilist:, etc.) — do not assume only kitsu.

### RESEARCH-216-02: Full Audit of Every Silent Drop in the Codebase
**Files to analyze:** Entire solution — focus on CatalogSyncTask.cs, metadata resolution, library scanning, EmbyStreams DB writers, and any early-exit paths. Use search patterns: `return null`, `continue;`, `if (string.IsNullOrEmpty(` , `if (!meta.` , `ResolveImdbId`, `catalog.Type`, `meta.Type`.  
**Effort:** L  
**What:**  
- Systematically list every possible silent-drop location.  
- For each, record: condition that causes the drop, what data is lost, and whether raw JSON is preserved.  
- Pay special attention to anime catalogs (catalog.Type == "anime") and any other catalog types that might be mis-routed by item-level type.  
- Output a complete “Silent Drop Inventory” table.

### RESEARCH-216-03: Design Debug Harness + Enforce Raw JSON Storage
**Files to analyze:** All DB interaction code (EmbyStreams DB schema, source/catalog/item writers), current JSON storage paths.  
**Effort:** M  
**What:**  
- Connect to the EmbyStreams database and inspect current raw JSON storage for: sources, catalogs, and every catalog item.  
- If raw JSON is NOT stored for every attribute, design the exact schema changes + code snippets needed to store it going forward (sources → raw JSON, catalogs → raw JSON, catalog items → raw JSON).  
- Design and document a **debug harness** (console app / SQL queries / extension method) that lets us query any item by ID and dump its complete raw JSON payload from every source.  
- Include sample queries we can run immediately to validate anime items (kitsu:, anilist:, etc.).

### RESEARCH-216-04: Comparative Analysis of Emby’s 4 Anime Plugins + NFO Decorator
**Context provided:** You have been given the full pasted text (#6 + 11 lines) containing the 4 anime plugins + the dedicated NFO decoration plugin. Treat this as the authoritative reference.  
**Files to analyze:** (External only — use the pasted descriptions; do NOT assume local code).  
**Effort:** M  
**What:**  
- For each of the 5 projects:  
  - How does it expect anime to be displayed in Emby (folder structure, type mapping, ID handling)?  
  - What metadata sources does it use and how does it decorate .nfo files?  
  - What are the key differences from InfiniteDrive’s current implementation?  
- Produce a comparison table (columns: Plugin, Anime Folder Logic, ID Tolerance, NFO Strategy, Dedup Behavior, Silent-Drop Handling).  
- Highlight any best practices we should adopt.

### RESEARCH-216-05: Compile Recommended Changes & Next-Sprint Backlog
**Files to update:** BACKLOG.md, REPO_MAP.md (research findings section).  
**Effort:** S  
**What:**  
- Synthesize all research into a concise, actionable recommendation list (exact code changes needed for anime routing, ID handling, logging, raw-JSON storage, dedup, etc.).  
- Prioritize fixes by impact (e.g., “Fix #1: anime catalog type override”).  
- Update BACKLOG.md with the full research output and proposed Sprint 217 tasks.  
- Update REPO_MAP.md with any new files/harness locations discovered.

## Verification (run these or it fails)
- [ ] All 5 research tasks completed with full documentation, tables, and sample queries.  
- [ ] No actual code changes were made (confirmed by git status).  
- [ ] Debug harness design is ready to copy-paste and run.  
- [ ] Silent Drop Inventory and plugin comparison tables are complete and accurate.  
- [ ] BACKLOG.md and REPO_MAP.md have been updated with research findings.  
- [ ] `./emby-reset.sh` still succeeds (sanity check only — no functional changes).

## Completion
- [ ] All tasks done  
- [ ] BACKLOG.md updated  
- [ ] REPO_MAP.md updated  
- [ ] git commit -m "chore: end sprint 216 — research complete, ready for fixes"
