# Discover in Emby Web

Open `/web/configurationpage?name=InfiniteDiscover` in an authenticated Emby Web
session. This is the plugin's embedded browser page; do not expect its custom
interface in native television or mobile apps.

## Browse and add

The current page displays catalog rails, a search field, movie/series/anime search
results and a title-details dialog. Cards can show artwork, year, rating,
certification and In Library/Pending status. The dialog offers library actions.

Adding a title records catalog/user intent and uses the managed import path.
In Repair mode it queues work rather than writing empty placeholder files.
Pending is not proof of a playable file or successful Emby indexing. Check the
native library and [import coverage](import-reconciliation.md) before concluding
that import has finished.

The current embedded page does not implement the historical My Picks/My Lists
tabs. Administrative subscriptions are managed through **Sources**; backend
per-user list APIs do not imply a list editor exists in this browser page.
See [external lists](EXTERNAL_LISTS.md).

## Search and access

Search combines supported catalog/provider results with known library state.
Availability depends on configured sources. A stream-only manifest can resolve
playback without providing useful browsing or external search. Empty results do
not prove a title is unavailable everywhere.

The server applies Discover restrictions and native user context. Emby also
controls access to indexed media. Do not treat a browser's badge or hidden card
as an independent authorization guarantee.

## Troubleshooting

- Blank page: verify authentication, plugin load and sanitized browser errors.
- Empty rails/search: check selected catalogs and source reachability.
- Add fails: check managed roots, permissions and sanitized server logs.
- Pending persists: review provider backoff, recovery budgets and native indexing.
- Wrong title/episode: verify provider IDs and numbering before retrying.

When reporting an issue, include Emby/plugin versions, timestamp and sanitized
errors in [GitHub Issues](https://github.com/OneHotTake/InfiniteDrive/issues).
Never paste private manifests, stream URLs or full configuration exports.

This guide was checked against the embedded HTML/JavaScript and service code on
September 29, 2026; it is not a new browser or physical-client acceptance test.
