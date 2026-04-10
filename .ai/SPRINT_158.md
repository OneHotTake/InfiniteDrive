# Sprint 158 — User Catalogs: Public Trakt + MDBList via RSS

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 157

---

## Overview

Add the "My Lists" tab: regular users paste a public Trakt or MDBList
RSS URL, click Add, and the items show up in Discover and in their My
Picks. Then add the **"Refresh now"** (per-list) and
**"Refresh all my lists"** (top-of-tab) buttons that synchronously pull,
parse, and ingest — no waiting for the 6-hour scheduled sync.

This sprint also finally **kills the phantom `SourceType.Trakt` and
`SourceType.MdbList` enum values** with a proper schema migration. They
were never wired to a client and leaving them around is architectural
clutter.

### Why This Exists

The shared library is genuinely shared, but today only admins can add
things to it. Users who curate lists on Trakt / MDBList have no way to
get them into Emby without waiting for an admin. The Sprint 148 design
called for user-facing catalog ingestion, and this sprint ships the
minimal version: **public RSS only, no OAuth, no API keys, no
scraping.** Same approach Radarr / Sonarr / every community
Trakt-to-library script already uses.

And the refresh story matters. From the design doc: *"I just spent all
afternoon updating my lists on Trakt, I go into Emby to watch
something, and I'm an impatient sonofabitch."* A 6-hour scheduled sync
is a backstop, not the primary refresh path.

Design spec: `docs/USER_DISCOVER.md`.

### Non-Goals

- ❌ Private Trakt watchlists (requires OAuth)
- ❌ MDBList API keys / account connection
- ❌ IMDB-list support (IMDB has no public RSS)
- ❌ Per-list sync schedules
- ❌ Any new admin-visible configuration (no attribution toggle, no
  anything)
- ❌ Any baked API keys
- ❌ A "My Lists" wizard — the whole flow is one paste + one button

---

## Phase 158A — Schema Migration (Kill the Phantoms)

### FIX-158A-01: Remove phantom SourceType values

**Files:** `Models/SourceType.cs` (modify), `Data/Schema.cs` (modify),
`Data/DatabaseInitializer.cs` (modify)

**What:**

1. `Models/SourceType.cs`: delete the `Trakt` and `MdbList` enum
   values. Add `UserRss`.
2. Grep the repo for every reader of `SourceType.Trakt` /
   `SourceType.MdbList` and delete the dead branches. They were never
   reachable — leaving them was a dodge.
3. `Data/Schema.cs`: rewrite the `sources.type` CHECK constraint
   (and any other table that references `SourceType` as a text column)
   to drop `'trakt'` and `'mdblist'` and add `'user_rss'`.
4. Bump `schema_version` per the existing pattern.
5. Add an upgrade path in `DatabaseInitializer.cs`:
   - On schema upgrade, run
     `UPDATE sources SET type = 'user_rss' WHERE type IN ('trakt','mdblist');`
     (there should be zero matching rows — but handle it anyway).
   - Then recreate the CHECK constraint (SQLite requires
     copy-to-new-table for CHECK changes).

---

### FIX-158A-02: New user_catalogs table

**Files:** `Data/Schema.cs`, `Data/DatabaseInitializer.cs`,
`Data/DatabaseManager.cs` (modify)

**What:**

1. Create the table per `docs/USER_DISCOVER.md`:
   ```sql
   CREATE TABLE user_catalogs (
       id                 TEXT PRIMARY KEY,
       owner_user_id      TEXT NOT NULL,
       source_type        TEXT NOT NULL CHECK (source_type IN ('user_rss')),
       service            TEXT NOT NULL CHECK (service IN ('trakt','mdblist')),
       rss_url            TEXT NOT NULL,
       display_name       TEXT NOT NULL,
       active             INTEGER NOT NULL DEFAULT 1,
       last_synced_at     TEXT,
       last_sync_status   TEXT,
       created_at         TEXT NOT NULL,
       UNIQUE (owner_user_id, rss_url)
   );
   CREATE INDEX idx_user_catalogs_owner  ON user_catalogs(owner_user_id);
   CREATE INDEX idx_user_catalogs_active ON user_catalogs(active) WHERE active = 1;
   ```
2. Add CRUD helpers to `DatabaseManager`:
   - `Task<string> CreateUserCatalogAsync(...)`
   - `Task<IReadOnlyList<UserCatalog>> GetUserCatalogsByOwnerAsync(string ownerUserId, bool activeOnly, CancellationToken ct)`
   - `Task SetUserCatalogActiveAsync(string catalogId, bool active, CancellationToken ct)`
   - `Task UpdateUserCatalogSyncStatusAsync(string catalogId, DateTimeOffset syncedAt, string status, CancellationToken ct)`

---

### FIX-158A-03: source_memberships link column

**Files:** `Data/Schema.cs`, `Data/DatabaseInitializer.cs`,
`Data/DatabaseManager.cs` (modify)

**What:**

1. Add `user_catalog_id TEXT NULL` to `source_memberships`.
2. `source_memberships` already tracks which "source" claims a given
   `catalog_items` row; the new column identifies which user catalog
   (if any) claimed it. Null means "claimed by the system catalog."
3. Add an index: `CREATE INDEX idx_source_memberships_user_catalog ON source_memberships(user_catalog_id);`
4. Helper: `Task<int> CountActiveClaimsAsync(string catalogItemId, CancellationToken ct)` —
   returns the count of active memberships (system catalog plus any
   `user_catalog_id` whose `user_catalogs.active = 1`). Used by the
   deprecation flow when a list is removed.

---

## Phase 158B — RSS Feed Parser

### FIX-158B-01: RssFeedParser

**File:** `Services/RssFeedParser.cs` (create)

**What:**

1. Small wrapper around `System.ServiceModel.Syndication.SyndicationFeed`.
2. Input: the raw RSS XML string from a Trakt or MDBList feed.
3. Output: a normalized list of items:
   ```csharp
   public sealed record RssItem(
       string Title,
       int? Year,
       string? ImdbId,        // extracted from link or guid
       string? Link,
       string? Summary);
   ```
4. Extract IMDB ID from either the `link` element or the `guid` using
   a regex (`tt\d{7,8}`). Items with no resolvable IMDB ID are
   skipped and counted as "skipped" for the refresh response.
5. Rip feed title from `SyndicationFeed.Title.Text` — callers use this
   as the initial `display_name`.
6. **Hard cap at 1000 items per feed** (per design spec). Items past
   the cap are dropped and a warning is logged.
7. **No network calls.** This class takes XML in, gives items out. The
   fetch happens in the sync service so cooldown and HTTP policy live
   in one place.

This parser is **only used for user lists**. The default rails in
Sprint 157 do not touch it — Cinemeta uses JSON.

---

### FIX-158B-02: Service auto-detection helper

**File:** `Services/RssFeedParser.cs` (same file)

**What:**

Static helper `string DetectService(string rssUrl)`:
- Host ends in `trakt.tv` → `"trakt"`
- Host ends in `mdblist.com` → `"mdblist"`
- Anything else → throws `ArgumentException("Unsupported RSS host")`

The create-list endpoint calls this before inserting a row.

---

## Phase 158C — UserCatalogSyncService

### FIX-158C-01: Sync one catalog

**File:** `Services/UserCatalogSyncService.cs` (create)

**What:**

1. New class `UserCatalogSyncService`, singleton.
2. Dependencies: `DatabaseManager`, `RssFeedParser`, `CinemetaProvider`,
   `StrmWriterService`, `CooldownGate`, `ILogManager`, `IHttpClient`.
3. Method:
   ```csharp
   Task<UserCatalogSyncResult> SyncOneAsync(
       string catalogId,
       CancellationToken ct);
   ```
4. Body:
   1. Load the `user_catalogs` row. If `active = 0`, no-op and return.
   2. `await _cooldown.WaitAsync(CooldownKind.CatalogFetch, ct);`
   3. HTTP GET the `rss_url`. On HTTP failure, update
      `last_sync_status` and return the error result (do not throw).
   4. Parse with `RssFeedParser`. Count `fetched`, `skippedNoImdb`.
   5. For each item:
      - Upsert `catalog_items` (look up via IMDB ID; enrich from
        `CinemetaProvider` on first insert). Count `added` / `updated`.
      - Call `StrmWriterService.WriteAsync(item, SourceType.UserRss,
        ownerUserId, ct)` (Sprint 156 unified writer).
      - Insert `source_memberships` row linking the item to this
        `user_catalog_id` (ignore on conflict).
   6. For each existing membership on this catalog that did **not**
      appear in the fresh feed, mark it inactive. Count `removed`.
      (This is how the Deep Clean later retires orphaned items.)
   7. Update `user_catalogs.last_synced_at = now()`,
      `last_sync_status = "ok"` or the error.
   8. Return `UserCatalogSyncResult { ok, fetched, added, updated,
      removed, skippedNoImdb, elapsedMs, error? }`.

---

### FIX-158C-02: Sync all catalogs owned by a user

**File:** `Services/UserCatalogSyncService.cs` (same file)

**What:**

```csharp
Task<IReadOnlyList<UserCatalogSyncResult>> SyncAllForOwnerAsync(
    string ownerUserId,
    CancellationToken ct);
```

1. Load all `active = 1` catalogs for `ownerUserId`.
2. Sequentially call `SyncOneAsync` for each (sequential — the
   cooldown gate handles politeness; parallelism would just stack
   waits).
3. Return the list of results. The endpoint caller aggregates them.

---

### FIX-158C-03: CatalogSyncTask integration

**File:** `Tasks/CatalogSyncTask.cs` (modify)

**What:**

1. After the existing system-catalog sync pass, load **every**
   `active = 1` user catalog on the server and call
   `UserCatalogSyncService.SyncOneAsync` for each.
2. Respect `CooldownProfile.CatalogSourcesPerRun` — if the combined
   (system + user) source count exceeds the cap for this instance
   type, pick a rolling subset so every list gets refreshed on
   average every few runs.
3. Log per-catalog sync result summaries at Info level.
4. The scheduled task cadence does not change — this is the 6-hour
   backstop, not the user-triggered refresh path.

---

## Phase 158D — User Endpoints

### FIX-158D-01: GET /User/Catalogs

**File:** `Services/UserCatalogsService.cs` (create)

**What:**

1. New IService class `UserCatalogsService`.
2. `GET /EmbyStreams/User/Catalogs`:
   - `RequireUser`.
   - Returns `{ catalogs: [...], limit: 5 }` for the caller's active
     catalogs only (by `owner_user_id`).
   - Each entry:
     `{ id, service, rssUrl, displayName, lastSyncedAt, lastSyncStatus }`.

---

### FIX-158D-02: POST /User/Catalogs/Add

**File:** `Services/UserCatalogsService.cs` (same file)

**What:**

1. `RequireUser`.
2. Body: `{ rssUrl: string, displayName?: string }`.
3. Validate:
   - `RssFeedParser.DetectService(rssUrl)` (returns `trakt` or
     `mdblist` or throws 400).
   - URL must be `https`. Reject `http` (400).
   - Caller must have fewer than `UserCatalogLimit` active catalogs
     (default 5, read from `CooldownProfile` — no UI field, admin
     override via `EmbyStreams.xml` only). On overflow, return 409
     with `"Catalog limit reached"`.
4. Fetch the feed once via the sync service to verify it parses and
   has at least one item. Reject with 400 on empty feeds.
5. Use the feed's `<title>` as `displayName` if caller didn't supply
   one.
6. Insert `user_catalogs` row.
7. Immediately call `UserCatalogSyncService.SyncOneAsync` (synchronous,
   eager — this is the user's first interaction with the list and
   they're watching the spinner).
8. Return the sync result directly:
   `{ ok, catalogId, fetched, added, updated, elapsedMs }`.

---

### FIX-158D-03: POST /User/Catalogs/Remove

**File:** `Services/UserCatalogsService.cs` (same file)

**What:**

1. `RequireUser`.
2. Body: `{ catalogId }`.
3. Verify the row's `owner_user_id` matches the caller (403 otherwise).
4. Set `active = 0`. Do **not** immediately delete items. The scheduled
   `DoctorTask` handles deprecation via `CountActiveClaimsAsync`: any
   `catalog_items` whose only remaining claim was this user catalog
   enter the normal retirement flow.
5. Return `{ ok: true }`.

---

### FIX-158D-04: POST /User/Catalogs/Refresh (the impatient button)

**File:** `Services/UserCatalogsService.cs` (same file)

**What:**

1. `RequireUser`.
2. Body: `{ catalogId?: string }` — optional.
3. Branches:
   - If `catalogId` is present: verify ownership (403 otherwise),
     call `UserCatalogSyncService.SyncOneAsync`, and return the single
     result.
   - If `catalogId` is absent: call
     `UserCatalogSyncService.SyncAllForOwnerAsync`, aggregate the
     per-list results, and return:
     ```json
     {
       "ok": true,
       "lists": 3,
       "fetched": 127,
       "added": 4,
       "updated": 22,
       "removed": 1,
       "elapsedMs": 5310,
       "perList": [ { ... }, { ... }, { ... } ]
     }
     ```
4. **Synchronous** — the request blocks until the sync completes. The
   per-list 1000-item cap and the cooldown gate together bound the
   wall-clock cost.
5. If any single list errors, the rest continue; the failed one
   appears in `perList` with `ok: false` and an error message.

This endpoint is the entire reason Sprint 158 exists as a separate
sprint. It is the answer to the impatient-user complaint. Do not
refactor it into a background job.

---

## Phase 158E — Parental Rating Guard for User Lists

### FIX-158E-01: Hide over-ceiling items in Discover surface

**File:** `Services/DiscoverService.cs` (verify — should already be in
Sprint 157's SQL filter)

**What:**

1. Confirm that the `catalog_items` query used by Browse and Search
   filters on `content_rating` regardless of whether the row was
   claimed by the system catalog or a user catalog. The filter lives
   on `catalog_items`, not on `sources` or `source_memberships`.
2. Add an explicit test: create a user catalog whose RSS feed
   contains an R-rated item. Verify a PG-13 test account cannot see
   the item in Browse, Search, Detail, My Picks, or any other
   surface, even though a different user claimed it.

The rule from `docs/USER_DISCOVER.md` is unambiguous: a user's
parental ceiling cannot be bypassed by another user sharing a
catalog.

---

### FIX-158E-02: Pin guard on auto-pin

**File:** `Services/EmbyEventHandler.cs` (modify — only if necessary)

**What:**

1. The existing playback auto-pin hook creates `user_item_pins` rows
   with `pin_source='playback'`. If the Discover filter correctly
   hides over-ceiling items, playback can never start and the hook
   never fires — but defence in depth: before inserting the pin,
   verify the caller's ceiling still permits the item. Log and drop
   the pin if not (indicates a previously-allowed item whose rating
   was later tightened).
2. If the existing code already handles this, no change is required —
   just add a comment pointing to `docs/USER_DISCOVER.md`.

---

## Phase 158F — UI

### FIX-158F-01: My Lists tab shell

**Files:** `Configuration/configurationpage.html`,
`Configuration/configurationpage.js` (modify)

**What:**

1. Add a new tab **"My Lists"** visible to every authenticated user
   (not gated on `isAdmin`).
2. Tab contents:
   - **Top row:** single big button `"Refresh all my lists"`.
     Calls `POST /User/Catalogs/Refresh` with no body.
   - **Add list:** one input (URL) + one button (Add). No type
     picker — the service auto-detects Trakt vs MDBList.
   - **List of lists:** for each row, show
     `displayName | service | lastSyncedAt | "Refresh now" | "Remove"`.
3. On successful `/Add`, toast the counts from the response:
   `"Added, fetched 47, 47 new items."`
4. On `/Refresh` (per-list or all), toast the counts:
   `"Added 3, updated 12."` Per-list refresh updates just that row;
   all-lists refresh updates every row.
5. On `/Remove`, remove the row from the list immediately and toast
   `"List removed. Items will be cleaned up on next DoctorTask pass."`
6. On limit overflow (409), toast `"You can have at most 5 lists.
   Remove one to add another."`

Explicit non-goals:
- No modal dialogs.
- No drag-and-drop reordering.
- No per-list sync schedule picker.
- No "edit list" button (display names are auto-set from the feed
  title and are not user-editable in this sprint).

---

## Phase 158G — Build & Verification

### FIX-158G-01: Build

`dotnet build -c Release` — 0 errors, 0 net-new warnings.

---

### FIX-158G-02: Grep checklist

| Pattern | Expected |
|---|---|
| `SourceType.Trakt` | 0 (source) |
| `SourceType.MdbList` | 0 (source) |
| `'trakt'` in `Data/Schema.cs` | only inside `user_catalogs.service` CHECK |
| `'mdblist'` in `Data/Schema.cs` | only inside `user_catalogs.service` CHECK |
| `ShowUserCatalogAttribution` | 0 (anywhere) |
| `UserRss` | ≥ 2 (enum + callers) |
| `user_catalogs` (SQL) | 1 CREATE TABLE + indexes |
| `api[_-]?key` grep | 0 (unchanged from Sprint 157) |

---

### FIX-158G-03: End-to-end user story

1. Create a non-admin Emby test user with no parental ceiling.
2. Log in as that user and open the configuration page.
3. Click "My Lists." Paste a known public Trakt list RSS URL. Click
   Add. Assert:
   - The toast shows a non-zero `fetched` count.
   - The list appears in the list-of-lists.
   - Opening Discover shows the ingested items (in the rails or via
     search).
4. Go to Trakt, add one title to the list, remove another.
5. Back in Emby, click "Refresh now" on that list. Assert:
   - The response returns `{ ok, added: 1, removed: 1, ... }`.
   - The new item appears in Discover within a few seconds.
6. Add a second Trakt list and a MDBList RSS list. Click "Refresh
   all my lists." Assert all three sync in one request and the
   aggregate counts add up.
7. Try to add a sixth list. Assert: 409 with `"Catalog limit
   reached"`.
8. Click Remove on one list. Wait for the next scheduled `DoctorTask`
   pass (or trigger manually). Assert items uniquely contributed by
   that list are retired per the normal soft-delete path.

---

### FIX-158G-04: Parental rating guard for user lists

1. Create `johnny-pg13` from Sprint 157's FIX-157F-02, if not already.
2. Log in as an unrestricted user and add an MDBList list known to
   contain at least one R-rated item.
3. Log in as `johnny-pg13`. Open Discover. Assert:
   - The R-rated item does **not** appear in any surface.
   - Browse/search return zero hits for the R-rated title.
   - `Discover/Detail` for that item's ID returns `404`.
   - `Discover/AddToLibrary` for that ID returns `403`.
4. The unrestricted user can still see and play the item. The user
   catalog claim does not override johnny's ceiling.

---

### FIX-158G-05: Schema migration fresh-install + upgrade

1. Fresh install (`./emby-reset.sh`): schema comes up with no
   `Trakt` / `MdbList` CHECK values, and the `user_catalogs` table
   exists.
2. Upgrade simulation: restore a pre-Sprint-158 database with a
   `sources.type = 'trakt'` row hand-inserted (to simulate the
   worst case). Start the plugin. Assert:
   - The upgrade converts the row to `type = 'user_rss'`.
   - The CHECK constraint is rewritten successfully.
   - No startup errors.

---

## Sprint 158 Completion Criteria

- [ ] `SourceType.Trakt` and `SourceType.MdbList` removed from the
      enum and every call site
- [ ] `sources.type` CHECK constraint rewritten via migration
- [ ] `SourceType.UserRss` added
- [ ] `user_catalogs` table created with CRUD helpers on
      `DatabaseManager`
- [ ] `source_memberships.user_catalog_id` column + index added
- [ ] `Services/RssFeedParser.cs` created (parse-only, no HTTP)
- [ ] `Services/UserCatalogSyncService.cs` created with
      `SyncOneAsync` and `SyncAllForOwnerAsync`
- [ ] `Services/UserCatalogsService.cs` created with
      `GET /User/Catalogs`, `POST /User/Catalogs/Add`,
      `POST /User/Catalogs/Remove`,
      `POST /User/Catalogs/Refresh`
- [ ] `POST /User/Catalogs/Refresh` is synchronous and returns
      `{ fetched, added, updated, removed, elapsedMs, perList }`
- [ ] "My Lists" tab visible to every authenticated user with
      `Refresh all my lists` + per-list `Refresh now` buttons
- [ ] `CatalogSyncTask` iterates user catalogs on its 6-hour
      backstop pass
- [ ] Parental rating guard confirmed for user-list items (Phase
      158E / FIX-158G-04)
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Grep checklist all clean (Phase 158G-02)
- [ ] End-to-end user story passes (Phase 158G-03)
- [ ] Schema migration fresh-install and upgrade both succeed
      (Phase 158G-05)
- [ ] **Zero** new admin-visible config fields

---

## Notes

**Files created:** 4 (`Services/RssFeedParser.cs`,
`Services/UserCatalogSyncService.cs`, `Services/UserCatalogsService.cs`,
plus a `Models/UserCatalog.cs` POCO)

**Files modified:** ~9 (`Models/SourceType.cs`, `Data/Schema.cs`,
`Data/DatabaseInitializer.cs`, `Data/DatabaseManager.cs`,
`Services/DiscoverService.cs` for user-list Discover rendering,
`Services/EmbyEventHandler.cs` for the pin guard note,
`Tasks/CatalogSyncTask.cs`, `Configuration/configurationpage.html`,
`Configuration/configurationpage.js`)

**Files deleted:** 0

**Config fields added (user-visible):** 0
**Config fields removed (user-visible):** 0
**Admin-visible config delta this sprint:** 0

**Risk: MEDIUM** — introduces a new user-writable surface, a new
table, and a schema CHECK migration. Mitigated by:
1. The refresh endpoint is synchronous — no hidden background-job
   state to get wrong.
2. Parental rating guard runs on every read path from Sprint 157 and
   is verified for user-list items here.
3. Schema migration is tested on both fresh install and simulated
   upgrade (FIX-158G-05).
4. Per-user cap (5 lists) and per-list cap (1000 items) bound the
   blast radius of abuse.
5. No OAuth / no API keys / no scraping — only public RSS. The worst
   a user can do is paste a URL that returns 404, in which case the
   Add endpoint cleanly rejects with 400.

**Elegance invariant:** zero new admin-visible config fields, zero
new user-visible wizard steps, zero baked API keys, zero phantom
enum values. After this sprint the `SourceType` enum has exactly
three members: `BuiltIn`, `Aio`, `UserRss`.

**Reference:** `docs/USER_DISCOVER.md` — full design context,
authorization matrix, parental rating hard rule, and the rationale
for RSS-only ingestion.
