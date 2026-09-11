# InfiniteDrive settings coverage matrix

Verified against the 0.42.2 source and isolated Emby 4.10.0.40 staging on
2026-09-11. This table describes the supported UI contract; persisted discovery
snapshots and migration fields are intentionally not presented as user rules.

| Intent/state | UI | Source of truth | Runtime behavior | Evidence |
|---|---|---|---|---|
| Movie/Series/Anime names and paths | Libraries | Plugin XML | Creates/follows Emby libraries and managed `.strm` roots | Chromium inspection; staging files |
| Metadata language | None | Matching Emby library | Catalog creation and search use Emby preference; `en` fallback | Source + clean build |
| Image language | None | Matching Emby library | Artwork requests use Emby preference; `en` fallback | Source + clean build |
| Certification country | None | Matching Emby library | Certification lookup uses Emby preference; `US` fallback | Source + clean build |
| Desired version buckets | Quality | Plugin XML | Edition representatives first, then bucket fill | Regression tests; Chromium inspection |
| Allow REMUX | Quality | Plugin XML, default off | Admits REMUX before selection/cache; never forces priority | Default-reject and explicit-admit tests; save/reload test |
| Allow CAM/TS | Quality | Plugin XML, default off | Admits CAM/TS before selection/cache | Default-reject and explicit-admit tests |
| Manifest 1 | Providers | Plugin XML | Active peer for catalogs, search, and resolution | Peer regression; staging E2E |
| Manifest 2 | Providers | Plugin XML when non-empty | Active peer; no enable/backup switch | Peer regression; Chromium inspection |
| Manifest catalogs | Sources | Manifest plus disabled-source/limit state | Catalogs from all peers are unioned and IDs deduplicated | Catalog sync regression; staging DB |
| System/user lists | Sources | List database | Synchronize independently of manifest catalogs | Starter/list state tests; Chromium inspection |
| No catalog/list content | None | Derived | Small Cinemeta starter for that sync | State-table regression tests |
| Discover restrictions | Restrictions | Plugin XML and block database | Server-side filtering alongside native Emby controls | Source + Chromium inspection |
| Marvin cadence/work size | None | Runtime/provider state | Automatic, bounded, respects provider backoff | Source + scheduled staging run |
| Run Marvin Now | Marvin | User action | Triggers immediate reconciliation | Chromium inspection |
| Cache freshness | None | Entry expiry/probe plus runtime fallback | Refreshes stale/dead candidates | Source + playback test |
| Provider pressure | None | Retry/backoff state | Background work pauses and resumes automatically | Source + regression coverage |
| Next episode | None | Indexed Emby episodes and premiere dates | Queues exactly the next released indexed episode | Source + clean build |
| Maximum versions | None | Emby capability | Hard ceiling of 8; editions preserved first | Regression tests |
| Physical duplicate | None | Emby identities and managed-path ownership | Pre-write retirement; owned path remains durable across catalog refresh; only verified disappearance resurrects streaming | Source + scope + durable-state regression |
| Logging/maintenance | Advanced | Plugin XML/user action | Changes verbosity or runs explicit maintenance | Chromium inspection |

## Removed rule fields

Regression tests assert that the removed configuration properties are absent,
including backup-enable, accepted-stream-type, duplicate Emby API/path, plugin
locale, `DontPanic`, future-episode, configurable cache lifetime/API budget,
dynamic-secondary, maximum-version, and extended-edition controls. See
[configuration.md](configuration.md) for migration details.
