# Settings and verification

Source review: September 29, 2026, release 0.42.7. The table identifies current
settings and verification scope. It does not claim a new live UI acceptance pass.
The September 11 staging matrix remains in the [archive](archive/settings-matrix.md).

| Intent | Page / owner | Behavior | Evidence |
|---|---|---|---|
| Movie, Series, Anime destinations | Libraries | Managed names and roots | Native settings source |
| Metadata language/artwork/certification | Emby libraries | Library preference with fallback | Existing preference tests |
| Desired versions | Quality | Per-record selection, up to eight; aliases can merge in Emby | Existing selection tests |
| REMUX and CAM/TS | Quality | Independent opt-ins, default off | Existing filter tests |
| Manifest peers | Providers | Nonempty manifests supply sources | Existing peer tests |
| Catalogs and system/user lists | Sources | Source intent, membership and limits | Source and existing catalog tests |
| Discover restrictions and blocks | Restrictions | User restrictions and explicit exclusion | Service/settings source |
| Run Marvin | Marvin | Existing orchestrator | Task/settings source |
| Check only / Repair & refresh / Classic importer | Marvin | Observe baseline required before Repair | Recovery regression and dated native QA |
| Check/retry and coverage refresh | Marvin | Queued actions versus read-only status | Recovery regression and dated native QA |
| Provider pressure | Runtime | Backoff and bounded recovery attempts | Recovery regression |
| Owned-media precedence | Emby identity + managed paths | Physical media excludes managed duplicates | Existing ownership tests |
| Logging and maintenance | Advanced | Explicit operator actions; reset/rebuild require scope review | Settings source |

Recovery has normal limits and a seven-day Infinite Improbability Drive switch for larger catch-up allowances. See the limits table in the recovery guide.
See [recovery](import-reconciliation.md), [configuration](configuration.md),
[QA evidence](import-reconciliation-qa.md) and the [developer guide](dev-guide.md).

`tools/generate-settings-matrix.rb` emits a separate source-reference inventory.
A textual reference is not proof of execution, persistence or successful live use.
