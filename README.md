# Emby Infinite(Improbability)Drive

🚀 **DON'T PANIC**

> *"The ships hung in the sky in much the same way that bricks don't."* — Douglas Adams, *The Hitchhiker's Guide to the Galaxy*

**CURRENT RELEASE:** Version 0.42.4 is built and tested against Emby 4.10.0.40.
Back up the Emby plugin configuration, database, and managed library state
before upgrading.

An Emby plugin that discovers streaming catalogs from [AIOStreams](https://github.com/Viren070/AIOStreams), writes `.strm` files, and resolves debrid URLs on demand. Like the Infinite Improbability Drive: a stream will appear. Probably.

---

## Documentation

- [ARCHITECTURE.md](./ARCHITECTURE.md) – High-level system design
- [MARVIN_STATE_MACHINE.md](./MARVIN_STATE_MACHINE.md) – How the core engine works
- [SETTINGS_DESIGN.md](./SETTINGS_DESIGN.md) – Current state-driven settings UI
- [Import recovery](./docs/import-reconciliation.md) – Coverage, automatic refill, limits and rollout
- [configuration.md](./docs/configuration.md) – Supported configuration contract
- [settings-matrix.md](./docs/settings-matrix.md) – Intent, derived state, and verification matrix
- [hardening report](./docs/overnight-hardening-report-2026-09-10.md) – 0.42.1 staging and production evidence

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
5. **ID Normalization** — retains provider-native stream IDs while preferring IMDb/TMDB/TVDB cross-references for Emby naming and matching

### Owned media always wins

InfiniteDrive never competes with a physical movie or series already owned by
the Emby server. Before any streamed file is written, InfiniteDrive compares
the catalog item's IMDb, TMDB, TVDB, and other stable provider IDs with Emby's
physical library. A matching item outside InfiniteDrive's configured roots is
recorded as `Retired`, its managed stream tree is removed, and no `.strm` is
created.

Catalog and list refreshes preserve that retirement. The item is eligible for
streaming again only when Marvin verifies that the recorded physical file or
series directory has genuinely disappeared. The post-library-scan check remains
as a safety net, but it is not the primary deduplication boundary.

---

## How we run it

Our September 2026 setup uses self-hosted **AIOStreams + AIOMetadata**, with
personalized configurations dedicated to InfiniteDrive. The settings below are
an example operating profile, not mandatory defaults or a guarantee that every
title has a matching stream.

```text
AIOMetadata: catalogs and title/episode metadata
       ↓
AIOStreams: combines sources, filters formats, ranks stream choices
       ↓
InfiniteDrive / Marvin: imports catalogs, writes STRM + NFO, repairs eligible gaps
       ↓
Emby: indexes the library and serves native clients
```

### Separate profiles, clear catalog ownership

We give InfiniteDrive its own AIOMetadata configuration and AIOStreams child
configuration. The child inherits provider and playback connections while
overriding catalogs, filtering, sorting and presentation. Other clients can have
their own profiles without changing what Emby imports.

AIOMetadata supplies six TMDB browse feeds: Popular, Top Rated and Trending,
each for movies and TV. Our import cap is 4,000 entries per feed; these overlap
and do not constitute an exhaustive database or 24,000 unique titles. Search
and calendar endpoints are excluded from bulk import. InfiniteDrive owns our
MDBList subscriptions directly, so their duplicate catalogs are disabled in the
metadata profile. Feed names are provider labels; actual ordering and coverage
depend on the provider.

### Video, audio and stream preferences

| Setting | Our profile |
| --- | --- |
| Resolution | Prefer 2160p/4K; retain lower-resolution fallbacks, including 1080p and 720p. Exclude unknown resolution. |
| Video codec | HEVC first, then AV1 and AVC, with other fallback codecs retained. HEVC does not necessarily mean the x265 encoder. |
| Source | Prefer Blu-ray encodes, WEB-DL and WEBRip. Exclude Blu-ray/DVD REMUX, CAM, SCR, TS, TC and 3D. |
| 4K bitrate | Aim for roughly 25–40 Mbps. Configure **0–40,000,000 bits/second** for movies, series and anime so smaller encodes remain eligible. |
| Picture | Prefer HDR with Dolby Vision, HDR10+ and other HDR formats; retain SDR fallback. |
| Audio | Prefer Atmos, DTS:X, TrueHD and DTS-HD MA; retain formats such as DTS, Dolby Digital Plus, Dolby Digital and AAC. Channel preference starts with 7.1, 6.1 and 5.1. |
| Language | Prefer English; retain unknown-language fallback. This is not an English-only filter. |
| Availability | Cached first; exclude explicitly uncached results. We retain TorBox and Usenet alternatives. |
| Choices | Up to eight AIOStreams results in total, at most three per resolution. InfiniteDrive also selects up to eight versions per record, with a two-version 1080p desired bucket. |
| Duplicates | Filename/hash plus smart metadata matching, keeping service alternatives separate and allowing same-release failover. |

Our upstream sort order is **cached → resolution → codec → source quality →
bitrate → picture format → audio → language → size**, with descending priority.
Bitrate uses reported values or a size/runtime estimate with metadata runtime
enabled. Unknown bitrate is allowed; the ceiling is not a measured peak-bandwidth
limit. A long film can still be a large file within that limit. Usenet's advertised
readiness does not necessarily mean it is already physically cached.

The upstream formatter uses two plain lines, for example:

```text
4K · Dolby Vision
28 GB · Dolby Atmos 7.1 · TorBox
```

This is an illustrative label. It shows resolution/picture, size, advertised
audio/channels and service, without release filenames, indexers or repeated
titles. Actual passthrough and HDR support depend on the player and display.
InfiniteDrive builds its own native version labels and ranking: the standard
formatted addon response does not pass all structured codec/HDR information to
its parser. Do not assume the upstream ordering or label survives unchanged.
Emby can also merge multiple provider aliases, showing more than eight versions.

### Keeping the library useful

Marvin keeps existing retention rules: a transient top-ten title can leave when
it disappears from every catalog, unless watched history, a save **or** existing
list/collection intent retains it. Explicit blocks prevent recovery. Eligible
missing files are automatically refilled in Repair mode, while owned media
continues to take precedence.

Changing the AIOStreams profile does not instantly replace stored selections.
Keep the managed STRMs and let Marvin refresh them after successful resolution.
Start with **Observe**, review coverage, then enable **Repair** through the native
admin controls. Each recovery slice has a two-minute budget, up to 20 stream
attempts and five ordinary version refreshes; the rolling daily limit is 200
stream attempts. Our ten-minute Marvin trigger is a schedule, not a promise that
the library will converge in ten minutes. See [Import recovery](./docs/import-reconciliation.md).

### Reproduce the setup

1. Set up [AIOStreams](https://github.com/Viren070/AIOStreams) using its
   [official documentation](https://docs.aiostreams.viren070.me/), and configure
   your own source/service connections. The [setup guide](https://docs.aiostreams.viren070.me/configuration/setup/)
   offers a community-template starting point; our profile is a smaller custom
   policy, not a copy of that complete template.
2. Set up [AIOMetadata](https://github.com/cedya77/aiometadata) using its README
   and deployment instructions. Create a dedicated configuration, enable the
   browse catalogs you want and add it to your AIOStreams configuration for
   catalog/metadata resources.
3. Apply your quality preferences in AIOStreams. Its [configuration reference](https://docs.aiostreams.viren070.me/configuration/options/)
   explains filtering, sorting, limits and formatters; the [Usenet guide](https://docs.aiostreams.viren070.me/guides/usenet/)
   covers that optional source path. Our resolver timeout is 60 seconds to
   accommodate upstream source deadlines of 45 seconds.
4. Install InfiniteDrive from [Releases](https://github.com/OneHotTake/InfiniteDrive/releases),
   connect the dedicated manifest, select your browse catalogs and configure
   managed library roots. Use the [configuration contract](./docs/configuration.md)
   and [recovery guide](./docs/import-reconciliation.md) for plugin settings.
5. Verify a small movie/episode sample through publication, native Emby indexing
   and playback before expanding catalog limits. Check upstream rate limits:
   a partial or empty catalog response is not proof that every item was imported.

Keep manifest URLs, configuration IDs/passwords, API keys, provider addresses
and signed stream links private. Share settings and illustrative labels rather
than complete configuration exports.

---

## Requirements

- Emby Server 4.10.0.40 (the ABI verified by the current build)
- An [AIOStreams](https://github.com/Viren070/AIOStreams) manifest URL (self-hosted or configured)
- A compatible streaming service/source configured in AIOStreams, such as TorBox, Real-Debrid or a supported Usenet connection
- .NET 8.0 runtime (bundled with Emby)

---

## Installation

1. On a Docker-capable host, run `./scripts/build-container.sh`. It extracts
   compile references from the exact pinned Emby image and runs the test suite.
2. Copy `artifacts/InfiniteDrive.dll` to your Emby plugins directory.
3. Preserve a rollback copy of the previous DLL, configuration, and database.
4. Restart Emby Server
5. Navigate to **Plugins → InfiniteDrive** to configure

For an isolated development server, use the dev scripts:

```bash
./emby-reset.sh   # disposable dev environment only: wipes its data
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

Catalog pages use the page size returned by the addon rather than assuming 100
items. MDBList and other external-list entries join Marvin's normal queued
resolution pipeline; they are never emitted as empty placeholder files.

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
     ├── ImportReconciliationService (Observe/Repair coverage and bounded recovery)
     ├── IdResolverService      (tt/tmdb/tvdb resolution chain)
     └── StrmWriterService      (writes .strm + NFO files with Emby scanner hints)

Emby Player → AioMediaSourceProvider (ranked, filtered Emby media sources)
                └── StreamProbeService (HEAD → bounded range GET fallback)
```

In Repair mode, the reconciliation worker owns gap filling and version refresh;
the legacy population/verification/version-refresh stages yield to it. Catalog
sync and existing retention cleanup continue.

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
- **Owned before streamed** — matching physical Emby media outside the three
  InfiniteDrive roots is authoritative. The streamed representation is retired
  before write and cannot be resurrected by an ordinary catalog refresh.
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

**0.42.4.0** — Durable import recovery with native indexing and existing retention policy

*(The answer is 42. We're still working on what the question is.)*

---

## License

MIT. See LICENSE file.

*"Would it save you a lot of time if I just gave up and went mad now?"*
