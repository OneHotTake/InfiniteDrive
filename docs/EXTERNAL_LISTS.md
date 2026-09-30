# External lists

External lists are first-class catalog sources, independent of AIOStreams
manifest catalogs. This is essential for stream-only manifests that advertise no
browsable catalogs.

## Supported sources

| Source | Credential | Typical input |
|---|---|---|
| MDBList | None for public lists | Normal public list URL |
| AniList | None for its public API; cross-ID enrichment may use configured metadata services | User/list URL |
| Trakt | Server-configured Trakt client ID | Normal Trakt list URL |
| TMDB | Server-configured TMDB API key | List or collection URL |

The Sources page masks provider credentials. System lists are shared catalog
intent; user lists belong to their Emby user and may create native playlist
state. Adding or refreshing a list fetches it through `ListFetcher`, normalizes
provider IDs, and records its sync/error state.

## MDBList media identity

MDBList entries retain their IMDb ID, media type and release year. Public JSON
arrays use `mediatype` (`show` becomes `series`) and `release_year`; grouped
responses use the `movies` and `shows` buckets when an item omits its type.
A contradictory type or an untyped array entry is rejected rather than routed
to the movie resolver.

This parser change prevents misclassification of newly imported rows. Refreshing
a list does **not** automatically convert an existing movie row into a series.
Previously misclassified rows need a separate, backed-up repair of their media
type and managed destination. Existing movie STRMs must never be renamed into
fake episodes. Preserve list memberships, user blocks, retry history and owned
media; verify episode imports separately from playback.

## Catalog-less behavior

Catalog synchronization evaluates list state before deciding that no providers
exist. Therefore:

1. active lists synchronize even when every manifest has zero catalogs;
2. list and manifest items are unioned and duplicate provider IDs collapse;
3. if neither an active list nor a manifest yields content, a small Cinemeta
   starter catalog is derived for that run;
4. the starter is not added when real list or manifest content exists.

## Limits and safety

List limits restrict creation, not the validity of already stored list state.
Provider errors retain prior durable state and are retried; an empty or failed
fetch must not silently erase a valid library. Manifest URLs and list
credentials must never be copied into issue reports or screenshots.
