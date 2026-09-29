# Import reconciliation QA — September 29, 2026


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
