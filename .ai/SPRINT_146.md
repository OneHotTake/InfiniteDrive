# Sprint 146 — Health Panel Upgrade + Refresh Now

**Version:** v4.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 145

---

## Overview

Upgrade the Improbability Drive Health Panel to reflect the two-worker architecture. Show per-worker health (Refresh, DeepClean), active step + item counter during runs, NeedsEnrich/Blocked counts, and add a Refresh Now button that triggers RefreshTask immediately.

### Why This Exists

The current Health Panel shows Doctor status only. With two workers, the admin needs visibility into:
- Is RefreshTask running? What step is it on? How many items processed?
- When did DeepCleanTask last run? Was it healthy?
- How many items are stuck in NeedsEnrich or Blocked?
- Can I trigger an immediate refresh without waiting 6 minutes?

---

## Phase 146A — Status Model Expansion

### FIX-146A-01: Expand ImprobabilityDriveStatus for two workers

**File:** `Services/StatusService.cs` (modify)

**What:**
1. Add new fields to `ImprobabilityDriveStatus`:
```csharp
// Refresh Worker
public bool RefreshHasRun { get; set; }
public string? RefreshLastRunAt { get; set; }
public string? RefreshActiveStep { get; set; }    // "collect", "write", "hint", "notify", "verify", or null
public int RefreshItemsProcessed { get; set; }

// DeepClean Worker
public bool DeepCleanHasRun { get; set; }
public string? DeepCleanLastRunAt { get; set; }

// Enrichment
public int NeedsEnrichCount { get; set; }
public int BlockedCount { get; set; }
```
2. Update `GetStatusAsync` (or equivalent) to populate new fields:
   - Read `last_refresh_run_time` from plugin_metadata (persisted by RefreshTask)
   - Read `last_deepclean_run_time` from plugin_metadata (persisted by DeepCleanTask)
   - Read active step from plugin_metadata (set/cleared during RefreshTask run)
   - Read NeedsEnrich/Blocked counts from catalog_items query
3. Derive overall status:
   - Green: both workers ran recently, no Blocked items
   - Yellow: one worker stale (2x interval), or NeedsEnrich > 0
   - Red: AIOStreams unreachable, or both workers stale (3x interval), or Blocked > 5

**Depends on:** Sprint 145 (DeepCleanTask persists run metadata)

### FIX-146A-02: RefreshTask persists active step

**File:** `Tasks/RefreshTask.cs` (modify)

**What:**
1. At the start of each step, persist active step to plugin_metadata:
```csharp
await db.PersistMetadataAsync("refresh_active_step", "collect", ct);
await db.PersistMetadataAsync("refresh_items_processed", "0", ct);
```
2. After each step, update items_processed count
3. On completion, clear active step:
```csharp
await db.PersistMetadataAsync("refresh_active_step", "", ct);
await db.PersistMetadataAsync("last_refresh_run_time", DateTime.UtcNow.ToString("o"), ct);
```
4. Show `IProgress<double>` progress: map step number to percentage (5 steps = 20% each)

**Depends on:** Sprint 143

### FIX-146A-03: DeepCleanTask persists last run time

**File:** `Tasks/DeepCleanTask.cs` (modify)

**What:**
1. On completion, persist last run time:
```csharp
await db.PersistMetadataAsync("last_deepclean_run_time", DateTime.UtcNow.ToString("o"), ct);
```

**Depends on:** Sprint 145

---

## Phase 146B — Health Panel UI

### FIX-146B-01: Update Improbability Drive HTML

**File:** `Configuration/configurationpage.html` (modify)

**What:**
1. Replace the single status dot with a two-row worker status:
   - Row 1: "Refresh Worker" — dot (green/yellow/red) + last run time + active step badge
   - Row 2: "Deep Clean" — dot (green/yellow/red) + last run time
2. Add enrichment summary row:
   - "Needs Enrich: X" | "Blocked: X"
3. Add "Refresh Now" button next to Refresh Worker row
4. Keep the Marvin quote section below (it is beloved)

### FIX-146B-02: Update Improbability Drive JS

**File:** `Configuration/configurationpage.js` (modify)

**What:**
1. Update `renderImprobabilityStatus` to render two-worker status:
   - Refresh: dot color based on recency (green = < 12 min, yellow = < 18 min, red = > 18 min)
   - DeepClean: dot color based on recency (green = < 36h, yellow = < 54h, red = > 54h)
   - Active step: show "Running: {step} ({count} items)" or "Idle"
2. Wire "Refresh Now" button:
   ```javascript
   function triggerRefreshNow(view) {
       ApiClient.getJSON(ApiClient.getUrl('ScheduledTasks')).then(function(tasks) {
           var refresh = (tasks || []).find(function(t) { return t.Key === 'EmbyStreamsRefresh'; });
           if (refresh) {
               ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('ScheduledTasks/Running/' + refresh.Id) });
           }
       });
       // Poll until active step appears (task started) then until it clears (task done)
       var pollInterval = setInterval(function() {
           refreshImprobabilityDrive(view, function(status) {
               if (!status.RefreshActiveStep) {
                   clearInterval(pollInterval); // task finished or never started after timeout
               }
           });
       }, 2000);
       // Safety: stop polling after 5 minutes max
       setTimeout(function() { clearInterval(pollInterval); }, 300000);
   }
   ```
3. Show live step progress: poll `/EmbyStreams/Status` every 2 seconds while RefreshTask is active

**Depends on:** FIX-146A-01

---

## Phase 146C — Build Verification

### FIX-146C-01: Build + UI verification

**What:**
1. `dotnet build -c Release` — 0 errors, 0 new warnings
2. `./emby-reset.sh` — server starts, plugin loads
3. Navigate to Improbability Drive tab
4. Verify two-worker status display
5. Verify "Refresh Now" button triggers RefreshTask
6. Verify active step updates during RefreshTask run
7. Verify NeedsEnrich/Blocked counts display correctly

**Depends on:** FIX-146B-02

---

## Sprint 146 Dependencies

- **Previous Sprint:** 145 (DeepCleanTask)
- **Blocked By:** Sprint 145
- **Blocks:** Sprint 147 (Doctor Removal needs Health Panel ready)

---

## Sprint 146 Completion Criteria

- [ ] ImprobabilityDriveStatus expanded with Refresh/DeepClean fields
- [ ] RefreshTask persists active step and item count to plugin_metadata
- [ ] DeepCleanTask persists last run time to plugin_metadata
- [ ] Health Panel shows two-worker status (Refresh + DeepClean)
- [ ] Active step displayed during RefreshTask run with live polling
- [ ] NeedsEnrich and Blocked counts displayed
- [ ] Refresh Now button triggers RefreshTask via ScheduledTasks API
- [ ] IProgress<double> wired to show percentage during run
- [ ] Overall status derived from both workers (green/yellow/red)
- [ ] Build succeeds with 0 errors, 0 new warnings

---

## Sprint 146 Notes

**Files modified:** 3 (`Services/StatusService.cs`, `Configuration/configurationpage.html`, `Configuration/configurationpage.js`)
**Files modified (minor):** 2 (`Tasks/RefreshTask.cs`, `Tasks/DeepCleanTask.cs` — metadata persistence)

**Risk assessment:** LOW. This is a UI-only sprint with no backend logic changes beyond status reporting.

**Design decisions:**
- The 2x/3x interval thresholds match the spec
- "Refresh Now" uses Emby's built-in ScheduledTasks API (same pattern as existing Summon Marvin)
- Live polling at 2-second intervals stops when active step clears
- **`IProgress<double>` and `plugin_metadata` coherence:** Map each step to a 20% range (Step 1: 0-20%, Step 2: 20-40%, etc.). Both `IProgress` and `plugin_metadata` use the same step mapping. `IProgress` drives Emby's native task progress bar; `plugin_metadata` drives the IID panel's live polling. They must report the same step simultaneously — set both at the start of each step in the same code block.
- **"Refresh Now" polling:** After triggering RefreshTask, poll `/EmbyStreams/Status` every 2 seconds. Check `refreshActiveStep` — if non-empty, show "Running: {step}". If empty (task not yet started or already finished), show a brief "Starting..." state then fall back to idle display. Do not use fixed-delay `setTimeout` — use an interval that checks the actual step value.

---
