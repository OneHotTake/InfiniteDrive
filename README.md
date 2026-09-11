# Emby Infinite(Improbability)Drive

🚀 **DON'T PANIC**

> *"The ships hung in the sky in much the same way that bricks don't."* — Douglas Adams, *The Hitchhiker's Guide to the Galaxy*

**CURRENT RELEASE:** Version 0.43.0 is built and tested against Emby 4.10.0.40.
Back up the Emby plugin configuration, database, and managed library state
before upgrading.

An Emby plugin that discovers streaming catalogs from [AIOStreams](https://github.com/aiostreams), writes `.strm` files, and resolves debrid URLs on demand. Like the Infinite Improbability Drive: a stream will appear. Probably.

---

## Documentation

- [ARCHITECTURE.md](./ARCHITECTURE.md) – High-level system design
- [MARVIN_STATE_MACHINE.md](./MARVIN_STATE_MACHINE.md) – How the core engine works
- [SETTINGS_DESIGN.md](./SETTINGS_DESIGN.md) – Current state-driven settings UI
- [configuration.md](./docs/configuration.md) – Supported configuration contract
- [settings-matrix.md](./docs/settings-matrix.md) – Intent, derived state, and verification matrix
- [hardening report](./docs/overnight-hardening-report-2026-09-10.md) – 0.43 staging and production evidence

---

## Design Principle: Simplicity Over Complexity

Users want simplicity, administrators want flexibility, nobody wants complexity. Fortunately for us, the debrid and usenet streaming world is inherently complex.

When making architectural decisions: prefer the simple approach that works over the sophisticated one that handles every edge case.

---

## What It Does

InfiniteDrive bridges your AIOStreams manifest to Emby:

1. **Catalog Sync** — unions catalogs from every configured manifest and from MDBList, AniList, Trakt, and TMDB-backed lists
2. **Stream Resolution** — resolves `.strm` playback requests against AIOStreams in real time, selecting the best debrid link
3. **Stream Probing** — quickly checks if candidate streams actually respond before serving them to your player
4. **Subtitle Fetching** — fetches external subtitles from AIOStreams and registers as an Emby `ISubtitleProvider` for the native subtitle picker
5. **ID Normalization** — resolves IMDb/TMDB/TVDB IDs from source addons so Emby can identify your content
6. **NFO Decoration** — writes Emby-native NFO files with proper scanner hints so Emby does its own metadata job

---

## Requirements

- Emby Server 4.10.0.40 (the ABI verified by the current build)
- An [AIOStreams](https://github.com/aiostreams) manifest URL (self-hosted or configured)
- A Real-Debrid, AllDebrid, or compatible debrid service account configured in AIOStreams
- .NET 8.0 runtime (bundled with Emby)

---

## Installation

1. On a Docker-capable host, run `./scripts/build-container.sh`. It extracts
   compile references from the exact pinned Emby image and runs the test suite.
2. Copy `artifacts/InfiniteDrive.dll` to your Emby plugins directory.
3. Preserve a rollback copy of the previous DLL, configuration, and database.
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

**Required setting:**
- **Manifest 1** — an AIOStreams manifest URL. Treat it as a credential; do not
  paste it into issues, screenshots, or logs.

**Optional:**
- **Manifest 2** — a second active peer. Presence enables it; there is no backup
  toggle.
- **Lists** — MDBList and AniList work without provider keys; Trakt and TMDB need
  their respective credentials.
- **Allow REMUX** and **Allow CAM/TS** — both default off.
- Desired-version buckets and Discover restrictions.

If a manifest exposes no catalogs, configured lists still supply content. When
neither a manifest nor a list yields content, InfiniteDrive derives a small
starter catalog for that sync so the library is not silently empty.

---

## User Interface

InfiniteDrive provides a user-facing **Discover UI** accessible via Emby's web interface.

### Access

- **Web:** Available at `/web/configurationpage?name=InfiniteDiscover` or via the admin plugin menu
- **Mobile apps:** Not supported — use web browser for full Discover experience

### Features

**Discover Tab:**
- Browse the full streaming catalog with posters, ratings, and parental filtering
- Search for movies and shows by title (with auto-debounce)
- View detailed information including synopsis, genres, and certifications
- Add items to your library with one click

**My Picks Tab:**
- View all items you've saved to your library
- Remove items with a single click
- Quick access to item details

**My Lists Tab:**
- Subscribe to public Trakt and MDBList RSS feeds
- View your custom lists with item counts and sync status
- Refresh lists individually or all at once
- Remove lists you no longer need

### Parental Controls

The Discover UI respects Emby's parental rating system:
- Items above your configured rating limit are hidden
- Unrated content can be hidden via admin settings
- Filtering is enforced server-side for security

For detailed documentation, see [USER_DISCOVER_UI.md](docs/USER_DISCOVER_UI.md).

---

## Architecture

```
AIOStreams API
     │
     ├── CatalogSyncTask        (sole upstream catalog reader; queues database rows)
     ├── RefreshTask            (consumes queued rows; writes .strm + NFO files)
     ├── IdResolverService      (tt/tmdb/tvdb resolution chain)
     └── StrmWriterService      (writes .strm + NFO files with Emby scanner hints)

Emby Player → AioMediaSourceProvider (ranked, filtered Emby media sources)
                └── StreamProbeService (HEAD → bounded range GET fallback)
```

### Key Design Decisions

- **No cross-service ID translation at browse time** — IDs are passed as-is to the source addon's own `/meta` endpoint (same approach as Nuvio). Cross-resolution happens lazily at sync time.
- **Rejected releases stay rejected** — CAM/telesync and REMUX candidates are
  excluded by default. The Quality page exposes separate **Allow CAM/TS** and
  **Allow REMUX** opt-ins; rejected candidates cannot re-enter through cache or
  another manifest.
- **Emby does its own metadata job** — we write scanner hints (`[imdbid-tt...]`, `[tmdbid-xxx]`) and NFO files; we don't try to replicate Emby's metadata logic
- **Secrets are log-redacted** — manifest credentials, signed CDN paths, query
  strings, and ffprobe diagnostics are sanitized before logging.
- **State, not Mycelium policy** — InfiniteDrive never assumes Mycelium exists
  and never lets it drive behavior. External entries are ordinary duplicates;
  only InfiniteDrive-managed virtual files are eligible for reconciliation.
- **Catalog-less is not content-less** — MDBList, AniList, Trakt and other lists
  continue to sync when a manifest has no catalogs. If neither a list nor a
  manifest supplies content, a small starter catalog is derived for that run.

---

## Development

```bash
# Clean ABI-matched build + tests + publish
./scripts/build-container.sh

# Watch server logs
tail -f ~/emby-dev-data/logs/embyserver.txt

# Full dev reset (wipes state)
./emby-reset.sh
```

The `.ai/` directory contains sprint planning documents and the repository map. `CLAUDE.md` has instructions for AI-assisted development sessions.

---

## Version

**0.43.0.0** — Emby 4.10 ABI and bounded-pipeline hardening

*(The answer is 42. We're still working on what the question is.)*

---

## License

MIT. See LICENSE file.

*"Would it save you a lot of time if I just gave up and went mad now?"*
