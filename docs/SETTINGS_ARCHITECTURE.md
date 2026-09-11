# Settings architecture

InfiniteDrive's settings expose connections and user intent. Operational state
is derived.

| Page | Persisted intent | Derived behavior |
|---|---|---|
| Overview | None | Readiness from library/provider/catalog state |
| Libraries | Names and paths | Emby locale, artwork, certification, and library existence |
| Quality | Desired buckets; Allow REMUX; Allow CAM/TS | Eight-version cap and edition diversity |
| Providers | Manifest 1/2 URLs | Every non-empty URL is an active peer |
| Sources | Catalog selection/limits, list credentials, system/user lists | Catalog union and starter fallback |
| Restrictions | Unrated and blocked-content intent | Server-side Discover/search filtering |
| Marvin | None beyond explicit run action | Schedule, batching, backoff, pruning protection |
| Advanced | Log level and explicit maintenance actions | Cache/database/service status |

Manifest URLs and provider keys render as masked fields. Per-instance
AIOStreams formatter passwords are never loaded from persistent configuration.

Removed rule fields and migration behavior are documented in
[configuration.md](configuration.md). The runtime/UI verification mapping is in
[settings-matrix.md](settings-matrix.md).
