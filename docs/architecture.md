# InfiniteDrive architecture

InfiniteDrive runs inside Emby. It combines upstream catalog/list intent with
Emby's existing library state, writes managed STRM/NFO files, and exposes source
choices through native media-source integration. AIOStreams resolves upstream
streams; metadata providers supply catalog and episode identities.

```text
Manifest catalogs + system/user lists
                ↓
         SQLite catalog and intent
                ↓
     Marvin → resolution → managed STRM/NFO files
                ↓                      ↓
   import coverage journal       Emby native indexing/playback
```

## Catalogs and ownership

Configured manifests are active peers. Lists remain independent inputs even if
a manifest exposes no catalogs. Stable provider identities deduplicate entries.
Owned physical movies and series take precedence; the plugin manages only files
under its configured roots. External media is not owned by InfiniteDrive.

Marvin coordinates ingestion, publication, cache work, collections and retention.
Transient titles can be pruned after leaving all catalogs unless watched, saved
or list/collection intent retains them. Explicit blocks prevent recovery.
Recovery does not override those retention decisions.

## Import recovery

`ImportReconciliationService` persists expected metadata, publication observations,
native indexing and retry state separately. `ImportInventory` integrates metadata,
Emby reads, resolution and publication. Additive SQLite tables hold coverage,
aliases and attempts independently of parent lifecycle state.

Observe reports gaps without recovery writes. Repair owns missing-file
materialization and bounded version refresh; corresponding legacy stages yield.
Catalog sync and retention continue. Indexing requires identity, numbering and
managed-path agreement, not merely a file on disk. See the
[recovery contract](import-reconciliation.md) for budgets, admission and exclusions.

## Playback and quality

Current STRM files contain resolved URLs and play through Emby. Stored versions
also feed `AioMediaSourceProvider`; the historical signed-resolver/OpenMediaSource
design is not the current implementation. `OpenMediaSource` explicitly rejects
use. Protect STRMs and cached version data as credential-bearing material.

Quality filtering excludes REMUX and CAM/TS unless explicitly enabled. The plugin
selects up to eight versions per catalog record. Emby can merge provider aliases
and display more than eight; this is not a universal client display limit.

## Settings and boundaries

Native Generic UI pages configure destinations, sources, quality, restrictions
and recovery. Emby owns library language, parental access, indexing and watch
history. Discover is an embedded web interface. Import administration requires
a native administrator session; status reads do not trigger work.

See [configuration](configuration.md), [security](SECURITY.md),
[developer guide](dev-guide.md) and [historical designs](archive/README.md).
