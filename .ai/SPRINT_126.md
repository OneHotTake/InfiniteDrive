# Sprint 126 — Versioned Playback: UI — Settings Page (Stream Versions)

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 125, Sprint 121

---

## Overview

 Sprint 126 adds the Stream Versions section to the admin settings page. Mirrors the wizard's Advanced panel with additional confirmation dialogs.

 **Key Constraint:** Uses Emby's declarative VMC/MC pattern only. **No custom HTML, CSS, or JavaScript is permitted.**

---

## Phase 126A — Extend ContentManagementViewModel

### FIX-126A-01: Add Stream Versions Tab to Settings

**File:** `Configuration/ContentManagementViewModel.cs` (modify)

**What:** Add Stream Versions tab to the admin settings page with version slot management UI.

 **New Fields:**

```csharp
// Individual bool properties per slot (NOT List<string> — Emby VMC/MC pattern requires concrete properties)
[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "HD · Broad", Description = "Always enabled — cannot be disabled")]
public bool SlotHdBroad { get; set; } = true; // Locked, always true

[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "Best Available", Description = "Highest quality from any source")]
public bool SlotBestAvailable { get; set; } = false;

[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "4K · Dolby Vision", Description = "4K Dolby Vision HDR")]
public bool Slot4kDv { get; set; } = false;

[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "4K · HDR", Description = "4K HDR10")]
public bool Slot4kHdr { get; set; } = false;

[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "4K · SDR", Description = "4K Standard Dynamic Range")]
public bool Slot4kSdr { get; set; } = false;

[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "HD · Efficient", Description = "HD with efficient codec")]
public bool SlotHdEfficient { get; set; } = false;

[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "Compact", Description = "720p compact streams")]
public bool SlotCompact { get; set; } = false;

[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "Default Version", Description = "Which version plays by default")]
public string DefaultVersion { get; set; } = "hd_broad"; // Dropdown, shows enabled slots only

[TabGroup("Stream Versions", Order = 5)]
[Display(Name = "Version Count", Description = "Current enabled version count")]
public string VersionCount { get; set; } // Computed: "Enabled: N / 8"

[TabGroup("Stream Versions", Order = 5)]
[Dangerous]
[Display(Name = "Save Version Changes", Description = "Save version slot configuration")]
public string SaveVersionChanges { get; set; }
// Validate + save + enqueue rehydration
```

**Settings Page Behavior:**
- Admin sees the Stream Versions" tab with slot checklist, default dropdown, version counter
 and Save button
- Clicking Save shows confirmation dialog (per design spec copy §11):
 Adding, removing, changing default)
- Confirmation dialog copy from design spec
 shown before saving
 estimated rehydration time calculated from catalog size
 After confirmed save, triggers rehydration through existing pipeline


 **Depends on:** FIX-125A-01 (Wizard), FIX-122B-01 (VersionSlotRepository)
**Must not break:** Existing tabs preserved.

---

## Phase 126B — Add VersionSlotController

### FIX-126B-01: Version Slot API Controller

**File:** `Controllers/VersionSlotController.cs` (create)

**What:** API endpoints for version slot management and rehydration triggering).

 **Endpoints:**

```csharp
 [Route("/EmbyStreams/Versions", "GET", Summary = "Get version slot configuration")]
 public class GetVersionsRequest : IReturn<object> { }

[Route("/EmbyStreams/Versions", "POST", Summary = "Update version slot configuration")]
 public class UpdateVersionsRequest : IReturn<object> {
 ... }
```

**Key Operations:**
- `GET /Versions` — returns current slot configuration
- `POST /Versions` — updates slots (with validation + confirmation dialog enforcement triggers rehydration)

**Validation:**
- Enforce max 8 enabled slots maximum
 HD Broad always enabled
- Default slot must be one of the enabled slots
- Reject changes if adding slot and 8 already enabled
- Reject removing hd_broad
 or Confirmation dialog shown for admin for destructive operations (removing slot, changing default)
- Rehydration triggered via RehydrationService

 `existing` trickling pipeline)


 **Confirmation dialog copy (per design spec):**
  - Adding slot: "Enable [slot label] for all titles? EmbyStreams will add this version across your entire catalog. Your library stays playable during this process. Estimated time: ~[N] hours for [count] titles."
"
  - Removing slot: "Remove [slot label] from all titles? All [slot label] files will be deleted immediately. This cannot be undone without re-enabling and rehydrating. Your other versions are not affected."
"
  - Changing default: "Set [slot label] as the default version? The base file for every title will be rewritten. Users pressing Play without choosing a version will get [slot label]. Your library stays playable during the transition.""


 **Depends on:** Sprint 123 (RehydrationService), Sprint 122 (VersionSlotRepository)
**Must not break:** Existing API endpoints. New endpoint at `/EmbyStreams/Versions`.

---

## Sprint 126 Completion Criteria

 - [ ] Settings page shows Stream Versions tab (individual slot checkboxes per slot)
- [ ] HD Broad checkbox is locked, cannot be unchecked
 - [ ] Default version dropdown shows enabled slots only
 - [ ] Version count: `Enabled: N / 8` displayed in real-time
 - [ ] Save button triggers confirmation dialog (with `[Dangerous]` fallback pattern)
 - [ ] Confirmation dialog shows correct copy per design spec
 - [ ] Rehydration enqueued after confirmation (via PendingRehydrationOperations)
 - [ ] 8-slot maximum enforced in UI and service layer
 - [ ] Catalog size estimated from database at dialog-open time
 - [ ] Build succeeds ( 0 warnings, 0 errors)

---

## Sprint 126 Notes

 **UI Pattern:**
- Uses existing `BasePluginViewModel` pattern with `TabGroup`, `DataGrid`, `RunButton`, `Dangerous` attributes
  - Slot checklist uses individual `bool` properties per slot (same as wizard in Sprint 125)
- Default version uses dropdown (only shows enabled slots)
- Save button uses `RunButton` attribute with `Dangerous` confirmation dialog

 Inline counter: computed from `version_slots` table, `COUNT(*) WHERE enabled = 1`

 Inline warning: shown when additional slot checked (uses declarative `Dangerous` attribute or custom validation in service layer)

 **`[Dangerous]` Fallback Pattern (Issue 11):**
- The `[Dangerous]` attribute's behavior in Emby SDK 4.10-beta is unverified
- Implementation must use a **two-step save pattern** as fallback:
  1. First click: POST returns a confirmation token + dialog text (JSON response)
  2. Frontend (Emby's auto-generated UI) shows confirmation dialog
  3. Second click: POST includes confirmation token → operation executes
- If `[Dangerous]` works correctly → use it directly
- If `[Dangerous]` does NOT work in 4.10-beta → fall back to two-step save:
  - `VersionSlotController.UpdateVersionsRequest` gets `ConfirmationToken` field
  - First POST without token → returns `{ "requiresConfirmation": true, "message": "...", "token": "..." }`
  - Second POST with token → executes the operation
- Test both paths during Sprint 130 integration testing

 **Rehydration Time Estimation:**
- Catalog size from `DatabaseManager.GetActiveCatalogItemCountAsync()`
- Estimated time = catalog size * estimated_seconds_per_title (configurable)
- Default estimate: 3 seconds per title ( AIOStreams resolution + file write)

 **Catalog Size Query:**
- Read from `media_items` count at runtime when confirmation dialog opens

 **Service Layer Validation:**
- `VersionSlotRepository.GetEnabledSlotsAsync()` → returns enabled slots
- `VersionSlotRepository` validates slot key exists, `hd_broad` always enabled, default is valid enabled slot
- `VersionSlotRepository` enforces max 8 enabled slots
- Confirmation required for: add slot, remove slot, change default
- No confirmation needed when only sort order changes

 **Confirmation Dialog Copy (exact strings per design spec):**
- Adding: "Enable [slot label] for all titles? EmbyStreams will add this version across your entire catalog. Your library stays playable during this process. Estimated time: ~[N] hours for [count] titles."

- Removing: "Remove [slot label] from all titles? All [slot label] files will be deleted immediately. This cannot be undone without re-enabling and rehydrating. Your other versions are not affected."

- Changing default: "Set [slot label] as the default version? The base file for every title will be rewritten. Users pressing Play without choosing a version will get [slot label]. Your library stays playable during the transition."
 |
