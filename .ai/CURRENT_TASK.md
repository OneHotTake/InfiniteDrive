---
status: complete
task: Sprint 116 Complete — Collection Management
next_action: Sprint 117 and 118 review
# SUMMARY: Sprint 116 implements collection management using Emby ICollectionManager API. Sources with ShowAsCollection=true are automatically synced to Emby BoxSets.
# SPRINT 118 BLOCKED: IContentSection API doesn't exist in current Emby SDK
# SPRINT 117 PARTIAL: Config page exists but missing v3.3 features
# PHASES: 116A (BoxSetRepository) ✓, 116B (BoxSetService) ✓, 116C (CollectionSyncService) ✓, 116D (CollectionTask) ✓
# BUILD: ✅ Success - 0 warnings, 0 errors
last_updated: 2026-04-06

---

## Sprint Status Summary

| Sprint | Status | Notes |
|--------|---------|----------|
| 109-116 | **Complete ✓** | All core functionality implemented |
| 117 | **Partial** | Config page exists (different structure), missing v3.3 features |
| 118 | **Blocked** | IContentSection API doesn't exist in current Emby SDK |
| 119-121 | **Complete ✓** | API endpoints, logging, E2E tests |

---

## Sprint 116 — Collection Management (v3.3 BoxSet API)

**Status:** COMPLETE | **Build:** ✅ Success

### Sprint 116 Summary

Sprint 116 implements collection management using Emby ICollectionManager API. Sources with `ShowAsCollection = true` are automatically synced to Emby BoxSets.

**Components Completed:**
- [x] BoxSetService - Manages Emby BoxSets via ICollectionManager
- [x] CollectionSyncService - Syncs sources to BoxSets
- [x] CollectionTask - Scheduled sync task

**BoxSetService Methods:**
- [x] FindBoxSet - Queries existing BoxSet by name
- [x] FindOrCreateBoxSetAsync - Finds or creates BoxSet
- [x] CreateBoxSetAsync - Creates new BoxSet via ICollectionManager
- [x] AddItemToBoxSetAsync - Adds items to BoxSet
- [x] RemoveItemFromBoxSetAsync - Removes items from BoxSet
- [ ] EmptyBoxSetAsync - Placeholder (requires SDK API investigation)

**API Findings:**
- ICollectionManager uses non-async methods (wrapped in Task.Run)
- AddToCollection uses long[] (InternalId), not Guid[]
- CreateCollection requires ItemIdList as long[]
- RemoveFromCollection requires BoxSet cast (not BaseItem)

**Build Status:** ✅ Success (0 warnings, 0 errors)

---

## Sprint 118 — Home Screen Rails

**Status:** BLOCKED | **Reason:** IContentSection API doesn't exist in current Emby SDK

### Sprint 118 Notes

The Sprint 118 specification requires implementing IContentSection interface and ContentSectionProvider class. However, the current Emby SDK (beta 4.10.0.8) does not include:

- `IContentSection` interface
- `ContentSectionList` class
- `ContentSectionListQuery` class
- `HomeSectionType` enum

These APIs may exist in a different SDK version or may have been removed. Sprint 118 requires SDK API investigation or alternative implementation approach.

**Created (later removed due to API not existing):**
- Services/HomeSectionTracker.cs (removed)
- Services/ContentSectionProvider.cs (removed)

---

## Sprint 117 — Admin UI

**Status:** PARTIAL | **Notes:** Existing config page uses different structure

### Sprint 117 Notes

The existing configuration page (configurationpage.html/js) has a complex, feature-rich UI with:
- Discover functionality
- Source management
- Collection sync
- Actions (sync, cleanup, etc.)
- Settings and preferences

However, the existing implementation differs from the v3.3 Sprint 117 specification:
- Different CSS classes and structure
- No "Needs Review" tab for superseded_conflict items
- No item inspector with season save display
- No install notice for THREE libraries
- Different API call patterns

**Missing Features (from Sprint 117 spec):**
- [ ] Needs Review tab (superseded_conflict items)
- [ ] Item inspector with season info
- [ ] Install notice for THREE libraries
- [ ] Toast notifications for save/block/unsave/unblock
- [ ] Item inspector modal with superseded status display

---

## Files Modified in Sprint 116

**Services:**
- Services/BoxSetService.cs - Emby BoxSet API wrapper using ICollectionManager
- Services/CollectionSyncService.cs - Syncs sources to BoxSets

**Sprint Documentation:**
- .ai/SPRINT_116.md - Marked as Complete
- .ai/SPRINT_117.md - Marked as Partial
- .ai/SPRINT_118.md - Marked as Blocked

**Build Status:** ✅ Success (0 warnings, 0 errors)
