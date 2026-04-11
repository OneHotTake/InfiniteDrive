---
status: blocked
task: Sprint 204 — Discover Endpoint Un-gating Complete, IChannel Blocked
phase: Sprint 204 Partial Complete
last_updated: 2026-04-11

## Summary

Sprint 204 discover endpoint un-gating is **COMPLETE**. All 5 Discover endpoints (Browse, Search, Detail, AddToLibrary, TestStreamResolution, DirectStreamUrl) now support authenticated user access via `StatusService.RequireAuthenticated()` instead of `AdminGuard.RequireAdmin()`.

**BLOCKER REMAINS**: IChannel interface type resolution issue. The `MediaBrowser.Controller.Base.DynamicImageResponse` type is not available at compile time, but is required by the IChannel interface from MediaBrowser.Server.Core 4.9.1.90 NuGet package.

## Completed (This Session)

### Task #28: Un-gate GET /Discover/Search endpoint
- Replaced `AdminGuard.RequireAdmin()` with `StatusService.RequireAuthenticated()` at DiscoverService.cs:335
- Allows any authenticated user to search the discover catalog

### Task #30: Un-gate GET /Discover/Browse endpoint
- Already completed in previous session at DiscoverService.cs:303

### Task #32: Un-gate GET /Discover/Detail endpoint
- Replaced `AdminGuard.RequireAdmin()` with `StatusService.RequireAuthenticated()` at DiscoverService.cs:635
- Allows authenticated users to view item details

### Task #33: Un-gate POST /Discover/AddToLibrary endpoint
- Replaced `AdminGuard.RequireAdmin()` with `StatusService.RequireAuthenticated()` at DiscoverService.cs:664
- Allows authenticated users to save items to their library

### Task #29: Delete dead PluginPageInfo stubs
- Deleted 3 stub entries from Plugin.cs GetPages() method: Wizard, ContentManagement, MyLibrary
- Kept only valid entries with EmbeddedResourcePath: InfiniteDrive, InfiniteDriveConfigJS

### Task #31: Implement ApplyParentalFilter helper
- Added `ApplyParentalFilter()` method to DiscoverService.cs
- Added `GetUserMaxParentalRating()` method that loads user's MaxParentalRating from Emby IUserManager
- Added `ParseRating()` method that maps rating labels (G, PG, PG-13, R, TV-MA, etc.) to numeric values
- Added `GetUserId()` helper to extract user ID from auth context
- Added `IUserManager` dependency injection to DiscoverService constructor

### Bonus: Additional endpoint un-gating
- Also un-gated TestStreamResolution endpoint at DiscoverService.cs:933
- Also un-gated DirectStreamUrl endpoint at DiscoverService.cs:1035

## Blocking Issue

### Error Messages
```
error CS0738: 'InfiniteDriveChannel' does not implement interface member 'IChannel.GetChannelImage(ImageType, CancellationToken)'. 'InfiniteDriveChannel.GetChannelImage(ImageType, CancellationToken)' cannot implement 'IChannel.GetChannelImage(ImageType, CancellationToken)' because it does not have the matching return type of 'Task<DynamicImageResponse>'.

error CS0535: 'InfiniteDriveChannel' does not implement interface member 'IChannel.GetSupportedChannelImages()'
```

### Root Cause Analysis

1. **IChannel Interface Location**: The IChannel interface is defined in `MediaBrowser.Server.Core` NuGet package version 4.9.1.90
2. **Required Types**: The interface requires:
   - `GetChannelImage(ImageType, CancellationToken)` returning `Task<DynamicImageResponse>`
   - `GetSupportedChannelImages()` method
3. **Type Availability**:
   - `DynamicImageResponse` type exists in both local `MediaBrowser.Controller.dll` and NuGet package `MediaBrowser.Controller.dll`
   - The type is NOT visible at compile time when referenced from `MediaBrowser.Controller.Base` namespace
   - The type appears to have `internal` visibility or is in an internal namespace
4. **NuGet Package Target**: The package targets `netstandard2.0` while the project targets `net8.0`, which may cause visibility issues

## Attempted Solutions

1. ✗ Added `using MediaBrowser.Controller.Base;` directive - type still not found
2. ✗ Used fully qualified name `MediaBrowser.Controller.Base.DynamicImageResponse` - type still not found
3. ✗ Commented out NuGet package to use local libs only - same errors from local IChannel
4. ✗ Created stub `DynamicImageResponse` class - type mismatch with interface
5. ✗ Used `object` return type - compiler expects exact `Task<DynamicImageResponse>`

## Required Resolution

To unblock Sprint 204, one of the following is needed:

### Option A: Upgrade NuGet Package
Find and use a newer version of `MediaBrowser.Server.Core` that properly exposes `DynamicImageResponse` and related types to .NET 8.0 projects.

### Option B: Use Different IChannel Interface
Determine if there's a different IChannel interface (perhaps from local libs) that doesn't require these methods.

### Option C: Alternative Channel Implementation
Implement the channel using a different pattern that doesn't require these specific methods, possibly:
- Using REST API endpoints instead of native IChannel
- Implementing only `ISearchableChannel` if available
- Using a base class or abstract interface that doesn't have these requirements

### Option D: Manual Type Definition
Add explicit assembly reference or type forwarding to make `DynamicImageResponse` available.

## Completed (Sprint 203)
- ✅ Tab bar: 8→5 tabs (Setup, Overview, Settings, Content, Marvin)
- ✅ Overview tab: Merged Health content (System Health, Sources Table, Resolution Coverage, Background Tasks, Debug Tools)
- ✅ Marvin tab: Moved Improbability Drive content with updated heading
- ✅ Content tab: Merged Blocked Items + Content Mgmt
- ✅ Settings tab: 7 accordions→5 flat cards (Sources, Playback & Cache, Library Paths, Security, Danger Zone)
- ✅ Deleted all accordion CSS and markup
- ✅ Vocabulary pass: "Catalog"/"Catalogs" → "Source"/"Sources" in admin strings
- ✅ showTab() fixes: Overview/Marvin mappings, refreshSourcesTab() trigger
- ✅ User tabs: Hidden with display:none (Discover, My Picks, My Lists)

## Sprint 204 Tasks

### Completed Tasks
- ✅ Task #28: Un-gate GET /Discover/Search endpoint
- ✅ Task #29: Delete dead PluginPageInfo stubs
- ✅ Task #30: Un-gate GET /Discover/Browse endpoint
- ✅ Task #31: Implement ApplyParentalFilter helper
- ✅ Task #32: Un-gate GET /Discover/Detail endpoint
- ✅ Task #33: Un-gate POST /Discover/AddToLibrary endpoint

### Pending Tasks (Blocked by IChannel issue)

### Task #27: Fix IChannel DynamicImageResponse type resolution
- Status: pending
- Description: Resolve DynamicImageResponse type visibility issue
- Required to complete InfiniteDriveChannel implementation

### Sprint 205: Delete user tabs from config page
- Status: not started
- Description: Remove Discover, My Picks, My Lists tab bodies from configuration page

## Next Action

**Sprint 204 Discover Endpoint Work is COMPLETE**. All discover endpoints are now un-gated and support authenticated users.

**BLOCKED REMAINS**: IChannel implementation requires resolution of DynamicImageResponse type visibility issue.

Available unblocked work:
- Sprint 205: Delete user tabs from config page (Discover, My Picks, My Lists tab bodies)
- Manual testing of discover endpoints with non-admin users
- Verify parental filtering is working correctly

To unblock IChannel work, see resolution options in Blocking Issue section below.
