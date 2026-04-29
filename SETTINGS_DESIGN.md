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
