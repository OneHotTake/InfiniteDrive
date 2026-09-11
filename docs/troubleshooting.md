# InfiniteDrive troubleshooting

Start with the **Overview** page in Emby's InfiniteDrive settings. It reports
provider, library, quality, and content readiness without exposing internal
rules. Manifest URLs and API keys are credentials: keep them out of screenshots,
commands saved to shell history, and issue reports.

## Plugin does not load

1. Confirm the DLL is in Emby's plugin directory and readable by the Emby user.
2. Confirm the server is Emby 4.10.0.40, the ABI used by the release build.
3. Restart Emby and inspect its startup log for `InfiniteDrive`,
   `TypeLoadException`, `MissingMethodException`, or dependency-load errors.
4. Rebuild with `./scripts/build-container.sh` rather than compiling against an
   arbitrary server-core package.

If an upgrade fails, stop Emby and restore the previous DLL, XML configuration,
database, and managed folders as one rollback set.

## Provider is unavailable

Use the masked connection tests on **Providers**. Every non-empty manifest is an
active peer; there is no primary/backup toggle. A failure on one peer does not
disable another configured peer or independent lists.

- A timeout or 5xx response leaves the peer temporarily unavailable.
- A 429 or `Retry-After` response pauses background work until provider backoff
  expires. Playback remains higher priority.
- Call counts in the historically named `api_budget` table are telemetry, not a
  configurable quota; only observed backoff affects work.

Do not replace a working manifest merely because it has no catalogs. A
stream-only manifest can resolve playback while lists supply library content.

## Library is empty

1. On **Libraries**, verify the configured roots exist, are writable by Emby,
   and are visible inside the server/container.
2. On **Providers**, test each configured manifest.
3. On **Sources**, confirm at least one manifest catalog or external list is
   enabled. Public MDBList and AniList sources need no provider credential;
   Trakt and TMDB-backed sources do.
4. Run **Marvin → Run Marvin Now**.
5. Confirm `.strm` and `.nfo` files appear under the managed roots, then start an
   Emby library scan.

Lists are evaluated even when every manifest advertises zero catalogs. If no
real catalog or list produces content, InfiniteDrive derives a small starter
catalog for that sync. If even the starter is absent, inspect sanitized logs for
network, database, or filesystem errors rather than enabling a nonexistent
catalog-failover rule.

## Items exist but metadata or artwork is missing

InfiniteDrive writes provider IDs and NFO scanner hints; Emby owns metadata and
artwork policy. Check that the item has a usable IMDb/TMDB/TVDB identity and
that the matching Emby library has metadata providers and language preferences
configured. Trigger an Emby metadata refresh after correcting library settings.

Do not add duplicate plugin language fields. InfiniteDrive derives metadata
language, image language, and certification country from Emby, with conservative
runtime fallbacks only when Emby exposes no preference.

## No playable versions

1. Test the provider connections.
2. Check whether the title's provider ID and episode coordinates are correct.
3. Inspect logs for provider backoff, `ContentMissing`, or failed range probes.
4. Remember that REMUX and CAM/TS are excluded by default. Enable either switch
   on **Quality** only if that format is genuinely acceptable.

Rejected REMUX/CAM candidates cannot re-enter through cache or another manifest.
When accepted candidates exist, InfiniteDrive preserves distinct editions and
then fills desired quality buckets up to Emby's eight-version ceiling.

## Playback buffers

Leave **Allow REMUX** off; exceptionally large remuxes are a common cause of
buffering even when their headline quality is highest. Prefer a smaller 4K or
1080p encode in the version picker. If all versions buffer, test client direct
play capability, server-to-provider throughput, and the selected URL's range
response independently.

A healthy direct stream normally accepts a small range request and returns
`206 Partial Content`. InfiniteDrive falls back from HEAD to a bounded range GET
when probing candidates.

## Next episode is not pre-warmed

InfiniteDrive asks Emby for the next numbered episode that is already indexed
and released. It deliberately ignores future-dated placeholders and does not
fabricate season counts. Verify the next episode exists in Emby with correct
season/episode metadata; there are no `SkipFutureEpisodes` or
`FutureEpisodeBufferDays` controls.

## Duplicate or owned media

Physical owned media wins over an InfiniteDrive-managed virtual file with the
same provider identity. Reconciliation may remove only files InfiniteDrive owns
beneath its configured roots. Mycelium and every other external virtual library
are ordinary duplicate state; InfiniteDrive never assumes they are installed
and never modifies their files.

If an unexpected file disappears, stop automated work, restore from the managed
folder/database backup, and verify the configured roots and ownership marker
before running Marvin again.

## Cache and rebuild recovery

Use **Advanced → Clear Stream Cache** when signed candidates are stale or probe
state is clearly wrong. Marvin will repopulate the cache. Use the scoped rebuild
action when catalog/database state must be reconciled with managed files.

Treat full reset as destructive: take a rollback backup first and verify the
displayed scope. Never point an InfiniteDrive root at a physical or external
media library.

## Logs and safe support bundles

Search the Emby server log for `InfiniteDrive`, `Marvin`, `CatalogSync`,
`PreCache`, and `StreamProbe`. Private manifest paths, signed URLs, query strings,
passwords, Trakt IDs, TMDB keys, and authorization headers must be redacted.
Report the plugin version, Emby version, timestamp, status class, and sanitized
message instead of the raw URL.

See [configuration](configuration.md), [provider health](PROVIDER_HEALTH_AND_CIRCUIT_BREAKER.md),
and [stream resolution](STREAM_RESOLUTION.md).
