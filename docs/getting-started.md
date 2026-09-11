# Getting started with InfiniteDrive

InfiniteDrive is an Emby plugin that turns AIOStreams catalogs and external
lists into managed `.strm` libraries, then resolves playable streams on demand.

## Prerequisites

- Emby Server 4.10.0.40 (the ABI verified by release 0.43).
- One configured AIOStreams manifest.
- A working debrid provider configured behind that manifest.
- Writable movie, series, and anime folders that are visible inside Emby.

Treat every manifest URL as a credential. Do not paste one into logs, issues, or
screenshots.

## Install

1. Build with `./scripts/build-container.sh`, or obtain the matching release
   artifact.
2. Back up the existing InfiniteDrive DLL, XML configuration, database, and
   managed library folders.
3. Copy `artifacts/InfiniteDrive.dll` into Emby's plugin directory.
4. Restart Emby and confirm InfiniteDrive appears under **Dashboard → Plugins**.

The build script compiles and tests against the exact pinned Emby image; a
locally installed SDK alone does not prove ABI compatibility.

## Configure

Open **Dashboard → Plugins → InfiniteDrive**.

1. On **Libraries**, choose the names and writable paths for Movies, Series,
   and Anime. InfiniteDrive derives language, image, and certification choices
   from Emby.
2. On **Providers**, enter **Manifest 1**. Add **Manifest 2** only when you have
   one; every non-empty manifest is an active peer, not a disabled backup.
3. On **Sources**, choose manifest catalogs and add any MDBList, AniList, Trakt,
   or TMDB-backed lists. Lists remain active when a manifest exposes no
   catalogs. If no real source produces content, the first sync derives a small
   starter catalog so setup does not silently create an empty library.
4. On **Quality**, leave **Allow REMUX** and **Allow CAM/TS** off unless you
   explicitly want those candidates. Configure desired-version buckets if the
   defaults do not fit.
5. Save changed pages, then open **Marvin** and select **Run Marvin Now**.

There is no sync-schedule, backup-provider, API-budget, or future-episode rules
panel. Marvin derives cadence, batching, backoff, pruning protection, and next
episode warming from runtime state.

## Verify

1. Confirm the Marvin page reports configured libraries and at least one
   provider.
2. Confirm `.strm` and `.nfo` files appear beneath the configured roots.
3. Run an Emby library scan if indexing has not begun automatically.
4. Open a title and confirm the version picker excludes REMUX and CAM/TS while
   their switches are off.
5. Play a version and confirm playback starts. HTTP range playback should return
   `206 Partial Content` when tested directly.

If the library remains empty, first verify list/catalog selection and provider
reachability; a manifest with zero catalogs is valid and should not suppress
configured lists. Continue with the [troubleshooting guide](troubleshooting.md).

## Safe upgrade and rollback

Stop Emby before replacing the DLL. Preserve the previous DLL, configuration,
database, and managed folders together so rollback restores one coherent state.
Never delete external or physical media while diagnosing InfiniteDrive; its
reconciliation ownership is limited to files it manages beneath configured
roots.

See [configuration](configuration.md), [settings architecture](SETTINGS_ARCHITECTURE.md),
and the [hardening report](overnight-hardening-report-2026-09-10.md).
