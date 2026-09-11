# InfiniteDrive documentation

## Start here

- [Getting started](getting-started.md) — installation and first setup.
- [Configuration](configuration.md) — the supported, state-driven settings contract.
- [Settings matrix](settings-matrix.md) — user intent, derived state, and evidence.
- [Architecture](../ARCHITECTURE.md) — current system design.
- [Troubleshooting](troubleshooting.md) — current operational checks and recovery.
- [Security](SECURITY.md) — credentials, playback URLs, and deployment boundaries.
- [Hardening report](overnight-hardening-report-2026-09-10.md) — exact 0.43 build,
  staging, production, UI, and rollback evidence.

## Current settings pages

InfiniteDrive uses Emby's native Generic UI:

| Page | Purpose |
|---|---|
| Overview | Readiness and setup guidance |
| Libraries | Movie, Series, and Anime names/paths; locale follows Emby |
| Quality | Desired versions, Allow REMUX, and Allow CAM/TS |
| Providers | Masked Manifest 1/2 active-peer connections and tests |
| Sources | Manifest catalogs, masked list credentials, system/user lists |
| Restrictions | Discover/search restrictions and blocked content |
| Marvin | Automatic-state summary and Run Marvin Now |
| Advanced | Logging and scoped maintenance |

## Feature guides

- [Discover](features/discover.md)
- [External lists](EXTERNAL_LISTS.md)
- [Anime identity](anime-id-handling.md) and [anime library setup](anime-library-setup.md)
- [Playback pipeline](REQUIRES_OPENING_PIPELINE.md)
- [Stream resolution](STREAM_RESOLUTION.md)
- [Catalog identity and deduplication](CATALOG_AND_DEDUPLICATION.md)

## Historical engineering documents

Documents not listed as current above, especially files with `Sprint`,
`FINDINGS`, `HISTORY`, `ANALYSIS`, `MIGRATION_PLAN`, or `*_SUMMARY` in their
names, preserve design history and investigation evidence. They may describe
removed settings, prototype APIs, older Emby versions, or superseded
architecture. They are not configuration instructions. Prefer the current
documents listed above whenever they disagree.

`failure-scenarios.md` is a pre-0.43 behavior inventory and is retained only as
historical test input.

Last updated: 2026-09-11.
