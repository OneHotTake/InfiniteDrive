# Sprint 125 — Versioned Playback: UI — Wizard Step 3 (Stream Quality)

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 122, Sprint 124

---

## Overview

 Sprint 125 adds the Stream Quality step ( to the first-run setup wizard ( Step 3 of 4). This step surfaces **Simple** vs **Advanced** mode for slot selection.



 **Key Constraint:** Uses Emby's declarative VMC/MC pattern only. **No custom HTML, CSS, or JavaScript is permitted.**



---

## Phase 125A — Extend WizardViewModel

### FIX-125A-01: Add StreamQuality Tab to Wizard

**File:** `Configuration/WizardViewModel.cs` (modify)

**What:** Add Stream Quality configuration as a new wizard step. This step replaces the existing Step 3 if it wizard re or a new Step 3b.



 **New Fields:**

```csharp
[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "Quality Mode", Description = "Simple or Advanced")]
public string QualityMode { get; set; } = "simple"; // "simple" (default) or "advanced"

// Individual bool properties per slot (NOT List<string> — Emby VMC/MC pattern requires concrete properties)
[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "HD · Broad", Description = "Always enabled — cannot be disabled")]
public bool SlotHdBroad { get; set; } = true; // Locked, always true

[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "Best Available", Description = "Highest quality from any source")]
public bool SlotBestAvailable { get; set; } = false;

[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "4K · Dolby Vision", Description = "4K Dolby Vision HDR")]
public bool Slot4kDv { get; set; } = false;

[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "4K · HDR", Description = "4K HDR10")]
public bool Slot4kHdr { get; set; } = false;

[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "4K · SDR", Description = "4K Standard Dynamic Range")]
public bool Slot4kSdr { get; set; } = false;

[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "HD · Efficient", Description = "HD with efficient codec")]
public bool SlotHdEfficient { get; set; } = false;

[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "Compact", Description = "720p compact streams")]
public bool SlotCompact { get; set; } = false;

[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "Default Version", Description = "Which version plays by default")]
public string DefaultVersion { get; set; } = "hd_broad"; // Dropdown, shows enabled slots only

[TabGroup("Stream Quality", Order = 3)]
[Display(Name = "Version Count", Description = "Current enabled version count")]
public string VersionCount { get; set; } // Computed: "Enabled: N / 8"
```

**Wizard Behavior:**
- Step 3 shows when `IsFirstRunComplete` is false (first run):
  - Simple mode is selected → wizard finishes immediately, no further configuration needed
  - HD Broad locked in
  - Advanced mode → shows slot checkboxes inline in Step 3
  - Admin can check additional slots and change default, then save and continue
  - `SlotHdBroad` checkbox is locked/disabled — cannot be unchecked
  - Version counter enforced: max 8 enabled
- Step 3 shows when re-entering wizard (`IsFirstRunComplete` is true):
  - Shows current slot configuration with confirmation dialogs
  - Enabling new slot → shows confirmation dialog → enqueues rehydration
  - Disabling slot → shows confirmation dialog → enqueues removal rehydration
  - Changing default → shows confirmation dialog → enqueues default change rehydration
  - Re-entrant wizard uses same validation as settings page (Sprint 126)


 **Depends on:** FIX-122B-01 (VersionSlotRepository)
**Must not break:** Existing wizard steps 1-2 preserved. New Stream Quality step added after them.

---

## Phase 125B — Extend ConfigurationController

### FIX-125B-01: Handle Stream Quality Wizard Step Save ConfigurationController

**File:** `Controllers/ConfigurationController.cs` (modify)

**What:** Add handler for the Stream Quality step of wizard. Loads/saves stream quality settings and persists through `VersionSlotRepository`.

 Saves settings to `PluginConfiguration`. Validates rules ( enforces  8-slot maximum, HD Broad always enabled, default slot must be an enabled slots).

**Validation:**
- `SlotHdBroad` must always be true — prevent save if somehow false
- Count of true slot bools > 8 → prevent save with error
- `DefaultVersion` must correspond to a slot that is checked true
- On save: map bool properties to `VersionSlotRepository` enable/disable calls
- Re-entrant saves (wizard re-opened after first run) trigger confirmation dialogs and enqueue rehydration operations


 **Depends on:** FIX-125A-01, Sprint 122 (schema)
**Must not break:** Existing wizard step 1-2 preserved.

---

## Sprint 125 Completion Criteria

 - [ ] Wizard Step 3 shows Stream Quality options (`Simple` vs `Advanced` modes)
- [ ] Simple mode: HD Broad locked in, no additional configuration
 - [ ] Advanced mode: individual slot checkboxes visible with HD Broad locked
 - [ ] Default version dropdown shows enabled slots only
 - [ ] `Enabled: N / 8` counter enforced in real-time
 - [ ] 8-slot maximum enforced in both UI and service layer
 - [ ] Re-entrant wizard shows confirmation dialogs and triggers rehydration
 - [ ] Saving wizard step updates PluginConfiguration correctly
 - [ ] Build succeeds ( 0 warnings, 0 errors)

---

## Sprint 125 Notes

 **Wizard Integration:**
- Existing wizard has 3 steps  (Provider, Libraries, Sync) or Replace with 4-step wizard
    1. Welcome
 2. Connect AIOStreams
3. Stream Quality ( NEW)
 4. Ready

 The Stream Quality step replaces the sync settings from saves the quality mode to and plugin configuration. When `IsFirstRunComplete = false`.
 After first hydration, the saved quality mode is persisted but but slots may tracked to and UI reflects changes in rehydrated state.


 **Service Layer Validation (per design spec):**
- `VersionSlotRepository.GetEnabledSlotsAsync()` → validate enabled count
- HD Broad always present (SlotHdBroad is always true)
- `VersionSlotRepository.SetEnabledAsync(slotKey, enabled)` → enable/disable slot in DB
- `VersionSlotRepository.SetDefaultSlotAsync(slotKey)` → change default, must be enabled slot
- Enable count (including hd_broad) must not exceed 8
- Default slot must be an enabled slot

