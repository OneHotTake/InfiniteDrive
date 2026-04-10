# Sprint 157 — Discover for Users + Cinemeta Default Rails + Hard Parental Guard

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 156

---

## Overview

Finish the user-facing half of Sprint 148. Un-gate the four user-friendly
Discover endpoints, keep the two diagnostic ones admin-only, and wire a
`CinemetaDefaultRailProvider` (a thin wrapper over the existing
`CinemetaProvider`) so a brand-new server with no configured sources
still has three populated rails: Top Movies, Top Series, Top Anime.

Every Discover surface filters server-side by the calling user's
Emby parental rating ceiling **as a hard rule**. Unknown/missing
rating tags fail closed — they are hidden from any user with a
ceiling. See `docs/USER_DISCOVER.md` → "Parental Ratings (Hard Rule,
Not a Suggestion)."

### Why This Exists

Sprint 148 was marked complete but only the admin side shipped. Every
Discover endpoint calls `AdminGuard.RequireAdmin()`, so a regular Emby
user loads the Discover tab and sees nothing. The `user_item_pins`
table already has a `'discover'` `pin_source` value in its CHECK
constraint, but there is no user-accessible endpoint that writes it.

This sprint also solves the "empty state" problem: a new user on a
fresh server with no AIOStreams manifest yet, or one whose manifest is
narrow, currently sees an empty Discover tab. Three hard-coded
Cinemeta catalog rails fix that without adding a single user-visible
config field and without baking any API key into the plugin binary.

Design spec: `docs/USER_DISCOVER.md`.

### Non-Goals

- **No TMDB integration.** Zero baked API keys of any kind.
- **No RSS parser** (that lands in Sprint 158 for user lists only).
- **No new admin-visible configuration.**

---

## Phase 157A — Un-gate DiscoverService

### FIX-157A-01: Introduce a user-context helper

**File:** `Services/AdminGuard.cs` (modify) or
`Services/UserContext.cs` (create — one of the two)

**What:**

1. Add a helper:
   ```csharp
   public static (bool ok, string? userId, object? deny) RequireUser(
       IAuthorizationContext authCtx,
       IRequest request);
   ```
2. Behaviour: returns `(true, userId, null)` if the request is from an
   authenticated Emby user (any user, admin or not). Returns
   `(false, null, 401-Unauthorized-response)` for anonymous requests.
3. Unlike `RequireAdmin`, it does not require `IsAdministrator == true`.
4. Internally reuses `authCtx.GetAuthorizationInfo(Request)` — same call
   the existing `RequireAdmin` uses.

This is **additive** — no existing `RequireAdmin` call changes. It just
gives us a second gate for endpoints we want authenticated-but-not-admin.

---

### FIX-157A-02: Browse endpoint

**File:** `Services/DiscoverService.cs` (modify, line ~297)

**What:**

1. Replace `AdminGuard.RequireAdmin(_authCtx, Request)` with
   `AdminGuard.RequireUser(_authCtx, Request)`.
2. Capture `userId` and pass it into the rail-building logic so
   server-side parental rating filtering can happen (Phase 157D).

---

### FIX-157A-03: Search endpoint

**File:** `Services/DiscoverService.cs` (modify, line ~334)

**What:**
Same change as 157A-02. Replace admin guard with user guard. Filter
results by caller's parental ceiling.

---

### FIX-157A-04: Detail endpoint

**File:** `Services/DiscoverService.cs` (modify, line ~639)

**What:**
Same change as 157A-02. Additionally: if the item's `content_rating`
exceeds the caller's ceiling **or is unknown and the caller has a
ceiling**, return `404 Not Found` (not 403 — a user who can't see the
item shouldn't know it exists).

---

### FIX-157A-05: AddToLibrary endpoint

**File:** `Services/DiscoverService.cs` (modify, line ~672)

**What:**

1. Swap to `RequireUser`.
2. **Server-side rating check (belt-and-suspenders):** if the item's
   `content_rating` exceeds the caller's ceiling, or is unknown and
   the caller has a ceiling, return `403 Forbidden` with
   `{ error: "Content rating exceeds your account limit" }`.
3. On success, the handler must:
   - Call `_strmWriter.WriteAsync(item, SourceType.Aio, userId, ct)`
     (Sprint 156 dependency).
   - Call `_db.UpsertUserPinAsync(userId, item.Id, pinSource: "discover", ct)`.
   - Return `{ ok: true, pinned: true, itemId: ... }` for the UI to
     update the button state to "In My Library."

---

### FIX-157A-06: Keep diagnostic endpoints admin-only

**File:** `Services/DiscoverService.cs` (modify, lines ~981 and ~1084)

**What:**
Leave `TestStreamResolution` and `DirectStreamUrl` calling
`AdminGuard.RequireAdmin` exactly as they are today. These are
troubleshooting surfaces, not user features. Add a one-line comment
explaining why.

---

## Phase 157B — Cinemeta Default Rails (No Baked Keys)

### FIX-157B-01: Cinemeta catalog fetch helper

**File:** `Services/CinemetaProvider.cs` (modify)

**What:**

1. Add a new method to the existing provider:
   ```csharp
   Task<IReadOnlyList<CinemetaCatalogItem>> FetchCatalogAsync(
       string catalogPath,          // e.g. "movie/top" or "series/top/genre=Anime"
       CancellationToken ct);
   ```
2. It hits `https://v3-cinemeta.strem.io/catalog/{catalogPath}.json`,
   parses the `metas` array, and returns normalized items with
   `{ imdbId, title, year, type, posterUrl, overview, imdbRating,
   contentRating }`.
3. Wrap the HTTP call with
   `await _cooldown.WaitAsync(CooldownKind.Cinemeta, ct);`
   (Sprint 155 dependency — `CooldownKind.Cinemeta` already exists).
4. No new `CooldownKind` is added. No TMDB. No API keys.
5. Log and swallow HTTP failures — return an empty list so the caller
   can fall back to cached data.

---

### FIX-157B-02: CinemetaDefaultRailProvider

**File:** `Services/CinemetaDefaultRailProvider.cs` (create)

**What:**

1. New class `CinemetaDefaultRailProvider`, registered as a singleton in
   `Plugin.cs`.
2. Constructor takes `CinemetaProvider`, `ILogManager`, and nothing else.
3. Method:
   ```csharp
   Task<IReadOnlyList<DiscoverRail>> GetDefaultRailsAsync(
       string userId,
       string? userMaxRating,      // null for admins/unrestricted
       CancellationToken ct);
   ```
4. Three hard-coded rail definitions:
   ```csharp
   private static readonly (string kind, string title, string path)[] Rails = new[]
   {
       ("movies", "Top Movies", "movie/top"),
       ("series", "Top Series", "series/top"),
       ("anime",  "Top Anime",  "series/top/genre=Anime"),
   };
   ```
5. Internal memory cache:
   `Dictionary<(string railKind, string ratingKey), (DateTimeOffset expires, DiscoverRail rail)>`.
   `ratingKey` is `"admin"` for null ceilings, otherwise the ceiling
   string. TTL = 4 hours.
6. On cache miss: call `CinemetaProvider.FetchCatalogAsync`, then
   **filter the items by the caller's ceiling before caching**. Hidden
   items never enter the cache for that rating bucket. Then store.
7. **Fail-closed filter:** items whose `contentRating` is missing, null,
   empty, or unparseable are **removed** from the rail for any caller
   with a non-null ceiling. Admins (ceiling == null) see all items.
8. On Cinemeta failure, return any stale cached entry (prefer stale
   over empty). If no cache exists, return an empty list — system
   catalog rails still render and Discover isn't broken.
9. **Optional XML override:** on startup read
   `EmbyStreams.xml` → `DefaultRailOverrides.{Movies|Series|Anime}`.
   If present, those catalog paths replace the built-in ones. XML-only,
   no UI field. This is the escape hatch from `docs/USER_DISCOVER.md`.

---

### FIX-157B-03: Wire default rails into Browse

**File:** `Services/DiscoverService.cs` (modify the Browse handler)

**What:**

1. Inject `CinemetaDefaultRailProvider`.
2. In the Browse response, prepend the default rails **only if** the
   existing system-catalog rails would otherwise be empty **or** the
   user explicitly passes `?includeDefaultRails=true`.
3. Default rails are **not** written as `.strm` files and do not enter
   `catalog_items` unless the user clicks "Add to My Library." They're
   pure browse data.
4. Each item in a default rail carries a flag `isDefaultRail = true`
   so the UI can render a small `"Cinemeta"` attribution badge and the
   add flow knows to mint a `catalog_items` row on first pin.

---

## Phase 157C — AddToLibrary From Default Rails

### FIX-157C-01: Mint a catalog_items row on default-rail pin

**File:** `Services/DiscoverService.cs` (modify `AddToLibrary` handler)

**What:**

1. If the request's `itemId` isn't found in `catalog_items`, check
   whether the payload identifies a Cinemeta default-rail item
   (IMDB ID already resolved — Cinemeta's catalog response carries it).
2. If yes:
   - Upsert into `catalog_items` with `source_type = SourceType.Aio`
     (default-rail adds are "I want this from the shared library,"
     which is still the AIO path).
   - **Re-run the parental rating check** on the freshly minted row
     before continuing. If it would be hidden, return `403` and do
     **not** write the `.strm` file or insert the pin.
   - Otherwise proceed with the normal `StrmWriterService.WriteAsync`
     and pin flow (Sprint 156).
3. If the default-rail item lacks an IMDB ID entirely (shouldn't
   happen for Cinemeta catalogs, but be safe), return
   `503 Service Unavailable` with a friendly message:
   `"Couldn't find a stream for this title right now. Try again later."`

---

## Phase 157D — Parental Rating Filter (The Hard Rule)

### FIX-157D-01: Emby user ceiling lookup

**File:** `Services/UserContext.cs` (modify — or `AdminGuard.cs`)

**What:**

1. Add helper `string? GetMaxParentalRating(string userId)` that reads
   the user's `MaxParentalRating` from Emby's `IUserManager`.
2. Returns `null` for admins and for users with no ceiling configured
   (they see everything).
3. Returns a normalized rating string (`"PG-13"`, `"R"`, etc.) that
   the catalog/Cinemeta filters understand.

---

### FIX-157D-02: Rating rank helper + SQL filter

**File:** `Services/DiscoverService.cs` (modify Browse and Search) +
`Data/DatabaseManager.cs` (modify)

**What:**

1. Add a shared in-memory rank table (exactly this, no others):
   ```csharp
   private static readonly Dictionary<string, int> RatingRank =
       new(StringComparer.OrdinalIgnoreCase)
   {
       { "G",     1 },
       { "PG",    2 },
       { "PG-13", 3 },
       { "R",     4 },
       { "NC-17", 5 },
   };
   ```
2. `TryRank(string? rating, out int rank)` returns false for null/
   empty/unknown strings.
3. When building rails/search results from `catalog_items`, pass the
   caller's ceiling to the DB query. The query filters in SQL:
   ```sql
   WHERE (:callerCeiling IS NULL)
      OR (content_rating IS NOT NULL
          AND rating_rank(content_rating) <= :ceilingRank)
   ```
4. **Fail-closed:** when `:callerCeiling IS NOT NULL`, items with
   `content_rating IS NULL` are **excluded**. Admins (NULL ceiling)
   still see them.
5. `rating_rank` is implemented as an in-C# filter over the result
   set if a SQL function isn't available, but the predicate is
   identical.

---

### FIX-157D-03: Cinemeta rail filter

**File:** `Services/CinemetaDefaultRailProvider.cs` (verify in 157B-02)

**What:**
Already wired in FIX-157B-02. Verify it actually filters:
- Smoke test with a PG-13 user sees no R-rated items in any rail.
- Smoke test with a PG-13 user sees **fewer** items than admin on
  rails where Cinemeta is missing certification tags (because those
  are hidden from PG-13 users).
- Admin sees everything.

---

### FIX-157D-04: Detail endpoint fail-closed

**File:** `Services/DiscoverService.cs` (verify 157A-04)

**What:**
Verify that the Detail endpoint returns `404` (not 403, not 200) for:
- An R-rated item when the caller has a PG-13 ceiling.
- An item with no `content_rating` when the caller has any ceiling.

A user who cannot see the item must not learn it exists.

---

## Phase 157E — UI Integration

### FIX-157E-01: Discover tab for non-admin users

**File:** `Configuration/configurationpage.js` (modify)

**What:**

1. The Discover tab is already cosmetically visible to all users. No
   HTML change.
2. JS: when initialising the Discover tab, remove any
   `if (!isAdmin) return;` guard that currently prevents non-admin API
   calls. The tab should fetch data for whoever loads it.
3. The "In My Library" button state reads from
   `/EmbyStreams/User/Pins` (already user-accessible) to decorate items
   the caller has already pinned.
4. When showing a default-rail item, render a small muted badge:
   `"Cinemeta — Top Movies"` / `"Cinemeta — Top Series"` /
   `"Cinemeta — Top Anime"`.

---

### FIX-157E-02: "In My Library" round-trip

**File:** `Configuration/configurationpage.js` (modify)

**What:**

After a successful `AddToLibrary` call:

1. Flip the button to `"In My Library"` (disabled) without a full page
   reload.
2. Emit a toast: `"Added to your library"`. Single line, no modal.
3. The My Picks tab, when next opened, will show the item (uses
   existing `/User/Pins` endpoint).

---

## Phase 157F — Build & Verification

### FIX-157F-01: Build

**What:**
`dotnet build -c Release` — 0 errors, 0 net-new warnings.

---

### FIX-157F-02: Non-admin parental rating smoke test

**What:**

Create a dedicated test account `johnny-pg13` with
`MaxParentalRating = "PG-13"`. Log into the configuration page as
that account.

1. Open Discover. Assert:
   - All three Cinemeta default rails render (if system catalog is empty).
   - **Zero R-rated items** appear anywhere (in default rails, system
     catalog rails, or search).
   - **Zero items with null/unknown content_rating** appear anywhere.
   - Search for a known R-rated title → returns zero results.
   - Search for a known PG-13 title → returns the item.
2. Call `Discover/Detail` with the ID of a known R-rated item directly
   (via curl, bypassing the UI). Assert: `404 Not Found`.
3. Call `Discover/Detail` with the ID of a known item whose
   `content_rating IS NULL` directly. Assert: `404 Not Found`.
4. Call `Discover/AddToLibrary` with the ID of a known R-rated item
   directly (bypassing the UI). Assert: `403 Forbidden` with the
   `"Content rating exceeds your account limit"` body.
5. Click "Add to My Library" on a PG-13 item via the UI. Assert:
   `200`, pin is written, My Picks shows it.
6. Open My Picks. Assert the added item is present with
   `pin_source='discover'`.

Then log back in as admin. Open Discover. Assert:
- R-rated items **are** visible in rails and search.
- Items with `content_rating IS NULL` **are** visible.
- AddToLibrary works without rating rejection.

**This is the sprint's contract.** If any assertion fails the sprint
is not complete.

---

### FIX-157F-03: Cinemeta cache verification

**What:**

1. Trigger `/Discover/Browse` twice within 4 hours as the same user.
2. Log inspection: confirm only the first call hits Cinemeta.
3. Trigger `/Discover/Browse` as a second user with a different
   ceiling. Confirm it hits Cinemeta once (different cache bucket).
4. Wait (or manipulate cache state) and confirm a call after 4h
   refetches.

---

### FIX-157F-04: TestStreamResolution and DirectStreamUrl still admin-only

**What:**
Non-admin call to `/Discover/TestStreamResolution` → 403.
Non-admin call to `/Discover/DirectStreamUrl` → 403.

---

### FIX-157F-05: No baked keys grep

**What:**
```
grep -rEi 'api[_-]?key|tmdb[_-]?key|omdb[_-]?key|fanart[_-]?key' \
  Services/ Data/ Plugin.cs Models/ Tasks/
```
Must return zero matches in the final binary (hits in `docs/` are
fine — they're explicit "don't do this" markers).

---

## Sprint 157 Completion Criteria

- [ ] `AdminGuard.RequireUser` (or `UserContext.RequireUser`) added
- [ ] `Discover/Browse`, `Discover/Search`, `Discover/Detail`,
      `Discover/AddToLibrary` all use `RequireUser`
- [ ] `Discover/TestStreamResolution` and `Discover/DirectStreamUrl`
      remain `RequireAdmin`
- [ ] `CinemetaProvider.FetchCatalogAsync` method added (wraps
      `CooldownKind.Cinemeta`)
- [ ] `Services/CinemetaDefaultRailProvider.cs` created with three
      hard-coded rails and 4-hour cache keyed by `(railKind, ratingKey)`
- [ ] Default rails prepend to Browse response when system catalog is
      empty or `includeDefaultRails=true`
- [ ] AddToLibrary from default rails mints a `catalog_items` row and
      re-validates the parental ceiling before writing the `.strm`
- [ ] Emby user parental rating read via `IUserManager` and applied
      **fail-closed** server-side to Browse, Search, Detail,
      AddToLibrary, and default rails
- [ ] Detail returns `404` (not `403`) for items the caller may not see
- [ ] Discover tab JS no longer gates on `isAdmin`
- [ ] "In My Library" button updates on successful add without reload
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Non-admin parental rating smoke test passes (Phase 157F-02) —
      **every single assertion**
- [ ] Cinemeta cache verification passes (Phase 157F-03)
- [ ] `grep` for baked API keys returns zero matches (Phase 157F-05)
- [ ] **Zero** new user-visible config fields

---

## Notes

**Files created:** 1 (`Services/CinemetaDefaultRailProvider.cs`).
Optional second: `Services/UserContext.cs` if the `RequireUser` helper
lives in its own file instead of being added to `AdminGuard.cs`.

**Files modified:** ~6 (`DiscoverService.cs`, `CinemetaProvider.cs`,
`AdminGuard.cs`, `Plugin.cs` for DI, `configurationpage.js`,
`DatabaseManager.cs` for the rating-filter query helper)

**Files deleted:** 0

**Config fields added (user-visible):** 0
**Config fields removed (user-visible):** 0

**Risk: MEDIUM** — un-gating endpoints is the high-consequence step.
Mitigated by:
1. **Fail-closed parental rating guard** on every surface —
   Browse, Search, Detail, AddToLibrary, and default rails all reject
   unknown/over-ceiling items server-side.
2. `AddToLibrary` validates the ceiling a **second time** on the
   minted `catalog_items` row, after upsert.
3. `TestStreamResolution` and `DirectStreamUrl` stay admin-only so
   diagnostic surfaces can't leak stream URLs to non-admin users.
4. Cinemeta default rails fail gracefully — if Cinemeta is
   unreachable the rails are empty and the rest of Discover still
   works.

**No baked API keys.** Cinemeta catalog endpoints are keyless. This
is non-negotiable — the sprint fails if any key is introduced, even
"temporarily."

**Dependency on Sprint 156:** hard. `AddToLibrary` must call
`StrmWriterService.WriteAsync`, which is introduced in Sprint 156. Do
not start Sprint 157 until 156 is merged.

**Dependency on Sprint 155:** soft but strongly preferred.
`CooldownKind.Cinemeta` already exists in Sprint 155's `CooldownGate`.
If 155 slips, the Cinemeta fetch can temporarily inline a
`Task.Delay` that's replaced later.

**Elegance invariant:** zero new admin-visible config fields. Zero
new user-visible wizard steps. Zero baked API keys. Users load the
Discover tab and it just works — and it never leaks content above
their parental ceiling.

**Reference:** `docs/USER_DISCOVER.md` — full design context, the
hard-rule parental guard, the complete authorization matrix, and the
rationale for Cinemeta over TMDB / MDBList / YTS.
