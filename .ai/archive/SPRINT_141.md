# Sprint 141 — Token Rotation Housekeeping

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 140

---

## Overview

Add a `strm_token_expires_at` column to the catalog items table and
extend `DoctorTask` with a Phase 5 that lazily rotates resolve tokens
in `.strm` files before they approach expiry. Ensures no user ever
encounters a `401` from a stale `.strm` token without any manual
intervention required.

### Why This Exists

Resolve tokens embedded in `.strm` files have a 365-day lifetime.
Without active rotation, a dormant library will eventually serve
expired tokens to users who return after a long absence — producing
a silent `401` with no clear error message in the Emby UI.

This sprint adds proactive rotation: the Doctor checks daily (every
4 hours) whether any `.strm` token is within 90 days of expiry and
silently rewrites the file with a fresh 365-day token. The user
never knows it happened.

The 90-day rotation window against a 365-day expiry means a user
would need to be absent for more than 275 days before they could
encounter a stale token — and even then only if the server was also
offline for that entire period, since the Doctor runs continuously.

---

## Architecture Notes

### Token State: Two Independent Sources of Truth

The system deliberately maintains token state in two places that are
**independent of each other**:

1. **The `.strm` file itself** — the token payload is
   `base64url("{expiry_unix}.{hmac}")`. The expiry is baked into the
   token. `ValidateResolveToken()` reads expiry from the token
   directly and never consults the database. This means validation
   is always correct even if the DB is stale or missing.

2. **`catalog_items.strm_token_expires_at`** — a Unix timestamp
   stored in SQLite. Used exclusively for scheduling rotation
   efficiently. This is a scheduling hint, not an authoritative
   source of truth.

If these two ever drift (crash during write, manual file copy, server
migration), playback is unaffected — the token in the `.strm` file
is always what matters. The DB column only determines when the Doctor
decides to visit an item during Phase 5.

### Legacy Items

Any item written before Sprint 141 will have
`strm_token_expires_at = NULL`. The Phase 5 query treats NULL as
"immediately due" — all legacy items are rotated on the first Doctor
pass after this sprint deploys. This is intentional: it backfills
the tracking column for the entire existing library.

### Rate Limiting in Phase 5

Phase 5 is **local disk + DB work only**. Unlike Phases 1–4, it does
not call AIOStreams. There is no external API budget being consumed.
Phase 5 therefore runs at full speed with no artificial rate limiting.
On a large library, the first pass after deployment may take a few
minutes; subsequent passes visit only items approaching expiry and
are fast.

This is different from the 8-second cadence that governs AIOStreams
calls in Phases 1–4. Do not apply that rate limit here.

### Atomic File Writes

`.strm` rewrite must be atomic. Emby indexes `.strm` files and may
read them at any time. A partial write produces a malformed URL that
Emby will cache and serve until the next library scan.

**Required pattern:**
```csharp
var tmpPath = targetPath + ".tmp";
await File.WriteAllTextAsync(tmpPath, newStrmContent);
File.Move(tmpPath, targetPath, overwrite: true);
File.Move with overwrite: true is an OS-level atomic rename on Linux, macOS, and Windows (NTFS). The old file is never in a partial state from Emby's perspective.

Do not use File.WriteAllText(targetPath, ...) directly — this truncates before writing and leaves a window where Emby could read an empty file.

Phase 141A — Schema Migration

FIX-141A-01: Add strm_token_expires_at to catalog_items

File: Data/DatabaseManager.cs or migration script (modify)

What:

sql
Copy
ALTER TABLE catalog_items
ADD COLUMN strm_token_expires_at INTEGER;
Type: INTEGER (Unix timestamp, seconds since epoch)
Nullable: yes — NULL means legacy item, not yet tracked
No index required initially; the query in Phase 5 is a full scan run infrequently on a local SQLite file (acceptable performance)
If catalog_items is split across multiple tables per tier, add the column to whichever table owns the .strm file record
Migration strategy: Add column with ALTER TABLE in the plugin's existing schema migration path. SQLite ALTER TABLE ADD COLUMN with no default sets existing rows to NULL automatically — no backfill query needed. The NULL → rotation behaviour in Phase 5 handles it.

Depends on: None

Phase 141B — VersionMaterializer Timestamp Write

FIX-141B-01: Write expiry timestamp after every .strm write

File: Services/VersionMaterializer.cs (modify)

What: After BuildStrmUrl() generates a resolve token and the .strm file is written to disk, update the DB:

csharp
Copy
var expiresAt = DateTimeOffset.UtcNow
    .AddDays(365)
    .ToUnixTimeSeconds();

await _repository.SetStrmTokenExpiryAsync(
    itemId, tier, expiresAt);
This must happen after successful disk write — not before. If the disk write fails, the DB timestamp must not be updated. Correct sequence:

unknown
Copy
1. GenerateResolveToken()       → token string
2. Build .strm content string
3. Atomic write to disk         → success or throw
4. UPDATE strm_token_expires_at ← only on success
Applies to all callers of VersionMaterializer:

CatalogSyncTask (bulk hydration)
RehydrationTask (full rehydration)
DoctorTask Phase 2 (write new tiers)
DiscoverService (Add to Library)
DoctorTask Phase 5 (token rotation — see below)
Depends on: FIX-141A-01

Phase 141C — DoctorTask Phase 5

FIX-141C-01: Add Phase 5 — Token Refresh to DoctorTask

File: Tasks/DoctorTask.cs (modify)

What: Add a fifth phase after the existing Health Check phase.

Query:

sql
Copy
SELECT id, quality_tier, primary_id, primary_id_type
FROM catalog_items
WHERE strm_token_expires_at < (unixepoch('now') + 7776000)
   OR strm_token_expires_at IS NULL
ORDER BY strm_token_expires_at ASC NULLS FIRST
7776000 = 90 days in seconds. NULLS FIRST ensures legacy items are processed before items that are merely approaching expiry, so the backfill completes as quickly as possible on first run.

For each result:

csharp
Copy
// 1. Generate fresh token
var newToken = PlaybackTokenService.GenerateResolveToken(
    item.QualityTier, item.PrimaryId, item.PrimaryIdType);

// 2. Build new .strm content
var newContent = VersionMaterializer.BuildStrmContent(
    newToken, item.QualityTier, item.PrimaryId, item.PrimaryIdType);

// 3. Atomic write
var tmpPath = item.StrmPath + ".tmp";
await File.WriteAllTextAsync(tmpPath, newContent);
File.Move(tmpPath, item.StrmPath, overwrite: true);

// 4. Update DB (only after successful write)
var newExpiry = DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds();
await _repository.SetStrmTokenExpiryAsync(
    item.Id, item.QualityTier, newExpiry);

// 5. Log
_logger.LogInformation(
    "{ts} | {id} | {idType} | {tier} | action=token_refreshed",
    DateTimeOffset.UtcNow, item.PrimaryId,
    item.PrimaryIdType, item.QualityTier);
Error handling:

If disk write fails: log warning, skip DB update, continue to next item. Do not abort the entire phase.
If DB update fails after successful disk write: log warning, continue. The token in the file is valid; the DB will just cause the item to be revisited on the next Doctor pass (harmless).
If .strm file does not exist on disk: log warning, skip. The Doctor's Health Check phase (Phase 4) should have already flagged this as missing — let that handle recovery.
Rate limiting: None. Phase 5 is local disk + DB only. No AIOStreams calls. Run at full speed.

Position in DoctorTask: After Phase 4 (Health Check), before the Report step.

Depends on: FIX-141A-01, FIX-141B-01

Phase 141D — Repository Method

FIX-141D-01: Add SetStrmTokenExpiryAsync to repository

File: Whichever repository owns catalog_items writes (modify)

What:

csharp
Copy
Task SetStrmTokenExpiryAsync(
    string itemId,
    string qualityTier,
    long expiresAtUnix,
    CancellationToken ct = default);
Simple UPDATE catalog_items SET strm_token_expires_at = @expiry WHERE id = @id AND quality_tier = @tier.

Depends on: FIX-141A-01

Phase 141E — Build Verification

FIX-141E-01: Build + smoke test

What:

dotnet build -c Release → 0 errors, 0 new warnings
./emby-reset.sh → clean start, plugin loads
Trigger a manual Doctor run
Confirm Phase 5 runs (check log for action=token_refreshed)
Confirm all existing items (NULL expiry) get rotated on first pass
Open a rotated .strm file — verify token is a base64url string, not the raw PluginSecret
Confirm strm_token_expires_at is populated in DB after rotation (SELECT id, strm_token_expires_at FROM catalog_items LIMIT 10)
Depends on: FIX-141C-01

Sprint 141 Dependencies

Previous Sprint: 140 (Improbability Drive Validation)
Blocked By: Sprint 140
Blocks: Nothing — housekeeping enhancement only
Sprint 141 Completion Criteria

 strm_token_expires_at INTEGER column exists in catalog_items
 NULL treated as immediately due for rotation
 VersionMaterializer writes expiry timestamp after every successful .strm disk write
 All callers of VersionMaterializer persist the timestamp (CatalogSyncTask, RehydrationTask, DoctorTask Phase 2, DiscoverService)
 DoctorTask Phase 5 queries items expiring within 90 days or NULL
 Phase 5 uses atomic write (tmp → rename) for every .strm
 Phase 5 updates DB only after successful disk write
 Phase 5 logs action=token_refreshed per item
 Phase 5 has no rate limiting (local disk + DB only)
 Phase 5 skips gracefully if .strm file missing on disk
 First Doctor pass after deploy rotates all legacy NULL items
 SetStrmTokenExpiryAsync repository method added
 Build succeeds with 0 errors, 0 new warnings
 Smoke test: rotated .strm contains base64url token, not raw PluginSecret
Sprint 141 Notes

Files modified: ~4 (Tasks/DoctorTask.cs, Services/VersionMaterializer.cs, Data/DatabaseManager.cs or migration, repository file owning catalog_items)

Risk assessment: LOW. No new endpoints, no external API calls, no changes to playback flow. The worst-case failure mode is a .strm file that isn't rotated on schedule — it still works until actual expiry 275+ days later.

Performance on large libraries: The first Doctor pass after deployment will rotate all legacy items (NULL expiry). On a library of 10,000 items × 4 tiers = 40,000 .strm files, at roughly 1ms per atomic file write + DB update, this completes in under a minute. Subsequent passes visit only items within the 90-day window, which on a stable library is near zero work.

Why Phase 5 has no rate limit: Phases 1–4 call AIOStreams externally (rate limited to 1 request / 8 seconds to protect the API). Phase 5 is entirely local: disk reads/writes and SQLite queries. There is no external service to protect. Applying the 8-second cadence here would cause a 40,000-item first-pass to take 89 hours instead of under a minute. The rate limit is an AIOStreams budget concern, not a general concurrency concern.

Why two independent sources of truth: The expiry embedded in the token payload and the strm_token_expires_at DB column are deliberately redundant. ValidateResolveToken() always reads expiry from the token itself — it never queries the DB. The DB column is a scheduling index only. If they drift (server crash, file restore, manual copy), playback is unaffected. The next Doctor pass will re-align them. This is a resilience choice, not an oversight.

Future documentation target: This sprint and Sprints 131–140 together form the complete playback token lifecycle: generation (Sprint 134/139), validation (Sprint 132/133), rotation scheduling (Sprint 141). When the architecture doc is written, these three concerns map to three sections: docs/architecture/token-lifecycle.md.

unknown
Copy
---
