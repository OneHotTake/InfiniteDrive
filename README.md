# Emby Infinite(Improbability)Drive

🚀 **DON'T PANIC**

InfiniteDrive brings [AIOStreams](https://github.com/Viren070/AIOStreams)
catalogs into Emby. It writes `.strm` files, finds streams when you press Play,
and fetches subtitles for Emby's subtitle picker. Marvin, its background worker,
imports titles and repairs missing files.

A stream will appear. Probably.

**Current release: 0.42.4**, built and tested against **Emby 4.10.0.40**.

## How we run it

We self-host AIOStreams and [AIOMetadata](https://github.com/cedya77/aiometadata).
Each has a configuration dedicated to InfiniteDrive. These are our September
2026 settings; adapt them to your sources, connection and players.

```text
AIOMetadata → catalogs and episode metadata
AIOStreams  → source filtering and stream choices
InfiniteDrive / Marvin → STRM files, NFO metadata and repairs
Emby → library, playback and watch history
```

### Profiles and catalogs

InfiniteDrive's AIOStreams child configuration inherits provider and playback
connections. It has its own catalogs, filters, sorting and labels. Other clients
use separate profiles.

AIOMetadata supplies Popular, Top Rated and Trending feeds for movies and TV.
We cap each of those six feeds at 4,000 entries. They overlap; this is neither
24,000 unique titles nor a complete TMDB import. Feed order depends on the
provider. We skip search and calendar feeds during bulk import.

InfiniteDrive handles our MDBList subscriptions directly. We disable their
copies in AIOMetadata to avoid importing the same lists twice.

### Formats and quality

| Setting | Our preference |
| --- | --- |
| Resolution | 4K first, with lower resolutions available. Exclude unknown resolution. |
| Codec | HEVC, then AV1 and AVC. Keep other codecs as fallbacks. |
| Source | Blu-ray encodes, WEB-DL, then WEBRip. Exclude REMUX, CAM, SCR, TS, TC and 3D. |
| 4K bitrate | Aim for 25–40 Mbps. Set the range to **0–40,000,000 bits/second** for movies, TV and anime, allowing smaller encodes. |
| Picture | Dolby Vision and HDR preferred; SDR available. |
| Audio | Atmos, DTS:X, TrueHD and DTS-HD MA preferred. Keep common formats such as DTS, DD+, DD and AAC. Prefer 7.1, 6.1, then 5.1 channels. |
| Language | English preferred. Allow unknown language. |
| Availability | Cached first; exclude explicitly uncached results. Keep TorBox and Usenet alternatives. |
| Choices | Eight results at most, with up to three per resolution. InfiniteDrive also selects up to eight versions per record and aims to include two at 1080p. |
| Duplicates | Match filenames, hashes and release metadata. Keep alternatives from different services and allow failover. |

AIOStreams sorts by **cached status → resolution → codec → source quality →
bitrate → picture → audio → language → size**.

The bitrate cap uses reported values or estimates from file size and runtime.
Unknown bitrates pass. It does not cap peak network traffic, and a long film
can still be large. HEVC identifies a codec, not necessarily the x265 encoder.
Usenet results marked ready may still need to be fetched.

### Version labels

We use two lines. For example:

```text
4K · Dolby Vision
28 GB · Dolby Atmos 7.1 · TorBox
```

No release filenames, indexer names or repeated movie titles. The label describes
the source; your player and display determine what you can actually use.

InfiniteDrive makes its own Emby labels and ranks results again. The formatted
addon response omits some codec and HDR data, so labels and order can differ.
Emby may also merge provider aliases and show more than eight versions.

### What stays, what goes

A movie that falls off a top-ten list can go when no other catalog contains it.
Watching it, saving it **or** retaining it through a list or collection keeps it
under Marvin's existing retention rules. Explicit blocks prevent recovery.

Owned media takes priority. If Emby already has a matching physical movie or
series outside InfiniteDrive's folders, Marvin retires the streamed copy and
removes its managed files. Catalog refreshes cannot bring it back while the
owned copy remains.

Repair mode refills eligible missing files and refreshes old stream choices.
There is no need to wipe STRMs after changing your AIOStreams profile. Marvin
replaces them after it successfully resolves new choices.

Start in **Observe**, review the results, then enable **Repair** in the admin
controls. Each recovery pass gets two minutes, up to 20 stream attempts and
five ordinary version refreshes. The rolling daily cap is 200 stream attempts.
Our Marvin task runs every ten minutes; a large library takes many passes.
See [Import recovery](docs/import-reconciliation.md) for limits and exclusions.

## Set it up

You'll need Emby Server **4.10.0.40**, an AIOStreams manifest and a compatible
source configured in AIOStreams, such as TorBox, Real-Debrid or Usenet.
Emby includes the required .NET 8 runtime.

1. Configure [AIOStreams](https://github.com/Viren070/AIOStreams). Its
   [documentation](https://docs.aiostreams.viren070.me/) covers hosting;
   the [setup guide](https://docs.aiostreams.viren070.me/configuration/setup/)
   offers a community template. We use a smaller custom policy.
2. Configure [AIOMetadata](https://github.com/cedya77/aiometadata) using its
   deployment instructions. Choose your catalogs and add the configuration to
   AIOStreams for catalog and metadata requests.
3. Set your filters, sorting and labels using the
   [AIOStreams settings reference](https://docs.aiostreams.viren070.me/configuration/options/).
   For Usenet, follow its [Usenet guide](https://docs.aiostreams.viren070.me/guides/usenet/).
   We allow 60 seconds for InfiniteDrive resolution and 45 seconds for upstream
   source requests.
4. Download `InfiniteDrive.dll` from [Releases](https://github.com/OneHotTake/InfiniteDrive/releases).
   Back up your existing plugin, configuration, database and managed library
   state. Copy the DLL into Emby's plugins directory and restart Emby.
5. Open **Plugins → InfiniteDrive**. Add your manifest, choose catalogs and set
   the managed library folders. Follow the [configuration guide](docs/configuration.md).
6. Try a few movies and episodes. Check that their files appear, Emby indexes
   them and playback works before raising catalog limits. Watch for upstream
   rate limits: an empty catalog response can mean a failed import.

Keep manifest URLs, configuration IDs, passwords, API keys, private provider
addresses and signed stream links out of issues and screenshots. Share settings,
not full configuration exports.

## Configuration

Open **Plugins → InfiniteDrive**, or use this URL on a default local server:

```text
http://localhost:8096/web/configurationpage?name=InfiniteDrive
```

- **Manifest 1:** your AIOStreams manifest URL. Treat it as a credential.
- **Manifest 2:** an optional second source, active whenever configured.
- **Lists:** MDBList and AniList need no provider keys; Trakt and TMDB need credentials.
- **Allow REMUX / Allow CAM/TS:** both off by default.
- **Desired versions:** choose which versions to keep.
- **Discover restrictions:** set browsing and parental controls.

Lists still work when a manifest has no catalogs. If neither supplies content,
Marvin derives a small starter catalog for that run. In **Repair** mode, new
Discover additions wait for resolution before becoming files. Off/Observe retain
the legacy importer, whose manual-add path can create empty pending placeholders;
their presence does not prove playback is ready.

## Discover

Open Discover from the web plugin menu or at
`/web/configurationpage?name=InfiniteDiscover`. This interface requires a browser;
mobile apps do not include it.

Browse catalog rails, search movies and shows, open details and add titles.
Cards show library or pending status. Manage catalog subscriptions through the
native **Sources** settings page. The embedded page does not currently include
the historical My Picks/My Lists tabs.

Emby's rating limits apply. You can also hide unrated titles. The server enforces
these restrictions. See the [Discover guide](docs/USER_DISCOVER_UI.md).

## Under the hood

| Component | Job |
| --- | --- |
| `CatalogSyncTask` | Reads upstream catalogs and queues entries. |
| `RefreshTask` | Resolves queued entries and writes STRM and NFO files. |
| `ImportReconciliationService` | Checks coverage and repairs eligible gaps. |
| `IdResolverService` | Matches provider IDs to IMDb, TMDB and TVDB. |
| `StrmWriterService` | Writes files with metadata hints for Emby's scanner. |
| `AioMediaSourceProvider` | Supplies ranked playback choices to Emby. |
| `StreamProbeService` | Checks sources with HEAD or a bounded range GET. |

Repair mode takes over missing-file recovery and version refresh from the legacy
worker stages. Catalog sync and retention cleanup continue.

Browsing uses the source addon's own IDs and metadata endpoint. ID matching
happens during sync. Emby handles library metadata; InfiniteDrive supplies NFOs
and scanner hints. Only InfiniteDrive's managed files qualify for repair.

Quality filters also apply to cached results and second manifests. Logging
redacts manifest credentials, signed paths, query strings and probe diagnostics.

## Development

Build on a Docker-capable host:

```bash
# Extract references from the pinned Emby image, test and publish
./scripts/build-container.sh
```

The DLL is written to `artifacts/InfiniteDrive.dll`. See the
[developer guide](docs/dev-guide.md) for prerequisites, tests and isolated QA,
and [Contributing](CONTRIBUTING.md) for the review workflow.

## Further reading

- [Documentation index](docs/README.md)
- [Architecture](docs/architecture.md)
- [Import recovery](docs/import-reconciliation.md)
- [Settings and verification](docs/settings-matrix.md)
- [Historical engineering archive](docs/archive/README.md)

## License

MIT. See [LICENSE](LICENSE).

Vibe coded with Codex, Grok, Claude, GLM, Nim, Opencode, and whoever else would give me a free trial.  Guaranteed to have bugs.  And security holes.  Good luck.
