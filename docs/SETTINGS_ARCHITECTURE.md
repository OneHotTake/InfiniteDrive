# Settings Page UX Architecture

## Overview

The InfiniteDrive admin settings page is a single HTML file + single JS file loaded by Emby's ServiceStack plugin framework. The redesign distributes health inline per tab, uses a uniform 3-button action system, and reorganizes tabs for logical flow.

## New Tab Order

**Overview → Providers → Libraries → Lists → Sources → Content Filtering → Security → Inspector**

Old tabs **Health** and **Repair** are removed. Their content is redistributed: health goes inline per tab, debug/repair tools go to Inspector.

---

## Per-Tab Architecture

### Overview (NEW)

Landing page with setup checklist and system status. Data source: `GET /InfiniteDrive/Status`.

**Setup checklist** (3 steps with tab links):
1. Connect a Provider → Providers tab
2. Set up Libraries → Libraries tab
3. Configure Content and Lists → Lists tab

**Sync status card**: Last sync time, catalog item count, resolution coverage %, AIOStreams online/offline.

**Quick health grid** (3 columns): Provider health, Library health, Content Filtering status.

**Actions**: Refresh only.

### Providers

Manifest configuration. Inline health shows primary/secondary manifest status.

**Cards**: AIOStreams Primary (required), AIOStreams Backup, Cinemeta fallback.

**Actions**: Save, Refresh.

### Libraries

One-shot configuration. Paths lock after first save (grey out + note).

**Cards**: Library Locations (base path + derived paths), Emby Server URL, Stream Versions (quality tiers), Metadata Preferences.

**Inline health**: "Libraries created" check, path writable check.

**Actions**: Save, Refresh.

### Lists (moved before Sources)

External list provider keys and server-wide lists. Description explains key requirements per provider.

**Cards**: List Provider Keys (Trakt Client ID, The Movie Database API Key, per-user limit), Server Lists (CRUD with inline add form).

**Friendly names**: "The Movie Database" not "TMDB". Links to Trakt/TMDB API pages.

**User catalog limit**: 0 = disabled (hides My Lists tab on Discover). Warns on reduction.

**Actions**: Save, Sync, Refresh.

### Sources

All enabled sources across providers. Replaces inaccurate RSS warning.

**Table columns**: (checkbox), Source, Provider (friendly name + type badge), Limit.
Removed: Progress column (was always blank).

**Actions**: Sync, Refresh.

### Content Filtering (renamed from "Parental")

Controls what appears on the user Discover page. Explicit about scope: Discover only, not library-level (Emby handles that).

**Cards**: Description, TMDB status (enabled/disabled inline health), Behavior Matrix, Blocked Content search.

**Actions**: Save, Refresh.

### Security

Minimal. Shows when secret was generated (not "last rotated"). Rotation de-emphasized under Advanced.

**Cards**: Storage warning, Playback Security (generated date + collapsed rotation).

**Actions**: Refresh only.

### Inspector (NEW, replaces Health debug + Repair)

Debug tools and admin actions.

**Sections**:
1. Source Inspector — dropdown to select source, item table with sortable columns, block/unblock, deep dive
2. Debug Tools (collapsed) — item inspector, raw AIOStreams, resolution coverage, cache stats, client intelligence, playback logs
3. Danger Zone (collapsed) — force sync, pre-warm cache, reset, purge, total existence failure

**Actions**: Refresh.

---

## Floating Action Buttons

Fixed bottom-right bar with 3 buttons:

| Button | Purpose | Triggers |
|--------|---------|----------|
| **Save** | Save current tab settings | Tab-specific save function |
| **Sync** | Fetch items from sources | `POST /InfiniteDrive/Trigger?task=catalog_sync` |
| **Refresh** | Reconcile/repair | Marvin via ScheduledTasks API |

Per-tab visibility:

| Tab | Save | Sync | Refresh |
|-----|------|------|---------|
| Overview | - | - | yes |
| Providers | yes | - | yes |
| Libraries | yes | - | yes |
| Lists | yes | yes | yes |
| Sources | - | yes | yes |
| Content Filtering | yes | - | yes |
| Security | - | - | yes |
| Inspector | - | - | yes |

**Guard rails**: Before Sync/Refresh, check prerequisites (providers connected, libraries configured). Show calm guidance with tab links if not met.

---

## Inline Health Pattern

Each tab has a `<div class="es-inline-health">` at the top showing contextual status:

```css
.es-inline-health {
  display: flex; align-items: center; gap: 1em;
  padding: .6em 1em; margin-bottom: 1em; border-radius: 8px;
  font-size: .85em; background: rgba(128,128,128,0.06);
}
```

Status dots: green (#28a745), amber (#ffc107), red (#dc3545).

Data source: single `GET /InfiniteDrive/Status` call, cached per page load.

---

## Discover Page Integration

### My Lists Tab Visibility

Controlled by `UserCatalogLimit` from `GET /InfiniteDrive/User/Catalogs/Providers`:
- **Limit = 0**: Tab button hidden entirely, redirect to Discover if somehow navigated
- **At limit**: "Add List" disabled, message "Remove an existing list to add a new one"
- **Over limit** (admin reduced): Warning banner, existing lists kept, no new adds

---

## Backend Endpoints Used

| Endpoint | Used By |
|----------|---------|
| `GET /InfiniteDrive/Status` | Overview, inline health on all tabs |
| `POST /InfiniteDrive/Status/Refresh` | Refresh button |
| `POST /InfiniteDrive/Trigger?task=catalog_sync` | Sync button |
| `POST /InfiniteDrive/Trigger?task=link_resolver` | Inspector |
| `GET /InfiniteDrive/Admin/Lists` | Lists tab |
| `POST /InfiniteDrive/Admin/Lists/Add` | Lists tab |
| `POST /InfiniteDrive/Admin/Lists/Remove` | Lists tab |
| `POST /InfiniteDrive/Admin/Lists/Refresh` | Lists tab |
| `GET /InfiniteDrive/Admin/Lists/Providers` | Lists tab |
| `GET /InfiniteDrive/Admin/Lists/UserCount` | Lists tab (limit reduction warning) |
| `GET /InfiniteDrive/User/Catalogs` | Discover My Lists |
| `GET /InfiniteDrive/User/Catalogs/Providers` | Discover (tab visibility) |
| Emby ScheduledTasks API | Marvin trigger |
| `GET /InfiniteDrive/Catalogs` | Sources tab |
| `GET /InfiniteDrive/Admin/VersionSlots` | Libraries tab |
| `POST /InfiniteDrive/Admin/VersionSlots/Toggle` | Libraries tab |
| `POST /InfiniteDrive/Invalidate` | Inspector |
| `POST /InfiniteDrive/Validate` | Setup validation |

---

## Key Files

| File | Role |
|------|------|
| `Configuration/configurationpage.html` | Admin settings HTML (~1100 lines) |
| `Configuration/configurationpage.js` | Admin settings JS (~3300 lines) |
| `Configuration/discoverpage.html` | User Discover page HTML |
| `Configuration/discoverpage.js` | User Discover page JS |
| `PluginConfiguration.cs` | Plugin config (UserCatalogLimit clamp) |
| `Services/StatusService.cs` | Status API (DisplayName in SyncStateEntry) |
| `Services/Api/AdminListEndpoints.cs` | Admin list endpoints (UserCount) |
| `Services/UserCatalogsService.cs` | User catalog endpoints (limit enforcement) |
