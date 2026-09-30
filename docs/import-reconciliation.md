# Import coverage and recovery

InfiniteDrive tracks expected episodes, managed STRM publication, and native Emby
indexing separately. An unchanged metadata list no longer strands an episode that
failed during an earlier import. Coverage is a dated observation; it does not
prove playback or a complete video file.

## Operation

The Marvin settings page calls the three modes **Check only** (Observe),
**Repair & refresh** (Repair), and **Classic importer** (Off). **Retry due items**
queues work; **Refresh status** reads the latest report. Check only is the default
for this feature.
Repair requires a successful observation baseline. Episode rows report gaps; series rows can include released specials. Unknown or
future release dates do not authorize new imports. Existing files remain visible.

Off and Observe retain the existing normal importer. Observe does not itself
resolve streams, publish files, or notify Emby. It is not a global read-only mode
for InfiniteDrive. In Repair, the durable worker owns normal import materialization,
gap recovery, and bounded version refresh; the legacy population/verification/
resurrection passes yield to it. New Discover intent queues work without writing
empty placeholder STRMs. Source catalog synchronization and owned-media policy
continue through the existing services.

Missing files, including previously published files and eligible gaps found on
migration, are automatically refilled while their title remains authorized.
Explicit blocks win. Recovery never creates a suppression merely because a file
is absent. Existing catalog pruning remains authoritative: transient titles can
leave the library after they disappear from every catalog unless retained by the
existing saved/list/collection or watched-content policy. A pruned title is not
recreated until catalog re-entry or new authorized intent. Another active source
keeps a shared title eligible. This feature does not change retention thresholds
or replace the pruning policy. Successful
native indexing requires matching provider identity, episode numbering, and a
managed path. A file alone is awaiting indexing. Multiple versions count as one
episode; an explicit native multi-episode range may cover multiple episode keys.

Provider IDs deduplicate source memberships. Conflicting IDs fail closed. Entire
owned series remain excluded, preserving the existing owned-first policy.
Collection recovery only attaches already imported native titles to authorized,
registered/marked InfiniteDrive collections. It does not complete franchises or
adopt unrelated collections by display name. User playlists retain their existing
membership contract.

The page shows the last recovery pass's stream-check and refreshed-item counts,
with relative check/retry times. The report covers that pass; it is not a running
whole-library completion counter. Unknown native state is displayed as needing
another check. The administrative API retains its technical field/state names.

## Metadata and persistence

`ImportInventory` wraps source metadata, Emby's in-process
`IProviderManager.GetAllEpisodes`, native library reads, the existing resolver and
version selector, and the STRM writer. The source inventory remains primary.
Provider-only keys require a compatible inventory with all source numbering keys
represented; ambiguous inventories stop new writes. Provider unavailability is
reported explicitly. The API choice follows the [Emby developer discussion](https://emby.media/community/topic/149945-getting-missing-episodes-from-a-series-and-movies-from-collections/). No `GetAllItems` franchise expansion is enabled.

Three additive SQLite tables implement the journal without changing existing
catalog state enum values or enrichment counters:

- `import_coverage`: one JSON document per canonical identity, containing catalog
  links, expected items, observations, cursors, leases and retry data.
- `import_aliases`: unique provider aliases mapped to canonical identities.
- `import_attempts`: the shared rolling daily attempt ledger.

The schema marker is `import_schema=1`; initialization is idempotent. Current run,
collection page, provider pause, and round-robin checkpoints use existing metadata
storage. Credential URLs and stream URLs are not included in the new coverage
journal or the public status response. Existing protected STRM/version stores
retain their normal contents. Each successful publication also retains per-file provider, resolution, codec
and exact byte size when the upstream response supplies it, bound to the current
STRM URL's SHA-256 hash. Rounded labels never establish an exact size. This evidence
contains no stream URLs, and allows a later audit to distinguish refreshed files
from old selections and files whose upstream size remains unknown.
Keep the database backup when rolling back binaries;
rollback does not require deleting the additive tables.

Empty, duplicate, or sharply reduced metadata responses retain the last snapshot
and block unsupported new writes. Metadata refresh is eligible after six hours;
failed refreshes retry after fifteen minutes. Removed upstream keys never delete
media. Absolute/unverified anime numbering is excluded rather than guessed.

## Infinite Improbability Drive

In **Plugins → InfiniteDrive → Marvin**, engage the **Infinite Improbability
Drive** switch for a temporary backlog refresh. **Repair & refresh** must already be enabled.
The switch shows **Engaged** and its expiry; switching it off displays
**Normality has been restored**. It automatically returns to normal after seven
days, including across server restarts. Switching an already-active drive on
again does not extend its window or reset its progress.

| Allowance | Normal | Drive engaged |
| --- | --- | --- |
| Recovery time per run | 2 minutes | 8 minutes |
| Stream attempts per run | 20 | 4,096 |
| Existing-version refreshes per run | 5 | 4,096 |
| Metadata refreshes per run | 5 | 1,000 |
| Stream attempts per rolling 24 hours | 200 | 40,000 |
| Concurrent recovery lookups | 1 | 64 |
| Starts per second in maintenance lane | — | 2 |
| Missing-file fills per run | 20 | Up to 4,096, sharing the total attempt budget |

These are ceilings, not promised throughput. Upstream latency, unavailable sources,
provider backoff, native indexing and the rest of Marvin's work determine actual
progress. The existing task schedule stays in place. Catch-up has a separate 64-slot AIO
maintenance lane, paced to two starts a second. Missing-file repairs and existing
version refreshes share its larger attempt allowance. Ordinary lookups retain their
shared two-slot lane. File publication and state changes remain serial. No extra
scheduler is started. Completed lookups publish between inventory checks, rather
than waiting for a full request batch or the end of the catalog page. Queued lookups are joined before a cancelled run releases
its lock; late results cannot publish after disengagement or a generation change.

A successfully refreshed movie/episode is skipped for the remainder of that drive
window. Its checkpoint survives a restart. Unfinished existing versions are
revisited alongside bounded missing-file repairs. Catch-up scans catalog rows with
existing managed paths first, using its own window/cursor; it also repairs eligible
gaps for these titles and due authorized work. It does not sweep the entire
unmaterialized catalog. The normal catalog
sweep resumes when catch-up ends. The cursor also revisits episode inventories
larger than a 200-key page. An unavailable
episode does not prevent refreshing its eligible siblings. Valid existing managed
files can be refreshed while native indexing is pending; publication still reports
awaiting indexing, and an identity mismatch prevents refresh. Failed attempts
retain their backoff; owned media, explicit blocks, pruning and publication checks
still apply. This switch changes recovery speed, not quality filters or retention.

Disengagement/expiry is checked before another stream attempt and again before
publication. A publication already inside its final lock can finish before the
switch action completes. Successful files stay; this is not a rollback. The shared
24-hour ledger is never cleared, so returning to normal after more than 200 recent
attempts defers further resolution until those attempts age out. Off/Observe suspend
the drive; returning to Repair within the original seven-day window resumes it.

## Limits and failures

At normal speed, each Marvin execution begins with a slice limited to 120 seconds. It selects up
to ten due titles and forty round-robin catalog rows, checking at most 200 episode
keys per title. Episode and catalog cursors survive restarts. At most five remote
metadata refreshes and twenty stream attempts run per slice, with a rolling cap of
200 stream attempts per day. Ordinary version refresh uses the same attempt budget,
with at most five upgrades per slice and a minimum one-hour age. Gaps take priority.
Ordinary AIO stream requests share a concurrency allowance of two; catch-up uses the bounded maintenance lane described above. Existing files are
eligible for refresh immediately when no successful refresh is recorded; mere
observation never marks their old selections fresh. Refresh resolves against the
current provider configuration and replaces files in place only after successful
selection/publication. A profile change does not require deleting the library.
Large libraries converge over multiple budgeted runs, not one instantaneous rebuild.

Ordinary recovery lookups have a sixty-second deadline. Catch-up permits 120
seconds including its paced dispatch wait; each HTTP operation still times out
at sixty seconds, and all work remains bounded by the slice.
Ten-minute leases are sufficient for this bounded worker; interrupted leases expire
before retry, and disk/native state is re-observed first. Stream lookups overlap outside the final publication lock. Each result merges
into the latest persisted episode state under that lock, preserving sibling
checkpoints and rejecting stale generations. Native identity/range observations
are cached only for the current slice; file existence and final ownership checks
remain live. Authorization, settings generation, lease
ownership and owned-media precedence are rechecked before publication. Blocks and
source removals coordinate with that lock. Files are written atomically and old
versions are removed only after every desired replacement is verified. Empty
results cannot delete working versions or neighboring episodes.

Transport failures back off for 15 minutes, 1 hour, 4 hours, then daily. Empty
eligible-stream results back off for 6 hours, then daily, then weekly after repeated
failures. Delays include up to 20% positive jitter and respect longer provider
cooldowns. Authentication failures pause recovery for the current provider
configuration; changing its configuration or explicitly retrying after a fix
releases the pause. Manual retry does not bypass normal time/budget limits.

Existing library-monitor notifications remain the first discovery mechanism.
The fallback is one coalesced native library scan per slice, never cancellation
of an existing scan. Each item receives at most three notifications, at least
thirty minutes apart. After those attempts and two hours, unresolved native
indexing becomes `indexing_attention`. Large or blocked Emby scans can therefore
leave publication pending; the worker does not repeatedly download/rewrite it.

## Administrative API

Both routes require a native administrator session through the existing
`AdminGuard`. A server API key without an administrator user is insufficient.

- `GET /InfiniteDrive/Imports?Offset=0&Limit=50`: read-only, bounded page; dated
  coverage, provider status, episode reasons, next retries, collection summary,
  speed, expiry and rolling attempt usage. LastRun includes elapsed seconds and
  publication/upgrade counts; they are not playback or completion claims.
- `POST /InfiniteDrive/Imports/Action`: `Action` is `mode`, `check`, `retry`,
  `include_specials`, `exclude_specials`, `resume_provider`, `start_catch_up`, or
  `stop_catch_up`. Catch-up actions need no extra fields; start requires Repair.
  Mode uses `Mode=Off|Observe|Repair`; specials actions use `Identity`. Existing
  block/unblock controls remain authoritative; there is no separate recovery suppression.

Actions return an operation identifier and queued/deferred status. They trigger
the existing locked Marvin path. They do not create another scheduler. Reading
status never creates tables, contacts providers, or triggers work.

## Verification and rollout

The regression suite uses the actual Emby SQLite provider with temporary databases,
plus controlled metadata/stream adapters. It covers unchanged inventory, partial
failure, database reopen, automatic refill, block/prune exclusions, metadata failure, future/unknown
releases, aliases and conflicts, native-index separation, budgets, provider pause,
leases, paths, and atomic writer failures. Run `dotnet test
Tests/InfiniteDrive.Tests.csproj -c Release` with the exact Emby SDK references and
the native SQLite library available. Release publication is `dotnet publish
InfiniteDrive.csproj -c Release -o artifacts`.

The optional `tools/ImportQa` source is excluded from release compilation. It is
only copied into a disposable, frozen beta build for native discovery, restart and
collection checks. It has fixed fixture roots and requires administrator access
and `INFINITEDRIVE_LAB_FREEZE=1`. It must never be included in a production artifact.

Start production rollout in Observe and review dated exclusions and source/provider
agreement before enabling Repair. The feature shipped in 0.42.4; franchise expansion remains outside scope. Native indexing QA uses synthetic stream targets; it does
not claim debrid playback or externally verified complete series inventories.
