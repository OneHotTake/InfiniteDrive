# Sprint 156 — Webhook Retirement & Unified Write Path

**Version:** v3.3 | **Status:** Plan | **Risk:** MEDIUM | **Depends:** Sprint 155

---

## Overview

Delete `WebhookService.cs` entirely and consolidate every `.strm` write
call site behind a single `StrmWriterService`. This closes the legacy
bypass that lets webhook-posted items skip `ItemPipelineService` and
`DoctorTask`, removes an unauthenticated ingestion surface, and finishes
the refactor scoped in the unfinished portion of Sprint 147.

### Why This Exists

`Services/WebhookService.cs` (488 lines, still live) accepts raw JSON
payloads via shared-secret auth and calls
`CatalogSyncTask.WriteStrmFileForItemPublicAsync` directly. Items that
enter this way skip verification, skip enrichment gates, and skip the
pipeline's source-type handling. Three other callers
(`CatalogSyncTask`, `FileResurrectionTask`, and the future
`DiscoverService` AddToLibrary flow) use the same public static escape
hatch.

In a manifest-first plugin, webhook-style "push an item" makes no sense:
if the item is in the AIOStreams manifest, it's already being synced; if
it isn't, no amount of webhook pushing will make AIOStreams resolve it.
The webhook is a leftover *arr-world abstraction that doesn't fit
embyStreams' model.

Sprint 156 **removes** the webhook and **consolidates** writes. Net
lines of code: negative. Net new user-visible config: zero.

Design context: `docs/USER_DISCOVER.md` (explains why Sprint 157 depends
on the unified writer landing first).

---

## Phase 156A — StrmWriterService

### FIX-156A-01: Create StrmWriterService

**File:** `Services/StrmWriterService.cs` (create)

**What:**

1. New class `StrmWriterService` registered as a singleton.
2. Single public method:
   ```csharp
   public async Task<string> WriteAsync(
       CatalogItem item,
       SourceType originSourceType,
       string? ownerUserId,
       CancellationToken ct);
   ```
3. The body is lifted verbatim from
   `CatalogSyncTask.WriteStrmFileForItemPublicAsync` — same filesystem
   paths, same signed URL format, same NFO generation. This is a
   **move, not a rewrite.** Do not change behaviour in this phase.
4. Constructor dependencies: `ILogManager`, `DatabaseManager`, `PluginSecret`,
   and whatever `WriteStrmFileForItemPublicAsync` currently touches
   (inspect to enumerate).

**Depends on:** none (pure extraction).

---

### FIX-156A-02: Register StrmWriterService

**File:** `Plugin.cs` (modify)

**What:**
Add to existing service registration block:
```csharp
serviceCollection.AddSingleton<StrmWriterService>();
```
Order: must be constructible before `CatalogSyncTask`,
`FileResurrectionTask`, and `DiscoverService` (all get it injected).

---

## Phase 156B — Migrate Callers

### FIX-156B-01: CatalogSyncTask

**File:** `Tasks/CatalogSyncTask.cs` (modify)

**What:**

1. Inject `StrmWriterService` via constructor.
2. Replace every call to `WriteStrmFileForItemPublicAsync(item, config)`
   inside `CatalogSyncTask` with
   `_strmWriter.WriteAsync(item, SourceType.Aio, ownerUserId: null, ct)`.
3. Delete the `WriteStrmFileForItemPublicAsync` static method from
   `CatalogSyncTask` **after** all callers are migrated (Phase 156B-03).

---

### FIX-156B-02: FileResurrectionTask

**File:** `Tasks/FileResurrectionTask.cs` (modify)

**What:**

1. Inject `StrmWriterService`.
2. Replace the `WriteStrmFileForItemPublicAsync` call with
   `_strmWriter.WriteAsync(item, item.SourceType, item.FirstAddedByUserId, ct)`.
3. If `FileResurrectionTask` currently reconstructs items without a known
   source type, pass `SourceType.Aio` as the fallback (matches today's
   behaviour — resurrected items were originally synced from AIO).

---

### FIX-156B-03: Delete the public static

**File:** `Tasks/CatalogSyncTask.cs` (modify)

**What:**

1. Verify there are zero remaining references to
   `WriteStrmFileForItemPublicAsync` (grep the repo, including
   `Services/DiscoverService.cs`).
2. Delete the method.
3. Delete any `#pragma` / `Obsolete` / "DO NOT DELETE" comments around it.

If DiscoverService still references it at this point, **stop and fix
DiscoverService first.** The whole point of this sprint is that nothing
bypasses `StrmWriterService` after it ships.

---

### FIX-156B-04: DiscoverService.AddToLibrary

**File:** `Services/DiscoverService.cs` (modify)

**What:**

1. If `DiscoverService.Post(DiscoverAddToLibraryRequest)` currently calls
   `WriteStrmFileForItemPublicAsync`, replace it with
   `_strmWriter.WriteAsync(item, SourceType.Aio, callerUserId, ct)`.
2. Capture `callerUserId` from `IAuthorizationContext.GetAuthorizationInfo(Request).UserId`.
3. Persist it via FIX-156C-01 (column addition) so attribution survives
   the round trip.

---

## Phase 156C — Attribution Column

### FIX-156C-01: Add first_added_by_user_id

**Files:** `Data/Schema.cs`, `Data/DatabaseInitializer.cs`,
`Data/DatabaseManager.cs` (modify)

**What:**

1. Add to the `catalog_items` table CREATE statement:
   ```sql
   first_added_by_user_id TEXT NULL,
   ```
2. Add a schema_version bump (existing pattern — append an ALTER for
   databases that already exist).
3. Expose via `CatalogItem` model (`Models/CatalogItem.cs` or equivalent).
4. `StrmWriterService.WriteAsync` writes the column **only if it's not
   already set** (first writer wins). System-sourced items pass `null`.

This column is the minimum infrastructure Sprint 157's Discover
AddToLibrary needs. It is intentionally scoped to "who added it first,"
not "who claims it" — that's Sprint 158's job with `source_memberships`.

---

## Phase 156D — Delete WebhookService

### FIX-156D-01: Remove WebhookService.cs

**File:** `Services/WebhookService.cs` (delete)

**What:**

1. `rm Services/WebhookService.cs`.
2. Grep the repo for any remaining references and remove them.
3. If `Plugin.cs` has an explicit DI registration for it, remove that
   line.

---

### FIX-156D-02: Remove WebhookSecret from config

**Files:** `PluginConfiguration.cs`,
`Configuration/configurationpage.html`, `Configuration/configurationpage.js`
(modify)

**What:**

1. Remove `WebhookSecret` property from `PluginConfiguration.cs` (and any
   `Validate()` line that references it).
2. Remove the corresponding input field, label, and help text from
   `configurationpage.html`.
3. Remove load/save references in `configurationpage.js`.
4. Grep for `WebhookSecret` to confirm zero remaining references.

**Net user-facing change:** one fewer field on the configuration page.

---

### FIX-156D-03: Remove webhook docs

**Files:** `README.md`, `docs/dev-guide.md`, `docs/getting-started.md`,
`docs/configuration.md`, `docs/troubleshooting.md` (modify — only those
that mention webhooks)

**What:**

1. Grep the `docs/` tree and `README.md` for "webhook" / "Webhook" /
   "Jellyseerr" / "Overseerr" / "Radarr" / "Sonarr" in contexts that
   advertise the webhook feature.
2. Delete those sections. Do not replace them with "removed in Sprint
   156" tombstones — just remove.
3. Do NOT touch historical sprint files in `.ai/SPRINT_*.md`. Those are
   history and stay as-is.

---

## Phase 156E — Clean Up Bypass Logic

### FIX-156E-01: Remove user-added bypass TODOs

**Files:** `Services/ItemPipelineService.cs`,
`Services/DigitalReleaseGateService.cs`, `Services/TriggerService.cs`
(modify)

**What:**

Grep for `// TODO: Get actual source type`, `user-added`,
`user_added`, `UserAdded`, and similar bypass markers that were left
behind for the webhook's sake. For each:

1. If the bypass was **only** needed because the webhook posted items
   without a source type, delete the bypass branch. The item now always
   has a real `SourceType` set by `StrmWriterService`.
2. If the bypass is still legitimate (e.g. Discover-added items
   genuinely need to skip the digital release gate), replace the TODO
   with an explicit check on `SourceType == SourceType.Aio && itemOrigin == Discover`
   or similar — **no more assumption-based bypassing.**
3. Log each one you find so the sprint report can list them explicitly.

Do not speculate. If a bypass is load-bearing and its rationale is not
clear, leave it and log it as "retained — investigate in later sprint."

---

## Phase 156F — Build & Verification

### FIX-156F-01: Build

**What:**
`dotnet build -c Release` — 0 errors, 0 net-new warnings.

---

### FIX-156F-02: Grep checklist

**What:**
After the build passes, grep for each of the following and confirm the
expected count:

| Pattern | Expected |
|---|---|
| `WriteStrmFileForItemPublicAsync` | 0 (in source; may remain in `dump_parts/`, `repo_dump.txt`, and `.ai/SPRINT_147.md` / `.ai/SPRINT_151.md` / `BACKLOG.md` history) |
| `class WebhookService` | 0 |
| `WebhookSecret` | 0 (in source) |
| `EmbyStreams/Webhook/Sync` | 0 (in source) |
| `StrmWriterService` | ≥ 4 (service itself + 3 callers) |

If any source-file count is non-zero, the sprint is not complete.

---

### FIX-156F-03: Smoke test — full sync

**What:**

1. `./emby-reset.sh`
2. Configure AIOStreams manifest via the wizard.
3. Trigger a catalog sync.
4. Verify `.strm` files are written, items appear in Emby, playback works.
5. Verify `catalog_items.first_added_by_user_id IS NULL` for all
   system-synced items.

This is identical to current behaviour — the sprint is a refactor, not
a feature change, for everything except the webhook removal.

---

## Sprint 156 Completion Criteria

- [ ] `Services/StrmWriterService.cs` created
- [ ] `StrmWriterService` registered in `Plugin.cs`
- [ ] `CatalogSyncTask`, `FileResurrectionTask`, `DiscoverService` all use
      `StrmWriterService.WriteAsync`
- [ ] `CatalogSyncTask.WriteStrmFileForItemPublicAsync` deleted
- [ ] `Services/WebhookService.cs` deleted
- [ ] `PluginConfiguration.WebhookSecret` removed (C#, HTML, JS)
- [ ] `catalog_items.first_added_by_user_id` column added
- [ ] User-added bypass TODOs audited (removed or explicitly retained)
- [ ] Webhook documentation removed from `README.md` and `docs/`
- [ ] `dotnet build -c Release` — 0 errors
- [ ] Grep checklist all zero for deleted symbols
- [ ] Full sync smoke test passes

---

## Notes

**Files created:** 1 (`Services/StrmWriterService.cs`)
**Files modified:** ~12 (Plugin, PluginConfiguration, configurationpage.html/js,
CatalogSyncTask, FileResurrectionTask, DiscoverService, ItemPipelineService,
DigitalReleaseGateService, TriggerService, Schema, DatabaseInitializer,
DatabaseManager)
**Files deleted:** 1 (`Services/WebhookService.cs`, 488 lines)
**Config fields added (user-visible):** 0
**Config fields removed (user-visible):** 1 (`WebhookSecret`)

**Net lines of code: strongly negative.** That's the point.

**Risk: MEDIUM** — touches every `.strm` write call site. Mitigated by:
1. Phase 156A is a pure extraction (same code, new location).
2. Phase 156B migrates one caller at a time, each independently testable.
3. Phase 156D runs only after 156A/B are green.
4. Existing E2E playback test (`test-signed-stream.sh`) catches any
   regression in the write/sign/serve path.

**Elegance invariant:** at the end of this sprint there is exactly **one**
place in the codebase that writes `.strm` files. Every caller goes through
it. Every item has a known source type. There are no unauthenticated
surfaces left.

**Reference:** `docs/USER_DISCOVER.md` — explains why the unified writer
must land before Sprint 157 un-gates Discover for regular users.
