# Sprint 211 — Reconciliation: Fix Wizard, Kill Dead Channel, Make Discover Visible

**Version:** v1.0 | **Status:** Under Review | **Risk:** MEDIUM | **Depends:** Sprint 210
**Owner:** Fullstack | **Target:** v0.41 | **PR:** TBD

---

## Overview

### Problem Statement

The plugin is unusable in its current state. Three critical bugs prevent basic functionality:

1. **Wizard catalogs fail on step 3** — entering step 3 shows "AIOStreams manifest URL not configured" even though the URL was entered on step 1
2. **Libraries are never created** — `finishWizard()` calls `ProvisionLibraries` but libraries don't appear in Emby
3. **Users see a dead channel** — `InfiniteDriveChannel` (IChannel) auto-registers with Emby, showing two useless folders (Lists, Saved) to every user

Additionally, the Discover page exists but is invisible to users (no navigation entry point), and Doctor/DeepClean naming debris persists in source files.

### Why Now

Sprints 202-210 accumulated implementation debt. The user tested the plugin end-to-end and found it broken. All previous sprints assumed working foundations that don't actually work.

### High-Level Approach

1. Fix the two root-cause bugs (catalog loading + library creation)
2. Delete the dead `InfiniteDriveChannel`
3. Make Discover page visible to all users via main menu
4. Clean up Doctor/DeepClean naming

---

## What the Research Found

### Bug 1: Catalogs Fail on Step 3 — Root Cause Confirmed

**JS side** (`configurationpage.js:1486-1487`): `loadCatalogs()` correctly reads the live manifest URL from the input field and passes it as a query parameter:
```javascript
var catalogsUrl = '/InfiniteDrive/Catalogs' +
    (liveManifestUrl ? '?manifestUrl=' + encodeURIComponent(liveManifestUrl) : '');
```

**Server side** (`StatusService.cs:907-968`): `CatalogsRequest` DTO has NO `ManifestUrl` property. ServiceStack never binds the query parameter. The handler reads only from saved config:
```csharp
if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
    && string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
    return new CatalogsResponse { Error = "AIOStreams manifest URL not configured" };
```

During the wizard, config hasn't been saved yet → `config.PrimaryManifestUrl` is empty → error.

**Fix:** Add `ManifestUrl` property to `CatalogsRequest` DTO. When provided, use it as the manifest URL instead of reading from config.

### Bug 2: Libraries Not Created — Root Cause Confirmed

**JS side** (`configurationpage.js:2215`): The wizard saves `SyncPathBase` but `PluginConfiguration` has NO `SyncPathBase` property. It has `SyncPathMovies`, `SyncPathShows`, `SyncPathAnime` as separate paths. The wizard never derives the individual paths from the base:

```javascript
var config = {
    SyncPathBase: esVal(view, 'wiz-base-path') || '/media/infinitedrive',  // NOT a config property!
    // ... but no SyncPathMovies, SyncPathShows, SyncPathAnime
};
```

**Server side** (`LibraryProvisioningService.cs:45-61`): Reads `config.SyncPathMovies` etc. which keep their defaults (`/media/infinitedrive/movies`). These defaults MAY work, but only if the user didn't change the base path. More critically, the `updatePluginConfiguration` → `ProvisionLibraries` chain may have a timing issue where `Plugin.Instance.Configuration` returns stale data.

**Fix:** Derive `SyncPathMovies`/`SyncPathShows`/`SyncPathAnime` from the base path in `finishWizard()` before saving config. Also ensure `ProvisionLibraries` re-reads config fresh.

### Bug 3: Dead Channel Auto-Discovery

`InfiniteDriveChannel` implements `IChannel`. Emby auto-discovers all `IChannel` implementations via reflection — no manual registration needed. Every user sees "InfiniteDrive" as a channel with two folders (Lists, Saved) that do nothing useful.

**Fix:** Delete `InfiniteDriveChannel.cs` entirely. Sprint 210 already marked it `[Obsolete]`.

### Bug 4: Discover Page Invisible

The Discover page IS registered in `Plugin.cs` with `EnableInMainMenu = false`. Users have no way to find it without knowing the direct URL.

**Fix:** Set `EnableInMainMenu = true` and `DisplayName = "Discover"`.

### Additional Findings

**Doctor/DeepClean naming debris (Sprint 202 incomplete):**
- `FileResurrectionTask.cs:21` — "Use the Doctor task instead" comment
- `EpisodeExpandTask.cs:26` — "Use the Doctor task instead" comment
- `TriggerService.cs:105` — "Doctor task removed in Sprint 147" comment
- `StatusService.cs:197/200/212` — `DeepCleanHasRun`, `DeepCleanLastRunAt`, `DeepCleanHealth` DTO properties
- `StatusService.cs:611-637` — Reads `last_deepclean_run_time` metadata, computes health status

**Wizard "only creates libraries on first run":** The wizard sets `IsFirstRunComplete = true` and hides itself. If libraries fail to create on first run, there's no recovery path. The Settings tab "Save & Sync" button doesn't call `ProvisionLibraries`.

---

## Breaking Changes

- **Removing `InfiniteDriveChannel`** — The "InfiniteDrive" channel disappears from Emby's channel list. Any users who bookmarked items inside it will lose those bookmarks (they were read-only anyway).
- **Renaming `DeepClean*` DTO properties** in `StatusService` — If any external tool consumes the `/InfiniteDrive/Status` endpoint, property names change. Unlikely for a pre-release plugin.

---

## Non-Goals

- ❌ Rewriting the entire admin UI — only fixing the two blocking bugs
- ❌ Adding new features — this sprint is purely about making existing features work
- ❌ Dead CSS cleanup from Sprint 208 — cosmetic, no functional impact
- ❌ Overview tab "no sources" state — cosmetic, not blocking

---

## Phase A — Kill the Dead Channel

### FIX-211A-01: Delete InfiniteDriveChannel.cs

**File:** `Services/InfiniteDriveChannel.cs` (delete)
**Estimated effort:** S
**What:**
Delete the entire file. IChannel auto-discovery will stop registering it. Users will no longer see a useless "InfiniteDrive" channel.

> ⚠️ **Watch out:** Verify no other code references this class. Since it uses IChannel auto-discovery, there should be no manual registration in Plugin.cs.

---

### FIX-211A-02: Remove InfiniteDriveChannel.csproj reference (if any)

**File:** `InfiniteDrive.csproj`
**Estimated effort:** S
**What:**
Check if InfiniteDriveChannel.cs has a .csproj entry. If so, remove it. Standard SDK-style projects auto-include .cs files, so this is likely a no-op.

---

## Phase B — Fix Wizard Catalog Loading

### FIX-211B-01: Add ManifestUrl to CatalogsRequest DTO

**File:** `Services/StatusService.cs` (modify DTO at line ~907)
**Estimated effort:** S
**What:**

Add a `ManifestUrl` query parameter to `CatalogsRequest`:
```csharp
[Route("/InfiniteDrive/Catalogs", "GET",
    Summary = "Returns catalog definitions discovered from the AIOStreams manifest")]
public class CatalogsRequest : IReturn<object>
{
    [ApiMember(Name = "ManifestUrl", Description = "Override manifest URL (used by wizard before config is saved)",
        ParameterType = "query", DataType = "string")]
    public string? ManifestUrl { get; set; }
}
```

### FIX-211B-02: Use ManifestUrl fallback in CatalogService.Get()

**File:** `Services/StatusService.cs` (modify handler at line ~957)
**Estimated effort:** S
**What:**

Change the manifest URL resolution to use the query parameter when provided:
```csharp
public async Task<object> Get(CatalogsRequest request)
{
    var deny = AdminGuard.RequireAdmin(_authCtx, Request);
    if (deny != null) return deny;

    var config = Plugin.Instance?.Configuration;
    if (config == null)
        return new CatalogsResponse { Error = "Plugin not initialised" };

    // Prefer the live URL from the wizard, fall back to saved config
    var aioUrl = request.ManifestUrl
        ?? config.PrimaryManifestUrl
        ?? config.SecondaryManifestUrl;

    if (string.IsNullOrWhiteSpace(aioUrl))
        return new CatalogsResponse { Error = "AIOStreams manifest URL not configured" };

    var aioResult = await FetchCatalogsFromAddonAsync(aioUrl, null, null, null, "AIOStreams");
    // ...
}
```

> ⚠️ **Watch out:** The `request.ManifestUrl` is a raw URL from the browser input. `FetchCatalogsFromAddonAsync` already handles URL validation and HTTP errors, so no additional validation needed.

---

## Phase C — Fix Wizard Library Creation

### FIX-211C-01: Derive SyncPathMovies/Shows from SyncPathBase in finishWizard

**File:** `Configuration/configurationpage.js` (modify `finishWizard` function around line 2207)
**Estimated effort:** S
**What:**

Replace `SyncPathBase` with the three derived path properties:
```javascript
var basePath = esVal(view, 'wiz-base-path') || '/media/infinitedrive';
var config = {
    PrimaryManifestUrl: url,
    AioMetadataBaseUrl: esVal(view, 'wiz-aio-metadata-url') || '',
    EnableBackupAioStreams: esChk(view, 'wiz-enable-backup-aio'),
    SecondaryManifestUrl: esChk(view, 'wiz-enable-backup-aio')
                               ? esVal(view, 'wiz-aio-backup-url') || ''
                               : '',
    SystemRssFeedUrls: esVal(view, 'wiz-rss-feeds') || '',
    // Derive actual path properties that LibraryProvisioningService reads
    SyncPathMovies: basePath + '/movies',
    SyncPathShows: basePath + '/shows',
    SyncPathAnime: basePath + '/anime',
    LibraryNameMovies: esVal(view, 'wiz-library-name-movies') || 'Streamed Movies',
    LibraryNameSeries: esVal(view, 'wiz-library-name-series') || 'Streamed Series',
    LibraryNameAnime: esVal(view, 'wiz-library-name-anime') || 'Streamed Anime',
    EnableAnimeLibrary: esChk(view, 'wiz-enable-anime'),
    MetadataLanguage: esVal(view, 'wiz-meta-lang') || 'en',
    MetadataCountry: esVal(view, 'wiz-meta-country') || 'US',
    ImageLanguage: esVal(view, 'wiz-meta-img-lang') || 'en',
    EnableCinemetaCatalog: esChk(view, 'wiz-use-cinemeta'),
    EmbyBaseUrl: esVal(view, 'wiz-emby-base-url') || window.location.origin,
    IsFirstRunComplete: true
};
```

### FIX-211C-02: Add ProvisionLibraries to Settings save (non-wizard)

**File:** `Configuration/configurationpage.js` (modify save handler for Settings tab)
**Estimated effort:** S
**What:**

Find the Settings tab "Save" handler and add a call to `ProvisionLibraries` after config save, so libraries are created on every save, not just wizard completion:

```javascript
// After config save succeeds:
return esFetch('/InfiniteDrive/Setup/ProvisionLibraries', {method:'POST'});
```

This ensures libraries are always in sync with config changes.

### FIX-211C-03: Verify SetupService reads fresh config

**File:** `Services/SetupService.cs` (review line ~117)
**Estimated effort:** S
**What:**

Verify that `Plugin.Instance.Configuration` returns the latest saved config by the time `ProvisionLibraries` is called. The `updatePluginConfiguration` API call triggers `Plugin.UpdateConfiguration()` which sets `Plugin.Configuration` synchronously before the HTTP response is sent. Confirm this is the case by checking Emby's plugin lifecycle.

If there's a race condition, add a small delay or re-read the config from disk.

---

## Phase D — Make Discover Visible

### FIX-211D-01: Enable Discover page in main menu

**File:** `Plugin.cs` (modify `GetPages()`)
**Estimated effort:** S
**What:**

Change the InfiniteDiscover page registration to show in the main navigation:
```csharp
new PluginPageInfo
{
    Name = "InfiniteDiscover",
    EmbeddedResourcePath = "InfiniteDrive.Configuration.discoverpage.html",
    IsMainConfigPage = false,
    EnableInMainMenu = true,       // Changed from false
    DisplayName = "Discover"       // Already set, confirm
}
```

This puts "Discover" in Emby's main left sidebar for all logged-in users.

> ⚠️ **Watch out:** `EnableInMainMenu = true` makes the page visible to ALL users, not just admins. This is intentional — the Discover page is the user-facing UI for browsing and adding content.

---

## Phase E — Clean Up Doctor/DeepClean Naming

### FIX-211E-01: Update obsolete comments in task files

**File:** `Tasks/FileResurrectionTask.cs` (line 21), `Tasks/EpisodeExpandTask.cs` (line 26)
**Estimated effort:** S
**What:**
Replace "Use the Doctor task instead" with "Use MarvinTask instead" in the `[Obsolete]` attributes.

### FIX-211E-02: Update TriggerService comment

**File:** `Services/TriggerService.cs` (line 105)
**Estimated effort:** S
**What:**
Replace "Doctor task removed in Sprint 147" with "MarvinTask (formerly Doctor)".

### FIX-211E-03: Rename DeepClean DTO properties in StatusService

**File:** `Services/StatusService.cs` (lines 197, 200, 212, 611-637)
**Estimated effort:** M
**What:**

Rename the DTO properties and metadata key:
- `DeepCleanHasRun` → `MarvinHasRun`
- `DeepCleanLastRunAt` → `MarvinLastRunAt`
- `DeepCleanHealth` → `MarvinHealth`
- `last_deepclean_run_time` metadata key → `last_marvin_run_time` (metadata read)

Also update the admin UI JS that reads these properties from the status response.

> ⚠️ **Watch out:** The metadata key in the database stores `last_deepclean_run_time`. Need to read from BOTH old and new keys during transition, and write to the new key. Or simpler: just rename the DTO properties but keep reading the same metadata key.

---

## Phase F — Build & Verification

### FIX-211F-01: Build check

`dotnet build -c Release` — 0 errors, 0 net-new warnings.

### FIX-211F-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `Doctor` in *.cs files | 0 | All Doctor references removed |
| `DeepClean` in *.cs files | 0 | All DeepClean references renamed |
| `InfiniteDriveChannel` | 0 | File deleted |
| `EnableInMainMenu.*true` in Plugin.cs | 1 | Discover page in main menu |
| `ManifestUrl` in CatalogsRequest | 1 | New query param for wizard |

### FIX-211F-03: Manual test — Clean install wizard

1. `./emby-reset.sh` — clean state
2. Start server, open Chromium
3. Login as admin, navigate to InfiniteDrive plugin
4. **Step 1:** Enter AIOStreams manifest URL, click Test Connection → should succeed
5. **Step 2:** Configure paths and library names, click Next
6. **Step 3:** Catalogs should load (NOT show "manifest URL not configured")
7. Select catalogs, click Finish & Sync
8. Verify progress bar animates
9. Verify completion screen shows stats
10. Navigate to Emby Dashboard → Libraries → verify "Streamed Movies" and "Streamed Series" libraries exist
11. Check `/media/infinitedrive/movies` directory exists and contains `.strm` files (after sync completes)

### FIX-211F-04: Manual test — Discover page

1. Login as regular user (non-admin)
2. Verify "Discover" appears in main left navigation
3. Click "Discover" → page loads with three tabs
4. Browse tab shows catalog items with posters
5. Search for a known movie title → results appear
6. Click an item → detail modal opens
7. Click "Add to Library" → success toast, "In Library" badge appears
8. Switch to "My Picks" tab → item appears
9. Click remove → item removed

### FIX-211F-05: Manual test — No dead channel

1. Login as any user
2. Navigate to Emby's Channels section
3. Verify "InfiniteDrive" channel does NOT appear
4. Only Emby's built-in channels should be visible

### FIX-211F-06: Manual test — Settings tab save creates libraries

1. Login as admin, navigate to InfiniteDrive plugin
2. Go to Settings tab
3. Change a library name
4. Click Save
5. Verify libraries are re-provisioned (check Emby Dashboard → Libraries)

---

## Rollback Plan

1. **InfiniteDriveChannel deletion:** Restore from git (`git checkout HEAD -- Services/InfiniteDriveChannel.cs`)
2. **Wizard fixes:** Revert JS changes to `finishWizard()` and `loadCatalogs()`
3. **CatalogsRequest DTO:** Remove `ManifestUrl` property, revert handler
4. **All changes:** `git revert HEAD` (single commit planned)

No schema changes in this sprint — rollback is clean.

---

## Completion Criteria

- [ ] Wizard step 3 loads catalogs successfully (no "manifest URL not configured" error)
- [ ] Wizard creates Emby libraries on completion (verified in Dashboard → Libraries)
- [ ] Settings tab save also creates/recreates libraries
- [ ] InfiniteDriveChannel.cs deleted, no dead channel in user UI
- [ ] Discover page appears in Emby main navigation for all users
- [ ] All Doctor/DeepClean references removed from source files
- [ ] `dotnet build -c Release` — 0 errors, 0 warnings
- [ ] All manual tests pass in Chromium

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | Does `updatePluginConfiguration` persist synchronously before the HTTP response? If async, `ProvisionLibraries` may read stale config. | Backend | Open — verify in testing |
| 2 | Should `DeepClean*` metadata keys in SQLite be migrated to `Marvin*`? Or just rename DTO and keep reading old keys? | Backend | Open — recommend keeping old keys for simplicity |
| 3 | Does Emby's `EnableInMainMenu` show the page icon/text for all users or only admins? Need to verify with non-admin user. | Frontend | Open — verify in testing |

---

## Notes

**Files created:** 0
**Files modified:** 6 (`Plugin.cs`, `configurationpage.js`, `StatusService.cs`, `FileResurrectionTask.cs`, `EpisodeExpandTask.cs`, `TriggerService.cs`)
**Files deleted:** 1 (`Services/InfiniteDriveChannel.cs`)

**Risk:** MEDIUM — wizard is the first-run experience; breaking it further would be catastrophic. Mitigated by clean-state Chromium testing before commit.

Mitigated by:
1. All changes are reversible (single commit revert)
2. No schema changes
3. CatalogService fix is additive (new optional parameter, existing callers unaffected)
4. Library creation is idempotent (safe to call repeatedly)
5. Channel deletion only removes a dead UI element
