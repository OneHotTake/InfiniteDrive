# InfiniteDrive configuration

InfiniteDrive is a state engine. Configure intent and connections; observed
manifest, Emby, cache, playlist, library, and provider state determine the rest.

The persisted XML is stored at
`{EmbyDataPath}/plugins/configurations/InfiniteDrive.xml`. Manifest URLs and API
keys are credentials and must never be copied into documentation or logs.

## Libraries

Set the display name and folder for Movies, Series, and Anime. InfiniteDrive
creates or follows those Emby libraries and writes its managed `.strm` files
there. Metadata language, image language, and certification country are derived
from the matching Emby library preference, then another configured Emby library,
then the conservative `en`/`US` fallback. There are no duplicate language fields
in the plugin.

## Quality

Desired-version buckets express resolution, audio profile, count, and priority.
InfiniteDrive preserves distinct editions before filling remaining slots and
never exposes more than Emby's fixed eight versions for an item.

- `AllowRemux` defaults to `false`. Enable it to admit REMUX candidates; it does
  not force them to rank first. This is deliberately visible because unusually
  large REMUX files commonly buffer.
- `AllowCam` defaults to `false`. Enable it only to admit CAM/telesync captures.

Both filters are enforced before caching and version selection, so a rejected
candidate cannot return through an alternate manifest or stale cache.

## Providers

`PrimaryManifestUrl` and optional `SecondaryManifestUrl` are active peers. Every
configured manifest contributes catalogs and search results. Stream resolution
may use either peer when one is unavailable. There is no "enable backup"
switch: presence is intent.

The labels "Manifest 1" and "Manifest 2" describe order, not primary/standby
behavior. Duplicate IDs are collapsed structurally. InfiniteDrive does not
dynamically swap a movie or episode to a separately stored "secondary version";
the provider set is evaluated when resolution is needed.

## Sources and lists

Manifest catalogs, MDBList, AniList, Trakt, TMDB-backed lists, system lists, and
per-user lists are independent discovery inputs.

A stream-only AIOStreams manifest is valid. Catalog synchronization therefore
does not stop when a manifest exposes zero catalogs:

1. active system and user lists still synchronize;
2. catalog items from every configured manifest are unioned;
3. if no manifest catalog and no active list supplies content, InfiniteDrive
   derives a small Cinemeta starter catalog for that run.

This prevents a healthy stream resolver from producing an empty Emby library.
When a real list or catalog exists, the starter is not added.

Provider-native IDs such as `tmdb:*` are valid catalog identities when the
manifest advertises them. InfiniteDrive retains that ID for stream requests,
captures an IMDb cross-reference from full series metadata when available, and
uses the cross-reference for Emby folder/file hints. External-list items enter
the same queued quality-selection pipeline as manifest items, so Marvin's
repair pass cannot discard them as untracked placeholder files.

`AioStreamsCatalogIds`, per-catalog limits, disabled source keys, and sync timing
are persisted source state. Provider API keys are optional and are needed only
for source APIs that require them.

## Restrictions

Discover/search restrictions are explicit user intent: hide unrated content,
the restricted-user unrated policy, default quality for manual additions, and
blocked items. Native Emby parental restrictions still apply to indexed media.

## Marvin

Marvin has one action: **Run Marvin Now**. Its normal cadence, work sizing,
provider backoff, pruning threshold, and playlist protection are internal or
derived state. The page intentionally exposes status rather than tuning knobs.

Owned physical media wins identity reconciliation over an InfiniteDrive-managed
virtual resolver file. The check runs before every streamed write using stable
provider identity; a match is stored with the real external file/directory path
and the catalog row remains `Retired` across later manifest and list updates.
Only an explicit resurrection check after that recorded owned path disappears
may queue it again. Only InfiniteDrive-managed files may be removed by this
process. Mycelium is neither required nor consulted; if its entries are visible
to Emby they are simply external duplicates.

Playback pre-warming queues exactly the next released episode that Emby has
already indexed. It does not fabricate future episode counts or expose future
buffer settings.

## Advanced

Advanced contains logging and reversible maintenance actions. Provider backoff
controls background API pressure; the database call count is telemetry, not a
user-configurable daily rules budget. Stream freshness uses bounded runtime
policy and observed expiry/probe state.

## Removed compatibility settings

The 0.42.1 hardening removed unused or rules-engine controls including
`EnableBackupAioStreams`, `AioStreamsAcceptedStreamTypes`, `EmbyApiKey`,
`LibraryRootMovies`, plugin-owned metadata/image/subtitle language fields,
`DontPanic`, future-episode switches, configurable stream-cache lifetime,
configurable API daily budget, configurable maximum versions, dynamic
secondary-version assignment, and extended-edition keyword rules.

Old XML elements are ignored by the current serializer. Back up the XML before
upgrading if rollback to an older DLL must preserve those values.
