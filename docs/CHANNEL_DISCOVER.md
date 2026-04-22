# IChannel "InfiniteDrive Discover" — Architecture

## Overview

`InfiniteDriveDiscoverChannel` implements Emby's `IChannel` interface to surface AIOStreams content as a native Emby channel. The folder tree is **manifest-driven**: catalog definitions from the AIOStreams manifest provide human-readable names, and the database provides the data.

Browse-only. Items already in the library (`is_in_user_library = 1`) are excluded by query — no ✓ decoration needed.

## Folder Routing

```
FolderId = null              → Root: dynamic folders from discover_catalog media types
FolderId = "movie"           → Catalog folders for type "movie"
FolderId = "series"          → Catalog folders for type "series"
FolderId = "anime"           → Catalog folders for type "anime"
FolderId = "cat:{id}"        → If catalog has genre extra → genre subfolders
                                If no genre extra → items from discover_catalog
FolderId = "cat:{id}:{genre}" → Genre-filtered items from discover_catalog
```

Root folders are dynamic: `SELECT DISTINCT media_type FROM discover_catalog WHERE is_in_user_library = 0`. If anime items exist in discover_catalog, the Anime folder appears automatically.

Example navigation:
```
InfiniteDrive Discover
├── Movies
│   ├── Popular        → cat:aiostreams (genre subfolders: Action, Comedy, etc.)
│   ├── Trending       → cat:torrentio_movies (genre subfolders)
│   ├── Netflix        → cat:nfx_catalog (flat, no genre subfolders)
│   └── ...
├── Series
│   ├── Popular
│   └── ...
└── Anime
    ├── Crunchyroll
    └── ...
```

## Architecture

```
Emby Channels UI
    │
    ▼
IChannel.GetChannelItems(InternalChannelItemQuery)
    │
    ├── FolderId == null → GetRootFolders()
    │   DB: GetDiscoverMediaTypesAsync() → dynamic [Movies, Series, Anime]
    │
    ├── FolderId == "movie"|"series"|"anime" → GetCatalogFolders(type)
    │   Manifest: cached catalog definitions (1hr TTL)
    │   DB: GetDiscoverCatalogSourcesAsync(type) → sources with data
    │   Match manifest catalogs to DB sources for human-readable names
    │
    ├── FolderId == "cat:{id}" → GetCatalogContent(folderId)
    │   Manifest: check for genre extra with options → genre subfolders
    │   No genre extra → items directly
    │
    └── FolderId == "cat:{id}:{genre}" → GetItemsForCatalog(source, genre)
        DB: GetDiscoverCatalogBySourceAsync(source, genre, 42, 0)
```

## Discover Cache

Items in `discover_catalog` with `is_in_user_library = 0` form the **discover cache**:

- **Searchable** via the HTML Discover UI (`/InfiniteDrive/InfiniteDiscover`)
- **Browsable** via this IChannel in Emby's native Channels UI
- **Not playable** until promoted to library via "Add to Library"

This is intentional: massive lists can be added as discover sources without cluttering the library. Users browse and search the cache, then selectively promote items they want. The IChannel acts as a window into the cache — only items NOT already in the library appear.

The sync pipeline populates `discover_catalog` for all configured catalogs. When an item is promoted to library (`.strm` file created), `is_in_user_library` is set to `1` and the item disappears from the channel.

## Search Mode

Not available via IChannel (`InternalChannelItemQuery.SearchTerm` not exposed in this SDK version). Search remains in the HTML Discover UI at `/InfiniteDrive/InfiniteDiscover`.

## Parental Controls

Set `OfficialRating` on each `ChannelItemInfo`:
```csharp
OfficialRating = entry.Certification,  // "PG", "R", "TV-MA", etc.
```

Emby's built-in parental control engine checks `OfficialRating` against the user's `Policy.MaxParentalRating` and hides items exceeding the allowed rating.

## Promotion Flow

```
Channel item → User clicks → Emby detail page
    → Client JS calls POST /InfiniteDrive/Discover/AddToLibrary
    → DiscoverService creates .strm file
    → is_in_user_library = 1 → item disappears from channel
    → Library refresh picks up new item
```

## Relationship to Existing Code

| Component | Status |
|-----------|--------|
| `DiscoverService` REST endpoints | Active, unchanged |
| HTML Discover pages | Active, unchanged |
| `InfiniteDriveDiscoverChannel` (IChannel) | Browse surface |
| `DatabaseManager` new methods | `GetDiscoverCatalogBySourceAsync`, `GetDiscoverMediaTypesAsync`, `GetDiscoverCatalogSourcesAsync` |
| `GetAllPinnedImdbIdsAsync()` | No longer needed (excluded by query) |

## Auto-Discovery

Emby auto-discovers `IChannel` implementations via reflection. No registration in `Plugin.cs` needed.

## File

`Channels/InfiniteDriveDiscoverChannel.cs` — single file, ~220 lines
