# Import coverage and recovery

InfiniteDrive tracks expected episodes, managed STRM publication, and native Emby
indexing separately. An unchanged metadata list no longer strands an episode that
failed during an earlier import. Coverage is a dated observation; it does not
prove playback or a complete video file.

## Operation

The Marvin settings page has Observe, Enable recovery, Off, Check/retry, and
read-only Refresh coverage controls. Observe is the default for this feature.
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
retain their normal contents. Keep the database backup when rolling back binaries;
rollback does not require deleting the additive tables.

Empty, duplicate, or sharply reduced metadata responses retain the last snapshot
and block unsupported new writes. Metadata refresh is eligible after six hours;
failed refreshes retry after fifteen minutes. Removed upstream keys never delete
media. Absolute/unverified anime numbering is excluded rather than guessed.

## Limits and failures

Each Marvin execution begins with a slice limited to 120 seconds. It selects up
to ten due titles and forty round-robin catalog rows, checking at most 200 episode
keys per title. Episode and catalog cursors survive restarts. At most five remote
metadata refreshes and twenty stream attempts run per slice, with a rolling cap of
200 stream attempts per day. Ordinary version refresh uses the same attempt budget,
with at most five upgrades per slice and a minimum one-hour age. Gaps take priority.
All AIO stream requests share a concurrency allowance of two. Existing files are
eligible for refresh immediately when no successful refresh is recorded; mere
observation never marks their old selections fresh. Refresh resolves against the
current provider configuration and replaces files in place only after successful
selection/publication. A profile change does not require deleting the library.
Large libraries converge over multiple budgeted runs, not one instantaneous rebuild.

Individual network operations have a sixty-second deadline bounded by the slice.
Ten-minute leases are sufficient for this bounded worker; interrupted leases expire
before retry, and disk/native state is re-observed first. Network activity happens
outside the final publication lock. Authorization, settings generation, lease
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
  coverage, provider status, episode reasons, next retries, and collection summary.
- `POST /InfiniteDrive/Imports/Action`: `Action` is `mode`, `check`, `retry`,
  `include_specials`, `exclude_specials`, or `resume_provider`.
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
agreement before enabling Repair. No production rollout or franchise expansion is
part of this branch. Native indexing QA uses synthetic stream targets; it does
not claim debrid playback or externally verified complete series inventories.
