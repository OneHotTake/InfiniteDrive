# Emby Infinite(Improbability)Drive

> **Succeeded by [Undertow](https://github.com/OneHotTake/undertow).**
>
> We now bridge self-hosted AIOMetadata/AIOStreams catalogs into Emby through a Jellyfin-compatible API. Undertow keeps metadata sync separate from on-demand playback and avoids InfiniteDrive's STRM/NFO publication and link-repair machinery. Same goal, fewer gears to feed Marvin.
>
> Undertow has been tested with AIOStreams/AIOMetadata and Remux. It should work with other Jellyfin-compatible servers implementing the required API subset; those integrations remain untested. See its [compatibility report](https://github.com/OneHotTake/undertow/blob/main/docs/compatibility.md).
>
> InfiniteDrive's code, releases and history remain here. Undertow is a successor, not an automatic database or watch-history migration. Read the [migration guide](https://github.com/OneHotTake/undertow/blob/main/docs/migration.md) before retiring a library. The README below describes InfiniteDrive's last release and is retained for existing users.


🚀 **DON'T PANIC**

InfiniteDrive brings [AIOStreams](https://github.com/Viren070/AIOStreams)
catalogs into Emby. It writes `.strm` files, finds streams when you press Play,
and fetches subtitles for Emby's subtitle picker. Marvin, its background worker,
imports titles and repairs missing files.

A stream will appear. Probably.

**Current version: 0.42.15**

Release 0.42.15 fixes diversity deadlines across routine refresh indexing and immediately releases rejected diversity leases. It retains the eventual provider diversity queue from 0.42.14 and administrator whole-title blocks from 0.42.13. It targets **Emby 4.10.0.40**.

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
We cap each movie feed at 200 titles and each TV feed at 50 series. They overlap;
750 memberships is the combined ceiling, not a unique-title count or a complete
TMDB import. Feed order depends on the
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

> **Bad formats can mean bad playback. The formatter is part of the integration.**
> Do not turn it into another Great Collapsing Hrung Disaster. Marvin cannot
> reconstruct information your formatter has removed.

**Configure the formatter on the exact AIOStreams profile whose manifest you
gave InfiniteDrive.** A formatter that looks lovely in another client's profile
does nothing for this one. Recheck it after importing a template, changing
inheritance or applying recommended settings.

#### Copy-and-paste custom formatter

In the **AIOStreams configuration used by InfiniteDrive**, open **Formatter**,
select **Custom**, and replace the **Name** and **Description** templates with
these two blocks. If this child profile inherits its formatter, override that
inheritance for the formatter first. Leave provider connections, filters and
sorting as they are.

**Name — paste this entire block into the Name field:**

```text
{stream.resolution::exists["{stream.resolution::replace('2160p','4K')}"||"Video"]}{stream.quality::exists[" · {stream.quality}"||""]}{stream.encode::exists[" · {stream.encode}"||""]}{stream.visualTags::exists[" · {stream.visualTags::join('/')}"||""]}
```

**Description — paste this entire block into the Description field:**

```text
{stream.size::>0["{stream.size::sbytes}"||"Size unknown"]}{stream.audioTags::exists[" · {stream.audioTags::join('/')}"||""]}{stream.audioChannels::exists[" {stream.audioChannels::join('/')}"||""]}{service.id::=aiostreams["{stream.type::=usenet[" · Service: Usenet"||""]}"||"{service.name::exists[" · Service: {service.name}"||"{service.id::exists[" · Service: {service.id}"||"{stream.type::=usenet[" · Service: Usenet"||""]}"]}"]}"]}{service.cached::isfalse[" · Not cached"||""]}
```

Copy the contents inside each code block, including braces and ordinary quotes.
Do not paste the Markdown fences, add JSON escaping, or put both templates in one
field. Each block is one logical line; visual wrapping in GitHub is harmless.
**Save/update the AIOStreams configuration** after pasting. Do not subsequently
apply InfiniteDrive's older recommended formatter, which would replace these
values. Confirm InfiniteDrive still uses this profile's current manifest.

For a cached TorBox stream with matching metadata, the AIOStreams preview is:

```text
4K · WEB-DL · HEVC · HDR10
28 GB · Atmos/DD+ 7.1 · Service: TorBox
```

The service comes from AIOStreams, not a fixed TorBox label: known services use
`service.name`, unfamiliar services fall back to `service.id`, and a Usenet
stream without a named service uses `Usenet`. AIOStreams' internal `aiostreams`
service ID is shown as `Usenet` only when `stream.type` is `usenet`; otherwise it
is omitted. A named provider such as TorBox keeps its name even for a Usenet
stream. Missing service information stays omitted; missing resolution/size shows
`Video`/`Size unknown`. Only an explicit uncached flag adds `Not cached`.

The Name intentionally includes source and codec as well as picture tags. These
are reported values, not proof that every audio tag/channel count describes the
same track. The formatter changes display text; it does not enforce HEVC, set a
bitrate cap, change providers or create missing filename/size metadata.

Verified September 29, 2026 with the installed AIOStreams formatter engine:
11 synthetic cases covering movies/episodes, TorBox, Real-Debrid, AllDebrid,
Premiumize, Usenet, an unfamiliar service ID, absent metadata and uncached results.
This checks template rendering; it is not a new playback acceptance test.

Standard Stremio responses can omit AIOStreams' structured `parsedFile` and
`service` fields. InfiniteDrive then reads `behaviorHints.filename` and formatted
text. Removing or disguising those signals can change filtering, ranking and
version selection, hide the delivery service, or leave an unsuitable playback
choice. A missing provider label alone does **not** make a valid URL unplayable.

The rules are deliberately boring:

- Preserve the real `behaviorHints.filename` in the response. It need not be
  displayed in the label; it carries resolution, codec, source and edition hints.
- Keep plain technical words: `2160p`/`4K`, `1080p`, `HEVC`/`x265`, `WEB-DL`,
  `BluRay`, `REMUX`, audio codec and channel count. Do not replace meaningful
  values with decorative nicknames or icons alone. Missing fields stay unknown;
  never label every result HEVC, cached or TorBox just because you prefer those.
- Keep size in response metadata (`behaviorHints.videoSize` or `size`, in bytes).
  The description fallback understands numbers followed by `GB`; do not rely on
  arbitrary units or decorative size text being parsed.
- Keep the **delivery service** explicit. `Service: TorBox`,
  `Service: Real-Debrid` or `Service: Your Provider` works as a separate text
  field. Bare recognized names such as `TorBox` or `Usenet` also work.
  Separate it with a newline, `·`, `•` or `|`. An indexer, addon or release group
  is not the delivery service. An unfamiliar bare name may be omitted; use the
  `Service:` prefix for it. Use the service's actual value, not a hard-coded brand.
- Preserve the playable `url`. Pretty labels cannot repair an expired URL,
  missing source, authentication failure or incompatible media.

No release filenames, indexer names or repeated movie titles. The label describes
the source; your player and display determine what you can actually use.

InfiniteDrive makes its own Emby labels and ranks results again. Standard addon
responses can omit structured codec/HDR fields, and not every formatted value
flows into InfiniteDrive's stored labels, so labels and order can differ.
Emby version labels now include the delivery service when identified, including
on the primary version. Structured service names/IDs support other providers;
standard responses use explicit `Service:`/`Provider:` text or recognized service
names such as TorBox, Real-Debrid, AllDebrid, Premiumize and Usenet. Unknown
services are omitted when the response does not identify them; URLs and release
filenames are never used to guess a service. Existing files gain labels through
normal successful version refreshes. No STRM wipe is required.
Emby may also merge provider aliases and show more than eight versions.

**Verify before a bulk import:** preview a movie and an episode, including each
delivery service you use. Then inspect a real response privately: confirm its
URL, filename hint, size and service text survived the formatter. Resolve a
small sample in InfiniteDrive, check Emby's version picker and actually play it.
A formatter preview, successful import or pretty poster is not a playback test.
Test again after formatter changes; existing files keep their old labels until
a successful refresh updates them. Do not wipe the library to test a formatter.

In 0.42.5, the plugin's **recommended formatter & sort** action uses an older
template that does not include the service and also changes sorting. It is not
the copy-and-paste formatter above. Review its preview and preserve your intended
quality policy; add the service field in AIOStreams before relying on provider
labels. See [configuration](docs/configuration.md#aiostreams-formatter).

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

For a big refresh, engage **Infinite Improbability Drive** on the Marvin page.
The switch runs up to 64 lookups together, paced to two starts a second. Each
pass gets eight minutes, up to 4,096 attempts shared by missing-file repairs and
existing-file refreshes, and a rolling daily ceiling of 40,000. Missing-file fills and existing-file refreshes alternate first priority across
native runs; either can use capacity left by the other. Completed lookups publish while inventory checks continue.
It isolates disputed episode numbering so confirmed siblings can proceed.
It remembers refreshed episodes,
keeps provider backoff and media protections, and switches itself off after seven
days. Disengage it any time: **Normality has been restored.** Actual speed depends
on your sources; the ceiling is not a completion estimate.
See [Import recovery](docs/import-reconciliation.md) for limits and exclusions.

### Backoff and deferred recovery

Marvin keeps a separate failure streak for each movie or episode. Transport failures
wait about 15 minutes, 1 hour, 4 hours, 1 day, 3 days, then 7 days. No matching
sources wait 6 hours, 1 day, 2 days, 3 days, then 7 days. Retries include up to
20% jitter and respect longer provider cooldowns. A successful publication clears
that item’s failure streak; attempts remain in the rolling ledger.

Repeated source errors open a maintenance circuit with waits of 5 minutes,
15 minutes, 1 hour, 4 hours and 1 day. Playback uses its own lane. After the pause,
Marvin probes once per pass until three recent responses include a source match.
This is AIO responsiveness, not a test of every indexer.

Use **Reprocess deferred items** for one durable pass over current eligible failures.
It preserves the saved files, original retry times and attempt history until an
actual lookup completes. While recovery is unproven it probes one item per pass;
when ready it uses the existing run budget. **Cancel pending reprocessing** leaves
in-flight work alone. Blocks, removals, owned media and disputed episode identities
always win. It does not add a missing provider slot to already successful choices.

### Eventual source diversity

With **Repair & refresh** enabled, Marvin gradually revisits indexed movies and episodes that have one known saved delivery source. Several TorBox quality variants still count as one source. A series is counted per episode; having TorBox on one episode and Usenet on another does not resolve either episode's gap.

The queue defaults on. **Pause source diversity** stops its lookups/additions while essential repair continues; **Enable eventual source diversity** resumes normal scheduled processing. Entries, backoff and attempt history persist. Neither button triggers a pass or resets retries. There is no force-drain action. Unknown labels, blocked/removed/owned titles, unconfirmed identities and essential failures are excluded.

New entries wait six hours. Routine refresh and re-indexing preserve an existing diversity deadline and backoff; they do not restart that wait. Final guard rejection clears only the completed lookup’s own lease. Missing items and ordinary refreshes have priority; diversity borrows at most two checks per normal pass or eight per catch-up pass from the existing shared allowance, serially. With stale health it can make one shared probe only if essential work has not already reserved a check. Same-source/empty diversity results escalate through 6h, 1d, 2d, 3d and 7d, plus up to 20% jitter and longer cooldowns. The queue persists beyond catch-up expiry; gradual processing does not guarantee a second source exists.

A successful diversity check **adds** one validated variant and preserves every working file. New variants require a known different label and positive exact upstream size no greater than 40GB. An item with all eight version slots occupied waits for capacity; Marvin does not delete a working choice to force diversity. A saved addition does not prove playback or independent provider infrastructure. Ordinary refresh retains its existing replacement behavior and can change coverage later.

Inspect dated queue state, reasons, streak and due time in native import status or the Failure Library. A one-source flag is not proof of a queue entry. **[Read the exact queue contract](docs/provider-diversity-queue.md)** for schema/leases, state transitions, health predicate, every ladder/counter, restart and partial-write recovery, operator controls, examples and rollback.

### Marvin dashboard

Open **Plugins → InfiniteDrive → Marvin** and use **Refresh dashboard**.
It shows a dated snapshot of the current run: current step/title, lookups in
flight, missing-file versus refresh attempts, matching sources, empty results,
actual AIO HTTP submissions/retries, transport failures, lookup deadlines, actual HTTP 429 responses and publication
outcomes. The rolling allowance is an attempt budget, not a library count.
When it is full, the dashboard shows when the earliest usable credit ages out.

Timings cover metadata, file/native observations, source resolution, publication
dispatch pacing, AIO HTTP response and observation checkpoints. Parallel request totals overlap; they do not add
up to elapsed time. Resolution includes dispatch pacing and provider wait.
Mean/max/count cover the run; p95 uses the latest 512 samples per stage. Recent
completed passes persist for seven days. Timing history starts with 0.42.11;
older reports keep their original counters. A server restart ends the live
sample, but preserves saved reports. Reading the dashboard starts no work.

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
