---
status: complete
task: Sprint 117 Complete — Declarative Plugin UI
next_action: Sprint 118 review
# SUMMARY: Sprint 117 implements declarative Plugin UI system with ViewModels and attributes
# SPRINT 118 BLOCKED: IContentSection API doesn't exist in current Emby SDK (stub implementations created)
# SPRINT 117 COMPLETE: Declarative UI with BasePluginViewModel, UI attributes, ViewModels, ConfigurationController, Plugin page registration
# BUILD: ✅ Success - 0 warnings, 0 errors
last_updated: 2026-04-06

---

## Sprint Status Summary

| Sprint | Status | Notes |
|--------|---------|----------|
| 109-116 | **Complete ✓** | All core functionality implemented |
| 117 | **Complete ✓** | Declarative Plugin UI with ViewModels |
| 118 | **Blocked** | Stub implementations created, waiting for 4.10.0.8-beta SDK |
| 119-121 | **Complete ✓** | API endpoints, logging, E2E tests |

---

## Sprint 117 — Declarative Plugin UI (Complete)

**Status:** COMPLETE | **Build:** ✅ Success (0 warnings, 0 errors)

### Sprint 117 Summary

Sprint 117 implements Admin UI using Emby's **declarative Plugin UI system**. UI is generated automatically from C# ViewModels decorated with attributes.

**Components Completed:**

**Base Classes:**
- [x] BasePluginViewModel - Base class extending BasePluginConfiguration
- [x] UI Attributes (TabGroup, DataGrid, RunButton, Dangerous, FilterOptions)

**Row Models:**
- [x] SourceRow - Source data with Name, ItemCount, LastSyncedAt, Enabled, ShowAsCollection
- [x] CollectionRow - Collection data with CollectionName, SourceName, LastSyncedAt
- [x] ItemRow - Item data with Title, Year, MediaType, Status, SaveReason, Superseded, SupersededConflict
- [x] WatchHistoryRow - Watch history with Title, Season, Episode, Status, LastWatchedAt

**ViewModels:**
- [x] WizardViewModel - Setup wizard with API key, library paths, sync settings
- [x] ContentManagementViewModel - Admin tabs (Sources, Collections, Items, Needs Review)
- [x] MyLibraryViewModel - Per-user tabs (Saved, Blocked, Watch History)

**Controller:**
- [x] ConfigurationController - Loads/saves ViewModels, handles button clicks

**Plugin Integration:**
- [x] Plugin.cs GetPages() - Registers Wizard, Content Management, My Library pages

**Build Status:** ✅ Success (0 warnings, 0 errors)

---

## Sprint 118 — Home Screen Rails

**Status:** BLOCKED | **Reason:** IContentSection API doesn't exist in current Emby SDK

### Sprint 118 Notes

The Sprint 118 specification requires implementing IContentSection interface and ContentSectionProvider class. However, current Emby SDK (beta 4.10.0.8) does not include:
- `IContentSection` interface
- `ContentSectionList` class
- `HomeSectionType` enum

**Stub Implementation:**
- `Services/HomeSectionStub.cs` - Extension methods providing missing APIs
- `Services/HomeSectionTracker.cs` - Per-user per-rail tracking with marker pattern
- `Services/HomeSectionManager.cs` - Home section management using stubs

**TODO when SDK becomes available:**
1. Remove HomeSectionStub.cs and extension methods
2. Update HomeSectionManager.cs to use real ContentSection type
3. Implement actual database queries for rail items
4. Test real home section behavior in Emby UI

---

## Files Modified in Sprint 117

**New Files:**
- Configuration/BasePluginViewModel.cs - Base class for all ViewModels
- Configuration/Attributes/TabGroupAttribute.cs - Groups properties into tabs
- Configuration/Attributes/DataGridAttribute.cs - Marks collections for data grid display
- Configuration/Attributes/RunButtonAttribute.cs - Marks methods as UI buttons
- Configuration/Attributes/DangerousAttribute.cs - Marks dangerous actions requiring confirmation
- Configuration/Attributes/FilterOptionsAttribute.cs - Defines filter options for properties
- Configuration/RowModels.cs - Data row DTOs for grid display
- Configuration/WizardViewModel.cs - Setup wizard ViewModel
- Configuration/ContentManagementViewModel.cs - Content management ViewModel
- Configuration/MyLibraryViewModel.cs - My library ViewModel
- Controllers/ConfigurationController.cs - Controller for ViewModel loading/saving

**Modified Files:**
- Plugin.cs - Updated GetPages() to register three new pages

**Build Status:** ✅ Success (0 warnings, 0 errors)
