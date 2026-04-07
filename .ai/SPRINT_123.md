# Sprint 123 — Versioned Playback: File Materialization + Rehydration

 and `.strm`/`.nfo` Writing

**Version:** v3.3 | **Status:** Plan | **Risk:** HIGH | **Depends:** Sprint 122

---

## Overview

  Sprint 123 implements the file materialization layer — writing versioned `.strm` and `.nfo` files — and the rehydration engine that orchestrates add/remove/reename operations across the catalog.



 **Key Principle:** Materialization writes files with slot suffixes. Rehydration flows through the existing trickle hydration pipeline. A single Emby library scan is triggered only on completion, not per-file.



---

## Phase 123A — Version Materializer

### FIX-123A-01: VersionMaterializer Service

**File:** `Services/VersionMaterializer.cs` (create)

**What:** Writes versioned `.strm` and `.nfo` files with slot suffixes. Generates the correct URL format for `.strm` content:

  ```
http://[emby-server-lan-ip]:[port]/EmbyStreams/play?titleId={id}&slot={slot_key}&token={api_token}
  ```

**Key methods:**

```csharp
// Write versioned .strm file for a slot
 public string WriteStrmFile(string mediaItem, string slotKey, VersionSlot slot, string embyBaseUrl, string apiToken)


// Write versioned .nfo file (per slot) public string WriteNfoFile(string mediaItem, string slotKey, VersionSlot slot, Candidate topCandidate)

// Get suffixed filename for slot: string GetSuffixedName(string baseName, VersionSlot slot)
  // Get base filename (no suffix): string GetBaseName(string baseName)
 VersionSlot defaultSlot)

// Build .strm URL with slot parameter: string BuildStrmUrl(string embyBaseUrl, string titleId, string slotKey, string apiToken)
 ```

**Naming Convention:**

- Suffix = slot label with `·` replaced by space and trimmed: `4K · HDR` → `4K HDR`
- Base pair (default slot) = no suffix
 e.g., `Avatar Fire and Ash (2025).strm`
- Suffixed pair = suffix applied e e.g., `Avatar Fire and Ash (2025) - 4K HDR.strm`



**Depends on:** FIX-122A-01 (version_slots), FIX-122A-02 (candidates), FIX-122B-01,VersionSlotRepository), FIX-122B-04,MaterializedVersionRepository)

**Must not break:** Existing Catalog sync task's file writing is unchanged. `BuildSignedStrmUrl()` method preserved for URL generation.



---

## Phase 123B — Rehydration Service

### FIX-123B-01: RehydrationService

**File:** `Services/RehydrationService.cs` (create)

**What:** Orchestrates catalog-wide rehydration operations. Three operation types:

 add slot, remove slot, change default.



 **Key Methods:**

```csharp
// Add slot for all titles ( RehydrationType.AddSlot )
//   - Writes new .strm/.nfo files incrementally
   - Flows through trickle hydration pipeline
   - No catalog-wide lock
   - Single Emby library scan on completion
   public async Task<RehydrationResult> AddSlotAsync(string slotKey, CancellationToken ct)
 {
     // 1. Load all active media items
     // 2. For each item: fetch AIOStreams streams
 normalize candidates
 match to slot, select top candidate
     // 3. Write .strm + .nfo files for     // 4. Record in materialized_versions table
     // 5. Trigger library scan on completion
 }

// Remove slot from all titles ( RehydrationType.RemoveSlot )
//   - Delete .strm + .nfo files immediately
     // 3. Remove materialized_versions records
     // 4. Trigger library scan
 public async Task<RehydrationResult> RemoveSlotAsync(string slotKey, CancellationToken ct) {
 ... }

// Change default slot ( RehydrationType.ChangeDefault )
//   - Rename: new default's suffixed files → base pair
     // 2. Rename: old base pair → suffixed with old default label
     // 3. Update materialized_versions records
     // 4. Net file count unchanged
     // 5. No candidate re-fetch required — rename only
 public async Task<RehydrationResult> ChangeDefaultAsync(string newDefaultSlotKey, CancellationToken ct) { ... }

 ```

**`enum RehydrationType { AddSlot, RemoveSlot, ChangeDefault }`

**Rehydration respects trickle rate limits:**
- Uses existing `ApiCallDelayMs` delay between items
- Uses existing `MaxConcurrentResolutions` limit
- No catalog-wide lock

 items processed one at a time


 **Depends on:** FIX-122C-01 (VersionPlaybackService), FIX-123A-01 (VersionMaterializer)

**Must not break:** Existing CatalogSyncTask behavior preserved. New rehydration triggers are explicitly scheduled actions.



---

### FIX-123B-02: RehydrationTask

**File:** `Tasks/RehydrationTask.cs` (create)

**What:** Scheduled task for background rehydration operations.



```csharp
public class RehydrationTask : IScheduledTask
 {
     public string Name => "EmbyStreams Rehydration";

     public string Key => "embystreams_rehydration";

     public string Description => "Materializes versioned stream files for configured quality slots";
";
     public string Category => "EmbyStreams";


     public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
 => Array.Empty);
     // No auto-trigger — admin-initiated only

     public async Task Execute(CancellationToken ct, IProgress<double> progress)
 {
         // Check for pending rehydration operations from queue
         // Process each operation through RehydrationService
 }
 }
 }
```

**Triggers:** None (admin-initiated only via API or manual trigger).

**Depends on:** FIX-123B-01 (RehydrationService)
**Must not break:** Existing scheduled tasks registry. No auto-triggers.



---

## Sprint 123 Dependencies

- **Previous Sprint:** 122 (Schema + Data)
  **Blocked By:** Sprint 122
 **Blocks:** Sprint 124 (Playback Endpoint)



---

## Sprint 123 Completion Criteria

 - [ ] VersionMaterializer writes .strm/.nfo files with slot suffixes naming
 - [ ] .strm URL format includes `titleId`, `slot`, `token` parameters
 - [ ] NFO files contain shared metadata + synthetic slot-specific streamdetails`  - [ ] RehydrationService handles add/remove/change-default operations correctly
 - [ ] Rehydration flows through trickle pipeline ( delays between items, no catalog-wide lock)
 - [ ] Single library scan triggered only on completion, not per-file or - [ ] Build succeeds ( 0 warnings, 0 errors)



---

## Sprint 123 Notes

 **File Naming:**

- Suffix format: ` - 4K HDR` (from slot label `4K · HDR` → `4K HDR`)
- Base pair (no suffix) — uses the base name + suffixed pairs: suffix applied to base name

 Default slot never gets suffixed filename for `{baseName} - {suffix}.strm`

 **URL rewrite vs rename:** When changing default, the new default's suffixed files already exist. Just rename them to base pair, Old base pair renamed to suffixed. No reURL rewrite needed — rename is a purely filesystem operation. Net file count unchanged per No candidate re-fetch required.



 **Rehydration through trickle pipeline:**
- Existing `LinkResolverTask` trickle pattern: delay between items, max concurrent resolution
 No catalog-wide lock.
 Progress reported incrementally.


 **Synthetic streamdetails:**
- NFO `<streamdetails>` values derived from AIOStreams candidate payload
 Marked clearly in code comments as synthetic
 derived, not probed. For Emby display polish only not for version selection.  No Emby probing.

   Resolution from `parsedFile.resolution`, codec from `encode`, HDR from `visualTags`, audio from `AudioTags`

/`Channels`
