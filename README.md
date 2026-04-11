# InfiniteDrive

🚀 **DON'T PANIC**

> *"The ships hung in the sky in much the same way that bricks don't."* — Douglas Adams, *The Hitchhiker's Guide to the Galaxy*

⚠️ **WARNING:** This project is currently under heavy development. It does not work. It may never work. It might make your Emby server question its own existence. You have been warned. Bring a towel.

An Emby plugin that discovers streaming catalogs from [AIOStreams](https://github.com/aiostreams), writes `.strm` files, and resolves debrid URLs on demand. Like the Infinite Improbability Drive: a stream will appear. Probably.

---

## Design Principle: Simplicity Over Complexity

Users want simplicity, administrators want flexibility, nobody wants complexity. Fortunately for us, the debrid and usenet streaming world is inherently complex.

When making architectural decisions: prefer the simple approach that works over the sophisticated one that handles every edge case.

---

## What It Does

InfiniteDrive bridges your AIOStreams manifest to Emby:

1. **Catalog Sync** — pulls movie/series/anime catalogs from your AIOStreams manifest and writes `.strm` files into Emby libraries
2. **Stream Resolution** — resolves `.strm` playback requests against AIOStreams in real time, selecting the best debrid link
3. **Stream Probing** — quickly checks if candidate streams actually respond before serving them to your player
4. **ID Normalization** — resolves IMDb/TMDB/TVDB IDs from source addons so Emby can identify your content
5. **NFO Decoration** — writes Emby-native NFO files with proper scanner hints so Emby does its own metadata job

---

## Requirements

- Emby Server 4.10.0.6+
- An [AIOStreams](https://github.com/aiostreams) manifest URL (self-hosted or configured)
- A Real-Debrid, AllDebrid, or compatible debrid service account configured in AIOStreams
- .NET 8.0 runtime (bundled with Emby)

---

## Installation

1. Build the plugin: `dotnet publish -c Release`
2. Copy `bin/Release/net8.0/publish/InfiniteDrive.dll` to your Emby plugins directory
3. Copy `plugin.json` alongside the DLL
4. Restart Emby Server
5. Navigate to **Plugins → InfiniteDrive** to configure

Or use the dev scripts:

```bash
./emby-reset.sh   # full reset (wipes data) — use when something is broken
./emby-start.sh   # build + deploy + start (no data wipe)
```

---

## Configuration

After installation, open the InfiniteDrive configuration page in Emby:

```
http://localhost:8096/web/configurationpage?name=InfiniteDrive
```

**Required settings:**
- **AIOStreams Manifest URL** — your full manifest URL (includes your API key)
- **Emby Base URL** — the URL Emby uses for internal stream signing (usually `http://localhost:8096`)

**Optional:**
- **AIOMetadata Manifest URL** — for additional ID resolution via AIOMetadata
- Quality tier preferences, sync schedule, stream probe settings

---

## Architecture

```
AIOStreams API
     │
     ├── CatalogSyncTask        (scheduled: discovers catalogs, writes .strm files)
     ├── CatalogDiscoverService (resolves catalog items, normalizes IDs)
     ├── IdResolverService      (tt/tmdb/tvdb resolution chain)
     └── StrmWriterService      (writes .strm + NFO files with Emby scanner hints)

Emby Player → ResolverService  (real-time: picks streams, probes URLs, returns M3U8)
                └── StreamProbeService  (HEAD → GET-range, 500ms/probe, 1.5s budget)
```

### Key Design Decisions

- **No cross-service ID translation at browse time** — IDs are passed as-is to the source addon's own `/meta` endpoint (same approach as Nuvio). Cross-resolution happens lazily at sync time.
- **Dead streams sink, not drop** — stream probing reorders the M3U8 playlist so working streams come first, but dead URLs remain as last-resort fallback for the player
- **Emby does its own metadata job** — we write scanner hints (`[imdbid-tt...]`, `[tmdbid-xxx]`) and NFO files; we don't try to replicate Emby's metadata logic
- **Fresh install required** — this is a breaking change from EmbyStreams. No migration path. Start clean.

---

## Development

```bash
# Build
dotnet build -c Release

# Watch server logs
tail -f ~/emby-dev-data/logs/embyserver.txt

# Full dev reset (wipes state)
./emby-reset.sh
```

The `.ai/` directory contains sprint planning documents and the repository map. `CLAUDE.md` has instructions for AI-assisted development sessions.

---

## Version

**0.40.0.0** — "almost 0.42"

*(The answer is 42. We're still working on what the question is.)*

---

## License

MIT. See LICENSE file.

*"Would it save you a lot of time if I just gave up and went mad now?"*
