# InfiniteDrive architecture

InfiniteDrive is an Emby-native virtual media state engine. Administrators
declare destinations, sources, quality intent, and restrictions. Runtime
behavior follows observed manifest, list, Emby, cache, playlist, and provider
state rather than a second layer of enable/failover rules.

## Data flow

```text
Manifest 1 ─┐
Manifest 2 ─┼─> catalog/list union ─> SQLite state ─> managed .strm + NFO
User lists ─┤                                  │
System lists┘                                  └─> Emby library scan

Emby play ─> AioMediaSourceProvider ─> filtered/ranked candidates
          └> OpenMediaSource ─> fresh signed provider URL ─> client
```

Every non-empty manifest is an active peer for catalogs, live search, and stream
resolution. Duplicate provider IDs collapse to one logical item. Lists are
independent catalog sources; a catalog-less manifest does not disable them. A
small Cinemeta starter is derived only when no active list or manifest produced
content.

## Marvin

`MarvinTask` is the only Emby-visible scheduled task. It orchestrates:

1. catalog and list ingestion;
2. queued `.strm`/NFO creation and series expansion;
3. stream pre-resolution and cache maintenance;
4. collection, pruning, and managed-file reconciliation.

Cadence, batch safety, playlist protection, absence thresholds, and provider
backoff are runtime policy or observed state. The UI exposes status and **Run
Marvin Now**, not a rules-engine control panel.

## Playback and quality

Candidates are filtered before caching and version selection. REMUX and CAM/TS
are excluded unless their independent Quality-page switches are enabled.
Edition representatives are selected first, then desired buckets and best
remaining candidates fill Emby's fixed eight-version ceiling.

There is no persisted dynamic movie/show secondary URL. Resolution evaluates
all configured manifest peers when needed. Provider backoff and cache expiry or
probe results control retries.

Episode pre-warming asks Emby for the next numbered, released, indexed episode.
It does not fabricate season counts or queue future-dated placeholders.

## Emby ownership

Emby library settings provide metadata language, image language, and
certification country. InfiniteDrive uses `en`/`US` only when Emby exposes no
preference.

Owned physical media wins over an InfiniteDrive-managed virtual resolver with
the same provider identity. Only files under configured InfiniteDrive roots are
owned by this policy. Mycelium and other external virtual libraries are ordinary
Emby state: InfiniteDrive neither requires them nor changes their files.

## Security

- Manifest URLs, AIOStreams passwords, Trakt IDs, and TMDB keys render masked.
- Per-instance AIOStreams passwords are used in memory and never persisted.
- Private manifest paths, signed URLs, query strings, and diagnostic text are
  redacted from plugin/test logs.
- Managed-file checks are structural and root-bounded.

## Build contract

`scripts/build-container.sh` extracts compile references from the pinned Emby
4.10.0.40 image, restores/tests in a .NET 8 SDK container, and publishes
`artifacts/InfiniteDrive.dll`. Do not reintroduce a server-core NuGet package;
an older package can compile successfully while producing a runtime ABI failure.

See [MARVIN_STATE_MACHINE.md](MARVIN_STATE_MACHINE.md),
[configuration.md](docs/configuration.md), and
[STREAM_RESOLUTION.md](docs/STREAM_RESOLUTION.md).
