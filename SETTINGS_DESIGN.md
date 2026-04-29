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
