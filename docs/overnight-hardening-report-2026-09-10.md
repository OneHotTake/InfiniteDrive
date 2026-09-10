# InfiniteDrive overnight hardening report — 2026-09-10

## Outcome

The 0.43.0 plugin is buildable against the exact Emby 4.10.0.40 runtime,
passes 20 executable regression tests, loads in an isolated Emby instance,
completes a bounded live-manifest Marvin run, rejects CAM/REMUX auto-selection,
serves byte ranges, and survives restart. Production deployment was deliberately
withheld: the live 46-group owned/Mycelium duplication baseline remains 46, and
the maintained Mycelium source does not expose the stable service API required
to make InfiniteDrive the policy layer for those independently generated items.

This is a failed production gate, not an unreported pass. Production Emby was
not restarted or modified.

## Environment

- Source: `https://github.com/OneHotTake/InfiniteDrive`
- Starting commit: `e43e13d1e6a6958395cdd7ec9beb8b4bf7e754a2`
- Working branch: `codex/overnight-hardening-20260910`
- Target: Emby 4.10.0.40 on TrueNAS Vault
- Exact target image: recorded in `scripts/build-container.sh`
- Maintained Mycelium source inspected at commit
  `298e94da7801073604300170ad35a9605b5a53fe`
- Tested plugin: 0.43.0.0
- Tested SHA-256:
  `eac10f6277650684ae131658aba1c87e1da343cf54ce009851f77ac7e52dfb3d`

Private manifest URLs, tokens, signed CDN URLs, and staging credentials are
intentionally absent from this report and repository.

## Repaired defects

| Defect reproduced | Repair | Regression/evidence |
|---|---|---|
| NuGet `MediaBrowser.Server.Core` silently overrode target references, producing a 4.9 ABI DLL that failed on Emby 4.10 with `TypeLoadException` | Removed the package and compile against exact runtime assemblies; restored assembly metadata generation | Clean build references 4.10.0.40; staging loads 0.43.0.0 with zero type-load errors |
| Authenticated manifest tokens containing `/` were truncated | Parser preserves every credential segment before `manifest.json` | `AuthenticatedManifestTokenMayContainSlashes` |
| Secondary manifest was used even when backup was disabled | All provider construction/fallback paths honor `EnableBackupAioStreams` | `BackupProviderRequiresExplicitEnableFlag`; staging showed primary 1 catalog, secondary 0 under the shared allowlist |
| Populate polled and recrawled every primary manifest catalog, bypassing allowlists, caps, backup policy, and sync interval | `CatalogSyncTask` is now the sole upstream catalog reader; Populate consumes the durable queue | `PopulateConsumesOnlyQueuedOrDueExpansionWork`; final log contains zero legacy fan-out messages |
| CAM and non-opted-in REMUX entries could be selected, including as secondary fallback URLs | Central filtering applies before version selection, fallback assignment, live resolution, and pre-cache | Auto-selection and resolver regression tests; staging generated 0 CAM and 0 REMUX paths |
| Signed manifest/CDN URLs could leak through URL logs, exception chains, and ffprobe stderr | Added URL/text redaction and removed unsafe exception detail logging | URL and diagnostic-text redaction tests; staging manifest-pattern log scan = 0 |
| `AutoDeduplicatePhysicalMedia` did not control the post-scan service; reconciliation was capped at 500 and lacked a scheduled safety net | Honor the toggle, remove the cap, and run reconciliation in Marvin repair phase | Source inspection and successful registered Marvin execution |
| Episode URL generation percent-escaped structural `:` separators | Escape only ID components, retaining the Stremio episode path contract | `LegacyIdentityAndUrlContractsRemainValid` |

## Test matrix

| Gate | Result | Concrete evidence |
|---|---|---|
| Clean restore/build | PASS | Fresh `bin`, `obj`, test output, and artifacts removed; SDK 8 container restored and published with zero warnings/errors |
| Executable tests | PASS | 20 passed, 0 failed, 0 skipped |
| Configuration persistence | PASS (serialization only) | Reflection test confirms every public configuration property has `DataMember` |
| Settings runtime wiring | FAIL | [Settings matrix](settings-matrix.md) identifies 25 compatibility/UI properties without a non-UI runtime reference |
| Six supplied manifest contracts | PASS | All six manifests parsed; catalog counts observed were 117, 53, 0, 0, 17, and 113; stream-only manifests remained valid |
| Representative movie streams | PASS | All six returned direct candidates for `tt0111161` |
| Representative episode streams | PASS | Two representative manifests returned 160 and 136 direct candidates for `tt0903747:1:1` |
| Staging plugin load | PASS | Emby 4.10.0.40 loaded InfiniteDrive 0.43.0.0; no `TypeLoadException` |
| Scheduled task registration | PASS | `InfiniteDriveMarvin`, 10-minute interval |
| Bounded catalog sync | PASS | Allowlist selected 1/117 primary and 0/53 secondary catalogs; cap produced 3 queued items |
| Marvin completion | PASS | Final isolated run status `Completed`; eight small resolver files written for one playable title |
| Quality rejection | PASS | 0 CAM and 0 REMUX generated paths with REMUX opt-in false |
| Private URL log scan | PASS | 0 full authenticated-manifest patterns in final staging log |
| Byte range / seek prerequisite | PASS | Non-zero range returned HTTP 206, exactly 1,024 bytes, after one redirect |
| Restart recovery | PASS | Eight resolver files before and after restart; server healthy; zero type-load errors |
| Apple TV / physical client | BLOCKED | No physical-device session was available; server-side direct-range prerequisites passed |
| Mycelium policy integration | FAIL | Maintained source exposes UI/session endpoints and token playback, but no stable InfiniteDrive catalog/resolver contract |
| Live owned/Mycelium deduplication | FAIL | Read-only Emby DB query reconfirmed 46 IMDb movie groups spanning owned and Mycelium roots |
| Production deployment | NOT RUN | Withheld because the two preceding required gates failed |

## Settings inventory

The generated [settings matrix](settings-matrix.md) inventories 83 public
properties with defaults, UI exposure, persistence, runtime source references,
test evidence, and staging status. It is regenerated with:

```bash
ruby tools/generate-settings-matrix.rb > docs/settings-matrix.md
```

The 25 no-runtime-reference rows are release debt. Some are stored compatibility
or display fields; others are user-facing controls that must be wired or removed
before calling the complete settings gate green.

## Staging deployment evidence

- Isolated container: `infinitedrive-emby-staging` (stopped after testing)
- Isolated port: 18067
- Staging root: `/mnt/vault/apps/infinitedrive-staging`
- Artifact ownership: UID/GID 1000:1000 after final install
- Final plugin database: 475,136 bytes
- Generated resolver files: 8; each remained small (no duplicated media bytes)
- Final task duration: bounded run completed successfully

Staging rollback data was retained:

- `/mnt/vault/apps/infinitedrive-staging/emby-config/data/InfiniteDrive/infinitedrive.db.pre-clean-20260910-172329`
- `/mnt/vault/apps/infinitedrive-staging/emby-config/data/InfiniteDrive/infinitedrive.db.pre-filter-20260910-172613`
- `/mnt/vault/apps/infinitedrive-staging/emby-config/data/InfiniteDrive/infinitedrive.db.pre-final-20260910-173408`
- `/mnt/vault/apps/infinitedrive-staging/media-acceptance-backups/`

To restore a staging checkpoint: stop the staging container, move the current
database and media directory aside, copy the selected checkpoint back as
`infinitedrive.db`, restore its paired media directory, ensure UID/GID 1000:1000,
then start only `infinitedrive-emby-staging`.

## Production and rollback

Production had no InfiniteDrive DLL, XML, or database before this pass and was
left in that state. Sportarr Metadata and Home Screen Companion remained present.
Because no production change was made, production rollback is “no action.”

## Branch delivery

- Branch: `codex/overnight-hardening-20260910`
- Runtime hardening commit: `96bc57a`
- Build/test foundation commit: `4488b64`
- Evidence/documentation commit: `af33a13`
- Push status: blocked by GitHub authorization. The active account
  `geofftrembley` has `READ` permission on `OneHotTake/InfiniteDrive`; the
  server rejected the push with HTTP 403.
- Complete verified Git bundle:
  `/Users/geoff/Documents/InfiniteDrive-codex-overnight-hardening-20260910.bundle`

After repository write access is granted, push without rewriting history:

```bash
git push -u origin codex/overnight-hardening-20260910
```

## Required next engineering step

Define and implement a versioned, authenticated, LAN-only Mycelium contract that
returns provider IDs, virtual-item identity, and durable resolver URLs without
exposing TorBox credentials. Then add an InfiniteDrive provider/reconciler that:

1. groups owned and Mycelium items by provider ID;
2. makes the owned file the preferred Emby version;
3. preserves deliberately distinct editions;
4. keeps the virtual version as reversible fallback;
5. survives Mycelium downtime without pruning; and
6. proves the live duplicate count falls from 46 and remains down after another
   Mycelium refresh, Emby scan, and restart.

Only after that integration and the 25 settings-wiring findings are resolved
should the checksum-tested artifact cross the production gate.
