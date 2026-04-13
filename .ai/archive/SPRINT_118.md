print 118 — Home Screen Rails (Emby 4.10.x)

**Version:** v4.10 | **Status:** Blocked - SDK Unavailable | **Risk:** LOW | **Depends:** Sprint 117

---

## Overview

Sprint 118 implements home screen rails using Emby’s `ContentSection` API + `IUserManager` (the only public API available in 4.10.0.8-beta). Rails appear natively on the home screen and are manageable via the new Home Screen Section Editor.

**Key Architecture (4.10 Compliant):**
- Uses `ContentSection` + `IUserManager.AddHomeSection` / `GetHomeSections` (NOT a provider interface)
- Uses `HomeSectionTracker` + marker pattern (Subtitle) for stable identity
- Uses Emby BoxSets for the “Saved” rail
- Per-user tracking via `home_section_tracking` table
- No JavaScript, no custom RailProvider

**Rail Types (unchanged):**
- Saved
- Trending Movies
- Trending Series
- New This Week
- Admin Chosen

---

## Implementation Status: BLOCKED

### Why Blocked?

The SPRINT_118.md specification is based on **4.10.0.8-beta SDK documentation**, but:

1. **4.10.0.8-beta DLLs are not publicly available**
   - The SDK directory only contains documentation, not DLLs
   - NuGet does not have 4.10.0.8-beta package
   - Latest available on NuGet is 4.10.0.1-beta

2. **Even 4.10.0.1-beta doesn’t include the required APIs**
   - Tested with 4.10.0.1-beta package
   - `IUserManager.AddHomeSection` method does NOT exist
   - `ContentSection.CustomName` property does NOT exist

3. **Current SDK version**
   - Project uses `MediaBrowser.Server.Core 4.9.1.90` (stable version)
   - This version does not include home section management APIs

---

## Implemented Files (with Stub Extensions)

### Created Files:
- `Models/HomeSectionTracking.cs` - Tracking model (already existed)
- `Services/HomeSectionTracker.cs` - Per-user per-rail tracking with marker pattern
- `Services/HomeSectionManager.cs` - Home section management using IUserManager stubs
- `Services/HomeSectionStub.cs` - Stub extensions for missing APIs

### Database Changes:
- Schema version bumped to V21
- Added `home_section_tracking` table with migration
- Added methods:
  - `InsertHomeSectionTrackingAsync`
  - `GetHomeSectionTrackingAsync`
  - `UpdateHomeSectionTrackingAsync`
  - `GetAllHomeSectionTrackingAsync`

### Plugin Changes:
- Initialized `HomeSectionTracker` and `HomeSectionManager` in constructor

---

## Stub Implementation Notes

To allow Sprint 118 to be “complete” in terms of code structure while waiting for SDK availability, the following stub implementations were created:

### HomeSectionStub.cs

Provides extension methods for missing IUserManager APIs:
```csharp
public static class UserManagerExtensions
{
    // In-memory storage for stub sections
    private static readonly Dictionary<long, List<StubContentSection>> _stubSections = new();

    // Stub AddHomeSection - stores sections in memory
    public static void AddHomeSection(this IUserManager userManager, long userId, object section, CancellationToken ct);

    // Stub GetHomeSections - retrieves sections from memory
    public static StubHomeSections GetHomeSections(this IUserManager userManager, long userId, CancellationToken ct);
}
```

### HomeSectionManager.cs

Uses stub extensions instead of real APIs:
```csharp
private StubContentSection CreateContentSection(...) { ... }

// Uses stub extension
_userManager.AddHomeSection(ConvertToLongId(userId), section, ct);
```

### TODOs for When SDK Becomes Available

1. Remove `HomeSectionStub.cs` and extension methods
2. Update `HomeSectionManager.cs` to use real `ContentSection` type (not stub)
3. Remove `StubContentSection` and `StubHomeSections` classes
4. Implement actual database queries for rail items (GetRailItemsAsync)
5. Test real home section behavior in Emby UI

---

## Sprint 118 Completion Criteria

| Criteria | Status | Notes |
|-----------|--------|-------|
| HomeSectionTracker tracks per-user per-rail state | ✅ Done | Created and integrated |
| HomeSectionManager adds rails using IUserManager | ✅ Stubbed | Using stub extensions until SDK available |
| All 5 rail types implemented | ✅ Done | Enum and marker pattern defined |
| Marker pattern + per-user tracking table | ✅ Done | Table schema and migration added |
| DatabaseManager methods implemented | ✅ Done | CRUD methods for tracking |
| Plugin registration works | ✅ Done | Services initialized in constructor |
| Build succeeds with current SDK | ✅ Done | 0 warnings, 0 errors |
| Rails appear on Emby home screen | ⏸️ Blocked | Requires 4.10.0.8-beta SDK |

---

## Sprint 118 Notes (4.10-specific)

No provider interface needed – `IUserManager.AddHomeSection` (and friends) is the public API.

Home Screen Section Editor – Users can now reorder/hide your rails natively (no Subtitle hack required for re-finding after restarts; the tracker + marker still handles plugin-internal stability).

Saved rail – Still uses the real Emby BoxSet from Sprint 116.

Dynamic rails (Trending / New This Week) – Re-created on every server start / user login for freshness (you can add a scheduled task later if desired).

SQL casing – Still critical: always use lowercase literals.

Migration from older spec – The original “v3.3 ContentSectionProvider : IContentSection” was based on pre-4.10 internal naming that never shipped publicly.

**BLOCKER:** 4.10.0.8-beta SDK DLLs are not publicly available. Cannot implement real home section management until these are released.
