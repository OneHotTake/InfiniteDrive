# InfiniteDrive Settings Design (Native PluginUI — 7 Tabs)

**Design philosophy**: Apple-simple, aggressive pruning, success-state first.

## Setup Sequencing Rationale

Tab order encodes intent-before-action. Marvin starts syncing the moment Providers are saved.
If Quality is configured *after* Providers, everything Marvin just synced may be wrong and
needs to be re-synced. The correct sequence is:

1. **Where** things go (Libraries)
2. **What** you want (Quality)
3. **Where** to get them (Providers)

Connecting a source last ensures Marvin's first sync run already knows the destination and
the quality intent — no wasted work, no immediate re-sync.

## Final Tab Order

0. **Overview** — Setup guidance + status for Libraries / Quality / Providers
1. **Libraries** — Folder paths, library names, metadata language/country/subtitle defaults
2. **Quality** — Version buckets (resolution + audio), remux preference, edition priority
3. **Providers** — Primary + secondary AIOStreams manifest URLs, test + dashboard links
4. **Sources** — Catalog enable/disable, list provider API keys, system lists, user lists
5. **Restrictions** — Parental controls, hide-unrated, blocked content
6. **Marvin** — Sync schedule, batch size, rate-limit ceiling, pruning rules
7. **Advanced** — Logging, cache clear, maintenance, factory reset

**Global behavior**: After ANY save on ANY tab, `TriggerBackgroundSync()` is called.

**Footer (on every tab)**: *Don't Panic* — Marvin is on the case.

---

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
- Dynamic `GenericItemList` table:
  **Columns:** Catalog Name | Media Type | Source Manifest (Primary / SecondaryManifestUrl) | Last Synced | Item Count | Status
- Per-catalog toggle (enable/disable)
- Buttons: `Refresh All Catalogs Now` + per-row "Refresh Now"
- **Catalog Sync Interval (hours)** (CatalogSyncIntervalHours)

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
- **Max Lists Per User** (default 10)
  Help text: "Each user list automatically creates a native Emby playlist."

## Tab 3: Content Controls — Detailed Layout (Sprint 505)

### Section 1: Quality & Resolution Preferences

**Per-Tier Source Limits** (dropdowns, 0–5)
- Six dropdowns controlling max streams per quality tier (default: 2 each):
  - 4K 5.1 / DTS (`MaxStreams4k51`)
  - 4K (any) (`MaxStreams4kAny`)
  - 1080p 5.1 (`MaxStreams1080p51`)
  - 1080p (any) (`MaxStreams1080pAny`)
  - 720p (`MaxStreams720p`)
  - SD / Unknown / Low-bandwidth (`MaxStreamsSd`)

**Default Quality Tier** (DefaultQualityTier)
- Single dropdown (required). Default value: "1080p (any)"

### Section 2: Parental Controls (Discover-only)

**Hide unrated content** (HideUnratedContent) — yes/no checkbox
- Help text: "Only affects InfiniteDrive Discover / search / browse results. Emby native library restrictions still apply for persisted items."

### Section 3: Blocked Content Management

Dynamic table using GenericItemList:
- **Columns:** Title | ID (TMDB/IMDB) | Reason | Blocked By | Blocked Since | Unblock
- **Add to Block List** button (opens a simple search modal: title or ID)

## Tab 4: Sync & Marvin — Detailed Layout (Sprint 506)

### Section 1: Marvin Process Schedule

**Marvin Process Interval (minutes)** (MarvinProcessIntervalMinutes) — number field, default 10
**Stream Resolution Batch Size** (StreamResolutionBatchSize) — number field, default 42
**Prominent button:** `Run Marvin Now`

### Section 2: Pruning & Deduplication (read-only summary + toggles)

- **Respect user playlists & self-managed collections when pruning** (toggle, default ON)
- **Auto-deduplicate against physical media in other libraries** (toggle, default ON)
- Read-only plain-English summary (non-editable):
  "Items are added/removed dynamically. Playlists and self-managed collections are respected. Physical media in other libraries is automatically deduplicated."

### Section 3: Rate-Limit & Safety

**Marvin Actions Per Hour** (MarvinActionsPerHour) — number field, default 360
- Help text: "Limits how aggressively Marvin runs to stay a good citizen with AIOStreams and debrid providers."

## Tab 5: Advanced — Detailed Layout (Sprint 507)

**"Show advanced settings" toggle** (at the very top — when OFF, the rest of the tab is hidden/collapsed)

### Section 1: Logging & Debugging (only visible when toggle is ON)

**Log Level** (PluginLogLevel) — dropdown with exactly these options:
- Error / Warning / Info / Debug / Trace (default = Info)
**Clear all caches** button (calls existing cache-clear logic)

### Section 2: Maintenance & Reset

- **Reset All InfiniteDrive Data** (button with big red warning confirmation)
- **Rebuild Libraries from Scratch** (button)
- **Reset to Factory Defaults** (button)
