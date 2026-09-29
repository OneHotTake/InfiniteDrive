# Anime identities

The current identity and naming code recognizes IMDb, TMDB, TVDB and anime-native
identifiers. It does not assume every source ID begins with `tt`.

`NamingPolicyService` prefers an IMDb identity, then TVDB for series/anime, then
TMDB, then a native provider tag such as `kitsu=...` or `anilist=...`. Title/year
is the final fallback. Provider tags are identifiers, not proof that an installed
Emby metadata provider can resolve them.

`IdResolverService` parses source identifiers and attempts cross-ID enrichment.
Review the actual provider IDs, NFO metadata and indexed Emby item when a title
matches incorrectly. Do not manufacture an IMDb ID or assume native numbering
maps to TV seasons. Import recovery excludes absolute/unverified anime numbering
rather than guessing missing episode keys.

See [anime setup](anime-library-setup.md) and [import recovery](import-reconciliation.md).
The Sprint 61 audit is retained in the historical archive; its named classes and
signed-URL pipeline no longer describe the current implementation.
