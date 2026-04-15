# Sprint 303 — Cleanup & Dead Code Removal

**Version:** v0.41.0.0 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 302

## Overview

Remove dead code, legacy patterns, and false promises from the codebase. Improve maintainability.

## Task 303-01: Remove Dead Debrid Fallback Code

**Problem:** Layer 3 direct debrid code exists but is non-functional, creates false confidence

**Files:** Multiple (audit needed)

**Changes:**
- Search for: `InfoHash`, `DirectDebrid`, `Layer3`, `DebridFallback`
- Remove all code paths that suggest direct debrid resolution
- Remove related configuration options if any
- Update any comments/docs that reference this fallback
- Keep debrid-related code only if actively used for primary resolution

**Acceptance Criteria:**
- [ ] No code suggests fallback beyond Secondary resolver
- [ ] No unused `InfoHash` storage or retrieval
- [ ] Configuration page doesn't show dead options
- [ ] Grep for "debrid" shows only active code paths

**Effort:** S

---

## Task 303-02: Remove Multi-Strm Remnants

**Problem:** Legacy code for multiple `.strm` versions per item clutters codebase

**Files:** `Services/StrmWriterService.cs`, potentially others

**Changes:**
- Search for: version arrays, quality-specific strm paths, multi-file write loops
- Remove any code that writes multiple `.strm` files per media item
- Consolidate to: one `.strm` per movie, one `.strm` per episode
- Clean up any database columns tracking multiple versions

**Acceptance Criteria:**
- [ ] Each movie has exactly one `.strm`
- [ ] Each episode has exactly one `.strm`
- [ ] No version/quality suffix in filenames
- [ ] Code is simpler and easier to follow

**Effort:** S

---

## Task 303-03: Consolidate Error Handling Patterns

**Problem:** Inconsistent try/catch patterns, some swallow errors silently

**Files:** Multiple services

**Changes:**
- Audit all `catch` blocks in resolution path
- Ensure all catches either: re-throw, return error result, or log at Warn+
- Remove empty catch blocks
- Standardize on: catch specific exceptions, not bare `Exception`
- Add context to log messages (mediaId, resolver name, etc.)

**Acceptance Criteria:**
- [ ] No silent error swallowing in playback path
- [ ] All errors logged with sufficient context
- [ ] Exception types are specific where possible
- [ ] Consistent pattern across services

**Effort:** M

---

## Task 303-04: Path Sanitization Hardening

**Problem:** `SanitisePath` doesn't block `..` traversal

**Files:** `Services/StrmWriterService.cs`

**Changes:**
- Add `..` to blocked character list
- Add validation that final path is within configured library root
- Use `Path.GetFullPath()` and verify starts with allowed prefix
- Log at Error if traversal attempt detected

**Acceptance Criteria:**
- [ ] `../` in title doesn't escape library directory
- [ ] Attempted traversal logged as potential attack
- [ ] Legitimate titles with `.` still work
- [ ] All write paths validated before write

**Effort:** S

---

## Task 303-05: Remove Unused Configuration Options

**Problem:** Dead config fields confuse users and developers

**Files:** `PluginConfiguration.cs`, `Configuration/configurationpage.html`

**Changes:**
- Audit each config field for actual usage
- Remove fields with no active code paths
- Remove corresponding UI elements
- Document remaining fields with clear descriptions

**Candidates to audit:**
- Anything debrid-related (if Layer 3 removed)
- Multi-version/quality-specific paths
- Legacy feature flags

**Acceptance Criteria:**
- [ ] Every config field has active code using it
- [ ] UI shows only functional options
- [ ] Config page is cleaner and less overwhelming

**Effort:** S

---

## Task 303-06: Logging Consistency Pass

**Problem:** Mix of log levels, some critical events at Debug, some noise at Info

**Files:** All services

**Changes:**
- Establish logging standards:
  - Error: System broken, needs attention
  - Warn: Degraded but functional, or user-facing issue
  - Info: Significant state changes, sync completions
  - Debug: Detailed flow, useful for troubleshooting
- Audit and adjust log levels across codebase
- Ensure structured logging (use `{Placeholder}` not string concat)
- Add correlation IDs where helpful (request ID through resolution chain)

**Acceptance Criteria:**
- [ ] Production logs (Info+) are meaningful signal, not noise
- [ ] Debug logs tell complete story for troubleshooting
- [ ] No PII in logs (user IDs OK, emails/IPs sparingly)
- [ ] Consistent format across services

**Effort:** M

---

## Sprint 303 Completion Criteria

- [ ] All 6 tasks implemented
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Grep "debrid" shows no dead code
- [ ] Grep "\.strm" shows single-file-per-item pattern
- [ ] No empty catch blocks in resolution path
- [ ] Path traversal blocked and logged
- [ ] Config page shows only active options
- [ ] Log output is clean and actionable

---
