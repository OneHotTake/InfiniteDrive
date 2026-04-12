# Sprint 209 — Content Ratings & Parental Controls for Discover

**Version:** v1.0 | **Status:** Under Review — not yet approved | **Risk:** MEDIUM | **Depends:** Sprint 207
**Owner:** Fullstack | **Target:** v0.50 | **PR:** TBD

---

## Overview

**Problem statement:** The Discover browse/search surface shows every catalog item regardless of parental rating. Users with parental controls enabled in Emby (e.g., max rating set to PG or PG-13) see R-rated, TV-MA, and unrated content alongside family-friendly titles. `ApplyParentalFilter` exists at DiscoverService.cs:1219 but is dead code — it returns items unchanged because `discover_catalog` has no certification column and no rating data source is wired. Admins have no way to configure parental behavior for the plugin.

**Why now:** Must ship before public release. Sprint 207 completed the per-user saves infrastructure. Sprint 204 un-gated Discover for all users. Parental filtering is the last safety gap. The user has decided that a TMDB API key (free, optional) and a server-wide toggle are acceptable for enabling this feature.

**High-level approach:** Add a `certification` column to `discover_catalog` and fetch MPAA/TV ratings from TMDB using an optional admin-configured API key. Implement real parental filtering that respects Emby's `User.Policy.MaxParentalRating` for rated items, and blocks unrated content for restricted users when a server toggle is enabled. Add admin UI for the TMDB key, toggle, and clear behavior matrix documentation.

### What the Research Found

**TMDB is the only viable rating source:**
| Source | Rating Field? | Notes |
|--------|--------------|-------|
| AIOStreams catalog + meta | `ageRating: null` (always) | Confirmed across 20+ items |
| Cinemeta | No certification field | Only `imdbRating` (1-10 scale) |
| OMDb | Has `Rated` field | Requires API key (excluded by user) |
| **TMDB** | Has `certification` in `/movie/{id}/release_dates` | **Requires API key — free, instant approval** |

**Emby SDK constraints (verified against 4.10 documentation):**
- `ISearchableChannel` is **OBSOLETE** — cannot be used for channel search
- `InternalChannelItemQuery` has **NO `SearchTerm` property** — channels cannot implement search
- Channels are browse-only; search must remain a REST endpoint (DiscoverService)
- `IProviderManager.GetRemoteSearchResults` does NOT return `OfficialRating` — cannot use for non-library items
- Channel items can have `OfficialRating` set, but this only affects channel-level filtering

**Why TMDB direct API is the right choice:**
- Emby's built-in TMDB key is **not accessible** to third-party plugins
- `DigitalReleaseGateService` already has the TMDB API pattern with caching
- The key is **optional** — without it, the feature degrades gracefully (no ratings, block-unrated still works)

### Breaking Changes

- **Schema V27:** New `certification TEXT` column on `discover_catalog` (nullable). Additive — no data loss.
- **No migration blocks.** Column is nullable; existing rows get `NULL` certification.
- **No behavior change without TMDB key.** The entire parental filter is a no-op if no key is configured.

### Non-Goals

- Channel search (IMPOSSIBLE in Emby 4.10 — ISearchableChannel obsolete, InternalChannelItemQuery has no SearchTerm)
- IProviderManager-based rating lookup (RemoteSearchResult lacks OfficialRating)
- Per-user parental overrides (server toggle only — simpler)
- Genre-based heuristic rating (unreliable)
- RPDB fallback (TMDB is sufficient; keep it simple)
- Blocking Add to Library (users can save anything; playback is controlled by Emby's native parental controls for library items)

---

## Phase A — Database Schema

### FIX-209A-01: Add `certification` column to `discover_catalog`

**File:** `Data/Schema.cs` (modify)
**Estimated effort:** XS
**What:**

Add `certification TEXT` column to the `discover_catalog` CREATE TABLE definition. Bump `CurrentSchemaVersion` from 26 to 27.

```sql
-- Add after imdb_rating column
certification TEXT,
```

### FIX-209A-02: Add V27 migration in DatabaseManager

**File:** `Data/DatabaseManager.cs` (modify)
**Estimated effort:** XS
**What:**

Add migration block in `Initialise()` for V27:

```csharp
if (version < 27)
{
    if (!ColumnExists(conn, "discover_catalog", "certification"))
        ExecuteInline(conn, "ALTER TABLE discover_catalog ADD COLUMN certification TEXT;");
    version = 27;
}
```

### FIX-209A-03: Update `ReadDiscoverCatalogEntry` and upsert to include `certification`

**File:** `Data/DatabaseManager.cs` (modify)
**Estimated effort:** S
**What:**

- Add `certification` to the SELECT column list in `ReadDiscoverCatalogEntry`
- Add `certification` to the UPSERT statement for `discover_catalog`
- Add `certification` parameter binding in the upsert method
- Add `Certification` property to `DiscoverCatalogEntry` model (nullable string)
- Add `UpdateDiscoverCertificationAsync(string imdbId, string certification, CancellationToken ct)` method

---

## Phase B — TMDB Configuration

### FIX-209B-01: Add `TmdbApiKey` and `BlockUnratedForRestricted` to PluginConfiguration

**File:** `PluginConfiguration.cs` (modify)
**Estimated effort:** XS
**What:**

```csharp
/// <summary>
/// TMDB API key for fetching content certifications (MPAA/TV ratings).
/// Free key from themoviedb.org → Settings → API.
/// Required for parental filtering. When empty, no ratings are fetched.
/// </summary>
public string TmdbApiKey { get; set; } = "";

/// <summary>
/// When enabled, users with parental restrictions (max rating < 999) will NOT
/// see content without known MPAA/TV ratings. Unrestricted users are never affected.
/// Default: enabled for safety.
/// </summary>
public bool BlockUnratedForRestricted { get; set; } = true;
```

---

## Phase C — Certification Resolver Service

### FIX-209C-01: Create `CertificationResolver` service

**File:** `Services/CertificationResolver.cs` (create)
**Estimated effort:** M
**What:**

New service that fetches US MPAA/TV certifications from TMDB for discover catalog items.

```csharp
public class CertificationResolver
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, (string? Cert, DateTimeOffset CachedAt)> _cache
        = new();

    /// <summary>
    /// Fetches US certification (MPAA rating) for a movie from TMDB.
    /// Returns null if no certification found or TMDB key not configured.
    /// </summary>
    public async Task<string?> FetchCertificationAsync(
        string tmdbId,
        CancellationToken ct)

    /// <summary>
    /// Batch-fetches certifications for multiple items.
    /// Respects TMDB rate limits (25ms delay between requests).
    /// Max 50 items per call to stay within free tier.
    /// Returns dictionary mapping IMDB ID to certification.
    /// </summary>
    public async Task<Dictionary<string, string>> FetchCertificationsBatchAsync(
        List<(string ImdbId, string? TmdbId)> items,
        CancellationToken ct)
}
```

**TMDB Endpoints:**
- Movies: `GET /movie/{tmdb_id}/release_dates` → extract US certification
  - Priority: type 3 (theatrical) > type 4 (digital) > type 1 (premiere) > any
- Series: `GET /tv/{tmdb_id}/content_ratings` → extract US rating
- IMDb→TMDB: `GET /find/{imdb_id}?external_source=imdb_id`

**Rate limiting:** 25ms delay between requests to respect TMDB free tier (40 req / 10 sec).

**Caching:** In-memory `ConcurrentDictionary` with 24h TTL.

**Returns:** `null` if no TMDB key configured or fetch fails (graceful degradation).

### FIX-209C-02: Register `CertificationResolver` in Plugin

**File:** `Plugin.cs` (modify)
**Estimated effort:** XS
**What:**

Instantiate and expose `CertificationResolver` as a singleton (follow existing pattern for `StrmWriterService`, `IdResolverService`, etc.):

```csharp
public CertificationResolver CertificationResolver { get; private set; }

// In constructor or initialization:
CertificationResolver = new CertificationResolver(
    new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
    _logger);
```

---

## Phase D — Wire Parental Filter

### FIX-209D-01: Implement `ApplyParentalFilter` in DiscoverService

**File:** `Services/DiscoverService.cs` (modify)
**Estimated effort:** S
**What:**

Replace the stub at line 1219 with real implementation:

```csharp
private List<DiscoverCatalogEntry> ApplyParentalFilter(
    List<DiscoverCatalogEntry> items,
    int maxRating,
    string? userId)
{
    // Unrestricted user: never block anything
    if (maxRating >= 999)
        return items;

    var config = Plugin.Instance?.Configuration;
    var blockUnrated = config?.BlockUnratedForRestricted ?? false;

    return items.Where(item =>
    {
        var itemRating = ParseRating(item.Certification);

        // Rated item: block if exceeds user's ceiling
        if (itemRating < 999)
        {
            return itemRating <= maxRating;
        }

        // Unrated item for restricted user: check server toggle
        if (itemRating >= 999)
        {
            return !blockUnrated; // Show only if NOT blocking unrated
        }

        return true;
    }).ToList();
}
```

**Wire into:**
- `Get(DiscoverBrowseRequest)` — after fetching paginated results (line ~338)
- `Get(DiscoverSearchRequest)` — after fetching search results (line ~410)
- `Get(DiscoverDetailRequest)` — check single item, return 403 if blocked (or just omit InLibrary flag)

### FIX-209D-02: Add `Certification` to `DiscoverItem` response DTO

**File:** `Services/DiscoverService.cs` (modify)
**Estimated effort:** XS
**What:**

Add `Certification` property to the `DiscoverItem` class so the frontend can display ratings.

```csharp
public class DiscoverItem
{
    // ... existing properties ...
    public string? Certification { get; set; }
}
```

Update `MapToDiscoverItem` to include `Certification` from the catalog entry.

---

## Phase E — Certification Sync Integration

### FIX-209E-01: Fetch certifications during Discover sync

**File:** `Services/CatalogDiscoverService.cs` (modify)
**Estimated effort:** S
**What:**

At the end of `SyncDiscoverCatalogAsync` (after all items are upserted, around line 103), add a certification pass:

```csharp
// Certification sync (if TMDB key configured)
var tmdbKey = Plugin.Instance?.Configuration?.TmdbApiKey;
if (!string.IsNullOrEmpty(tmdbKey))
{
    // Query items needing certification (limit 50 per sync run)
    var itemsNeedingCert = await _db.GetDiscoverCatalogNeedingCertificationAsync(50, ct);

    if (itemsNeedingCert.Count > 0)
    {
        var certs = await Plugin.Instance.CertificationResolver
            .FetchCertificationsBatchAsync(itemsNeedingCert, ct);

        foreach (var (imdbId, certification) in certs)
        {
            await _db.UpdateDiscoverCertificationAsync(imdbId, certification, ct);
        }
    }
}
```

Add `GetDiscoverCatalogNeedingCertificationAsync(int limit, ct)` to DatabaseManager.

---

## Phase F — Admin UI

### FIX-209F-01: Add Parental Controls section to configuration page

**File:** `Configuration/configurationpage.html` (modify)
**Estimated effort:** S
**What:**

Add a dedicated "Parental Controls" card with the behavior matrix:

```html
<div class="verticalSection">
    <h2 class="sectionTitle">Parental Controls</h2>

    <div class="inputContainer">
        <label class="inputLabel" for="txtTmdbApiKey">
            TMDB API Key <span class="es-required">*</span>
        </label>
        <input type="password" id="txtTmdbApiKey" placeholder="Your TMDB API key" required />
        <div class="fieldDescription">
            Required for MPAA/TV ratings. <a href="https://www.themoviedb.org/settings/api" target="_blank">Get free key at themoviedb.org → Settings → API</a>.
        </div>
    </div>

    <div class="inputContainer">
        <label class="inputLabel">Behavior Matrix</label>
        <div class="es-behavior-matrix">
            <table>
                <thead>
                    <tr>
                        <th>User Type</th>
                        <th>Item Has Rating</th>
                        <th>Block Unrated = OFF</th>
                        <th>Block Unrated = ON</th>
                    </tr>
                </thead>
                <tbody>
                    <tr>
                        <td>Unrestricted (no limit)</td>
                        <td>Any</td>
                        <td class="es-status-success">✅ Show</td>
                        <td class="es-status-success">✅ Show</td>
                    </tr>
                    <tr>
                        <td>Restricted (max PG)</td>
                        <td>G, PG</td>
                        <td class="es-status-success">✅ Show</td>
                        <td class="es-status-success">✅ Show</td>
                    </tr>
                    <tr>
                        <td>Restricted (max PG)</td>
                        <td>PG-13, R, TV-MA</td>
                        <td class="es-status-error">❌ Block</td>
                        <td class="es-status-error">❌ Block</td>
                    </tr>
                    <tr>
                        <td>Restricted (max PG)</td>
                        <td><strong>Unrated</strong></td>
                        <td class="es-status-success">✅ Show</td>
                        <td class="es-status-error">❌ Block</td>
                    </tr>
                </tbody>
            </table>
        </div>
    </div>

    <div class="inputContainer">
        <label class="es-toggle">
            <input type="checkbox" id="chkBlockUnrated" />
            <span>Block Unrated for Restricted Users</span>
        </label>
        <div class="fieldDescription">
            When enabled, users with parental restrictions (PG, PG-13, etc.) will NOT see content
            without known MPAA/TV ratings. Unrestricted users (no rating limit) are never affected.
        </div>
    </div>

    <div class="inputContainer">
        <label class="inputLabel">Filtering Status</label>
        <div class="es-filter-status" id="filterStatus">
            <span class="es-status-indicator es-status-inactive">⚠️</span>
            <span class="es-status-text">No TMDB key configured — parental filtering is inactive</span>
        </div>
    </div>
</div>
```

Add CSS for the matrix and status indicator.

### FIX-209F-02: Wire settings in configurationpage.js

**File:** `Configuration/configurationpage.js` (modify)
**Estimated effort:** XS
**What:**

- Load `TmdbApiKey` and `BlockUnratedForRestricted` in `populateSettings()`
- Save both in `saveSettings()`
- Update filter status indicator based on whether TMDB key is configured

---

## Phase G — Build & Verification

### FIX-209G-01: Build

**File:** Project root
**Estimated effort:** XS
**What:**

```
dotnet build -c Release
```

Expected: 0 errors, 0 net-new warnings.

### FIX-209G-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `certification` in Schema.cs | ≥1 | New column on discover_catalog |
| `CertificationResolver` | ≥2 | Class declaration + registration |
| `ApplyParentalFilter` callers in DiscoverService | ≥2 | Called from Browse + Search handlers |
| `BlockUnratedForRestricted` | ≥3 | PluginConfig + filter logic + UI |
| `TmdbApiKey` in PluginConfiguration | 1 | Config property |
| `certification IS NULL` | ≥1 | Query for items needing certification fetch |
| `User.Policy.MaxParentalRating` | ≥1 | Check in ApplyParentalFilter |

### FIX-209G-03: Manual test — no TMDB key

1. Deploy with no TMDB key configured
2. Browse Discover — all items visible (no filtering)
3. Check filter status indicator — shows "inactive"
4. Enable "Block Unrated" toggle — no change (need key for filtering)
5. Confirm no errors in log about missing TMDB key

### FIX-209G-04: Manual test — with TMDB key (unrestricted user)

1. Configure TMDB API key in Settings
2. Trigger discover sync (wait for certification batch fetch)
3. Query SQLite: `SELECT imdb_id, certification FROM discover_catalog WHERE certification IS NOT NULL LIMIT 10`
4. Expect certifications like "PG-13", "R", "TV-MA" for popular titles
5. Browse Discover as unrestricted user (no parental limit) — all items visible regardless of rating
6. Search and verify certification appears in results

### FIX-209G-05: Manual test — with TMDB key (restricted user)

1. Configure test user with MaxParentalRating = PG (300)
2. Browse Discover — should only show G, PG items
3. Search "horror" — R-rated items should be filtered out
4. Enable "Block Unrated" toggle — unrated items should now be hidden
5. Disable "Block Unrated" toggle — unrated items should now be visible
6. Confirm filtering behavior matches the behavior matrix

---

## Rollback Plan

- `git revert` the sprint commit.
- The `certification` column is nullable/additive — its presence doesn't break anything.
- If `TmdbApiKey` is empty, the entire certification pipeline is a no-op.
- `ApplyParentalFilter` with no certification data returns all items (same as current behavior).

---

## Completion Criteria

- [ ] `certification` column added to `discover_catalog` (V27 migration)
- [ ] `DiscoverCatalogEntry` model has `Certification` property
- [ ] `CertificationResolver` fetches US certifications from TMDB (batch and single)
- [ ] `TmdbApiKey` and `BlockUnratedForRestricted` in PluginConfiguration
- [ ] `ApplyParentalFilter` filters browse/search results by user's MaxParentalRating
- [ ] "Block Unrated for Restricted Users" toggle controls unrated content visibility for restricted users
- [ ] Unrestricted users (MaxParentalRating >= 999) are never affected by filtering
- [ ] Admin UI for TMDB key + toggle + behavior matrix
- [ ] Plugin works without TMDB key (no errors, no filtering, same as today)
- [ ] `dotnet build -c Release` — 0 errors, 0 warnings

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | None | — | — |

---

## Notes

**Files created:** 1 (`Services/CertificationResolver.cs`)

**Files modified:** 8
- `Data/Schema.cs`
- `Data/DatabaseManager.cs`
- `Models/DiscoverCatalogEntry.cs`
- `Services/DiscoverService.cs`
- `Services/CatalogDiscoverService.cs`
- `PluginConfiguration.cs`
- `Configuration/configurationpage.html`
- `Configuration/configurationpage.js`
- `Plugin.cs`

**Risk:** MEDIUM — additive schema change, optional feature, existing plumbing already in place.

**Mitigated by:**
1. Entire feature is a no-op without TMDB API key
2. Nullable column — no data loss on migration
3. Existing `ApplyParentalFilter`/`ParseRating` methods are already tested
4. Server toggle defaults to ON (safe default for families)
5. Unrestricted users are never affected (no regression for power users)
6. Emby's built-in parental controls continue to work independently for library items

---

## Design Decision: Behavior Matrix

The admin UI includes a clear behavior matrix to explain the filtering logic:

| User Type | Item Has Rating | Block Unrated = OFF | Block Unrated = ON |
|-----------|-------------|-------------------|------------------|
| Unrestricted (no limit) | Any | ✅ Show | ✅ Show |
| Restricted (max PG) | G, PG | ✅ Show | ✅ Show |
| Restricted (max PG) | PG-13, R, TV-MA | ❌ Block | ❌ Block |
| Restricted (max PG) | **Unrated** | ✅ Show | ❌ Block |

This makes it immediately clear that:
- **Unrestricted users are never affected** — the toggle only applies to users with parental limits
- **Rated items are always filtered** based on the user's Emby policy
- **Unrated items can be optionally blocked** for restricted users via the toggle

The filtering logic is a single source of truth in `ApplyParentalFilter`, called from Browse, Search, and Detail handlers.
