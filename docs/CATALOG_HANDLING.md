EmbyStreams
Design Specification
Version 3.3
Status: Ship Candidate
Aligned with Emby Server 10.0.8-beta SDK
 
 
⚠  Breaking change from all prior versions. Beta testers: see §17 (Migration) before doing anything.
 
Audience: UX Developer + Plugin Engineer
Prerequisites: Emby plugin development, .strm + .nfo file conventions, TMDB/AniList ID-based media identification, AIOStreams integration
 

SDK Smoke Test — Emby 10.0.8-beta
✅  Major wins — these things are now easier than prior SDK versions
 
	•	ProviderIds handling is first-class and consistent → MediaId model is correct.
	•	BoxSet / Collection membership updates are safer and deterministic — no more weird refresh cycles.
	•	ItemAdded / ItemUpdated events are reliable → index confirmation can be event-driven. Polling is fallback only.
	•	User policy updates for library hiding are cleaner (still per-user only).
	•	Your Files detection is stronger via multi-ProviderIds matching across libraries.
 
⚠  Still unchanged / requires defensive design
 
	•	No global "library hidden by default" enforcement.
	•	Anime metadata quality depends entirely on installed Emby metadata providers.
	•	Folder naming hints beyond TMDB/IMDB are not reliable.
	•	AIOStreams ID prefixes remain opaque → must stay configurable.
 
Biggest architectural change from 3.2: Moved from TMDB-centric to a true provider-agnostic identity system. This is the difference between a plugin and a platform.

1. What This Document Is
The authoritative design specification for EmbyStreams. It supersedes all prior documents. In any conflict, this document wins.
Read it end to end before writing a line of code.
 
2. The Mental Model
Three sentences:
EmbyStreams manages one library. Sources bring items into it. Collections — and native Dynamic Media sections — are how users browse them.
 
Everything else is detail on top of that.
 
3. Vocabulary
Four words. Use them consistently everywhere — in code, logs, UI copy, comments, and conversation.
 
Term
Definition
Source
A feed of externally identifiable media references. Each item must resolve to at least one valid Provider ID supported by Emby (TMDB, IMDB, TVDB, AniList, AniDB, etc.). Users never browse a Source directly.
Collection
A named, navigable shelf in Emby. What users see. Optionally created per Source.
Saved
An item the user has explicitly kept. The system never removes it.
Blocked
An item the user has permanently rejected. The system never shows it again.
 
Supporting concepts (invisible to viewers):
 
Term
Definition
Your Files
Items that exist in Emby libraries EmbyStreams does not manage. Always deferred to.
Lifecycle Status
Where in the pipeline an item currently sits.
Grace Period
A countdown before an unclaimed, unsaved item is removed.
 
Canonical Identity Rule
EmbyStreams never assumes a single global ID system. Each item is identified by a primary (id_type, id_value) pair. Additional provider IDs are stored for cross-resolution and matching. All deduplication, storage, file naming, and pipeline logic operates on this typed identity.
 
4. Physical Architecture
4.1  One Library
/embystreams/
├── library/
│   ├── movies/
│   │   └── {Title} ({Year}) [id-{type}-{value}]/
│   │       ├── {Title} ({Year}).strm
│   │       └── {Title} ({Year}).nfo
│   └── series/
│       └── {Title} ({Year}) [id-{type}-{value}]/
│           └── ...
└── db/
    └── embystreams.db
 
/embystreams/ maps to the /fastmedia NFS mount. .strm files are metadata pointers. Actual media never lives here.
Folder naming is advisory only. Emby reliably parses TMDB and IMDB hints. Non-TMDB IDs (AniList, AniDB, etc.) are not guaranteed to be recognized from folder names.
The .nfo file is the authoritative source of identity. Folder names are for human debugging only.
 
4.2  The Emby Library
EmbyStreams creates and manages exactly one Emby library on install:
 
Setting
Value
Display Name
EmbyStreams
Root Path
/embystreams/library/
Show on Home
YES
Search
YES
Type
Movies + Series
 
Items are discoverable through Collections and search. Users are not expected to browse the raw library — but if they do, that is their right.
 
4.3  The Visibility Problem — Stated Honestly
Emby's library visibility is a per-user setting, not a library-level setting. EmbyStreams cannot enforce hidden-by-default without crossing a boundary.
 
What EmbyStreams does on install: Sets the EmbyStreams library to hidden in the navigation panel for every user that exists at install time. The administrator sees this notice:
"We've added the EmbyStreams library and hidden it from all current users. Viewers will discover content through Collections and search. You can change this per user in Emby's user settings at any time."
 
What EmbyStreams does NOT do:
	•	Apply this to users created after install. That is the administrator's job.
	•	Override a user who explicitly shows the library in their nav panel.
	•	Enforce this setting permanently.
 
This is an opinionated default, not a lock.
Implementation: On install, iterate GetUsers() and call UpdateUserPolicy() setting the visibility flag for the EmbyStreams library ID. Log each user affected. Do not re-apply on subsequent plugin restarts.
 
5. Sources
5.1  What a Source Does
A Source fetches externally identifiable media references on a schedule and hands them to the sync pipeline. Each item must resolve to at least one valid Provider ID. That is its entire job.
 
5.2  Source Settings
Each Source has exactly two settings. Nothing else.
 
Setting
Options
Behavior
1. Enabled / Disabled
Toggle
Enabled: items processed through pipeline. Disabled: items exclusive to this source enter a grace period. Saved items are never touched.
2. Show as Collection
Checkbox
Checked: EmbyStreams creates an Emby Collection for this Source. Unchecked: items still indexed and searchable — no named shelf.
 
5.3  Built-in Sources
Ship with EmbyStreams. URLs maintained by the plugin. Users never see or configure the URL.
 
Source
Default
Collection
Max Items
Trending Movies
Enabled
✓ Trending Movies
200
Trending Series
Enabled
✓ Trending Series
200
New Movie Releases
Enabled
✓ New This Week
150
New & Returning Series
Enabled
✓ New & Returning
150
 
Digital Release Gate (built-in sources only): Before any item proceeds past Known status, EmbyStreams verifies via TMDB: status == Released AND release_type IN (4=Digital, 5=Physical). Theatrical-only titles are dropped here.
 
5.4  My Lists — User-Added Sources
Users may add any public Trakt or MDblist URL. Public only. No authentication. No OAuth. No API keys. If it requires a login, it does not exist to EmbyStreams.
Error on 401/403: "This list isn't publicly accessible. Make sure it's set to public in its settings."
 
5.5  AIOStreams Sources
Populated automatically from configured AIOStreams providers. Each detected streaming service (Netflix, Disney+, etc.) appears as a Source with the same two-setting interface.
 
5.6  Item Persistence — The Coalition Rule
An item stays in the library as long as any enabled Source claims it.
 
The removal decision is never based on a single Source. Before any item is touched, the system evaluates all enabled Sources simultaneously.
 
Example: Item is in "Trending Top 20" (week 1). Week 2, it drops from Top 20 but appears in "Top 100 Most Popular." From the user's perspective: nothing happened. From the system's perspective: source membership updated, no file operation, no status change.
 
Example: Item drops from all active Sources. Grace period begins (default: 7 days). If no Source reclaims it and it is not Saved, it is removed.
 
Implementation: Before evaluating any removal, execute a single query across all source_memberships joined to sources WHERE enabled = TRUE. If any row exists for that primary_id + primary_id_type + media_type, the item is claimed. One query, not N queries.
 
6. Item Lifecycle
Every item moves through a defined pipeline. Status is observable, queryable, and logged at every step.
 
6.1  Lifecycle Statuses
Known → Resolved → Hydrated → Created → Indexed
                                              ↓
                            [Saved | Active | Blocked | Superseded | Failed]
 
Status
Meaning
Known
Media ID received from a Source. Nothing written yet.
Resolved
AIOStreams returned ≥1 playable stream. All streams logged.
Hydrated
Metadata fetched. NFO data ready. Primary Media ID confirmed.
Created
.strm + .nfo written to disk.
Indexed
Emby has scanned and confirmed the item in its database.
Active
Fully live. Claimed by ≥1 Source. Not saved, not blocked.
Saved
User has explicitly kept this item. Never auto-removed.
Blocked
Permanently rejected by user. Files deleted. Never re-added.
Superseded
Exists in a non-EmbyStreams library. Files deleted. Logged.
Failed
Pipeline stalled. See failure_reason. Retried on next sync.
 
6.2  The File Rule
A .strm + .nfo are only written when both of these are true, in this order:
	•	AIOStreams returns ≥1 playable stream for this Media ID (Resolved).
	•	Metadata is successfully fetched and a primary Media ID is confirmed (Hydrated).
 
If either fails, no file is written. The item stays in the pipeline at Failed with a logged reason. Retried up to max_retries (default: 5) on subsequent sync cycles.
 
Identity Injection Rule
The .nfo file is the authoritative mechanism for injecting Provider IDs into Emby. Folder naming is not relied upon for identity resolution.
 
Movie (TMDB primary):
<movie>
  <title>Dune: Part Two</title>
  <year>2024</year>
  <uniqueid type="tmdb" default="true">693134</uniqueid>
  <uniqueid type="imdb">tt15239678</uniqueid>
</movie>
 
Anime series (AniList primary):
<series>
  <title>Frieren: Beyond Journey's End</title>
  <year>2023</year>
  <uniqueid type="anilist" default="true">154587</uniqueid>
  <uniqueid type="anidb">12345</uniqueid>
</series>
 
Implementation: Pipeline is a sequential per-item processor. KnownItemProcessor → StreamResolver → MetadataHydrator → FileWriter → EmbyIndexWatcher. Each stage returns Result<T>. Failure at any stage short-circuits and writes to item_pipeline_log. The item is parked at Failed and re-queued on next sync.
 
6.3  Failure Reasons
failure_reason
Meaning
Auto-retried?
no_streams_found
AIOStreams returned 0 playable streams across all configured connections.
Yes
metadata_fetch_failed
No usable metadata returned for this Media ID.
Yes
file_write_error
Disk write failed. Check permissions and NFS mount.
Yes
emby_index_timeout
Emby did not confirm indexing within expected window.
Yes
digital_release_gate
Not yet digitally released. Re-evaluated on next sync.
Yes
blocked
Blocked by user.
No — permanent.
 
6.4  Sync Pipeline
 1.  Fetch all enabled Sources simultaneously
 2.  Build unified Media ID set — one record per unique
       (primary_id, primary_id_type, media_type)
 3.  Filter: Blocked items → skip. Always. First.
 4.  Filter: Your Files items → skip (multi-ProviderIds matching)
 5.  Filter: Digital Release Gate (built-in sources only)
 6.  Diff against DB:
       New items      → enter pipeline at Known
       Existing items → update source_memberships,
                        recalculate Collection membership
       Dropped items  → update last_seen_at,
                        start/continue grace period if unclaimed
 7.  Process new items through pipeline:
       batch 42, delay 30s, targeted Emby scan per batch
 8.  Update Emby Collection memberships via API
 9.  Update sources.last_synced_at
Batch size of 42 is not negotiable. It is the answer.
 
6.5  Removal Pipeline
Evaluated nightly and after every sync.
 
Is item Blocked?
  YES → Delete files + Emby entry. Log. Done.

Is item Superseded (Your Files match)?
  If also Saved → Flag as superseded_conflict.
                  Surface for admin review. Stop.
  If not Saved  → Delete files + Emby entry.
                  Set superseded = true. Log. Done.

Is item Saved?
  YES → Stop. Never remove.

Is item claimed by any enabled Source?
  YES → No removal. Done.
  NO  → Is grace period active?
          NO  → Start grace period. Log. Done.
          YES → Has grace period elapsed?
                  NO  → Wait. Done.
                  YES → Delete files.
                        Wait for Emby index removal confirmation.
                        On confirmation: mark deleted, log. Done.
 
The Emby confirmation gate is not optional. Do not delete the .strm file and assume Emby caught up. Wait for Emby to confirm the item is no longer in its index before writing status = deleted to the database. A tile with no file behind it is a broken experience.
 
Implementation: Subscribe to Emby's ItemRemoved event (primary). Poll ItemsService for the known emby_item_id as fallback. Only on confirmed absence: commit DB update to deleted, write audit log entry.
 
7. Collections
7.1  What a Collection Is
An Emby Collection (BoxSet) created and maintained by EmbyStreams. It is the shelf users see and browse. It is fed by a Source.
 
Emby 10.x provides stable APIs for BoxSet membership updates without requiring library rescans. Membership changes are deterministic and immediate.
 
When a Source has "Show as Collection" checked:
	•	EmbyStreams creates the Collection via Emby API on first sync if it doesn't exist.
	•	On every sync, EmbyStreams adds new members and removes dropped members via API.
	•	Removing a member from a Collection never deletes the underlying .strm file.
	•	If the Source is disabled, the Collection is emptied but not deleted.
 
7.2  Home Screen Rails
EmbyStreams will prefer native Dynamic Media home screen sections (Emby 4.10+). Collections remain for backward compatibility.
Up to 6 home screen rails. Fixed order:
 
1. Continue Watching    — Emby native. Always first if non-empty.
2. Saved                — User's Saved items. Always second if non-empty.
3. Trending Movies      — Collection, if enabled.
4. Trending Series      — Collection, if enabled.
5. New This Week        — Collection, if enabled.
6. [Admin-configured]   — One admin-chosen Source Collection.
 
Future: When Emby 4.10 Dynamic Media sections are stable, rails 3–6 become native dynamic sections. The SDK inspection for this is a planned task — do not block on it now.
 
8. Saved and Blocked
8.1  Saved
The user explicitly keeps an item. It will never be auto-removed by any system process, for any reason.
 
Triggers:
	•	User taps "Save" on any item.
	•	User watches any episode of a series — any duration. The full season is Saved (see §8.3).
 
Removal: Only by explicit user action. Two confirmations required before an item goes from Saved to gone.
 
8.2  Blocked
The user permanently rejects an item. It disappears from all surfaces and never returns via any Source.
 
What happens immediately on Block:
	•	.strm + .nfo files deleted.
	•	Emby item removed via API.
	•	All Source memberships noted in DB but permanently filtered going forward.
	•	blocked = true, blocked_at = now() written.
 
Sync filter: The very first operation in every sync pipeline run is: filter Blocked items. Before any Media ID is evaluated, before any file is considered. Blocked is the wall.
 
Unblock: Admin-only action via the Library tab. On unblock, the item re-enters the pipeline at Known on the next sync if any Source still includes it.
 
Block confirmation copy:
"Hide this forever? This title won't appear anywhere in EmbyStreams. You can unblock it in Settings." [ Block ] [ Cancel ]
 
8.3  Series: The Season Rule
When a user watches any episode of a series — any duration — the entire season is Saved. Not the episode. Not the series. The season.
 
Why the season is the correct unit:
	•	Saving the full series on one watched episode is too aggressive.
	•	Saving only the episode leaves the season prunable mid-binge.
	•	The season is the correct unit of viewing intent.
 
Implementation: On any watch event where media_type = 'series': identify season_number from the Emby watch event payload. Set saved = TRUE, saved_season = season_number, saved_by = 'system:watch', save_reason = 'watch_episode'. For movies: Save on any watch progress > 0.
 
9. User-Facing UI
9.1  Item Badges
There is no badge for pipeline status. Viewers see a clean library. Lifecycle states are admin-only.
 
State
Badge
Where
Active (unsaved)
(none)
—
Saved
"Saved" — filled, prominent
Top-left of card
Blocked
(hidden from all surfaces)
—
 
9.2  Detail Page Actions
Standard item:
[ ▶ Play ]   [ Save ]   [ Block ]
 
Saved item:
[ ▶ Play ]   [ ✓ Saved ]   [ Remove ]   [ Block ]
 
Block is always available but visually de-emphasized — small, secondary, below the fold. It is not a casual tap.
 
9.3  Remove and Block Confirmations
Remove from Saved (step 1 of 2):
Stop saving this?
If it's no longer in any active list, it may be removed automatically.
[ Stop Saving ]   [ Cancel ]
 
This moves the item from Saved to Active. The system now manages it.
 
Remove from Active (step 2 of 2 — only reachable after step 1):
Remove from library?
It will disappear if it's not in any active list.
[ Remove ]   [ Cancel ]
 
Two deliberate taps from Saved to gone.
 
Block (one step, from any state):
Hide this forever?
This title won't appear anywhere in EmbyStreams. You can unblock it in Settings.
[ Block ]   [ Cancel ]
 
10. Playback
10.1  Resolution
User presses Play
→ Emby reads .strm file
→ GET /resolve?id={value}&idType={type}&mediaType={movie|series}

1. Query AIOStreams (all configured connections, HA/failover hierarchy)
   using configured prefix mapping for id + idType
2. Found → return best stream URL immediately
           Log all returned streams to stream_resolution_log
           If series episode, any duration → Save full season (async)
3. Not found → 503
               "Don't Panic. We couldn't find a stream for this title right now."
               Log miss. Flag item for re-resolve on next sync.
 
The .strm file does not encode identity. Identity is resolved via metadata and stored ProviderIds at playback time.
 
Stream URLs are never cached for playback. Resolution is live on every play. All streams returned by AIOStreams — not just the one used — are logged to stream_resolution_log for diagnostics.
 
11. Your Files
11.1  The Rule
If a title exists in any Emby library that EmbyStreams does not manage, EmbyStreams removes its own copy and never touches that title again. The user's own library always wins.
 
11.2  Detection
Matching is performed using all known provider IDs, not title or year matching. ProviderIds matching across libraries is first-class and reliable in the 10.x SDK.
 
Runs:
	•	At plugin install (full scan of all non-EmbyStreams Emby libraries).
	•	Weekly, on schedule.
	•	On-demand: admin taps "Scan My Files Now."
 
Implementation: For each item in non-EmbyStreams libraries, read all ProviderIds. Match against media_item_ids table. Any hit on any ID type = Your Files match. This catches the case where Sonarr has a TVDB ID, an anime has an AniList ID, and they are the same show.
 
11.3  Conflict: Your Files + Saved
If a Your Files match is found for a Saved item:
	•	Do not silently delete.
	•	Set superseded_conflict = true.
	•	Surface in the admin Library tab under "Needs Review."
	•	Admin chooses: Confirm (delete EmbyStreams copy) or Keep Both (mark reviewed, EmbyStreams copy stays).
 
This is the one scenario where automatic behavior would be wrong. The user saved it deliberately. We ask.
 
12. Item Inspector — The Admin's Diagnostic Tool
Every item has a fully observable record. The answer to "why isn't this showing up?" lives here.
Access: Admin → Library tab → any item row → "Inspect"
 
┌─────────────────────────────────────────────────────────────────┐
│  Severance (2022)                          [tmdb:95396]         │
│  Series                                                         │
├─────────────────────────────────────────────────────────────────┤
│  STATUS        Indexed · Active                                 │
│  SAVED         No                                               │
│  BLOCKED       No                                               │
├─────────────────────────────────────────────────────────────────┤
│  IDENTITY                                                       │
│  Primary:    tmdb:95396                                         │
│  Also known: imdb:tt14681924  ·  tvdb:371980                    │
├─────────────────────────────────────────────────────────────────┤
│  SOURCES  (2 active)                                            │
│  · Trending Series           last seen: today                   │
│  · Apple TV+ (AIOStreams)    last seen: today                   │
├─────────────────────────────────────────────────────────────────┤
│  COLLECTIONS  (1)                                               │
│  · Trending Series                                              │
├─────────────────────────────────────────────────────────────────┤
│  STREAMS  (logged at last resolve: 2h ago)                      │
│  · AIOStreams/provider-1   HLS   1080p   ✓                      │
│  · AIOStreams/provider-2   MP4    720p   ✓                      │
│  · AIOStreams/provider-3   HLS      4K   ✓                      │
├─────────────────────────────────────────────────────────────────┤
│  FILES                                                          │
│  /embystreams/library/series/Severance (2022) [id-tmdb-95396]/  │
│  ├── Severance (2022).strm        ✓ exists                      │
│  └── Severance (2022).nfo         ✓ exists                      │
├─────────────────────────────────────────────────────────────────┤
│  EMBY                                                           │
│  Item ID:     a1b2c3d4                                          │
│  Indexed:     3 days ago                                        │
│  ProviderIds: Tmdb=95396, Imdb=tt14681924, Tvdb=371980          │
├─────────────────────────────────────────────────────────────────┤
│  PIPELINE HISTORY                                               │
│  Known      3 days ago   via Apple TV+ source sync              │
│  Resolved   3 days ago   3 streams found                        │
│  Hydrated   3 days ago   metadata OK                            │
│  Created    3 days ago   files written                          │
│  Indexed    3 days ago   Emby confirmed (ItemAdded event)        │
├─────────────────────────────────────────────────────────────────┤
│  WATCH                                                          │
│  Last played: yesterday  ·  Progress: 34%  ·  Favorited: No     │
├─────────────────────────────────────────────────────────────────┤
│  ACTIONS                                                        │
│  [ Save ]  [ Block ]  [ Force Re-resolve ]  [ Remove ]          │
│  [ Mark as My File ]  [ View Full Audit Log ]                   │
└─────────────────────────────────────────────────────────────────┘
 
12.1  Failed Items List
Failed Items   (12 items)

Title                    ID              Failure Reason         Last Retry  Actions
───────────────────────────────────────────────────────────────────────────────────
The Brutalist (2024)     tmdb:123456     no_streams_found       2h ago      [Retry][Block][Dismiss]
Some Movie (2023)        tmdb:789012     metadata_fetch_failed  1d ago      [Retry][Block][Dismiss]
Frieren (2023)           anilist:154587  no_streams_found       3h ago      [Retry][Block][Dismiss]
 
Retry — re-enter the pipeline from Known. Dismiss — remove from the failed list without blocking. Block — permanent rejection.
 
13. Admin UI
13.1  Entry Point
Emby Dashboard → Plugins → EmbyStreams → Settings
Four tabs: Sources  ·  Library  ·  System  ·  About
 
13.2  Sources Tab
Primary configuration surface. Most admins only ever visit this tab.
 
Built-in Sources

Trending Movies          [✓ Enabled]   [✓ Collection: Trending Movies    ]
Trending Series          [✓ Enabled]   [✓ Collection: Trending Series    ]
New Movie Releases       [✓ Enabled]   [✓ Collection: New This Week      ]
New & Returning Series   [✓ Enabled]   [ ] Collection                    ]


Your Streaming Services   (detected from AIOStreams)

Netflix                  [✓ Enabled]   [✓ Collection: Netflix            ]
Disney+                  [✓ Enabled]   [ ] Collection                    ]
Prime Video              [ ] Enabled   —


My Lists

trakt.tv/users/you/lists/favorites  [✓ Enabled]  [✓ Collection: My Favorites]  [×]
mdblist.com/lists/imdb250           [✓ Enabled]  [ ] Collection               [×]

[ + Add a List ]
 
Toggle behavior: Enabled is a toggle. Collection is a checkbox. Collection name is inline-editable when checked. All changes apply immediately. Inline "Saved" confirmation appears. No save button. No page refresh.
 
Expandable per-source detail:
▾ Trending Movies
  Max items:    [ 200 ▾ ]
  Sync every:   [ 6 hours ▾ ]
  Last synced:  2 hours ago
  Active items: 187
  Failed items: 3   [View →]
 
Add a List modal:
┌────────────────────────────────────────────────┐
│  Add a List                                    │
│                                                │
│  Paste a public Trakt or MDblist URL:          │
│  [__________________________________________]  │
│                                                │
│  Max items: [ 100 ▾ ]                          │
│                                                │
│  [ Add ]   [ Cancel ]                          │
│                                                │
│  ⓘ The list must be publicly accessible.       │
└────────────────────────────────────────────────┘
 
13.3  Library Tab
Library

  Active         796 items    [ Browse ]
  Saved           47 items    [ Browse ]   [ Bulk Remove ]
  Blocked         12 items    [ View ]     [ Bulk Unblock ]
  Failed           8 items    [ View ]     [ Retry All ]
  Needs Review     3 items    [ Review ]   ← Your Files conflicts

Actions
  [ Export Report ]   [ Run Removal Check Now ]   [ Force Full Re-resolve ]
 
Every item row has an Inspect button that opens the Item Inspector (§12).
 
13.4  System Tab
Improbability Drive
  Status:         Running
  Library items:  843
  Last sync:      14 minutes ago
  [ Sync Now ]

Your Files
  Physical items tracked:  2,341
  Last scanned:            6 days ago
  [ Scan My Files Now ]

Storage
  /embystreams/library/   1.2 MB   (843 items)

Danger Zone
  [ Reset EmbyStreams ]
  Removes all EmbyStreams files, database records, and the
  EmbyStreams Emby library. Your personal media is untouched.
  Type RESET to confirm.
 
13.5  About Tab
EmbyStreams v3.3

Improbability Drive is running.

Sources:      6 active
Collections:  5 managed
Items:        843 indexed  ·  47 saved  ·  8 failed

[ View Changelog ]   [ File a Bug ]
 
14. Database Schema
SQLite is used throughout. Timestamps are stored as ISO 8601 TEXT. Booleans as INTEGER (0/1). UUIDs as TEXT.
 
-- Core item record
CREATE TABLE media_items (
  id                    TEXT  PRIMARY KEY
                              DEFAULT (lower(hex(randomblob(16)))),
  primary_id            TEXT  NOT NULL,
  primary_id_type       TEXT  NOT NULL
                              CHECK (primary_id_type IN
                              ('tmdb','imdb','tvdb','anilist','anidb','kitsu')),
  media_type            TEXT  NOT NULL
                              CHECK (media_type IN ('movie', 'series')),

  -- Lifecycle
  status                TEXT  NOT NULL DEFAULT 'known'
                              CHECK (status IN (
                                'known','resolved','hydrated',
                                'created','indexed','active',
                                'saved','blocked','superseded',
                                'failed','deleted'
                              )),
  failure_reason        TEXT,
  retry_count           SMALLINT NOT NULL DEFAULT 0,
  last_retry_at         TEXT,

  -- Saved
  saved                 INTEGER NOT NULL DEFAULT 0,
  saved_at              TEXT,
  saved_by              TEXT,   -- 'user' | 'admin' | 'system:watch'
  save_reason           TEXT,   -- 'explicit' | 'watch_episode' | 'admin_override'
  saved_season          INTEGER,

  -- Blocked
  blocked               INTEGER NOT NULL DEFAULT 0,
  blocked_at            TEXT,

  -- Your Files
  superseded            INTEGER NOT NULL DEFAULT 0,
  superseded_at         TEXT,
  superseded_conflict   INTEGER NOT NULL DEFAULT 0,

  -- Grace period
  grace_started_at      TEXT,

  -- Physical files
  strm_path             TEXT,
  nfo_path              TEXT,

  -- Emby
  emby_item_id          TEXT,
  emby_indexed_at       TEXT,

  -- Watch state
  last_watched_at       TEXT,
  watch_progress_pct    INTEGER NOT NULL DEFAULT 0,
  favorited             INTEGER NOT NULL DEFAULT 0,

  -- Audit
  created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),

  UNIQUE (primary_id, primary_id_type, media_type)
);

-- Secondary IDs for cross-provider matching
CREATE TABLE media_item_ids (
  id       TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
  item_id  TEXT NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
  id_type  TEXT NOT NULL,
  id_value TEXT NOT NULL,
  UNIQUE (item_id, id_type)
);

-- Sources
CREATE TABLE sources (
  id                  TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
  name                TEXT NOT NULL,
  source_type         TEXT NOT NULL CHECK (source_type IN ('builtin','aio','trakt','mdblist')),
  url                 TEXT,
  enabled             INTEGER NOT NULL DEFAULT 1,
  show_as_collection  INTEGER NOT NULL DEFAULT 0,
  collection_name     TEXT,
  max_items           INTEGER NOT NULL DEFAULT 100,
  sync_interval_hours INTEGER NOT NULL DEFAULT 6,
  last_synced_at      TEXT,
  created_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Source memberships
CREATE TABLE source_memberships (
  id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
  primary_id      TEXT NOT NULL,
  primary_id_type TEXT NOT NULL,
  media_type      TEXT NOT NULL CHECK (media_type IN ('movie','series')),
  source_id       TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
  first_seen_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  last_seen_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  UNIQUE (primary_id, primary_id_type, media_type, source_id)
);

-- Emby Collections managed by EmbyStreams
CREATE TABLE collections (
  id                 TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
  name               TEXT NOT NULL,
  emby_collection_id TEXT,
  source_id          TEXT REFERENCES sources(id) ON DELETE SET NULL,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Stream resolution log (all streams per resolve, diagnostic only)
CREATE TABLE stream_resolution_log (
  id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
  primary_id      TEXT    NOT NULL,
  primary_id_type TEXT    NOT NULL,
  media_type      TEXT    NOT NULL,
  resolved_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  aio_provider    TEXT    NOT NULL,
  stream_url      TEXT,
  quality         TEXT,
  format          TEXT,
  playable        INTEGER NOT NULL DEFAULT 0,
  selected        INTEGER NOT NULL DEFAULT 0
);

-- Per-item pipeline event log (full audit trail)
CREATE TABLE item_pipeline_log (
  id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
  primary_id      TEXT NOT NULL,
  primary_id_type TEXT NOT NULL,
  media_type      TEXT NOT NULL,
  from_status     TEXT,
  to_status       TEXT,
  trigger         TEXT,   -- 'sync'|'play'|'watch_episode'|'user_save'
                          -- |'user_block'|'user_remove'|'grace_expiry'
                          -- |'your_files'|'admin'|'retry'
  actor           TEXT,   -- 'system'|'user'|'admin'
  note            TEXT,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Schema version tracking (required from day one)
CREATE TABLE schema_version (
  version    INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  description TEXT
);
INSERT INTO schema_version (version, description) VALUES (1, 'EmbyStreams v3.3 initial schema');

-- Indexes
CREATE INDEX idx_status          ON media_items (status);
CREATE INDEX idx_saved           ON media_items (saved) WHERE saved = 1;
CREATE INDEX idx_blocked         ON media_items (blocked) WHERE blocked = 1;
CREATE INDEX idx_superseded      ON media_items (superseded_conflict)
                                  WHERE superseded_conflict = 1;
CREATE INDEX idx_primary_id      ON media_items (primary_id, primary_id_type, media_type);
CREATE INDEX idx_grace           ON media_items (grace_started_at)
                                  WHERE grace_started_at IS NOT NULL
                                  AND saved = 0 AND blocked = 0;
CREATE INDEX idx_failed          ON media_items (status, retry_count)
                                  WHERE status = 'failed';
CREATE INDEX idx_media_ids       ON media_item_ids (id_type, id_value);
CREATE INDEX idx_source_enabled  ON sources (enabled) WHERE enabled = 1;
CREATE INDEX idx_membership      ON source_memberships
                                  (primary_id, primary_id_type, media_type);
CREATE INDEX idx_pipeline_id     ON item_pipeline_log
                                  (primary_id, primary_id_type, media_type);
CREATE INDEX idx_pipeline_time   ON item_pipeline_log (created_at DESC);
CREATE INDEX idx_stream_id       ON stream_resolution_log
                                  (primary_id, primary_id_type, media_type);
 
15. C# Model
There is no Ghost, Resident, Citizen, or Sovereign enum. Those concepts do not exist. If you find them anywhere in the codebase, delete them.
 
// ── Identity ───────────────────────────────────────────────

public enum MediaIdType
{ Tmdb, Imdb, Tvdb, AniList, AniDB, Kitsu }

/// <summary>
/// Typed external identifier — the universal key throughout EmbyStreams.
/// </summary>
public record MediaId(MediaIdType Type, string Value)
{
    /// AIOStreams prefix — MUST be configurable, not assumed.
    public string ToAioStreamsPrefix(
        IReadOnlyDictionary<MediaIdType, string> prefixMap)
    {
        if (!prefixMap.TryGetValue(Type, out var prefix))
            throw new InvalidOperationException(
                $"No AIOStreams prefix configured for {Type}");
        return $"{prefix}:{Value}";
    }

    /// Exact SDK ProviderIds key (case-sensitive).
    public string ToEmbyProviderKey() => Type switch
    {
        MediaIdType.Tmdb    => "Tmdb",
        MediaIdType.Imdb    => "Imdb",
        MediaIdType.Tvdb    => "Tvdb",
        MediaIdType.AniList => "AniList",
        MediaIdType.AniDB   => "AniDB",
        MediaIdType.Kitsu   => "Kitsu",
        _ => throw new ArgumentOutOfRangeException(nameof(Type))
    };

    /// For folder naming: [id-tmdb-693134], [id-anilist-154587].
    /// Advisory only — .nfo is authoritative.
    public string ToFolderHint() =>
        $"[id-{Type.ToString().ToLowerInvariant()}-{Value}]";

    public override string ToString() =>
        $"{Type.ToString().ToLowerInvariant()}:{Value}";
}

// ── Lifecycle ────────────────────────────────────────────────

public enum ItemStatus
{
    Known, Resolved, Hydrated, Created, Indexed,
    Active, Saved, Blocked, Superseded, Failed, Deleted
}

public enum FailureReason
{
    None, NoStreamsFound, MetadataFetchFailed,
    FileWriteError, EmbyIndexTimeout,
    DigitalReleaseGate, Blocked
}

public enum SaveReason
{ Explicit, WatchedEpisode, AdminOverride }

public enum PipelineTrigger
{
    Sync, Play, WatchEpisode, UserSave, UserBlock,
    UserRemove, GraceExpiry, YourFiles, Admin, Retry
}

// ── Source ───────────────────────────────────────────────────

public enum SourceType { BuiltIn, Aio, Trakt, MdbList }

public class Source
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public SourceType SourceType { get; init; }
    public string? Url { get; set; }
    public bool Enabled { get; set; } = true;
    public bool ShowAsCollection { get; set; } = false;
    public string? CollectionName { get; set; }
    public int MaxItems { get; set; } = 100;
    public int SyncIntervalHours { get; set; } = 6;
    public DateTimeOffset? LastSyncedAt { get; set; }
}

// ── MediaItem ────────────────────────────────────────────────

public class MediaItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public MediaId PrimaryId { get; init; } = null!;
    public string MediaType { get; init; } = string.Empty;
    public ItemStatus Status { get; set; } = ItemStatus.Known;
    public FailureReason FailureReason { get; set; } = FailureReason.None;
    public int RetryCount { get; set; } = 0;
    public bool Saved { get; set; } = false;
    public DateTimeOffset? SavedAt { get; set; }
    public string? SavedBy { get; set; }
    public SaveReason? SaveReason { get; set; }
    public int? SavedSeason { get; set; }
    public bool Blocked { get; set; } = false;
    public DateTimeOffset? BlockedAt { get; set; }
    public bool Superseded { get; set; } = false;
    public bool SupersededConflict { get; set; } = false;
    public DateTimeOffset? GraceStartedAt { get; set; }
    public string? StrmPath { get; set; }
    public string? NfoPath { get; set; }
    public string? EmbyItemId { get; set; }
    public DateTimeOffset? EmbyIndexedAt { get; set; }
    public int WatchProgressPct { get; set; } = 0;
    public bool Favorited { get; set; } = false;
}

// ── AIOStreams prefix configuration ──────────────────────────

public static class AioStreamsPrefixDefaults
{
    public static readonly IReadOnlyDictionary<MediaIdType, string>
        DefaultPrefixMap = new Dictionary<MediaIdType, string>
    {
        { MediaIdType.Tmdb,    "tmdb"    },
        { MediaIdType.Imdb,    "imdb"    },
        { MediaIdType.Tvdb,    "tvdb"    },
        { MediaIdType.AniList, "anilist" },
        { MediaIdType.AniDB,   "anidb"   },
        { MediaIdType.Kitsu,   "kitsu"   },
    };
}
 
16. Build Order
Build in this exact sequence. Each step is a dependency for the next. Do not skip ahead.
 
1.  DB schema + migrations + schema_version table
All tables, indexes, constraints from §14.
schema_version table tracks every migration from day one.
Write migration 001 as the initial schema.
Use SQLite-compatible types throughout.
 
2.  Core domain models + ItemPipelineService
MediaId, MediaItem, Source, SourceMembership, ManagedCollection.
ItemPipelineService is the SINGLE authority on status transitions.
No other service writes status directly — all callers go through this.
Multi-ID collision detection: if multiple IDs resolve to the same Emby item, merge into one MediaItem.
Write unit tests for every valid and invalid transition BEFORE writing any other service.
 
3.  Your Files reconciliation job
Scan all non-EmbyStreams Emby libraries. Read ALL ProviderIds per item (not just TMDB).
Match against media_item_ids table — any hit on any ID type = match.
This must exist before any sync runs.
 
4.  Source model + persistence
CRUD for Sources. Enabled toggle. ShowAsCollection checkbox.
Collection name inline edit. Sync interval. Max items.
No sync logic yet — just the model and admin UI persistence.
 
5.  AIOStreams prefix configuration
Load AioStreamsPrefixMap from plugin configuration.
Default values from AioStreamsPrefixDefaults.
Admin can override per installation.
This must be configurable BEFORE the resolver is built.
 
6.  Sync pipeline — fetch and diff
Fetch all enabled Sources simultaneously.
Normalize to typed MediaId.
Apply filters in order: Blocked → Your Files → Digital Release Gate.
Diff against DB: new items → Known, existing → update memberships, dropped → grace period.
No file operations yet. Verify coalition rule.
 
7.  Stream resolver — AIOStreams integration
StreamResolver service: given MediaId + media_type, construct AIOStreams URL using configured prefix map.
Log ALL returned streams to stream_resolution_log (selected = false).
Mark the chosen stream with selected = true.
Test: zero streams → FailureReason.NoStreamsFound.
Test: AIOStreams unreachable → circuit breaker, not crash.
 
8.  Metadata hydrator
MetadataHydrator: given MediaId + media_type, fetch metadata.
Build .nfo with <uniqueid> tags for every known ID. Primary ID gets default="true".
Test: metadata unavailable → FailureReason.MetadataFetchFailed.
 
9.  File writer
Create {Title} ({Year}) [id-{type}-{value}]/ directory.
Write .strm (resolver URL) + .nfo (all known ProviderIds as <uniqueid> tags).
Atomic: write to temp path, rename on success.
Test: disk full → FailureReason.FileWriteError, no partial files.
 
10.  Emby index watcher
PRIMARY: Subscribe to _libraryManager.ItemAdded event.
FALLBACK: If no event received within timeout, poll ItemsService.
On timeout: status → Failed, FailureReason.EmbyIndexTimeout.
Trigger targeted scan per batch — 42 items, 30s between batches.
 
11.  Full pipeline integration
Wire steps 7–10 into the sync pipeline from step 6.
Known → Resolved → Hydrated → Created → Indexed → Active.
Verify: anime item with AniList primary → correct .nfo, correct folder hint, correct AIOStreams prefix.
 
12.  Removal pipeline
Nightly scheduler (3:00 AM). Evaluate grace periods. Confirm coalition rule.
Delete files only when: unclaimed + unsaved + grace elapsed.
Wait for Emby index removal confirmation before writing status = Deleted.
Test: Saved item is never evaluated.
Test: service-layer rejection of Saved/Blocked items — not just a WHERE filter.
 
13.  Save and Block user actions
SaveItem(MediaId, mediaType, savedBy, saveReason).
BlockItem(MediaId, mediaType).
UnblockItem(MediaId, mediaType) — admin only.
Test: blocking Active item → files deleted, Emby item removed.
Test: blocking Saved item → same. Block overrides Save.
Test: unblocking → item re-enters at Known on next sync.
 
14.  Series season save on watch
Subscribe to Emby playback events.
On any series episode watch (any duration): identify season_number, SaveItem with saved_season.
On any movie watch (progress > 0): SaveItem.
Async. Must not block playback.
 
15.  Collection management
Create Emby BoxSet via API if not exists for ShowAsCollection sources.
On every sync: add new members, remove dropped members.
Removing a member NEVER deletes the .strm file.
If Source disabled: empty the Collection, do not delete it.
 
16.  Your Files conflict resolution
If superseded_conflict = true: surface in Library tab.
Admin: Confirm → delete EmbyStreams copy.
Admin: Keep Both → mark reviewed, copy stays.
Log both outcomes to item_pipeline_log.
 
17.  Admin UI — Sources tab
Enabled toggle + ShowAsCollection checkbox per Source.
Inline collection name editor. Expandable per-source stats.
All changes immediate. Inline "Saved" confirmation.
 
18.  Admin UI — Library tab
Counts: Active / Saved / Blocked / Failed / Needs Review.
Item Inspector for every row (§12).
Failed Items list with Retry / Block / Dismiss per item.
Bulk actions. Run Removal Check Now.
 
19.  Admin UI — System tab
Improbability Drive status card. Your Files scan card.
Storage path + size. AIOStreams prefix configuration display.
Reset EmbyStreams (type RESET to confirm).
 
20.  Emby library visibility on install
Set EmbyStreams library hidden for all current users on install.
Log each user affected. Display install notice to administrator.
Do not re-apply on plugin restart.
Do not override users who later show the library manually.
 
21.  End-to-end integration pass
Complete human walkthrough: Source enabled → Known → Resolved → Hydrated → Created → Indexed → Active → user plays → watch event → season Saved → Source drops item → grace period → item stays (Saved) → user removes Save → grace active → grace expires → files deleted → Emby confirms → status = Deleted → logged.
Separately — anime path: AIOStreams Source with AniList item → correct prefix, correct NFO, correct folder hint.
Separately — block path: Active → Block → gone → sync → still filtered → admin Unblocks → returns.
Every transition verified in item_pipeline_log. Every file operation verified on disk.
 
17. Migration from Prior Versions — Beta Tester Instructions
⚠  Breaking schema change. There is no migration path. A full wipe is required.
 
Before wiping, save these breadcrumbs:
	•	Note which Sources you had enabled (Sources tab screenshot).
	•	Note which items you had Saved (Library tab → Saved → Export Report).
	•	Note any custom list URLs you added (Sources tab → My Lists).
 
Wipe procedure:
1. Emby Dashboard → Plugins → EmbyStreams → Settings → System
2. Type RESET in the Danger Zone field and confirm.
   (Removes all EmbyStreams files, database, and the EmbyStreams
    Emby library. Your personal media is untouched.)
3. Restart the Emby Server service.
4. EmbyStreams re-initializes on restart:
   new schema, new library, new sync from scratch.
5. Re-add your custom list URLs.
6. Re-save any items you want preserved — they will
   re-enter the pipeline and be Saved once indexed.
 
18. Personality
Hitchhiker's Guide to the Galaxy references in exactly three places. Nowhere else.
 
Location
Copy
System tab section heading
Improbability Drive
Stream not found error
"Don't Panic. We couldn't find a stream for this title right now."
Empty library state
"The universe is still warming up. Check back soon."
 
These are Easter eggs. The person watching a movie on a Tuesday night should never see them. The admin who stayed up too late debugging a sync issue might, and that's exactly the right moment for a little warmth.
 
19. Non-Goals for v1
Do not build any of the following.
 
	•	Per-user personalized recommendations
	•	"Because you watched…" source feeds
	•	Trakt OAuth or user account integration
	•	MDblist API key management
	•	Genre or mood sub-libraries
	•	Per-item stream quality preferences
	•	Mobile push notifications
	•	Custom user-managed Collections (Sources create Collections; users don't build their own in v1)
	•	AI/ML ranking of any kind
	•	Emby 4.10 Dynamic Media sections (planned — do not block on it)
	•	Family/shared profile separation
	•	Hardcoded AIOStreams prefix strings (must be configurable per §15)
 
20. Hidden Risks Addressed in 3.3
Risk
Mitigation
Multi-ID collisions
media_item_ids table + merge logic in ItemPipelineService. If two different Sources surface the same show via different ID types, they must resolve to a single media_items row. Build step 2 tests this explicitly.
AIOStreams prefix ambiguity
Prefix mapping is configurable at runtime via AioStreamsPrefixDefaults + admin override. Never hardcoded in the resolver. If AIOStreams changes anilist: to al:, the admin updates one config value.
Metadata fallback gaps
When the primary ID is AniList/AniDB and the user has no anime metadata plugin, EmbyStreams generates minimal fallback metadata from TMDB (if available) or the source payload. The .nfo is always written — even if sparse.
Folder naming ignored by Emby
The .nfo <uniqueid> tag is the authoritative identity injection. Folder hints like [id-anilist-154587] are advisory — useful for human debugging, ignored by Emby for non-TMDB/IMDB types.
Your Files false negatives
Cross-provider matching via media_item_ids catches the case where Sonarr has a TVDB ID and the AIOStreams source has a TMDB ID for the same show. Any overlap on any ID type = match.
Event-driven index failure
ItemAdded / ItemRemoved events are primary. Polling is the fallback. If neither confirms, the item is marked Failed with emby_index_timeout — never silently assumed indexed.
SQLite concurrency
Use WAL mode (PRAGMA journal_mode=WAL) and a single writer connection with queued writes. Never open concurrent write transactions.
 
21. The Complete Flow, Narrated
This is what happens from install to watching a show. Read it before you build anything. Refer back when something feels wrong.
 
Install
Admin installs EmbyStreams. The plugin creates /embystreams/library/. One Emby library is provisioned — EmbyStreams. The library is set to hidden for all existing users. The admin sees the visibility notice. Your Files scan runs: 2,341 physical items indexed across all non-EmbyStreams libraries, matched by full ProviderIds — TMDB, IMDB, TVDB, AniList, all of them.
 
First Sync
All enabled Sources are fetched simultaneously. Trakt trending, Netflix via AIOStreams, two custom MDblist URLs. The unified set is built — 600 unique Media IDs after deduplication.
Filters run in order. 12 blocked items skipped. 83 Your Files matches skipped. 14 theatrical-only titles gated by the Digital Release Gate. 491 items enter the pipeline at Known.
For each item: AIOStreams is queried using the configured prefix map. tmdb:693134 for Dune. anilist:154587 for Frieren. All returned streams are logged. NFOs are written with every known ProviderId as a <uniqueid> tag. .strm files point to the resolver endpoint. Files are written in batches of 42, 30 seconds apart. Emby's ItemAdded event confirms each batch.
Sources with "Show as Collection" checked get Emby BoxSets created. Members added via API. Home screen shows Trending Movies, Trending Series, New This Week, Netflix.
 
A User Searches
A user searches for Frieren. Emby searches the EmbyStreams library. Frieren appears. No "Available" badge — it's just there. The user taps it.
 
Play
The user presses Play. The .strm file hits the resolver: GET /resolve?id=154587&idType=anilist&mediaType=series. The resolver constructs anilist:154587, queries AIOStreams, gets 2 streams. Best one returned. User is watching.
 
Watch
The user watches Episode 3 of Season 1. Any amount. The Emby playback event fires. EmbyStreams identifies: series, season 1. The entire season is Saved. saved = true, saved_by = 'system:watch', saved_season = 1. The removal pipeline will never evaluate this item again.
 
A Source Drops the Item
Next week, Frieren drops off the Trending Series feed. But Netflix still claims it. Source membership updated. No file operation. No status change. Coalition rule holds.
Two weeks later, Netflix drops it too. No enabled Source claims it. Grace period starts: 7 days. But the item is Saved. The removal pipeline checks Saved first. It stops. Frieren stays.
 
Block
The user finds a horror movie on the Trending shelf. Taps Block. Confirms: "Hide this forever?". Files deleted immediately. Emby item removed via API. blocked = true. Next sync runs. 600 items fetched. The horror movie is filtered at step 1, before anything else happens. It does not exist.
Six months later, the admin unblocks it. On the next sync, if any Source still claims it, it re-enters the pipeline at Known and works through Resolved → Hydrated → Created → Indexed → Active like any new item.
 
That is the complete system.
 

