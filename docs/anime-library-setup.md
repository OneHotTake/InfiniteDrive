# Anime Library Setup

EmbyStreams can route anime content from AIOStreams into a dedicated Emby library. For the best experience, install the Emby Anime Plugin to enhance metadata with artwork from AniDB/AniList.

## Prerequisites

- **Emby Anime Plugin** (optional but recommended) can be installed from the Emby Plugin Catalog. It provides enhanced artwork and metadata for anime content.

## Setup

1. **Install the Emby Anime Plugin**
   - Go to Emby Dashboard → Plugins → Catalog
   - Find and install "Emby Anime" (or "Emby.Plugins.Anime")
   - Restart Emby if prompted

2. **Enable the Anime Library**
   - Go to EmbyStreams settings → Catalog Sync tab
   - Check "Enable Anime Library" and set the anime library path (default: `/media/embystreams/anime`)
   - Save settings — EmbyStreams creates the directory and a Series library automatically

3. **Configure metadata providers**
   - Go to Emby Dashboard → Libraries → "Streamed Anime"
   - Under Metadata, set AniDB as the **priority** metadata provider
   - This ensures anime gets proper Japanese titles, artwork, and descriptions

## How It Works

When anime is **enabled**:
- AIOStreams items with `type: "anime"` are routed to the anime library path
- Items are stored with Series folder structure (`Show/Season 01/S01E01.strm`)
- Only IMDB-prefixed IDs (`tt...`) produce `[imdbid-...]` folder name suffixes

When anime is **disabled** (default):
- `type: "anime"` items are filtered out entirely during catalog sync
- No anime `.strm` files are created

## Non-IMDB IDs

Anime items with non-IMDB IDs (e.g., `kitsu:12345`, `anilist:12345`) now flow through the standard pipeline and get full NFO files with all their unique IDs. No special handling is needed.

## Troubleshooting

- **Anime items not appearing**: Verify the anime library path exists and the Emby library scan has completed.
- **Enhanced metadata**: To get the best anime metadata, install the Emby Anime Plugin and set AniDB as the priority metadata source in the anime library settings.
