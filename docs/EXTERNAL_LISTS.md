# Zero-Friction External List Import

## Context

InfiniteDrive currently only accepts RSS feed URLs from Trakt/MDBList. Users must find obscure RSS URLs (`rss.trakt.tv/users/...`), which is high friction. The goal: user pastes a normal browser URL and it "just works." Two audiences: admins (server-wide lists) and regular users (personal lists). Design philosophy: **Apple-simple.**

## Provider Matrix

| Provider | Admin key needed? | Visible to users when... | Returns IMDb IDs? | URL examples |
|----------|-------------------|--------------------------|-------------------|-------------|
| **MDBList** | None | Always visible | Yes, directly | `mdblist.com/lists/user/slug` |
| **Trakt** | Trakt Client ID | `TraktClientId` is set | Yes, directly | `trakt.tv/users/X/lists/Y` |
| **TMDB** | TMDB API Key | `TmdbApiKey` is set | Yes, via extra API calls | `themoviedb.org/list/12345` |
| **AniList** | None (own API), but needs Trakt OR TMDB for IMDb resolution | Always visible (warns if no resolution key) | No -- needs Trakt/TMDB search to resolve | `anilist.co/user/X/animelist` |

**Key insight:** Providers are gated by admin keys. No key = provider doesn't appear in the user's dropdown. MDBList is always available. AniList is always available but warns if no resolution key exists (Trakt or TMDB).

**No generic RSS fallback.** Remove the RSS path entirely. Only the four supported providers above.

## Design Decisions

1. **Trakt auth:** Admin enters Trakt Client ID once in plugin settings. Users never see it.
2. **TMDB auth:** Admin enters TMDB API Key once. Users never see it.
3. **Admin list limit:** No limit. Admins are trusted.
4. **User list limit:** Admin-configurable via `UserCatalogLimit` (default 5).
5. **Storage:** Same `user_catalogs` table for admin and user lists. Admin = `owner_user_id = "SERVER"`.
6. **No RSS:** Remove RSS feed support entirely. Only the four supported providers.
7. **Key-gated providers:** User's "List Type" dropdown only shows providers where admin has configured the required key. MDBList always shown (no key needed).
8. **Immediate feedback:** On save, fetch the list right away. Show clear error if URL is invalid or list is empty/inaccessible.

## Architecture

### 1. PluginConfiguration

File: `PluginConfiguration.cs`

```
TraktClientId     (string, default "")
TmdbApiKey        (string, default "")
UserCatalogLimit  (int, default 5)
```

### 2. ListFetcher Service

File: **NEW** `Services/ListFetcher.cs`

URL-sniffing dispatcher. Given a list URL and available keys, fetches items and returns normalized results.

```
class ListFetcher
  FetchAsync(listUrl, config, ct) -> ListFetchResult
    1. DetectProvider(url) -> "mdblist" | "trakt" | "tmdb" | "anilist"
    2. GetEnabledProviders(config) -> which providers have keys
    3. If detected provider requires a key and none set -> error immediately
    4. Dispatch to FetchMdblist / FetchTrakt / FetchTmdb / FetchAnilist
    5. Return ListFetchResult { Items, DisplayName, Error }

  GetEnabledProviders(config) -> List<string>
    - "mdblist"  -> always
    - "anilist"  -> always (but check resolution deps)
    - "trakt"    -> config.TraktClientId is not empty
    - "tmdb"     -> config.TmdbApiKey is not empty
```

Provider fetchers modeled after HomeScreenCompanion (`../research-lists/HomeScreenCompanion/ListFetcher.cs`):

- **FetchMdblist**: Append `/json`, paginate offset/1000, extract imdb_id. No key needed.
- **FetchTrakt**: Normalize URL path, append `/items`, hit api.trakt.tv with trakt-api-key header. Needs TraktClientId.
- **FetchTmdb**: Parse URL for list/collection/popular patterns, resolve TMDB IDs to IMDb via external_ids. Needs TmdbApiKey.
- **FetchAnilist**: GraphQL query to graphql.anilist.co, resolve titles to IMDb via Trakt/TMDB search. Needs Trakt or TMDB key.

### 3. Model & DB

**Models/UserCatalog.cs:**
- `RssUrl` column stays for migration compat, code treats as `ListUrl`
- `SourceType`: add `"external_list"`
- `Service`: expand to `"trakt" | "mdblist" | "tmdb" | "anilist"`

**Data/DatabaseManager.cs** -- V31 migration:
- Rebuild `user_catalogs` with relaxed CHECK constraints
- Migrate existing rows: `SET source_type = 'external_list'`

### 4. API Endpoints

**Admin** (new `Services/Api/AdminListEndpoints.cs`):
- `GET /InfiniteDrive/Admin/Lists`
- `POST /InfiniteDrive/Admin/Lists/Add` -- `{ provider, displayName, listUrl }`
- `POST /InfiniteDrive/Admin/Lists/Remove`
- `POST /InfiniteDrive/Admin/Lists/Refresh`
- `GET /InfiniteDrive/Admin/Lists/Providers`

**User** (modify `Services/UserCatalogsService.cs`):
- `GET /InfiniteDrive/User/Catalogs` -- returns `provider` + `listUrl`
- `POST /InfiniteDrive/User/Catalogs/Add` -- `{ provider, displayName, listUrl }`
- `GET /InfiniteDrive/User/Catalogs/Providers` -- enabled providers + count/limit
- `POST /InfiniteDrive/User/Catalogs/Remove`
- `POST /InfiniteDrive/User/Catalogs/Refresh`
- `GET /InfiniteDrive/User/Catalogs/{id}/Items` -- items with poster + resolution

### 5. UI -- Two Pages, Two Audiences

**Admin** -> `Configuration/configurationpage.html` + `configurationpage.js`
- API keys section (Trakt Client ID, TMDB API Key)
- Server-wide list management (unlimited)
- Per-user list limit setting

**Users** -> `Configuration/discoverpage.html` + `Configuration/discoverpage.js`
- "My Lists" tab (already exists) with provider dropdown
- Add List modal with immediate validation feedback
- List detail view with item cards (poster, resolution status)

### 6. Immediate Feedback on Save

1. Frontend sends `{ provider, displayName, listUrl }`
2. Backend calls `ListFetcher.FetchAsync()` synchronously
3. If fetch fails or 0 items -> error, nothing saved
4. If success -> save catalog, return item count
5. Frontend shows success/error

Human-friendly error messages:
- "This Trakt list couldn't be found. Check the URL and try again."
- "This MDBList is empty or private."
- "AniList user 'xyz' not found."
- "Trakt isn't configured. Ask your admin to set up a Trakt Client ID."

## Modified Files

| File | Change |
|------|--------|
| **NEW** `Services/ListFetcher.cs` | URL-sniffing dispatcher + 4 provider fetchers |
| **NEW** `Services/Api/AdminListEndpoints.cs` | Admin CRUD for server-wide lists |
| `PluginConfiguration.cs` | Add `TraktClientId`, `TmdbApiKey`, `UserCatalogLimit` |
| `Models/UserCatalog.cs` | Add `ListUrl`, expand `Service` and `SourceType` options |
| `Data/DatabaseManager.cs` | V31 migration: relax CHECK constraints, migrate existing rows |
| `Services/UserCatalogsService.cs` | New DTOs, config-driven limit, Providers + Items endpoints |
| `Services/UserCatalogSyncService.cs` | Replace RSS fetch with `ListFetcher.FetchAsync()` dispatch |
| `Services/RssFeedParser.cs` | Remove or mark obsolete |
| `Configuration/configurationpage.html` | API keys section, admin lists section |
| `Configuration/configurationpage.js` | Admin list CRUD, provider key management |
| `Configuration/discoverpage.html` | Provider dropdown in Add List modal, list detail view, item cards |
| `Configuration/discoverpage.js` | Fetch enabled providers, list CRUD, item browsing |

## Implementation Order

1. PluginConfiguration -- 3 new fields
2. ListFetcher.cs -- dispatcher + MDBList + Trakt
3. DB migration V31
4. UserCatalogSyncService -- switch to ListFetcher
5. UserCatalogsService -- new DTOs, Providers + Items endpoints
6. AdminListEndpoints -- admin CRUD
7. ListFetcher additions -- TMDB + AniList fetchers
8. Admin config UI (configurationpage.html/js)
9. Discover UI updates (discoverpage.html/js)

Steps 1-5 are backend (testable via API). Steps 6-9 are UI.

## Reference Projects

- `../research-lists/HomeScreenCompanion/` -- Emby plugin with complete URL-sniffing list fetcher
- `../research-lists/jellyfin-plugin-collection-import/` -- Simple MDBList JSON import
- `../research-lists/list-sync/` -- Provider registry pattern, AniList GraphQL provider
