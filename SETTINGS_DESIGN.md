# InfiniteDrive Settings Design (Native PluginUI – 5 Tabs)

> **Sprint 502 backend plumbing:** All tab properties exist in PluginConfiguration.cs. Marvin-on-save hook live in SettingsController.cs.

**Post-Sprint 500 design** – Apple-simple, aggressive pruning, success-state first.

## Tab Order
1. Setup
2. Catalogs & Lists
3. Content Controls
4. Sync & Marvin
5. Advanced (behind "Show advanced settings" toggle)

**Global behavior**: After ANY save on ANY tab, `MarvinTask.TriggerFullRun()` is automatically called (implemented in Sprint 502).

*(Full detailed spec of every field, section, help text, defaults, and UI controls will be added in Sprint 503–507. This file will be updated at the end of those sprints.)*

## Tab 3: Content Controls — Detailed Layout (Sprint 505)

### Section 1: Quality & Resolution Preferences

**Preferred Quality Tiers** (PreferredQualityTiers)
- Multi-select checkboxes — exactly these 8 items, all checked by default:
  - 4K REMUX / HDR / Atmos
  - 4K 5.1 / DTS
  - 4K (any)
  - 1080p Atmos / TrueHD
  - 1080p 5.1
  - 1080p (any)
  - 720p
  - SD / Unknown / Low-bandwidth

**Default Quality Tier** (DefaultQualityTier)
- Single dropdown (required). Default value: "1080p (any)"

### Section 2: Parental Controls (Discover-only)

**Hide unrated content** (HideUnratedContent) — yes/no checkbox
- Help text: "Only affects InfiniteDrive Discover / search / browse results. Emby native library restrictions still apply for persisted items."

### Section 3: Blocked Content Management

Dynamic table using GenericItemList:
- **Columns:** Title | ID (TMDB/IMDB) | Reason | Blocked By | Blocked Since | Unblock
- **Add to Block List** button (opens a simple search modal: title or ID)

### Footer (on every tab)
- Small gray text in bottom-right corner:
  *Don't Panic* — Marvin is on the case.
  (Click → tooltip: "42 is the answer, but we batch at 42 anyway.")

### Required Behavior
- After any save on this tab → MarvinTask.TriggerFullRun() fires automatically.
- Virtual item providers (CatalogDiscoverService, GetMediaItems, etc.) filter using HideUnratedContent and the blocked list (implemented in Sprint 502).

## Tab 1: Setup — Detailed Layout (Sprint 503)

### Status Banner
- Green: "Ready — Primary manifest configured"
- Yellow: "Degraded — Primary manifest unreachable, using backup"
- Red: "Not Ready — configure required fields"
- Button: `Run Full Setup Test`

### Section 1: AIOStreams Providers
- Primary Manifest URL (PrimaryManifestUrl) + Test button
- Secondary Manifest URL (SecondaryManifestUrl) + Test button

### Section 2: Emby Server
- External Base URL (EmbyBaseUrl) — must be externally reachable

### Section 3: Library Mappings
- Movies: name + path
- Series: name + path
- Anime: name + path (always enabled)

### Section 4: Metadata Defaults
- Metadata Language, Certification Country, Default Subtitle Language

### Section 5: Default Quality
- Default Quality Tier (dropdown, 8 options)

## Tab 2: Catalogs & Lists — Detailed Layout (Sprint 504)

### Section 1: AIOStreams System Catalogs
- Dynamic `GenericItemList` table (reuse existing catalog loading code):
  **Columns:** Catalog Name | Media Type | Source Manifest (Primary / SecondaryManifestUrl) | Last Synced | Item Count | Status
- Per-catalog toggle (enable/disable – already exists)
- Buttons: `Refresh All Catalogs Now` (lightweight) + per-row "Refresh Now"
- **Catalog Sync Interval (hours)** (bind to existing `CatalogSyncIntervalHours`)

### Section 2: List Provider API Keys
- **Trakt Client ID**
- **TMDB API Key**
  Help text: "Used for all system-wide and user lists. Public RSS feeds work without keys."

### Section 3: System-Wide Lists
- Dynamic table: Name | Provider (Trakt / MDBList / AniList) | URL | Last Synced | Item Count | Status
- **Add New List** button (modal: Name + URL; provider auto-detected)
- Per-row: Edit / Remove / Refresh Now

### Section 4: User Lists
- Same columns as System-Wide Lists
- **Max Lists Per User** (new property if not present; default 10)
  Help text: "Each user list automatically creates a native Emby playlist."
- Help text: "User lists are managed by individual users via the Discover page. This is a read-only summary for admins."

### Status Banner
- Green: "Ready — Primary manifest configured"
- Yellow: "Degraded — Primary manifest unreachable, using backup"
- Red: "Not Ready — configure required fields"
- Button: `Run Full Setup Test`

### Section 1: AIOStreams Providers
- Primary Manifest URL (PrimaryManifestUrl) + Test button
- Secondary Manifest URL (SecondaryManifestUrl) + Test button

### Section 2: Emby Server
- External Base URL (EmbyBaseUrl) — must be externally reachable

### Section 3: Library Mappings
- Movies: name + path
- Series: name + path
- Anime: name + path (always enabled)

### Section 4: Metadata Defaults
- Metadata Language, Certification Country, Default Subtitle Language

### Section 5: Default Quality
- Default Quality Tier (dropdown, 8 options)
