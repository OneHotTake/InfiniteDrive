# Sprint 149 — Critical Hotfix (P0 Security & Correctness)

**Status:** Ready for Implementation  
**Priority:** BLOCKING — Do not ship without these fixes  
**Estimated Effort:** 4 hours  
**Based on:** 2026-04-10 Code Review

---

## Overview

Six critical bugs identified in production code review. All are small, surgical fixes.

---

## Task C-1: Fix Inverted Enrichment Logic in DeepCleanTask

**File:** Tasks/DeepCleanTask.cs:343-391

**Problem:** Success/failure branches are swapped.

**Fix:** Change line 343 from `if (enriched != null)` to `if (enriched == null)`

The success block (write NFO, set Enriched) should run when enriched is NOT null.
The failure block (increment retry, maybe block) should run when enriched IS null.

**Priority:** P0 | **Effort:** 15 min

---

## Task C-2: Use Timing-Safe HMAC Comparison

**File:** Services/PlaybackTokenService.cs:75 and :209

**Problem:** Plain == comparison enables timing oracle attack.

**Fix at line 75:**
Replace: `return parts[2] == expectedSignature;`

With:
```csharp
return CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(parts[2] ?? string.Empty),
    Encoding.UTF8.GetBytes(expectedSignature));
Fix at line 209: Replace: return computedSignature == providedSignature;

With:

csharp
Copy
return CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(computedSignature ?? string.Empty),
    Encoding.UTF8.GetBytes(providedSignature ?? string.Empty));
Add using: using System.Security.Cryptography;

Priority: P0 | Effort: 10 min

Task C-3: Add Authentication to DiscoverService

File: Services/DiscoverService.cs

Fix:

Make class implement IRequiresRequest
Inject IAuthorizationContext in constructor
Add AdminGuard.RequireAdmin(_authCtx, Request); as first line in each endpoint method
Priority: P0 | Effort: 30 min

Task C-5: Filter Blocked Items from Active Queries

File: Data/DatabaseManager.cs

Fix: Add AND blocked_at IS NULL to WHERE clauses in:

GetActiveCatalogItemsAsync
GetItemsMissingStrmAsync
GetItemsByNfoStatusAsync
Priority: P0 | Effort: 30 min

Task H-2: Escape uniqueid Values in Enriched NFO

File: Tasks/RefreshTask.cs:671,673

Fix: Wrap meta.ImdbId and meta.TmdbId with SecurityElement.Escape()

Priority: P1 | Effort: 2 min

Task L-3: Stop Leaking Exception Messages

File: Services/DiscoverService.cs (multiple catch blocks)

Fix: Replace return Error(500, "server_error", ex.Message);

With: return Error(500, "server_error", "An internal error occurred. Check server logs.");

Priority: P1 | Effort: 10 min

Commit Message

unknown
Copy
fix(sprint-149): critical security and correctness hotfix

SECURITY FIXES:
- Use timing-safe HMAC comparison (C-2)
- Add admin auth to DiscoverService (C-3)
- Escape XML in NFO uniqueid (H-2)
- Stop leaking exception details (L-3)

DATA CORRECTNESS:
- Fix inverted enrichment logic in DeepCleanTask (C-1)
- Filter blocked items in active queries (C-5)

All P0 issues from 2026-04-10 code review addressed.
