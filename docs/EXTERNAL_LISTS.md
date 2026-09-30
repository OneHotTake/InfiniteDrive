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

## Media identity

Each supported parser returns `movie` or `series`, along with its provider ID
and available release year. These values select different metadata and import
paths: a show must reach the episode importer. Provider terms such as `show` and
`tv` cannot be passed through unchanged or replaced with a movie default.

| Source | Identity handling |
|---|---|
| MDBList | Public JSON arrays use `mediatype` and `release_year`. `show` becomes `series`; grouped `movies`/`shows` responses supply the type when an item omits it. Contradictory buckets and untyped array entries are skipped. |
| Trakt | `movie` and `show` wrappers supply the type; an explicit `type` must agree. Flat entries require an explicit supported type. Episode/history and other unsupported entries are skipped rather than promoted to a whole series. |
| TMDB | Explicit `media_type=tv` becomes `series` and uses `first_air_date` for the year. Movies use `release_date`. V3 movie list entries without a type retain movie compatibility when they have a movie `title`; an untyped name-only entry is skipped. |
| AniList | The query requests both `type` and `format`. `ANIME`/`MOVIE` becomes a movie; TV, TV_SHORT, OVA, ONA and SPECIAL become series. Manga, music and unknown formats are skipped. Native `anilist:` IDs remain available for downstream ID enrichment. |

AniList's `ANIME` describes its media category; it does not mean every entry is a
television series. Episodic AniList entries use the series import path and series
library destination. This does not enable a new absolute-numbered anime policy.
Season/episode identity still depends on the configured metadata provider.

These parser repairs are on the review branch and are **not part of release
0.42.8**. On September 30, 2026, synthetic response tests reproduced the original
Trakt, TMDB and AniList defects before the fixes. The tests exercise the production
parsers and check that their output agrees with the episode import contract.
The repaired pinned-ABI build passed all 157 tests and published successfully.
They do not establish live provider availability, successful enrichment or playback.

### Existing misclassified rows

Refreshing a list does **not** automatically convert an existing movie row into
a series. Parser fixes protect newly imported rows; existing rows retain their
media type. Previously misclassified rows need a separate, backed-up repair of
their media type and managed destination. Existing movie STRMs must never be
renamed into fake episodes. Preserve list memberships, user blocks, retry history
and owned media; verify episode imports separately from playback.

## Other implementations

We checked other list consumers while investigating these repairs:

- [Kometa's MDBList parser](https://github.com/Kometa-Team/Kometa/blob/master/modules/mdblist.py)
  reads `mediatype`/`type` and assigns separate movie/show ID families. Its
  [TMDB list builder](https://github.com/Kometa-Team/Kometa/blob/master/modules/tmdb.py)
  also distinguishes movies from shows.
- [HomeScreenCompanion's list fetcher](https://github.com/soderlund91/HomeScreenCompanion/blob/main/HomeScreenCompanion/ListFetcher.cs)
  collapses MDBList and Trakt results to IMDb IDs.
  Its [consumer](https://github.com/soderlund91/HomeScreenCompanion/blob/main/HomeScreenCompanion/HomeScreenCompanionTask.cs)
  looks up existing Movie/Series items for list selection. That does not establish
  that ignoring the type is safe when creating a new import.
- [Kometa's AniList user-list reader](https://github.com/Kometa-Team/Kometa/blob/master/modules/anilist.py)
  returns AniList IDs for later conversion. It does not assign every entry a
  television import type at the list-parser boundary.

These are source comparisons, not runtime tests of those projects. Their
selection and conversion workflows differ from InfiniteDrive's STRM creation.

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
