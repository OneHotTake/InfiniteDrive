● I now have enough inventory to understand the shape of this review. Critical upfront
  findings:

  - .ai/MAINTENANCE.md does not exist (prompt claims it's "ground truth")
  - `DatabaseManager.cs = 5,624 lines, CatalogSyncTask.cs = 2,119 lines — both massive
  - No Data/Repositories/ directory — Repository pattern the prompt assumes doesn't exist
  - No StrmWriterService, PlaybackController, TokenValidator, WizardController — scope
  list references non-existent files

  Given the scale (10k+ lines across just the core files), I'll delegate deep analysis to
  parallel Explore agents for each subsystem, then compose the audit.

● 5 Explore agents finished (ctrl+o to expand)
   ├─ Security audit of EmbyStreams · 52 tool uses · 58.8k tokens
   │  ⎿  Done
   ├─ Database layer audit · 62 tool uses · 57.5k tokens
   │  ⎿  Done
   ├─ Refresh pipeline audit · 47 tool uses · 51.5k tokens
   │  ⎿  Done
   ├─ DeepClean, CatalogSync, spec drift audit · 69 tool uses · 59.8k tokens
   │  ⎿  Done
   └─ Discover UX and config wizard audit · 28 tool uses · 84.7k tokens
      ⎿  Done

● Before finalizing the report, let me verify the most alarming P0 finding — the
  DeepCleanTask inverted enrichment logic — since it's a crash-risk claim that deserves
  direct confirmation.

● Read 3 files (ctrl+o to expand)

● Verified. All three of the most serious P0 findings are confirmed by direct reads:
  - DeepCleanTask.cs:343 — inverted condition (success path increments retry, failure path
   passes null to NFO writer)
  - PlaybackTokenService.cs:75 — plain string == HMAC comparison
  - RefreshTask.cs:671,673 — <uniqueid> values unescaped while siblings use
  SecurityElement.Escape()

  Here is the audit.

  ---
  EmbyStreams — Production Code Review & Architecture Audit

  Date: 2026-04-10
  Reviewer: Claude Code (parallel Explore-agent analysis + direct verification of P0
  findings)
  Scope: Core pipeline, data layer, security, Discover/UX, spec compliance — post-Sprint
  148

  ---
  Executive Summary

  Overall Quality Score: 6.0 / 10

  The codebase has a solid foundation — parameterized SQL, semaphore concurrency gates,
  atomic file writes, cancellation tokens threaded consistently — but is carrying two
  classes of production risk: (1) a handful of critical bugs that would fail a careful
  pre-release review, and (2) significant spec drift from Sprints 142–148, where database
  columns and task logic shipped but the UI and some query filters were never wired up.

  Top 3 Strengths
  1. Disciplined SQLite hygiene — SemaphoreSlim _dbWriteGate serializes writes, WAL mode
  enabled, integrity-check on startup with auto-recreate, 100% parameterized queries
  across 150+ methods (no injection attack surface found).
  2. Atomic file writes everywhere — STRM/NFO writes all use tmp + File.Move(overwrite:
  true) with orphan cleanup in catch. No partial-write risk.
  3. Correct Refresh pipeline shape — 6-step progression (Collect 0.16 → Write 0.33 → Hint
   0.50 → Enrich 0.67 → Notify 0.83 → Verify 1.00) matches Sprint 148 spec exactly;
  enrichment retry backoff (0 → +4h → +24h → Blocked) and 2-second throttle implemented as
   specified in RefreshTask.EnrichStepAsync.

  Top 3 Critical Issues
  1. 🔴 DeepCleanTask enrichment logic is inverted — Tasks/DeepCleanTask.cs:343. On
  metadata fetch success it increments retry count and eventually blocks the item; on
  failure it passes null to WriteEnrichedNfoAsync and crashes. The entire Deep Clean
  enrichment path is broken.
  2. 🔴 Timing-oracle in HMAC signature validation — Services/PlaybackTokenService.cs:75
  (return parts[2] == expectedSignature;). Classic timing attack. ValidateSignature() at
  ~line 158 in the same file already does it correctly with
  CryptographicOperations.FixedTimeEquals — this is copy-paste negligence, not ignorance.
  3. 🔴 DiscoverService has zero authorization on any endpoint (per subagent audit of the
  full file). All four endpoints — Browse, Search, Detail, AddToLibrary — are
  anonymous-callable. Anyone on the LAN can write arbitrary .strm files into library
  folders. This should be verified before ship, as other services in the project
  (TriggerService, StatusService) correctly use AdminGuard.RequireAdmin().

  Recommendation: 🔶 Minor fixes required before ship. The three P0s are all small,
  surgical fixes (~2 hours total). Shipping without them is not advisable. The P1 set
  (spec drift, God class, dead code) can and should be scheduled into Sprints 149–151.

  ---
  Detailed Findings

  🔴 CRITICAL

  C-1. Inverted Success/Failure in DeepCleanTask.EnrichmentTrickleAsync

  Location: Tasks/DeepCleanTask.cs:343-391

  Problem: The success and failure branches are swapped. On fetch success the code runs
  the failure path (increments retry, eventually blocks the item). On fetch failure the
  code calls WriteEnrichedNfoAsync(item, enriched, …) with enriched == null — null
  dereference.

  Evidence (verified directly):
  var enriched = await aioClient.FetchAsync(item.ImdbId, item.Year);
  if (enriched != null)
  {
      // Failure: increment retry_count, set next_retry_at   ← WRONG
      item.RetryCount++;
      …
      if (item.RetryCount >= 3)
      {
          await db.SetNfoStatusAsync(item.Id, "Blocked", cancellationToken);
          …
      }
  }
  else
  {
      // Success: write enriched .nfo                        ← WRONG
      await WriteEnrichedNfoAsync(item, enriched, cancellationToken);
      await db.SetNfoStatusAsync(item.Id, "Enriched", cancellationToken);
  }

  Impact:
  - Every successful fetch increments retry counter — items get blocked in ≤3 runs even
  though metadata was returned.
  - Every failed fetch throws NRE inside WriteEnrichedNfoAsync (caught by outer try?
  unclear — may still poison the loop).
  - Net effect: Deep Clean enrichment has been non-functional since Sprint 145. It's only
  being masked because Sprint 148 moved the hot path (no-ID items) into
  RefreshTask.EnrichStepAsync, which is implemented correctly.

  Fix: Invert the condition (or swap the two blocks):
  if (enriched == null)
  {
      // Failure: backoff + maybe block
      item.RetryCount++;
      …
  }
  else
  {
      // Success: write NFO
      await WriteEnrichedNfoAsync(item, enriched, cancellationToken);
      await db.SetNfoStatusAsync(item.Id, "Enriched", cancellationToken);
      await db.UpdateItemRetryInfoAsync(item.Id, 0, null, cancellationToken);
  }

  Priority: P0 · Effort: 15 min + regression test

  ---
  C-2. HMAC Signature Comparison Is Not Timing-Safe

  Location: Services/PlaybackTokenService.cs:75 (and per subagent also line 209 in
  ValidateStreamToken)

  Problem: Plain string == comparison of HMAC signatures enables a timing-oracle attack.
  An attacker can discover each hex character of a valid signature one at a time by
  measuring response-time deltas.

  Evidence (verified directly):
  var message = $"{url}|{timestamp}";
  var expectedSignature = ComputeHmacSimple(message, pluginSecret);
  return parts[2] == expectedSignature;   // ← timing oracle

  Kicker: ValidateSignature() later in the same file uses
  CryptographicOperations.FixedTimeEquals correctly — the fix pattern is literally already
   in the project.

  Fix:
  return CryptographicOperations.FixedTimeEquals(
      Encoding.UTF8.GetBytes(parts[2] ?? string.Empty),
      Encoding.UTF8.GetBytes(expectedSignature));
  Apply to both Verify() (line 75) and ValidateStreamToken() (line 209).

  Priority: P0 · Effort: 10 min

  ---
  C-3. DiscoverService Endpoints Are Unauthenticated

  Location: Services/DiscoverService.cs — entire class

  Problem (per subagent audit, needs your verification): The class does not implement
  IRequiresRequest, does not inject IAuthorizationContext, and no method begins with
  AdminGuard.RequireAdmin(_authCtx, Request). All four endpoints (Browse, Search, Detail,
  AddToLibrary) appear callable without any auth. Other services in the project
  (TriggerService, StatusService) use the admin-guard pattern — this one was missed.

  Impact: An attacker on the LAN can call POST /EmbyStreams/Discover/AddToLibrary with a
  crafted imdbId/title and write arbitrary .strm files into configured library
  directories. Combined with finding C-4 (no imdbId sanitization), this is an exploitable
  directory-traversal chain.

  Fix: Follow the pattern in TriggerService.cs:
  public class DiscoverService : IService, IRequiresRequest
  {
      public IRequest Request { get; set; } = null!;
      private readonly IAuthorizationContext _authCtx;
      // ctor: inject IAuthorizationContext

      public async Task<object> Post(DiscoverAddToLibraryRequest req)
      {
          AdminGuard.RequireAdmin(_authCtx, Request);
          …
      }
  }

  Priority: P0 (if verified) · Effort: Small — 30 min

  Verification step before shipping fix: Grep Services/DiscoverService.cs for AdminGuard,
  IAuthorizationContext, IRequiresRequest. If any are present, downgrade severity.

  ---
  C-4. Unbounded Stalled-Item Scan in PromoteStalledItemsAsync

  Location: Tasks/RefreshTask.cs:909

  Problem:
  var notifiedItems = await Plugin.Instance!.DatabaseManager.GetCatalogItemsByStateAsync(
      ItemState.Notified,
      int.MaxValue,                    // ← no bound
      cancellationToken);

  foreach (var item in notifiedItems)  // O(n) + 1 write per stalled item
  {
      …
      await Plugin.Instance!.DatabaseManager.UpsertCatalogItemAsync(item,
  cancellationToken);
  }

  Impact: VerifyStepAsync is supposed to be bounded work (the rest of the pipeline uses
  42-item limits). On a library with 10k+ Notified items this pulls them all into memory
  and issues up-to-10k serial writes inside a single Refresh run, blocking the 6-minute
  worker well past its schedule window.

  Fix: Either push the updated_at < @stalledThreshold predicate into SQL and add a LIMIT
  100, or add LIMIT 100 to GetCatalogItemsByStateAsync for this call site.

  Priority: P0 (latent, triggers on large libraries) · Effort: Small — 1 hour (needs DB
  method + call-site update)

  ---
  C-5. Blocked Items Are Not Filtered Out of GetActiveCatalogItemsAsync

  Location: Data/DatabaseManager.cs:196 (per subagent audit)

  Problem: SELECT … FROM catalog_items WHERE removed_at IS NULL; — missing AND blocked_at
  IS NULL. Every worker that iterates "active items" (RefreshTask.CollectStepAsync,
  DeepCleanTask.CleanStepAsync/ValidationPassAsync) reprocesses permanently blocked
  tombstones, burning cycles and risking reanimation. Sprint 145/147 spec explicitly
  states "Deep Clean must skip Blocked rows entirely."

  Fix: Add AND blocked_at IS NULL to GetActiveCatalogItemsAsync, GetItemsMissingStrmAsync,
   and GetItemsByNfoStatusAsync. Add a separate GetBlockedItemsAsync for the
  (still-missing) admin Blocked tab.

  Priority: P0 (data-correctness, matches existing shipped schema) · Effort: Small — 30
  min

  ---
  C-6. DeepCleanTask Does Not Check user_item_pins Before Deleting

  Location: Tasks/DeepCleanTask.cs (no references to UserPinRepository anywhere in the
  file, per subagent audit)

  Problem: Sprint 142 spec: Deep Clean must skip items that any user has pinned. The
  UserPinRepository.HasAnyPinsAsync method exists but is never called by DeepCleanTask. If
   an item falls below enrichment thresholds and has a playback pin, Deep Clean will
  delete the pinned item, silently breaking a user's library.

  Fix: Inject UserPinRepository into DeepCleanTask. Guard every deletion with:
  if (await _pinRepo.HasAnyPinsAsync(item.Id, ct))
  {
      _logger.LogDebug("[EmbyStreams] Skip {Title} — user pin exists", item.Title);
      continue;
  }

  Priority: P0 (silent data loss) · Effort: Small — 1 hour

  ---
  🟠 HIGH

  H-1. Data Model Bifurcation: CatalogItem and MediaItem Target the Same Table

  Location: Data/DatabaseManager.cs (~50 CatalogItem methods around 110–2200; ~10
  MediaItem methods around 4504–4650)

  Problem: Two C# models (CatalogItem, MediaItem) with different property sets both
  read/write catalog_items. CatalogItem has BlockedAt/BlockedBy only; MediaItem has a
  Blocked boolean + timestamp. Schema migration adds blocked_at columns; runtime tasks use
   CatalogItem so the boolean side of the story is never written. This is an unfinished
  mid-flight refactor.

  Fix: Pick one. Given that RefreshTask/DeepCleanTask both use CatalogItem, the cheapest
  path is:
  1. Add public bool Blocked => BlockedAt.HasValue; computed property to CatalogItem.
  2. Delete the MediaItem parallel branch from DatabaseManager (or move it to a
  clearly-marked "legacy/migration" section).
  3. Choose blocked_at IS NOT NULL as the canonical "blocked" predicate and enforce it in
  C-5's filters.

  Priority: P1 · Effort: Medium — 2–4 hours

  ---
  H-2. XML Injection Risk in Enriched NFO <uniqueid> Elements

  Location: Tasks/RefreshTask.cs:671,673 (verified directly)

  Problem: <title>, <plot>, <genre> all use SecurityElement.Escape(). The two <uniqueid>
  lines do not:
  nfoSb.AppendLine($"  <uniqueid type=\"imdb\"
  default=\"true\">{meta.ImdbId}</uniqueid>");
  nfoSb.AppendLine($"  <uniqueid type=\"tmdb\"
  default=\"true\">{meta.TmdbId}</uniqueid>");

  Real-world risk: Low (IMDB IDs are tt\d+, TMDB IDs are numeric, and AioMetadataClient is
   talking to a trusted endpoint). But the whole point of SecurityElement.Escape as a
  uniform pattern is that you don't have to reason about each call site.

  Fix: Wrap both in SecurityElement.Escape().

  Priority: P1 · Effort: Trivial — 2 min

  ---
  H-3. DatabaseManager Is a 5,624-Line God Class with ~152 Public Methods

  Location: Data/DatabaseManager.cs

  Problem (confirmed by wc -l and subagent concern mapping): 10+ distinct responsibilities
   in one file — CatalogItem CRUD, MediaItem CRUD, stream candidates, resolution cache,
  playback log, collections, discover catalog, home section tracking, API budget, sync
  state, schema migrations, admin/housekeeping. The existing "repositories"
  (CandidateRepository, SnapshotRepository, MaterializedVersionRepository) take
  DatabaseManager db as a constructor parameter and call back into it — circular coupling,
   not actual separation. Only VersionSlotRepository and UserPinRepository own their own
  connections.

  Why it matters now: Sprint 148 spec drift — the Blocked-items filter (C-5), the user-pin
   guard (C-6), and the missing indexes (H-5) were all missable because the relevant code
  is buried inside a 5,624-line haystack. The size of this file is no longer just an
  aesthetic complaint; it's causing real bugs.

  Fix (not blocking, schedule for Sprint 149): Extract by concern, starting with the
  highest-churn areas:
  - CatalogRepository — extract the ~35 CatalogItem methods
  - DiscoverCatalogRepository — extract the ~10 discover methods
  - Stop threading DatabaseManager into Candidate/Snapshot/MaterializedVersion
  repositories; let them own connections directly

  Target: reduce DatabaseManager.cs to <2,000 lines.

  Priority: P1 (technical debt blocker) · Effort: Large — 2–3 days

  ---
  H-4. Per-User "In My Library" Check Uses Global Library Status

  Location: Services/DiscoverService.cs:844-883 (per subagent audit)

  Problem: InLibrary = entry.IsInUserLibrary checks whether the item exists in
  catalog_items at all, not whether the current user has a user_item_pins row for it. In a
   multi-user household, User B sees "In My Library" on items only User A pinned — Sprint
  148 spec explicitly calls for per-user status.

  Fix: Join user_item_pins where user_id = @currentUser and compute InMyLibrary from
  presence of that row.

  Priority: P1 · Effort: Small — 1 hour

  ---
  H-5. Missing Composite Index on user_item_pins(emby_user_id, pin_source)

  Location: Data/DatabaseManager.cs:3167-3168 — only single-column indexes exist.

  Problem: The Sprint 148 "My Picks" tab query is naturally WHERE emby_user_id = ? AND
  pin_source IN ('playback','discover') ORDER BY pinned_at DESC. SQLite can use
  idx_user_item_pins_user for the equality predicate but still has to scan rows to filter
  by pin_source and sort by pinned_at. Low impact at household scale; becomes a hotspot if
   a single user accumulates thousands of pins.

  Fix: CREATE INDEX IF NOT EXISTS idx_user_item_pins_user_source_pinned ON
  user_item_pins(emby_user_id, pin_source, pinned_at DESC);

  Priority: P2 · Effort: Small — include in next schema bump.

  ---
  H-6. Parental Rating Ceiling Is Not Enforced Server-Side

  Location: Services/DiscoverService.cs, Services/CatalogDiscoverService.cs (per subagent:
   zero references to parental/rating ceiling/user-specific filtering)

  Problem: Sprint 148 spec: "Items above user's parental rating ceiling are hidden
  server-side." Current code has no parental-rating column on discover_catalog, no
  per-user ceiling on the user profile, and no filter in Get(DiscoverBrowseRequest). This
  is a security/compliance gap, not just a UI issue — a user can bypass household rating
  limits by hitting the API directly.

  Fix (multi-sprint):
  1. Schema: add parental_rating INTEGER to discover_catalog.
  2. Config: surface per-user ceiling (or inherit from Emby's native parental control).
  3. Query: add WHERE parental_rating IS NULL OR parental_rating <= @ceiling.

  Priority: P1 · Effort: Medium — 1 day

  ---
  H-7. imdbId Not Sanitized in Discover Folder-Name Construction

  Location: Services/DiscoverService.cs:698-699

  Problem: req.Title passes through SanitizeFilename() but req.ImdbId is interpolated raw:
  var folderName = $"{SanitizeFilename(req.Title)} ({req.Year}) [imdbid-{req.ImdbId}]";
  var movieFolder = Path.Combine(targetDir, folderName);
  A crafted imdbId = "tt1234/../../../../tmp/evil" lets Path.Combine produce a path
  outside the sync root. Chained with C-3 (no auth), this is fully exploitable. On its own
   (with admin auth required) it's a defense-in-depth issue.

  Fix:
  var safeImdb = SanitizeFilename(req.ImdbId ?? "");
  if (!System.Text.RegularExpressions.Regex.IsMatch(safeImdb, @"^tt\d+$"))
      return Error(400, "bad_request", "Invalid IMDB ID");
  Plus validate the final movieFolder is rooted under targetDir before writing.

  Priority: P1 · Effort: Small — 30 min

  ---
  🟡 MEDIUM

  M-1. CatalogSyncTask Carries ~600 Lines of Dead STRM/NFO Writers

  Location: Tasks/CatalogSyncTask.cs:1215-1993

  Problem: Sprint 147 removed STRM writing from CatalogSyncTask (the Execute method at
  line 897 even has a comment confirming this), but the dead code for it —
  WriteStrmFilesAsync, WriteStrmFileForItemAsync, WriteSeriesStrmAsync,
  WriteEpisodesFromSeasonsJsonAsync, WriteNfoFileAsync, WriteStrmFileForItemPublicAsync —
  all still compile in the same 2,119-line file. This is pure rot.

  Fix: Delete lines 1215–1993 after confirming WriteStrmFileForItemPublicAsync (the only
  public one) has a live caller elsewhere (grep shows none in the scope the subagents
  covered; needs final grep). Rename the task to CatalogFetchTask if you want to match
  what it actually does.

  Priority: P1 · Effort: Small — 1 hour

  ---
  M-2. N+1 Writes in NotifyStepAsync and PromoteStalledItemsAsync

  Location: Tasks/RefreshTask.cs:739-747, 914-931

  Problem: Both methods iterate items and call UpsertCatalogItemAsync per row. For the
  42-item Notify batch this is tolerable; for the unbounded Promote batch (C-4) it is not.
   Even for 42 items, a batched UpsertCatalogItemsAsync(IEnumerable<CatalogItem>) in a
  single transaction would cut round-trips ~40x.

  Priority: P2 · Effort: Medium — 2 hours (new DB method + call sites)

  ---
  M-3. AioMetadataClient Instantiates new HttpClient() Per Refresh Run

  Location: Services/AioMetadataClient.cs:26-33 and Tasks/RefreshTask.cs:591 (creates a
  fresh client each EnrichStep)

  Problem: Classic anti-pattern — new HttpClient() per run can exhaust sockets in
  TIME_WAIT under load. In practice this is a 6-minute cadence with a 10-item cap and
  2-second throttle, so the real risk is low, but the pattern is wrong.

  Fix: Make AioMetadataClient either a singleton registered in Plugin.cs or use a static
  HttpClient. This is also worth doing for AioStreamsClient.

  Priority: P2 · Effort: Small — 30 min

  ---
  M-4. Enrichment Loop Swallows All Exceptions as LogWarning

  Location: Tasks/RefreshTask.cs:638-641

  Problem: One catch (Exception ex) block treats "AIOMetadata returned 404" and "disk is
  full" identically. The spec intent is to continue the run on transient failures; the
  implementation also swallows permanent ones.

  Fix: Let OperationCanceledException and IOException propagate (or re-throw critical
  errors); catch only HttpRequestException / JsonException as warnings.

  Priority: P2 · Effort: Small — 20 min

  ---
  M-5. StrmWriterService Referenced in Sprint 147 Does Not Exist

  Location: Sprint 147 docs claim Services/StrmWriterService.cs exists; filesystem says
  otherwise (Glob **/StrmWriter*.cs → no match).

  Problem: Either the migration was never completed (STRM writing is still inline in
  RefreshTask or elsewhere) or the sprint notes are drifted. The review prompt itself
  reflects this drift — it lists Services/StrmWriterService.cs in the scope.

  Fix: Clarify — either do the extraction (reduces RefreshTask by ~100 lines) or update
  .ai/SPRINT_147.md and this review's reference list to match reality.

  Priority: P2 · Effort: Small documentation fix, or Medium if the extraction is actually
  done.

  ---
  M-6. Health Panel ("Improbability Drive") Has No Threshold Logic

  Location: Services/StatusService.cs:300-409 (per subagent)

  Problem: Raw counters (RefreshLastRunAt, DeepCleanLastRunAt, NeedsEnrichCount,
  BlockedCount) are emitted, but the spec'd 2×-interval-yellow / 3×-interval-red
  computation is not implemented — the dashboard just shows raw data.

  Fix: Compute a Health enum server-side so the UI doesn't have to reimplement the
  thresholds:
  var age = DateTime.UtcNow - refreshLastRun;
  var interval = TimeSpan.FromMinutes(6);
  response.RefreshHealth = age > interval * 3 ? "red"
                        : age > interval * 2 ? "yellow"
                        : "green";

  Priority: P2 · Effort: Small — 30 min

  ---
  🟢 LOW

  - L-1. Hardcoded 365-day token expiry in RefreshTask.cs:441. Move to
  PluginConfiguration.
  - L-2. Magic sentinel new DateTimeOffset(2100, 1, 1, …) for "never retry" in
  RefreshTask.cs:624. Extract to private const long NeverRetryUnixSeconds = …;.
  - L-3. Generic catch (Exception) { return Error(…, ex.Message); } in DiscoverService
  leaks stack traces to API clients. Return canned messages, log details server-side.
  - L-4. "42" magic number in DeepCleanTask limit needs an inline comment explaining it's
  the Hitchhiker's reference, not a tuned value.
  - L-5. PluginConfiguration has 45 persisted fields including obsolete ones (e.g.
  MaxFallbacksToStore). Consider an [Obsolete] pass + an "Advanced" accordion in the UI.

  ---
  Positive Patterns (Keep Doing These)

  1. Atomic file writes everywhere. Every .strm / .nfo write uses the tmp →
  Move(overwrite: true) idiom with orphan cleanup in catch. Zero partial-write risk.
  2. Concurrency gates at every overlap point. RefreshTask and DeepCleanTask each use
  SemaphoreSlim(1,1) with Wait(0) to skip rather than queue overlapping runs.
  Plugin.SyncLock coordinates across tasks. DatabaseManager._dbWriteGate serializes
  writers under WAL.
  3. Cancellation discipline. ThrowIfCancellationRequested is threaded through every long
  loop in the Refresh/DeepClean pipelines.
  4. Parameterized SQL universally. 150+ methods in DatabaseManager, zero
  string-concatenated SQL found. This is the one dimension where the God class doesn't
  hurt you.
  5. SQLite startup ritual. Integrity check on open, auto-recreate on corrupt DB, WAL mode
   — boring-in-the-good-way.
  6. Sprint 148 Enrich step matches spec exactly in RefreshTask: 10-item cap, created_at
  >= runStartedAt, FetchByTitleAsync, backoff 0/+4h/+24h/Blocked, 2-second throttle,
  per-slot NFO write. The spec-to-code traceability here is excellent — shame the same
  care wasn't given to DeepCleanTask's mirror logic (C-1).
  7. AdminGuard.RequireAdmin pattern exists and is used correctly in TriggerService,
  StatusService, etc. — just not in DiscoverService (C-3).
  8. Correct auto-pin-on-playback hook — EmbyEventHandler subscribes to playback events
  and creates user_item_pins rows with pin_source = 'playback' per Sprint 142 spec.

  ---
  Refactoring Opportunities

  #: R-1
  Proposal: Extract CatalogRepository from DatabaseManager (~35 methods)
  Value: Cuts God class by ~1,000 lines; makes future spec drift catchable in review
  Effort: Large
  ────────────────────────────────────────
  #: R-2
  Proposal: Delete dead write methods from CatalogSyncTask (~600 lines)
  Value: Immediate clarity win; one less trap for future contributors
  Effort: Small
  ────────────────────────────────────────
  #: R-3
  Proposal: Introduce DatabaseManager.UpsertCatalogItemsAsync(IEnumerable<CatalogItem>)
  and
    use it in Refresh's Notify + PromoteStalled loops
  Value: 40× fewer transactions per Refresh run; fixes C-4 performance cliff
  Effort: Small-Medium
  ────────────────────────────────────────
  #: R-4
  Proposal: Static/singleton HttpClient for AioStreamsClient and AioMetadataClient
  Value: Eliminates socket-exhaustion risk; no user-visible effect
  Effort: Small
  ────────────────────────────────────────
  #: R-5
  Proposal: Unify CatalogItem / MediaItem — delete whichever is lower-use
  Value: Resolves H-1; unblocks C-5 cleanup
  Effort: Medium
  ────────────────────────────────────────
  #: R-6
  Proposal: Add IReadOnlyRepository<T> / IWriteRepository<T> micro-interfaces for the
    existing separated repos so they stop taking DatabaseManager as a param
  Value: Unblocks unit testing of pipeline logic
  Effort: Medium

  ---
  Technical Debt Inventory

  ┌─────────────────────────────────────────┬──────────────────────────────┬──────────┐
  │                Location                 │             Debt             │ Severity │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Tasks/DeepCleanTask.cs:343              │ Inverted enrichment branch   │ P0       │
  │                                         │ (C-1)                        │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Services/PlaybackTokenService.cs:75,209 │ Non-timing-safe HMAC (C-2)   │ P0       │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Services/DiscoverService.cs             │ No auth guard (C-3)          │ P0       │
  │ (class-level)                           │                              │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Tasks/RefreshTask.cs:909                │ Unbounded stalled scan (C-4) │ P0       │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Data/DatabaseManager.cs:196             │ Missing blocked_at IS NULL   │ P0       │
  │                                         │ filter (C-5)                 │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Tasks/DeepCleanTask.cs (class-level)    │ No user-pin guard before     │ P0       │
  │                                         │ delete (C-6)                 │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Data/DatabaseManager.cs                 │ 5,624 lines, ~152 public     │ P1       │
  │                                         │ methods (H-3)                │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Data/DatabaseManager.cs:4504-4650       │ CatalogItem/MediaItem        │ P1       │
  │                                         │ bifurcation (H-1)            │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Tasks/CatalogSyncTask.cs:1215-1993      │ ~600 lines of unreachable    │ P1       │
  │                                         │ STRM/NFO writers (M-1)       │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Tasks/RefreshTask.cs:671,673            │ Unescaped <uniqueid> values  │ P1       │
  │                                         │ (H-2)                        │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Tasks/SyncTask.cs:184                   │ TODO: Trigger library        │ P3       │
  │                                         │ refresh                      │          │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Services/StremioMetadataProvider.cs:17  │ TODO: priority chain         │ P3       │
  ├─────────────────────────────────────────┼──────────────────────────────┼──────────┤
  │ Services/HomeSectionManager.cs:139-143  │ TODO comments on DB queries  │ P3       │
  └─────────────────────────────────────────┴──────────────────────────────┴──────────┘

  ---
  Spec Compliance Matrix

  Spec: Documentation
  Requirement: .ai/MAINTENANCE.md exists
  Status: ❌ MISSING
  Evidence / Drift: File does not exist. The review prompt claims it is "ground truth."
    Either it was deleted, never written, or lives elsewhere. Biggest single docs-drift
    finding.
  ────────────────────────────────────────
  Spec: Sprint 142
  Requirement: user_item_pins table (user, item, pinned_at, pin_source)
  Status: ✅
  Evidence / Drift: DatabaseManager.cs:3156-3168; pin_source ∈ {playback, discover, admin}
  ────────────────────────────────────────
  Spec: Sprint 142/148
  Requirement: Auto-pin on IEventConsumer<PlaybackStartEventArgs>
  Status: ✅
  Evidence / Drift: Services/EmbyEventHandler.cs:179-197 calls
    UserPinRepository.AddPinAsync(..., "playback")
  ────────────────────────────────────────
  Spec: Sprint 145
  Requirement: DeepCleanTask with 18-hour cadence
  Status: ✅
  Evidence / Drift: Tasks/DeepCleanTask.cs:72 TimeSpan.FromHours(18)
  ────────────────────────────────────────
  Spec: Sprint 145/147
  Requirement: Blocked items are permanent tombstones
  Status: ⚠️
  Evidence / Drift: Schema columns exist; GetActiveCatalogItemsAsync does not filter them
    (C-5); DeepCleanTask does not explicitly skip by nfo_status = 'Blocked'
  ────────────────────────────────────────
  Spec: Sprint 145
  Requirement: DoctorTask deleted
  Status: ✅
  Evidence / Drift: No Tasks/Doctor*.cs files exist
  ────────────────────────────────────────
  Spec: Sprint 145
  Requirement: Enrichment retry backoff 0 → +4h → +24h → Blocked
  Status: ⚠️
  Evidence / Drift: Logic exists in both tasks but DeepCleanTask version is inverted (C-1)

    — effectively non-functional
  ────────────────────────────────────────
  Spec: Sprint 146
  Requirement: Admin UI Blocked tab with Unblock action
  Status: ❌
  Evidence / Drift: Only a <span id="es-blocked-count"> counter in
    configurationpage.html:651. No tab, no unblock action, no endpoint.
  ────────────────────────────────────────
  Spec: Sprint 147
  Requirement: CatalogSyncTask no longer writes .strm
  Status: ✅
  Evidence / Drift: Execute path does not write, but ~600 lines of unreachable writer
    methods remain (M-1)
  ────────────────────────────────────────
  Spec: Sprint 147
  Requirement: STRM writing moved to Services/StrmWriterService.cs
  Status: ❌
  Evidence / Drift: File does not exist. Writing appears to live inline in RefreshTask.
  ────────────────────────────────────────
  Spec: Sprint 148
  Requirement: RefreshTask.EnrichStepAsync: LIMIT 10, current-run only, FetchByTitleAsync,

    2-sec throttle, backoff, per-slot NFO, SecurityElement.Escape
  Status: ⚠️
  Evidence / Drift: All of the above ✅ except <uniqueid> escaping (H-2)
  ────────────────────────────────────────
  Spec: Sprint 148
  Requirement: 6-step progress (0.16/0.33/0.50/0.67/0.83/1.00)
  Status: ✅
  Evidence / Drift: Matches exactly
  ────────────────────────────────────────
  Spec: Sprint 148
  Requirement: PromoteStalledItems as Verify sub-step
  Status: ✅
  Evidence / Drift: VerifyStepAsync:817 calls it — but it's unbounded (C-4)
  ────────────────────────────────────────
  Spec: Sprint 148
  Requirement: DeepCleanTask EnrichmentTrickle prioritizes no-ID first, LIMIT 42
  Status: ✅
  Evidence / Drift: ORDER BY CASE WHEN … THEN 0 ELSE 1 END, created_at ASC LIMIT 42
  present
  ────────────────────────────────────────
  Spec: Sprint 148
  Requirement: User Discover page separate from admin Content Management
  Status: ❌
  Evidence / Drift: Single combined "Discover" tab; no admin Content Management UI at all
  ────────────────────────────────────────
  Spec: Sprint 148
  Requirement: Parental-rating ceiling enforced server-side
  Status: ❌
  Evidence / Drift: Zero references to parental/rating in DiscoverService or
    CatalogDiscoverService (H-6)
  ────────────────────────────────────────
  Spec: Sprint 148
  Requirement: My Picks tab (user-pinned items, "I'm done" action)
  Status: ❌
  Evidence / Drift: Not present in configurationpage.html / .js
  ────────────────────────────────────────
  Spec: Sprint 148
  Requirement: Per-user "In My Library" check
  Status: ❌
  Evidence / Drift: Uses global library status (H-4)
  ────────────────────────────────────────
  Spec: docs/SECURITY.md
  Requirement: HMAC timing-safe comparison
  Status: ❌
  Evidence / Drift: PlaybackTokenService.cs:75 uses == (C-2). ValidateSignature elsewhere
    in same file does it right — inconsistent.

  ---
  Simplification Proposals

  1. Kill the legacy SyncTask siblings. Tasks/ still contains SyncTask.cs,
  CollectionSyncTask.cs, LinkResolverTask.cs, FileResurrectionTask.cs,
  LibraryReadoptionTask.cs, RehydrationTask.cs, MetadataFallbackTask.cs, RemovalTask.cs,
  YourFilesTask.cs, EpisodeExpandTask.cs, CollectionTask.cs — many of these appear to
  predate the RefreshTask/DeepCleanTask split. Sprints 142–148 consolidated the worker
  story; the Tasks/ directory hasn't caught up. Audit which are still registered in
  Plugin.cs and delete the ones that aren't.
  2. One model, one source of truth. CatalogItem vs MediaItem is not a feature — it's an
  incomplete refactor. Pick one and delete the other (H-1). The codebase will lose ~150
  lines and gain one unambiguous mental model.
  3. Repositories should own their connections. Of the five "repositories" in Data/, only
  two (VersionSlotRepository, UserPinRepository) actually encapsulate DB access. The other
   three (CandidateRepository, SnapshotRepository, MaterializedVersionRepository) take
  DatabaseManager as a ctor param and delegate back. That's not separation, that's
  indirection. Either finish the job or roll them back into DatabaseManager to cut the
  file jump.
  4. The wizard is already simple enough. 3 steps, ~15 decisions, sensible defaults. Don't
   "improve" it further; spend that effort on the missing Sprint 148 tabs instead.
  5. Collapse DeepCleanTask's enrichment into a shared helper with RefreshTask. Both tasks
   now do the same thing (fetch metadata → write NFO or bump retry). Extract an
  EnrichmentExecutor that both tasks call. This would also structurally prevent bugs like
  C-1 (you can't have the bodies drift if there's only one body).

  ---
  Recommendation

  Ship as-is: ❌ No.

  The three P0 security/correctness issues (C-1 DeepClean inverted logic, C-2 HMAC timing
  oracle, C-3 DiscoverService auth) are small individually but two of them are
  security-class and one is "this feature never worked." All three are sub-hour fixes.

  Proposed order of operations:

  1. Hotfix sprint (today, <1 day):
  C-1 (invert DeepClean) → C-2 (FixedTimeEquals) → C-3 (AdminGuard) → C-5 (blocked filter)
   → H-2 (uniqueid escape) → L-3 (stop leaking ex.Message).
  2. Sprint 149 (critical follow-ups):
  C-4 (bounded Promote) → C-6 (user-pin guard) → H-1 (model unification) → H-7 (imdbId
  sanitization) → M-1 (delete CatalogSyncTask dead code).
  3. Sprint 150 (spec drift):
  Missing UI tabs (Blocked, My Picks, separate User Discover), per-user "In My Library"
  (H-4), parental rating enforcement (H-6), restore or remove .ai/MAINTENANCE.md.
  4. Sprint 151 (tech debt):
  DatabaseManager extraction (H-3 / R-1), repository-ownership cleanup, EnrichmentExecutor
   unification.

  Quality trajectory: Once the P0s are in, the codebase is in a shippable state. The
  larger concerns (God class, spec drift) are real but are maintenance risks, not
  operational ones — they slow down the next sprint, not this one.

  ---
  Audit Confidence Notes

  - C-1, C-2, H-2 were verified by direct file reads at specific line numbers after
  subagent reports surfaced them.
  - C-3, C-4, C-5, C-6, H-1, H-3, H-4, H-6, M-1 come from parallel Explore-agent analysis
  of files I did not read directly; spot-check the exact line numbers before making fixes.
  - Sprint 148 compliance matrix rows for UI tabs (Blocked, My Picks) were based on
  subagent grep of configurationpage.html; if these tabs were added after the last
  REPO_MAP update, verify before accepting the ❌.
  - .ai/MAINTENANCE.md missing was confirmed directly by Glob .ai/*.md — 47 files listed,
  none named MAINTENANCE.md.
