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
 public string QualityMode { get; set; } = "simple"; // = "Simple` (default), or `advanced`
 = `advanced`



 [TabGroup("Stream Quality", Order = 3)]
    [Display(Name = "Default Version", Description = "Which version plays by default if none")]
 public string DefaultVersion { get; set; } = "hd_broad"; // Default slot key

 HD Broad

 readonly dropdown

 shows enabled slots



 [TabGroup("Stream Quality", Order = 3)]
    [Display(Name = "Additional slots", Description = "Additional quality versions to enable")]
 public List<string> AdditionalSlots { get; set; } = new(); // e.g., "4k_hdr", "4K SDR"
 empty list

 initially disabled until enabled

 shows when enabled,

 string.Empty;

 }

 }
```

**Wizard Behavior:**
- Step 3 shows when `IsFirstRunComplete` is false` (first hydration):
 Simple mode is selected → wizard finishes immediately, no further configuration needed
 HD Broad locked in.
 Advanced mode → shows slot checklist, inline in Step 3
 Admin can check/unenable additional slots and change default, and save and continue
 `Slot enabled > 7` counter enforced. HD Broad cannot be unchecked.


 **Depends on:** FIX-122B-01 (VersionSlotRepository)
**Must not break:** Existing wizard steps 1-2 preserved. New Stream Quality step added after them.

---

## Phase 125B — Extend ConfigurationController

### FIX-125B-01: Handle Stream Quality Wizard Step Save ConfigurationController

**File:** `Controllers/ConfigurationController.cs` (modify)

**What:** Add handler for the Stream Quality step of wizard. Loads/saves stream quality settings and persists through `VersionSlotRepository`.

 Saves settings to `PluginConfiguration`. Validates rules ( enforces  8-slot maximum, HD Broad always enabled, default slot must be an enabled slots).

**Validation:**
- `hd_broad` must always be `AdditionalSlots` list
 removing it is `AdditionalSlots` disables it warns
 then prevents save
 removing `hd_broad` from `AdditionalSlots` → prevent save
 error, `AdditionalSlots` count > 8 → prevent save with error
 removing slots from disabled slots from `AdditionalSlots` after save
 error, `Default version must be enabled slot


 **Depends on:** FIX-125A-01, Sprint 122 (schema)
**Must not break:** Existing wizard step 1-2 preserved.

---

## Sprint 125 Completion Criteria

 - [ ] Wizard Step 3 shows Stream Quality optionsSimple` vs `Advanced` modes)
- [ ] Simple mode: HD Broad locked in, no additional configuration
 - [ ] Advanced mode: slot checklist visible with HD Broad locked
 - [ ] Default version dropdown shows enabled slots only - [ ] `Enabled: N / 8` counter en real-time
 - [ ] 8-slot maximum enforced in both UI and service layer)
 - [ ] Saving wizard step updates PluginConfiguration correctly
 - [ ] Build succeeds ( 0 warnings, 0 errors) |

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
 hd Broad always present
  VersionSlotRepository` validates slot is not enabled + already → saves new slot to  `VersionSlotRepository` validates  slot exists in `enabled` → returns error
  `VersionSlotRepository.SetEnabledAsync(slotKey)` to enable slot → saves config
 `VersionSlotRepository` validates default slot is valid → saves config. If slot key matches an enabled slot → returns error (must be an enabled slots)

