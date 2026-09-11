# Architectural overview

The canonical architecture is [the root architecture document](../ARCHITECTURE.md).
This page summarizes the contracts most relevant to maintainers.

## Contracts

- `CatalogSyncTask` is the only upstream catalog reader. It unions all active
  manifest peers and independent list sources into durable catalog state.
- `RefreshTask` consumes queued state; it must not recrawl manifests.
- `MarvinTask` is the only registered scheduled task and coordinates catalog,
  write, resolution, and repair phases.
- `AioMediaSourceProvider` supplies filtered Emby media sources and opens a
  selected source using fresh provider state.
- `StreamCacheService` and the database store resolution state. Provider
  backoff, expiry, and probes—not a configurable daily budget—govern refresh.
- `LibraryProvisioningService` creates/follows the three configured Emby roots.
  Locale and certification preferences come from Emby.
- `LibraryPostScanReadoptionService` may reconcile only InfiniteDrive-managed
  virtual files. External virtual libraries are outside its ownership boundary.

## Invariants

1. A configured manifest participates; an empty manifest field does not.
2. A zero-catalog manifest is valid and does not suppress lists.
3. REMUX and CAM/TS remain explicit, default-off user intent.
4. Distinct editions are preserved inside the eight-version ceiling.
5. Transient provider failure cannot become evidence for deletion.
6. Provider IDs are identity authority; fuzzy title matching is not.
7. Credentials and signed URLs must remain masked and log-redacted.
8. Mycelium is optional prior art/external state, never a dependency.

## Verification

The release build is produced by `scripts/build-container.sh`. The 0.42.1
state-engine revision passed 44 automated tests and an Emby 4.10.0.40
configure→sync→`.strm`→HTTP 206 playback loop in staging and production. See the
[hardening report](overnight-hardening-report-2026-09-10.md) for evidence and
rollback locations.
