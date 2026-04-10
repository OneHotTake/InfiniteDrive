# USER_DISCOVER — Discover as a First-Class User Space

**Status:** Design Spec | **Owner:** embyStreams | **Target Sprints:** 157 (Discover un-gating) + 158 (User RSS catalogs)

---

## Why This Exists

The Sprint 148 design called for a per-user Discover page. The admin side
shipped (`DiscoverService` with 6 endpoints). The user side did not — every
Discover endpoint still calls `AdminGuard.RequireAdmin()`, so non-admin Emby
users see an empty shell with no usable buttons.

This spec finishes Sprint 148 and extends it one step further: regular users
can not only browse and pin content, they can bring their own public Trakt
and MDBList RSS feeds. Admins are no longer the only path to "add things to
the library."

The shared Emby library is genuinely shared — items added by any user become
visible to every user. That's not a bug, it's the operating model. This
design leans into it instead of fighting it.

> **Elegance invariant:** every user-facing improvement in this spec
> **removes** or **reuses** complexity. Zero new admin knobs. One new user
> surface ("My Lists"). One new table. No per-user libraries, no scopes, no
> ACLs beyond "admin" vs "not admin."

---

## The User-Visible Mental Model

```
EVERY USER SEES:
  Discover        browse + search + add to library
  My Picks        things I pinned (auto via playback OR manual)
  My Lists        public RSS lists I subscribed to (Trakt/MDBList)

EVERY USER'S LIBRARY IS THE SAME LIBRARY.
  Adding something via Discover adds it for everyone.
  Pinning is per-user. Adding is global.
  There is no "my library." There is only "the library, and my pins in it."
```

Users never see the words "catalog," "source," "AIOStreams," "IMDB," or
"pipeline." They see:

- **Discover** — a Netflix-ish browse page
- **My Picks** — their favorites
- **My Lists** — "lists I follow" in the sense every modern app uses

Admins continue to see all of the above plus the existing Content Mgmt /
Blocked Items / Setup / Improbability / Health tabs. Those tabs are not
touched.

---

## Authorization Matrix

| Endpoint | Today | After Sprint 157 |
|---|---|---|
| `GET /Discover/Browse` | admin | **user** |
| `GET /Discover/Search` | admin | **user** |
| `GET /Discover/Detail` | admin | **user** |
| `POST /Discover/AddToLibrary` | admin | **user** (creates `user_item_pins` row with `pin_source='discover'`) |
| `GET /Discover/TestStreamResolution` | admin | admin (diagnostic) |
| `GET /Discover/DirectStreamUrl` | admin | admin (diagnostic) |
| `GET /User/Pins` | user | user (unchanged) |
| `POST /User/Pins/Remove` | user | user (unchanged) |
| `GET /User/Catalogs` | — | **user** (Sprint 158) |
| `POST /User/Catalogs/Add` | — | **user** (Sprint 158) |
| `POST /User/Catalogs/Remove` | — | **user** (Sprint 158) |
| `POST /User/Catalogs/Refresh` | — | **user** (Sprint 158) |

Rules:
- **User endpoints require authenticated Emby user context** (not anonymous).
  Read `IAuthorizationContext.GetAuthorizationInfo(Request).UserId`.
- **All user-write endpoints are tagged with the user who called them.**
  Pins are scoped by `emby_user_id`. User catalogs are scoped by
  `owner_user_id`. Items in the global catalog carry attribution but are
  not scoped.
- **No anonymous surface.** Webhook is being retired in Sprint 156.

---

## Default Rails (Empty-State Content)

A brand-new server with no configured sources and no user lists still has
to look alive. The fix is three default rails pulled from **Cinemeta's
official catalog endpoints** — the same Stremio meta addon
`CinemetaProvider.cs` already talks to for per-item metadata lookups.

```
Row 1  Top Movies    https://v3-cinemeta.strem.io/catalog/movie/top.json
Row 2  Top Series    https://v3-cinemeta.strem.io/catalog/series/top.json
Row 3  Top Anime     https://v3-cinemeta.strem.io/catalog/series/top/genre=Anime.json
```

**Why Cinemeta — and not TMDB, not MDBList RSS, not YTS:**

- **No API keys. No baked secrets.** Cinemeta is the free public Stremio
  meta addon. Everything is keyless and anonymous.
- **Already in the codebase.** `CinemetaProvider.cs` already parses
  Cinemeta JSON. Sprint 157 adds a thin `CinemetaDefaultRailProvider`
  wrapper with caching and parental filtering — no new HTTP client, no
  new provider interface, no new trust boundary.
- **Already rate-limited.** Sprint 155's `CooldownKind.Cinemeta` is the
  exact gate default rails need. Adding three catalog calls costs nothing
  new in operational complexity.
- **No curator dependency.** Unlike MDBList lists owned by individual
  community members, Cinemeta's top catalogs are Stremio-official and
  will not vanish because a single account gets deleted.
- **Native anime support.** `catalog/series/top/genre=Anime.json` is
  Cinemeta's own filter. No separate provider, no hand-tuned genre
  logic, no second trust boundary for one rail.
- **Not TMDB:** TMDB requires an API key baked into the plugin binary.
  Baked keys leak, get revoked, and hit quotas on behalf of users who
  never consented. Nonstarter.
- **Not MDBList RSS:** MDBList RSS is the right format for user-owned
  lists (Sprint 158) but the wrong format for defaults. Defaults need
  curator-proof durability; user lists are explicitly curator-owned.
- **Not YTS:** YTS is a torrent index. Embedding a YTS URL in an
  open-source plugin's default configuration creates distribution and
  legal risk that "it's just metadata" does not mitigate.

**Resolution model.** Default rails populate UI metadata only. No streams
are resolved until a user pins or plays an item, at which point
AIOStreams does its normal resolution pass against the item's IMDB ID.
This matches the Ghost → Resident → Citizen flow from
`docs/CITIZENSHIP.md` — default-rail items are effectively Ghosts until
someone interacts with them.

**Parental ratings.** Cinemeta catalog responses include `imdbRating`
and, for most items, a certification string. The
`CinemetaDefaultRailProvider` filters items server-side by the caller's
ceiling before returning. **Fail closed:** items whose certification is
unknown or unparseable are **hidden** from any user whose account has a
parental ceiling configured (admins still see them). Rails may render
shorter for restricted users. That is the correct trade-off — we would
rather show an empty row than leak an R-rated thumbnail to a 12-year-old
because Cinemeta forgot to tag one item.

**Caching.** Rail responses are cached server-side for 4 hours, keyed by
`(railKind, maxRating)`. The Discover tab never hits Cinemeta on a hot
path. On Cinemeta failure the provider returns any stale cache entry
(prefer stale over empty). A temporary outage never empties the Discover
tab.

**Escape hatch.** Admins who want to curate their own empty-state
content can override any of the three URLs in `EmbyStreams.xml` under
`DefaultRailOverrides` — **XML-only, no UI field**. This is the one
escape hatch and it exists for admins, not users.

**Default rails are not written as `.strm` files on fetch.** They
populate the browse UI only. An item enters `catalog_items` and the
unified write pipeline at the moment a user clicks "Add to My Library."

---

## What Users Can Do in Discover

### Browse

- Three default rails on landing (Cinemeta top movies / top series /
  top anime).
- Additional rails pulled from the **admin-configured system catalog**
  (AIOStreams manifest) as already implemented in Sprint 148.
- Additional rails pulled from **every active user catalog on the server**
  (Sprint 158) — attributed with the owner's display name ("From Alice's
  Trakt list").
- Attribution is always visible. Users can see who added what.

### Search

- Full-text search across the global catalog plus Cinemeta lookup by
  title/year (uses existing `CinemetaProvider`).
- Results filtered server-side by the calling user's parental rating
  ceiling.

### Detail

- Poster, synopsis, cast, runtime (from Cinemeta).
- "Add to My Library" button (or "In My Library" if already pinned).
- "Play" button (standard Emby playback through the pinned `.strm`).

### Add

- One click. Server-side:
  1. Upsert item into `catalog_items` (if not already there).
  2. Write `.strm` via the **unified `StrmWriterService`** (Sprint 156 — no
     more `WriteStrmFileForItemPublicAsync` bypass).
  3. Insert `user_item_pins` row with
     `pin_source='discover'`, `emby_user_id=<caller>`.
  4. Return immediately. Enrichment and stream resolution happen in the
     background via the normal `ItemPipelineService` → `DoctorTask` flow.

### My Picks

Already works. No changes in Sprint 157. Stays as-is.

---

## What Users Can Do in "My Lists" (Sprint 158)

### Add a list

User pastes a public RSS URL:

- Trakt: `https://trakt.tv/users/<u>/lists/<slug>.rss`
- MDBList: `https://mdblist.com/lists/<u>/<slug>/rss`

The plugin auto-detects which service by URL host, fetches the feed once to
verify it parses and has at least one item, extracts the list display name
from the RSS `<title>`, and creates a `user_catalogs` row:

```
owner_user_id        <caller>
source_type          user_rss
service              trakt | mdblist   (detected from host)
rss_url              <full URL as pasted>
display_name         <from RSS title, user-editable later>
active               true
last_synced_at       null (sync runs immediately after add)
created_at           now()
```

**No API keys. No OAuth. No account connection. No scraping.** Just RSS.
This is how Radarr, Sonarr, and every community Trakt-to-library script
already work.

### Refresh a list

Two buttons in "My Lists": one **"Refresh now"** next to each list, and
one **"Refresh all my lists"** at the top of the tab. Both call the same
endpoint — the per-list button passes a `catalogId`, the all-lists
button iterates over the caller's active `user_catalogs` rows
server-side.

Refresh is **synchronous and eager**: the request fetches the RSS feed,
parses it, upserts each item into `catalog_items`, writes `.strm` files
via `StrmWriterService`, inserts any missing `source_memberships` rows,
and returns a response body the UI can show directly:

```
{ ok: true, fetched: 47, added: 3, updated: 12, removed: 0, elapsedMs: 2430 }
```

The button then flashes a one-line toast: *"Added 3, updated 12."* No
modal, no progress bar — if a feed is so big that synchronous parse is
unreasonable, the per-list 1000-item cap already clamps it.

This exists because users who just finished curating on Trakt or MDBList
are impatient by design. A 6-hour scheduled sync is a **backstop**, not
the primary refresh path.

### Remove a list

Marks `user_catalogs.active=false`. The scheduled `DoctorTask` then handles
deprecation: any `catalog_items` whose **only remaining claim** was this
user catalog enter the normal retirement flow. Items that overlap with the
system catalog or another active user catalog stay untouched.

Deprecation is not immediate delete. It's the same soft-delete path system
items use. Users who were mid-watch don't get content yanked out from
under them.

### Limits

- Default **max 5 active lists per user** (config-invisible constant — admins
  who really need more can change it in `EmbyStreams.xml`).
- Default **max 1000 items per list** (RSS feeds with more are truncated and
  a warning logged).
- **Scheduled refresh cadence:** once every 6 hours, inside the existing
  `CatalogSyncTask`. Not a separate task. Not a separate scheduler.

---

## Attribution and Privacy

- `catalog_items` gains a nullable `first_added_by_user_id` column (set on
  first write, never updated).
- `source_memberships` (already exists) gains rows linking each item to
  every `user_catalog_id` that claims it, so deprecation knows when the
  last claimer drops off.
- The Discover UI shows attribution as `"From Alice's Trakt"` for every
  user — only the owner's display name (not email, not user ID).
- Attribution is **always visible**. There is no toggle. Admins see the
  same attribution strings regular users see (and can see who added what
  from the Content Management tab as well). This is a shared household
  library by design — households that cannot tolerate knowing who added
  what should not be using shared libraries.
- **Zero new admin configuration.** This section introduces no new
  settings, no new toggles, and no new user-visible fields.

---

## Parental Ratings (Hard Rule, Not a Suggestion)

The Emby user profile already carries a max parental rating (PG-13, R, etc.)
set by the admin. **Every user-facing endpoint in this design reads that
ceiling and filters server-side before returning anything.** This is a
hard rule: if johnny has a PG-13 ceiling, no surface anywhere in the
plugin — Discover, My Picks, My Lists, search, detail, add, play — may
ever return an R-rated item for johnny. Period.

### Where the filter runs

- **Default Cinemeta rails:** `CinemetaDefaultRailProvider` filters
  every item it returns by the caller's ceiling, **before** the response
  leaves the service. The cache is keyed by `(railKind, maxRating)` so
  PG-13 johnny and unrestricted admin get different cached payloads.
- **System catalog browse:** the SQL query that builds rails from
  `catalog_items` is rewritten to include
  `WHERE rating_rank(content_rating) <= rating_rank(:callerCeiling)`.
  Filtering happens in the database, not the UI.
- **Search:** same SQL rewrite. Results the caller cannot pin never
  appear in the result set.
- **Detail endpoint:** if the looked-up item's rating exceeds the
  caller's ceiling, return `404 Not Found` — not `403`. A user who
  cannot see the item should not even learn it exists.
- **"Add to My Library":** even though the item can't appear in browse
  or search for a restricted user, `AddToLibrary` still performs its
  own server-side rating check and returns `403 Forbidden` with
  `{ error: "Content rating exceeds your account limit" }` if someone
  crafts a POST by hand. **Belt and suspenders.** The UI gate is a
  courtesy; the server gate is the contract.
- **User RSS lists (Sprint 158):** items pulled from another user's
  list are still subject to the caller's ceiling on read. A user whose
  ceiling would hide an item from Discover also cannot pin it and
  cannot play it, even if Alice's Trakt list contains it. The
  `source_memberships` claim does not override the parental ceiling.

### Fail closed on unknown ratings

Cinemeta and the Stremio addon ecosystem do not always carry a
certification string. When `content_rating IS NULL` **and** the caller
has any parental ceiling set, the item is **hidden**. This is the
opposite of the default-forward behaviour most metadata systems use. It
is intentional and non-negotiable: the cost of hiding a kid-safe movie
because its tag is missing is a slightly emptier rail. The cost of
leaking an R-rated item because its tag is missing is a 12-year-old
seeing Big Booty Babes. We pick "emptier rail."

The in-memory rank table used by both the SQL helper and the Cinemeta
filter:

```
{ "G": 1, "PG": 2, "PG-13": 3, "R": 4, "NC-17": 5 }
```

Items whose rating string is present but not in the table are also
treated as "unknown" and therefore hidden from any user with a
ceiling. Admins and users with **no ceiling configured** see everything.

### Admins

Admins with no parental ceiling set see and can add everything. This is
unchanged. The server distinguishes "admin" from "user with R ceiling"
by reading `IUserManager` directly — there is no per-endpoint opt-out.

---

## DB Schema Changes

### New table (Sprint 158)

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

CREATE INDEX idx_user_catalogs_owner   ON user_catalogs(owner_user_id);
CREATE INDEX idx_user_catalogs_active  ON user_catalogs(active) WHERE active = 1;
```

### Column additions (Sprint 157/158)

- `catalog_items.first_added_by_user_id TEXT NULL` (Sprint 157)
- `source_memberships.user_catalog_id TEXT NULL` (Sprint 158)

### Enum change (Sprint 158)

`SourceType` enum (`Models/SourceType.cs`):
- **Remove** the phantom `Trakt` and `MdbList` values. They were never
  wired to an actual client. Leaving them was a schema-migration
  avoidance dodge and it has to end — a cleaner repo is worth one
  migration.
- **Add** `UserRss`.

Schema migration:
1. Rewrite the `sources.type` CHECK constraint (and any other table that
   references `SourceType` as text) to drop `'trakt'` and `'mdblist'`
   and add `'user_rss'`.
2. Data migration: any row whose type is `'trakt'` or `'mdblist'` today
   is converted to `'user_rss'` during the upgrade. (There should be
   zero such rows in practice — the values were phantom — but the
   migration handles them anyway for safety.)
3. Bump `schema_version` per the existing pattern.

The `service` discriminator (`trakt` vs `mdblist`) lives on the
`user_catalogs` row, not on `SourceType`. This is the right place for
it: it's a user-catalog property, not a write-path fork.

---

## What This Design Explicitly Does Not Add

- ❌ Per-user virtual libraries
- ❌ Private Trakt watchlists / OAuth / API keys
- ❌ MDBList API keys or account connection
- ❌ **Any baked API keys of any kind.** TMDB, Fanart, OMDb, whatever —
  not here, not in the binary, not \"just for defaults.\" If an
  integration requires a key, it is not in this design.
- ❌ IMDB-list support (IMDB has no public RSS; professional APIs required)
- ❌ User-configurable rail counts or positions
- ❌ A "My Lists" wizard page (the entire flow is one paste + one button)
- ❌ **Any new admin configuration, period.** No attribution toggle, no
  rail overrides in the UI, no parental-rating-override switch.
- ❌ Per-list sync schedules
- ❌ Rail reordering / drag-and-drop
- ❌ A separate "user Discover service" class (the existing
  `DiscoverService` gets un-gated, not duplicated)
- ❌ Any path that lets a user see or pin content above their Emby
  parental ceiling. Not by clever URL crafting, not via another user's
  shared catalog, not via an "unknown rating" loophole.

---

## Implementation Order

1. **Sprint 156** — Webhook retirement + write-path consolidation.
   *Must ship first* because it introduces the unified `StrmWriterService`
   that Sprint 157's user AddToLibrary flow will call.
2. **Sprint 157** — Discover un-gating + Cinemeta default rails +
   parental rating filters (the hard-rule version above). After this,
   regular users can use the Discover tab and will never see content
   above their ceiling.
3. **Sprint 158** — User RSS catalogs (public Trakt + MDBList), the
   "Refresh all my lists" button, and the phantom-enum schema
   migration. Lands after Discover is already user-accessible.

Sprint 155 (CooldownGate) runs orthogonally and has no dependency on any
of these three.

---

## Success Criteria

1. A non-admin Emby user loads the configuration page, clicks Discover,
   and sees the three Cinemeta default rails (Top Movies / Top Series /
   Top Anime) populated within 2 seconds.
2. That user clicks "Add to My Library" on an item. Within 5 seconds
   the item is in their My Picks and visible in the shared library.
3. That user clicks "My Lists," pastes a Trakt RSS URL, clicks Add,
   and sees their list synced within 30 seconds.
4. That same user clicks "Refresh all my lists" after making changes
   on Trakt and, within a few seconds, sees a toast summarising
   `added / updated / removed` counts — without waiting for the 6-hour
   scheduled sync.
5. When that user removes their list, the items it uniquely
   contributed are gone from the library by the next scheduled
   `DoctorTask` pass (24h by default).
6. **A user whose Emby account has a PG-13 ceiling never sees an
   R-rated item on any Discover surface, from any catalog, from any
   user, from any default rail, ever.** This is tested explicitly with
   a non-admin PG-13 test account in FIX-157F-02. Items with missing
   rating tags are also hidden from that user.
7. Attempting to POST `AddToLibrary` for an R-rated item as the PG-13
   user returns `403`, even though the button is not shown in the UI.
8. Attempting to GET `Discover/Detail` for an R-rated item as the
   PG-13 user returns `404` (not `403`).
9. The configuration page has **zero** new admin-visible fields after
   this whole design ships. The only removed field is Sprint 156's
   `WebhookSecret`. Net user-visible config delta: **-1**.
10. Every `.cs` grep for `ApiKey`, `apiKey`, `TMDB_KEY`,
    `tmdb_api_key`, or similar returns zero matches in the final
    binary. No baked keys, anywhere.
