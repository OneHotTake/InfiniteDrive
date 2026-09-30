# Import reconciliation QA — September 29, 2026


## Episode numbering and fair reconciliation — September 30, 2026

Version 0.42.11: **213/213 tests passed**, zero skipped, against pinned Emby
4.10.0.40 and real SQLite. New regressions check per-key source/provider
agreement, duplicate and unverified numbering, legacy whole-series gate
reevaluation, identity safeguards, failed-refresh diagnostics/backoff across a
database reopen, alternating lane priority and authorization of deferred calls.

An isolated frozen beta used a new fixed fixture root and separate fixture DB.
With only episode 1 confirmed by the provider inventory, one episode group/two
STRMs published and indexed; episode 2 remained excluded with numbering conflict.
A native restart preserved both outcomes. Advancing the fixture clock and
restoring complete numbering published episode 2 and refreshed episode 1; four
STRMs then indexed as two matching native episodes. There was no fixture DB reset.
These synthetic targets verify publication and native identity/numbering/indexing,
not playback. Production was unchanged throughout this QA. The lab retains its
freeze, read-only physical-media mounts and network isolation; its prior artifact,
configuration and unrelated state are preserved. Temporary QA code never enters
the release DLL. Only the named fixture/library and temporary hidden administrator
are removed during cleanup; the private backup is retained.

## Scheduled cursor correction — September 29, 2026

All **126 tests passed** against the pinned Emby runtime and real SQLite, with
zero build warnings. The first live 0.42.7 catch-up pass stopped before dispatch: Emby's SQLite
provider bound the initial empty cursor as SQL NULL, violating the metadata
value constraint. 0.42.8 coalesces bound empty metadata values to an SQL empty
string. Added real-SQLite regressions exercise the default scheduled catch-up
path, stale-window cursor reset, existing-file priority, and empty-value readback.
A fatal coordinator failure now records a failed pass before propagating to
Emby's task result. Existing coverage, attempts and file state are preserved.
0.42.7 is superseded; its activation was not a completed refresh.


## Parallel catch-up and Marvin page — September 29, 2026

Version 0.42.7: **124 tests passed**, zero warnings, against pinned Emby
4.10.0.40 and its real SQLite provider. The added cases cover 64 overlapping
lookups with serial publication, sibling checkpoint preservation, stale-generation
rejection, cancellation joining every pending lookup, refresh before native
indexing completes, bounded gap filling, exact bytes independent of rounded
labels, and per-file evidence bound to the written target without storing URLs.
The catch-up cursor also handles inventories longer than a 200-key page.

Native frozen-beta UI checks verified the rewritten full Marvin page, last-pass
counts, released-item denominator, missing/retry/indexing/future descriptions,
Check-only activation refusal, Repair activation and disengagement. The old beta
artifact/settings and its saved report are restored after testing. The status
fixture created no media, stream targets or catalog intent. Freeze and mount/network
isolation remained enforced. This verifies native rendering and settings actions;
automated adapters verify the parallel worker, not whole-library playback.

A production-host read-only AIOStreams metadata benchmark measured 128 requests
with 64 workers and starts paced to two per second in 93.92 seconds: all 128
returned responses, 104 had streams, no transport/rate-limit errors. Median lookup
latency was 30.15 seconds. This is a dated source-capacity measurement, not a
completed STRM rebuild or a guaranteed duration on another host.


## Infinite Improbability Drive — September 29, 2026

Version 0.42.6: all **117 tests passed** against the pinned Emby 4.10.0.40
assemblies and real SQLite provider. Release compilation produced no warnings.
New cases cover finite-window admission/expiry, the 128-attempt slice and
100-upgrade limits, the persisted 8,000-attempt ceiling, once-per-window progress
across database reopen, indexed siblings of failed episodes, provider/ownership
exclusions, and disabling/expiring during resolution. A regression exposed
fractional timestamp formatting in SQLite JSON comparisons; the catch-up queue
now compares parsed times so completed items leave it correctly.

In the isolated, persistently frozen Emby beta, the actual native **Engaged**
switch rejected activation in Observe, activated in the controlled Repair test
precondition, retained its exact start/expiry across a beta restart, and switched
off with **Normality has been restored**. Existing admin browser login was used;
no maintenance user or credentials were created. The native beta switch test
never dispatched provider work. Existing freeze, network/mount isolation and
readonly production media remained enforced. Automated adapters exercise the
worker behavior; this native check verifies settings rendering and persistence.
Neither test establishes playback or full-library convergence.


Verified against Emby Server 4.10.0.40 on an isolated development instance.
Production was not deployed or restarted. The prior development plugin artifact
was restored after testing; temporary libraries, collections, media, database and
the password-protected QA account were removed. Backups and test evidence remain
in the private development build area.

## Automated checks

86 tests passed, zero failed or skipped. Release compilation and publication are
warning-free against the exact Emby SDK. The suite includes the existing 52 tests
and 34 new checks, using Emby's SQLite provider with real temporary databases.
Coverage includes durable unchanged-inventory retry, parent-state independence,
due-work selection, alias deduplication/conflicts, daily budgets, provider pause,
automatic refill, retained catalog aliases, prune/re-entry, concurrent blocks, indexing limits,
unsafe paths, and failed/empty/unchanged multi-version publication.

The versioned 0.42.3 release also verifies that observing existing files does not
mark old choices refreshed: the first eligible Repair slice resolves them against
the current provider configuration. The remainder of native QA below predates
that small adoption-time correction.

## Native Emby checks

| Check | Result |
|---|---|
| Observe two synthetic episodes | Zero resolutions and files; both gaps visible |
| Simulated episode-2 failure | Episode 1 published; episode 2 retained in backoff |
| Server restart with pending gap | Retry state and publication observations retained |
| Native child discovery | Episode 1 acquired matching native identity, numbering and path |
| Retry with identical expected inventory | Exactly one resolution and publication, for episode 2 |
| Subsequent native read-back | Two episode identities indexed across four version files; complete |
| Delete episode 1 | Exactly one automatic resolution/publication; episode 2 unchanged |
| Catalog prune | Zero resolutions/publications after removal from the catalog |
| Catalog re-entry | Exactly one resolution/publication; native indexing re-established |
| Explicit block | Zero resolutions/publications, also after restart and catalog re-entry |
| Existing native identities | Episode-1 native IDs retained through retry/refill |
| Provider API | `GetAllEpisodes` executed successfully; zero provider episodes in the offline fixture |
| Collection API | Seeded QA collection and membership confirmed by native read-back |
| Access controls | Anonymous and non-admin requests denied; admin accepted |
| Repair admission | Missing observation baseline rejected |
| Status reads | Repeated GET did not launch observation or repair |

The initial beta broad library scan was too slow to isolate this fixture promptly.
The test used Emby's `Folder.ValidateChildren` on the fixed fixture root with
`MetadataRefreshOptions`, then the production inventory reader checked actual
native records. The production worker uses library-monitor notifications plus a
bounded native scan fallback. Neither elapsed time nor a successful notification
was treated as indexed evidence.

These tests used controlled metadata and synthetic stream URLs. They verify
publication and native indexing, not debrid playback. The offline provider result
does not independently establish the completeness of a real upstream TV catalog.
The existing broader catalog pagination/truncation behavior and retention policy are outside this change. Tests check that recovery honors prune decisions; they do not re-audit every native watch/save/collection workflow.
Franchise expansion and partial owned-series filling remain out of scope.

## Production rollout correction

The first 0.42.3 production Observe run returned zero catalog rows. A new regression
reproduced this with the actual Emby SQLite provider: an empty initial cursor binds
as NULL, so `id > @after` cannot select the first page. 0.42.4 coalesces that cursor
to an empty SQL string and tests the default scheduler path without preselected
fixtures. Repair was not enabled on 0.42.3. Use 0.42.4 or newer.
