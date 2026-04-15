Got it. Multi-strm stays — you have plans for it.

---

# Sprint 312 — Dead Code Cleanup

**Status:** Draft | **Risk:** LOW | **Depends:** Sprint 311 | **Target:** v0.42

## Why
Doctor-era code and deprecated tasks create confusion and maintenance burden. Marvin has fully replaced Doctor. Dead code should be removed to keep the codebase navigable.

## Non-Goals
- Multi-strm code (intentionally preserved for future iteration)
- State enum consolidation (separate sprint, larger effort)
- Any behavioral changes

---

## Tasks

### FIX-312-01: Delete DoctorTask and related files
**Files:** `Tasks/DoctorTask.cs` (delete), any `*Doctor*.cs` files
**Effort:** S
**What:** Grep for `DoctorTask`, `Doctor` class references. Delete the task file(s). Remove any registrations in `Plugin.cs` or scheduled task setup. **Gotcha:** Verify no active code paths call Doctor — should be fully replaced by Marvin.

### FIX-312-02: Delete FileResurrectionTask
**Files:** `Tasks/FileResurrectionTask.cs` (delete)
**Effort:** S
**What:** Already marked `[DEPRECATED]` in codebase. Delete file, remove any task registration. **Gotcha:** Grep for `FileResurrection` to ensure no callers remain.

### FIX-312-03: Remove dead debrid fallback code
**Files:** Multiple (grep first)
**Effort:** S
**What:** Grep for `InfoHash`, `DirectDebrid`, `Layer3`, `DebridFallback`. Remove all code paths, config options, and comments that reference direct debrid resolution. Keep only debrid-related code used by AIOStreams integration. **Gotcha:** Don't remove `ProviderPriorityOrder` or stream-type filtering — those are active.

### FIX-312-04: Update Doctor-era comments
**Files:** Multiple (grep for "Doctor", "Phase 2", "Phase 3")
**Effort:** S
**What:** Find comments referencing "Doctor Phase X" or Doctor-era architecture. Update to reference Marvin or remove if obsolete. **Gotcha:** Don't change actual logic — comments only.

### FIX-312-05: Remove DirectStreamUrl remnants (if any remain)
**Files:** `Configuration/configurationpage.js`, `Configuration/discoverpage.js`, any UI files
**Effort:** S
**What:** Sprint 310 deleted the endpoint. Grep for `DirectStreamUrl` in JS/HTML files and remove any UI elements, fetch calls, or references. **Gotcha:** May already be clean after Sprint 310 — verify and close.

### FIX-312-06: Audit and remove unused configuration fields
**Files:** `Configuration/PluginConfiguration.cs`, `Configuration/configurationpage.html`
**Effort:** S
**What:** Grep each config field for usage. Remove fields with zero references in service code. Remove corresponding UI elements. **Gotcha:** Preserve `ApiCallDelayMs` and similar if still read anywhere, even if deprecated in favor of `CooldownGate`.

---

## Verification

- [ ] `dotnet build -c Release` — 0 errors, 0 warnings
- [ ] `./emby-reset.sh` succeeds + Discover UI loads
- [ ] Grep verification:
  - [ ] `grep -r "DoctorTask" .` — 0 results
  - [ ] `grep -r "FileResurrection" .` — 0 results
  - [ ] `grep -r "DirectDebrid\|Layer3\|InfoHash" .` — 0 results (outside comments)
  - [ ] `grep -r "DirectStreamUrl" .` — 0 results
- [ ] Manual test: Full sync completes (Marvin still works)
- [ ] Manual test: Playback still works (no regressions)

---

## Completion

- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated (remove deleted files)
- [ ] git commit -m "chore: end sprint 312 — dead code cleanup"
