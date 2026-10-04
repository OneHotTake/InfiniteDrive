# Eventual source diversity — Marvin's durable queue

Implemented in 0.42.14; the 0.42.15 source fixes refresh/indexing deadlines and final-guard lease cleanup, targeting Emby 4.10.0.40. Deployment and dated QA evidence are recorded separately in the release notes and stack handoff. This queue improves already indexed items with one known delivery source. It does not guarantee a second source exists, represent a download, or establish playback.

## Enable, pause and ownership

`ImportProviderDiversityEnabled` defaults to **true**, including when an older configuration has no such XML member. The native Marvin page has **Enable eventual source diversity** and **Pause source diversity**. Both require Emby's normal settings administration. The administrator-only POST `/InfiniteDrive/Imports/Action` accepts `enable_diversity` or `disable_diversity`; GET never starts work. Neither action triggers Marvin, changes Repair/catch-up, clears backoff, deletes entries or expands source policy.

Diversity dispatch/publication also requires Repair mode. Off runs no coordinator. Observe can discover/reconcile durable queue entries but makes no diversity requests/publications; it is not a global read-only switch for the legacy importer. Pausing diversity retains all queue state and lets essential repair continue. Disabling during a lookup prevents its additive publication at the final guard, clears its lease and leaves a waiting entry with a five-minute guard-change deferral. It cannot undo an addition already published. Re-enable waits for normal scheduled passes and existing due times/guards. There is no force-reprocess-diversity or mass-reset action; the queue drains automatically. **Reprocess deferred items** is the distinct essential-failure recovery queue and does not bypass diversity due times.

## Unit, labels and eligibility

One entry belongs to `(import_coverage.Identity, ImportEpisode.Key)`: `movie` or confirmed `aired:S:E`. Several resolutions/releases from TorBox still count as one source. A series with ten TorBox-only and ten Usenet-only episodes has twenty single-source episodes, regardless of the series' aggregate labels.

Only current `Paths` and matching `Versions.Path` supply provider evidence. Every retained path must have exactly one unambiguous recognized label; missing/conflicting/unrecognized evidence is unknown. Retired version paths do not count. Recognized labels are TorBox, Usenet, Real-Debrid, AllDebrid, Premiumize, Debrid-Link, Offcloud, EasyDebrid, Debrider, Easynews, NZBDav, AltMount and StremThru; comparison ignores case. `nntp` and `stremionntp` normalize to Usenet. The Usenet label is a delivery lane, not a particular indexer; two labels do not prove independent infrastructure.

Eligibility requires a successful inventory snapshot, no coverage exclusion, Expected and Eligible true, indexed state, no essential Failure, and exactly one known source. Authoritative alias blocks/removals, incompatible identities and physical owned media are rechecked before dispatch and publication. No-source items remain essential missing-file repair. Future/special/disputed/ineligible keys stay excluded. A failed essential refresh retains its own failure/backoff and takes the item out of active diversity. Deliberately archived blocked-title references are not diversity gaps.

Before an outbound diversity lookup, production also verifies the retained file set, managed-root/symlink protections and each retained URL hash. A changed set/hash postpones this entry six hours as `retained_file_changed` without charging a request. Before writing, the same protections are checked again. Unknown **sizes** on working files do not authorize deletion and do not by themselves prevent an additive improvement; unknown **provider identity** prevents single-source classification.

## Durable schema, migration and generations

`ImportEpisode.Diversity` is an additive JSON object inside the existing `import_coverage.payload`; no new SQLite table or destructive migration is needed. Older rows deserialize with null Diversity. The episode's unique location makes census idempotent: repeated observations update one object, never append jobs. SQLite coverage saves are atomic document upserts under the existing `MutationGate`; only one native run holds `RunGate`.

| Field | Meaning |
| --- | --- |
| Status | `waiting`, `in_flight`, `capacity`, `resolved` or `retired` |
| Profile | SHA-256 endpoint fingerprint from the configured AIO client; no endpoint/token is copied into the entry |
| Generation | Coverage's authorization/specials generation at creation |
| Reason | Last eligibility, deferral, verification or completion reason |
| Streak | Consecutive diversity deferrals, separate from essential failure streak |
| Attempts | Reserved diversity checks, not indexer HTTP calls |
| CreatedAt | First entry creation for this profile/generation |
| CheckedAt | Last reservation or completed/deferred check |
| NextAttempt | Earliest allowed check; not a promised execution time |

Creation, endpoint/profile fingerprint change or coverage generation change creates one new entry with a **six-hour** initial wait and zero diversity streak/attempts. It does not reset the essential failure state, existing attempt ledger or provider cooldown. The fingerprint detects configured endpoint changes, not opaque AIO server-side policy edits at the same endpoint; those do not automatically shorten due times.

A successful ordinary single-source refresh with the same profile/generation temporarily sets the episode to `awaiting_indexing`. An existing diversity entry remains `waiting / awaiting_indexing`, preserving its CreatedAt, NextAttempt, Streak, Attempts and CheckedAt. Dispatch remains forbidden until the episode is indexed. Re-indexing restores `single_source` without postponing the deadline; repeated hourly refreshes do not restart the six-hour window. Hard failures, exclusions, missing/unconfirmed identities or changed profile/generation are not transient indexing and retain their existing retirement/new-entry rules. Entries already delayed by 0.42.14 are not bulk-reset or backdated: 0.42.15 prevents future drift, while their persisted deadline remains authoritative.

Dispatch claims the existing episode lease with a random ID and **ten-minute** expiry, persists entry `in_flight`, increments its Attempts and reserves the shared attempt credit before starting transport. Publication rechecks live generation, lease, enabled mode, authorization/ownership, current profile and a signature of retained paths/hashes/labels. A changed guard prevents addition. Final guard rejection immediately clears the completed diversity lookup’s own Lease and LeaseUntil before returning. Mode, disable, profile/generation or signature changes defer it as `waiting / guard_changed` for five minutes; blocked, owned, unexpected/ineligible, excluded or essential-failed work becomes `retired / ineligible`. These guard outcomes preserve reserved attempts and streak, do not record source health failures, and publish nothing. Missing coverage/episode means there is no current entry to clean up. A different current lease belongs to newer work and must never be cleared or changed. Every completed path still owning its lease clears it; budget cancellation leaves queue work waiting five minutes without penalizing the repair item. After a crash, a missing/expired lease on `in_flight` becomes waiting with a five-minute interruption delay when reconciled. Active leases cannot be stolen.

Markers `import_diversity_schema=1` and `import_diversity_enabled` are written by native passes for read-only reporting. Their timestamps/snapshots are not instantaneous UI control acknowledgements. Reading administrator coverage/status or the public collector never creates entries.

## Priority, pages, deadlines and request accounting

The queue uses **the existing native schedule**, not a new timer. All discovered missing, ordinary refresh and essential reprocess candidates are handled before diversity. Existing missing/refresh priority rotation remains unchanged. Diversity gets at most **two reservations per normal pass** or **eight per catch-up pass**, processes them serially and can only use capacity remaining in that pass's shared budget. Normal: 120-second pass, 20 total attempts, 5 ordinary upgrades, 200 rolling daily attempts. Catch-up: 480 seconds, 4,096 total attempts/upgrades, 40,000 rolling daily safeguard. Those are ceilings, not measured provider allowances. Diversity does not enlarge them or alter AIO/indexer concurrency.

The inventory cursor discovers entries in existing catalog scans; every pass also loads up to ten due diversity titles ordered by coverage check time and catalog ID. Episode candidates are ordered by NextAttempt, then identity/key. Existing observations check up to 200 keys per title. Discovery is incremental, so the dashboard need not have a queue entry for every single-source reference immediately after upgrade. An ongoing urgent backlog can starve diversity; there is no independent minimum quota or completion deadline. Unresolved entries remain beyond catch-up expiry under normal limits.

Diversity uses one maintenance AIO submission with the coordinator's lookup deadline (60 seconds normal, 120 catch-up), no transport retry. It selects a validated unfamiliar source from the response **before** quality-bucket selection can hide that source. AIO's configured providers/filters remain authoritative. It does not make separate per-provider/indexer searches. AIO fan-out can still make multiple downstream requests, so one logical reservation is not one indexer call.

The shared `import_attempts` reservation is persisted **before** transport and remains even if cancellation happens before HTTP submission. Counters therefore distinguish reserved attempts, actual `HttpRequests`/`HttpRetries`, matching/empty outcomes, saved variants and group publications. Diversity attempts are reported separately from MissingAttempts and RefreshAttempts; DiversityPublished counts additive movie/episode-group events and is included in total Published. It does not increment Refreshed because old choices were not replaced. Same-source matches can increase Matched without resolving diversity. Old reports lack these new counters and read as zero.

## Exact health and backoff rules

Every reservation requires provider maintenance not paused, global cooldown elapsed, the native source circuit not paused, per-item essential backoff/lease respected, available daily/pass credit and current authorization. For full diversity capacity, `ImportSourceHealth.RecoveryReady(now)` requires no pause, ResponsiveReplies >=3, MatchedReplies >0, LastResponseAt and LastMatchAt within 15 minutes, and ConsecutiveErrors ==0. These are AIO-path observations, not tests of every indexer. The existing responsive counter resets on a response gap longer than 15 minutes; it is not a separate timestamped rolling list of three responses.

If that readiness is stale/unproven, **at most one total reservation in the entire pass** may be a diversity probe. If essential work already reserved a check, diversity cannot add a stale-health probe. A paused/error circuit is not bypassed. Successful same-source replies are responsive source matches; empty usable selections are responsive empties. Transport/deadline/429/configuration retain the existing circuit semantics. No new all-provider health polling is introduced.

A diversity deferral increments its own Streak, regardless of prior reason. `same_source`, `source_unavailable` and `no_valid_addition` use this ladder: **6h, 24h, 48h, 72h, then 7d**. Transport, lookup deadline, HTTP429, provider configuration and publication failure use **15m, 1h, 4h, 1d, 3d, then 7d** at the current streak index. Delays multiply by random jitter from **0 through 20%** and then take the maximum with global cooldown, including longer provider Retry-After handling. The capped base is seven days; jitter can make the resulting wait 8.4 days. A different error type does not restart the diversity streak. Confirmed additional-source publication clears it. A new endpoint/generation creates a new entry as described above.

Slice cancellation, interrupted lease and changed publication guard wait five minutes without incrementing Streak. Changed retained file evidence waits six hours without a request or streak increment. Capacity is checked without making a request. Actual transport/empty/429/publication failures are stored **on the diversity entry**, without marking a working indexed item's essential repair failed, clearing its original Failure/NextAttempt, or moving LastVersionRefresh.

## State transitions

| Event / guard | Durable outcome | Reserved credit | Next check |
| --- | --- | --- | --- |
| New eligible single-source item | waiting / single_source | none | creation +6h |
| Same identity/profile/generation census | retains entry/streak/due time | none | unchanged |
| Ordinary single-source refresh awaiting Emby indexing | waiting / awaiting_indexing; no diversity dispatch | none for reconciliation; ordinary refresh retains its own reservation | unchanged |
| Re-indexed after that refresh | waiting / single_source; retained counters | none | unchanged |
| Eight current variants | capacity / version_limit | none | no diversity dispatch until room exists |
| Capacity becomes <8 | waiting / single_source | none | retained due time |
| Due + enabled Repair + guards + spare budget | in_flight; lease; Attempts+1 | one shared reservation | lookup deadline applies |
| Same-source or no usable additional selection | waiting; distinct reason; Streak+1 | retains reservation | no-source ladder + jitter/cooldown |
| Transport/deadline/429/config/publication failure | waiting; distinct reason; Streak+1 | retains reservation | error ladder + jitter/cooldown |
| Valid second source saved/evidence verified | resolved / multiple_sources; streak0 | retains reservation | null |
| Source/authorization/generation changes in flight | no addition; guard_changed if still live | retains reservation | +5m, then reconciliation |
| Block/ownership/ineligible snapshot | retired / ineligible; completed diversity lease cleared immediately if still owned | none beyond prior reservation | no dispatch |
| Multiple saved sources found by census | resolved / multiple_sources | none | no dispatch |
| Retired/resolved item later eligible with one source | waiting / single_source; no force reset of old streak | none | now +6h |
| Process/slice interruption | waiting / interrupted or slice_cancelled | prior reservation retained | +5m after reconciliation/cancellation |
| Disable, Observe, health pause, exhausted budget, future due | entry retained; no new dispatch/addition | none | guard must clear; original due remains |

## Additive publication, capacity and recovery

Only a new recognized label from the configured AIO response is accepted. The candidate must pass existing CAM/REMUX policy, have an HTTP(S) URL and **positive exact upstream SizeBytes <=40,000,000,000**. Rounded size text is insufficient. Unknown labels/sizes and ambiguous identities are never forced.

The queue adds **one** variant per resolved item. The real writer shares the folder lock with ordinary replacement and uses a deterministic URL-hash-suffixed Emby variant name. It verifies an existing identical target for idempotence or moves a temporary file with overwrite **false**. It never deletes/rewrites an existing STRM, including unknown-size originals or adjacent episodes. Existing hash/size evidence is retained; the new file gets its own URL-bound evidence. Catalog stored versions are merged instead of replaced. No metadata/source/media ownership is reassigned.

The existing eight-version limit remains. A single-source item already using eight slots is surfaced as capacity, not trimmed. Ordinary authorized refresh may later free a slot; diversity never does so itself. Normal refresh still has its original replacement semantics and can subsequently change source coverage, causing a new six-hour wait. Diversity does not guarantee permanent two-provider coverage against later source changes.

Filesystem publication and SQLite are not one atomic transaction. After a crash between writing an addition and checkpointing evidence, preserve the file. A census with an unbound/unlabelled added path classifies coverage unknown, retires unsupported diversity work and requires evidence review/ordinary safe refresh; it does not invent metadata, delete the orphan or publish duplicates. A completed lookup/pass is not completion: only verified additional saved evidence resolves the entry. A new alternate need not yet be indexed even when the original item remains indexed.

## Inspection, examples and rollback

Native Marvin displays the enabled/paused setting, diversity attempts and active/capacity episode entries (up to 50 displayed per title). Administrator GET `/InfiniteDrive/Imports` returns paged episodes, recognized provider labels and Diversity fields. The public Failure Library reads a sanitized saved projection: one-source filter/counts per item, actual queue states, safe reason, reserved checks, streak and next-check time. Missing entries are labelled **no native entry**, never fabricated queued. Failed/older-than-five-minute snapshots are historical. No provider URLs, file paths, credentials, profile fingerprints or authentication storage are exported to the public collector.

Examples:

- Three TorBox variants: one queue entry, initial wait six hours. A TorBox-only response defers at least six hours, leaves all three files and never starts an immediate loop.
- TorBox then validated Usenet: one additive STRM, old hashes/paths unchanged, entry resolved. This does not prove playback or a particular Usenet indexer worked.
- HTTP429: retain TorBox and store the actual reason; apply the current error rung plus jitter and any longer global cooldown. Native circuit also closes; no queue-wide reset.
- Outage: no calls while health/cooldown pauses. Afterwards one bounded shared probe while readiness is unproven; full 2/8 cap only with readiness and spare credits.
- Restart: waiting state/due/streak persist. An expired in-flight lease waits five more minutes and revalidates before another reservation.
- Block during lookup: final alias/ownership guards prevent publication, retire the diversity entry and immediately clear only its own lease; unblocking is never automatic.
- Endpoint change: a new six-hour entry for the new fingerprint; an old result cannot publish. Server-side edits at the same endpoint do not reset waiting times.
- Catch-up expires with 1,000 entries: they remain and contend only for normal remaining capacity. No window extension, quota increase or completion promise.

Before upgrade, take private current configuration and stopped-database backups. Prefer pausing diversity through the native control for behavior rollback; this preserves the queue and newer state. A binary downgrade never requires restoring an older database or deleting ledger/coverage/archives. Older binaries can ignore/drop unknown Diversity JSON members when rewriting coverage; preserve the current backup for audit and expect conservative rediscovery after re-upgrade, rather than restoring historical user state. Protect active playback/recordings during any approved Emby restart. The beta/isolated QA plugin must never be reused as a production artifact.

## October 4 lifecycle regression verification

The release-0.42.14 code failed seven new runtime regression cases: six guard changes retained their completed lease, and the first hourly refresh/re-index cycle postponed its diversity deadline. The patched coordinator passes six consecutive normal hourly refresh/re-index cycles, SQLite reopen and a due additive lookup; it immediately settles Observe/generation/block/ownership/Expected/Eligible changes, while leaving a replacement lease untouched. A policy regression also preserves due/streak through eight transient indexing cycles and confirms a hard exclusion still retires work. Full pinned-ABI test/build and production deployment evidence are recorded separately; synthetic URLs establish state and publication behavior, not playback.
