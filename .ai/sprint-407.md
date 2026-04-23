# Sprint 407 — Native Plugin UI Migration (Phase 1: Foundation)

**Status:** In Progress | **Risk:** MED | **Depends:** none | **Target:** Phase 1

## Why (2 sentences max)
The 74.5KB HTML + 3800-line JS config page is unmaintainable and causes content stacking/ theme bugs. The SDK DLLs for GenericEdit/GenericUI are already referenced — zero new dependencies.

## Non-Goals
- Inspector tab migration (stays custom — real-time polling exceeds declarative framework)
- Discover page changes (user-facing, not config)

## Tasks

### FIX-407-01: SDK base classes
**Files:** Configuration/UI/ControllerBase.cs, Configuration/UI/views/PluginViewBase.cs, Configuration/UI/views/PluginPageView.cs (create)
**Effort:** S
**What:** Copy/adapt from Emby SDK demo UIBaseClasses — ControllerBase, PluginViewBase, PluginPageView

### FIX-407-02: Tab infrastructure
**Files:** Configuration/UI/TabPageController.cs, Configuration/UI/MainPageController.cs (create)
**Effort:** S
**What:** Generic tab factory + tabbed page host with IHasTabbedUIPages. Phase 1: only Providers tab.

### FIX-407-03: Providers tab (first native tab)
**Files:** Configuration/UI/views/ProvidersUI.cs, Configuration/UI/views/ProvidersPageView.cs (create)
**Effort:** S
**What:** EditableOptionsBase with 3 string fields (PrimaryManifestUrl, SecondaryManifestUrl, EmbyApiKey). Save loads from PluginConfiguration.

### FIX-407-04: Plugin.cs integration
**Files:** Plugin.cs (modify)
**Effort:** S
**What:** Add IHasUIPages interface, UIPageControllers property returning MainPageController. Keep IHasWebPages for Discover+Inspector.

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] Plugin loads in Emby without errors in log
- [ ] Native config page appears in Emby dashboard
- [ ] Providers tab renders with native Emby styling
- [ ] Save persists to InfiniteDrive.xml correctly

## Completion
- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated
- [ ] git commit -m "feat: native plugin UI — Phase 1 foundation + Providers tab"
