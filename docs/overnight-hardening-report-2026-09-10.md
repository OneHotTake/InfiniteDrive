# InfiniteDrive end-to-end hardening report — 2026-09-10/11

## Outcome

InfiniteDrive 0.42.1 now implements the state-engine configuration model and is
verified against Emby 4.10.0.40 in the isolated Vault staging environment. The
runtime implementation passed 46 tests, catalog synchronization, managed-file
writing, HTTP byte-range playback, settings persistence, visual settings
inspection, and a cold container restart.

No private manifest, provider credential, signed stream URL, or staging password
is recorded here. The fully exercised runtime artifact was promoted to
production after the gates passed and the same E2E checks were repeated there.
The final release candidate also corrects production-only gaps hidden by the
original three-item acceptance fixture: provider-native TMDB catalog IDs,
20-item Stremio pagination, and external-list orphan cleanup.

## Final artifact

- Branch: `main`
- Release tag: `v0.42.1`
- Target: Emby 4.10.0.40
- Plugin: 0.42.1.0
- Release DLL SHA-256: `0e3afdacda28af000d51fa04f0212cdd8e4bf9221e25c3c635f1b0b0c2c76683`
- Production-verified predecessor SHA-256:
  `20f911595af63110aabd3715fe673e3471a5bc87753f088297c51a0fd0f37195`
- Staging: `/mnt/vault/apps/infinitedrive-staging`
- Latest staging rollback snapshot:
  `/mnt/vault/apps/infinitedrive-staging/backups/20260911-023020`
- Production pre-install rollback snapshot:
  `/mnt/fast/configs/emby/backups/infinitedrive-preinstall-20260911-022043`

## Implemented behavior

| Area | Result |
|---|---|
| Manifest state | Every non-empty Manifest 1/2 is an active peer for catalogs, search, and resolution; duplicate IDs collapse |
| Catalog-less manifests | Active MDBList/AniList/Trakt/etc. lists still run; a derived Cinemeta starter runs only when no list or manifest supplied content |
| Quality | REMUX and CAM/TS are independent, explicit opt-ins and default off; filtering occurs before caching and selection |
| Editions | Distinct edition representatives are retained first, then other desired versions, within Emby's fixed eight-version limit |
| Locales | Metadata, image, and certification preferences derive from Emby library state, with bounded `en`/`US` fallback |
| Episodes | Pre-warm selects exactly the next released, indexed Emby episode and ignores future placeholders |
| Provider pressure | Backoff and cache/probe state govern work; API call count remains telemetry, not a daily rules budget |
| Reconciliation | Owned physical media wins over an InfiniteDrive-managed resolver file; external virtual systems never drive deletion |
| Dynamic failover | No separately persisted movie/show secondary version is assigned; all configured manifests are evaluated at resolution time |
| Marvin UI | Automatic state summary plus **Run Marvin Now**; operational rules are no longer exposed as tuning knobs |

## Mycelium clarification

The earlier report incorrectly treated a Mycelium integration contract and the
live owned/Mycelium duplicate baseline as release gates. They are not.
InfiniteDrive never assumes Mycelium is installed and does not use it to choose
behavior. Mycelium can be studied as prior art, but an entry it creates is merely
an external duplicate from InfiniteDrive's perspective. Reconciliation is
strictly scoped to InfiniteDrive-managed files so another service cannot trigger
destructive behavior.

## Reference-project findings

- Gelato's future visibility and binge grouping informed the release-aware
  interpretation, without copying its configurable policy surface.
- Remux's Next Up and provider Retry-After handling reinforced using indexed
  release state and provider backoff.
- Riven's air-date/cache handling reinforced state-derived scheduling.
- `jf-resolve` and `jfresolve` were treated as implementation experiments, not
  requirements. Their dynamic failover ideas were deliberately not exposed as
  normal movie/show behavior.

## Verification matrix

| Gate | Result | Evidence |
|---|---|---|
| Exact-runtime restore/build/publish | PASS | SDK 8 build against assemblies extracted from the pinned Emby 4.10 image |
| Executable tests | PASS | 46 passed, 0 failed, 0 skipped |
| Configuration contract | PASS | Removed rule fields are absent; every remaining public setting persists |
| REMUX/CAM policy | PASS | Default rejection and explicit admission regression tests |
| Manifest peer behavior | PASS | Regression test covers every configured manifest as active |
| Catalog-less/list behavior | PASS | State table covers active-list, fetched-catalog, and starter-catalog cases |
| Staging plugin load | PASS | InfiniteDrive 0.42.1.0 loaded with no `TypeLoadException` |
| Catalog and files | PASS | Two manifest peers yielded 168 raw/84 deduplicated catalog rows; Geoff saw 65 Streamed Movies and 3 Streamed Series after the bounded production scan |
| Playback | PASS | A production 1080p non-REMUX candidate returned a 1 KiB HTTP 206 byte range |
| Cold restart | PASS | stop/start returned healthy and plugin entry points restarted |
| Production promotion | PASS | Final 0.42.1 artifact loaded; 65 movies and 3 series visible to Geoff, REMUX/CAM file counts zero, HTTP 206 and cold restart verified |
| Plugin coexistence | PASS | InfiniteDrive, Sportarr 4.1.7.1117 and Home Screen Companion 4.1.4.0 loaded together |
| External-path ownership | PASS | Mycelium path rejected by regression test; zero external watcher messages after final production load |
| Settings persistence | PASS | Allow REMUX was saved/reloaded on, then saved/reloaded off |
| Secret rendering | PASS | Manifest URLs, AIOStreams passwords, Trakt client ID, and TMDB key render as masked inputs in live Emby |
| Chromium visual review | PASS | Overview, Libraries, Quality, Restrictions, Marvin, and Advanced visually inspected; Providers/Sources inspected with secret fields obscured |
| Physical Apple TV session | NOT RUN | No physical-device session was available; server-side range prerequisite passed |

## UI findings

The final Quality page clearly describes the eight-version ceiling and shows
**Allow REMUX** and **Allow CAM/TS**, both off. Libraries contains only names and
paths and says locale choices follow Emby. Marvin contains no cadence, rate,
batch, or pruning controls. Provider secret inputs render as password fields.
Sources retains manifest catalogs, API-backed sources, system lists, and user
lists—the list path required for stream-only manifests.

The first production promotion was not a valid content acceptance test: it
retained a three-movie fixture cap, selected no series catalog, and configured
no external list. The follow-up live test found and fixed two additional bugs:
TMDB-only metas were dropped as non-IMDb, and MDBList files were written without
persisting ownership before orphan cleanup. The production run also showed why
initial series breadth must remain bounded: eager multi-version expansion of a
large long-running-series set can create thousands of provider calls. The live
initial series working set was reduced while completed items were retained.

## Rollback

For staging, stop only `infinitedrive-emby-staging`, restore the DLL, XML, and
database from the timestamped backup, then start that container and repeat the
health/plugin-load checks. For production rollback, stop only `emby-v410`, move
the InfiniteDrive DLL/XML/database and three managed library definitions aside,
restore `library.db` and retained plugin state from the pre-install snapshot,
then start Emby and verify the two pre-existing plugins. Preserve generated
resolver files until the rollback is accepted so the operation remains
recoverable.

## Delivery

The state-engine implementation, regression coverage, production-verification
documentation, and final Manifest 1/2 UI wording are published on `main` in
`OneHotTake/InfiniteDrive`. Historical commit IDs are intentionally omitted
because the repository underwent a credential-removal history rewrite.
A complete offline bundle is also retained at
`/Users/geoff/Documents/InfiniteDrive-codex-overnight-hardening-20260910.bundle`.
