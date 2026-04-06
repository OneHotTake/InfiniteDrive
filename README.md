# 🚀 DON'T PANIC

*"The ships hung in the sky in much the same way that bricks don't."*
— Douglas Adams, The Hitchhiker's Guide to the Galaxy

> ⚠️ **WARNING:** This project is currently under **heavy development**.
> It does not work. It may never work. It might make your Emby server
> question its own existence. You have been warned.
> Bring a towel.

---

# EmbyStreams

![Version](https://img.shields.io/badge/version-0.52.0-blue)
![Framework](https://img.shields.io/badge/framework-net8.0-purple)
![Emby](https://img.shields.io/badge/Emby-4.8%2B-green)
![License](https://img.shields.io/badge/license-MIT-lightgrey)

> DON'T PANIC.

EmbyStreams is a single `.dll` Emby Server plugin that wires your [AIOStreams](https://github.com/Viren070/AIOStreams) instance directly into Emby — featuring **Netflix-like Discover browsing**, **intelligent multi-layer failover**, **automatic catalog sync**, and a sprinkle of *The Hitchhiker's Guide to the Galaxy*.

---

## 🚧 Status: Pre-Alpha / Work in Progress

This is an early-stage project under active development. Many features are incomplete, undocumented, or may change without notice.

### Roadmap

---

> **Design Principle: Simplicity Over Complexity**
>
> Users want simplicity, administrators want flexibility, nobody wants complexity. Fortunately for us, the debrid and usenet streaming world is inherently complex.
>
> When making architectural decisions: prefer the simple approach that works over the sophisticated one that handles every edge case.

---

### Roadmap

| Phase | Status |
|-------|--------|
| Core AIOStreams integration | ✅ Complete |
| Netflix-style Discover UI | ✅ Complete |
| Multi-layer failover | ✅ Complete |
| Configuration wizard | ✅ Complete |
| Library auto-creation | ✅ Complete |
| Documentation | 🚧 In Progress |
| Testing | 🚧 In Progress |
| Stability polish | 🚜 Planned |

---

## Overview

EmbyStreams syncs streaming catalogs from AIOStreams into your Emby library as `.strm` files, providing:

- **Netflix-style Discover** — Browse and search available streaming catalog with one-click "Add to Library"
- **One-click playback** — Stream URLs resolve in under 100 ms from an in-memory/SQLite cache
- **Four-layer failover resilience** — Primary AIOStreams → Fallback instances → Direct debrid API → Friendly error page
- **Automatic catalog sync** — Zero-config defaults with daily schedule
- **Smart background resolution** — Cache pre-warming, binge-watch support, quality tier ranking
- **Self-contained** — Runs entirely inside the Emby process, no Docker or extra services

---

## Required Environment Variables / Configuration

The plugin uses Emby's built-in configuration system. No environment variables are required for basic operation.

**Key configuration settings (configured via Emby Dashboard → Plugins → EmbyStreams):**

| Setting | Description | Required |
|---------|-------------|----------|
| `PrimaryManifestUrl` | AIOStreams manifest URL (e.g., `https://your-host/stremio/uuid/token/manifest.json`) | Yes |
| `SyncPathMovies` | Filesystem path for movies `.strm` files | Yes |
| `SyncPathShows` | Filesystem path for TV shows `.strm` files | Yes |
| `EmbyBaseUrl` | Auto-detected from browser; default `http://localhost:8096` | Auto |
| `ProxyMode` | Stream serving mode: `auto` / `redirect` / `proxy` | Optional |
| `PluginSecret` | Auto-generated HMAC-SHA256 key for signing .strm URLs | Auto |
| `FallbackManifestUrls` | Secondary AIOStreams instances for failover | Optional |
| `DebridApiKey` | Direct debrid API key (Real-Debrid, TorBox, Premiumize, AllDebrid) | Optional |

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Emby Server 4.8+ | Linux or Windows; tested against 4.10.x |
| AIOStreams instance | Self-hosted or via DuckKota's hosted wizard |
| Debrid service | Real-Debrid, TorBox, Premiumize, or AllDebrid — active subscription |
| .NET 8 runtime | Provided by Emby — no separate install needed |

---

## Installation (For the Brave)

> **Remember:** This is pre-alpha software. Backup your Emby server before installing.

### Build from source

```bash
git clone https://github.com/OneHotTake/embyStreams.git
cd embyStreams
dotnet publish --configuration Release
# Output: bin/Release/net8.0/publish/
```

### Deploy to Emby

```bash
# Create plugin folder
mkdir -p /var/lib/emby/plugins/EmbyStreams

# Copy all publish files (including plugin.json!)
cp bin/Release/net8.0/publish/* /var/lib/emby/plugins/EmbyStreams/

# Restart Emby
systemctl restart emby-server
```

### First-run setup

1. Open **Emby Dashboard → Plugins → EmbyStreams → Settings**
2. Run the **Setup Wizard**:
   - Step 1: Paste your AIOStreams manifest URL
   - Step 2: Configure media folders
   - Step 3: Review settings and click **Save & Start Sync**
3. `.strm` files should appear within ~1 minute

---

## Architecture

```
┌─ Emby Library ─────────────────────────────────────────────┐
│  Dune (2021).strm  →  http://localhost:8096/EmbyStreams/Play│
└─────────────────────────────────────────────────────────────┘
                          │ Play request
                          ▼
┌─ PlaybackService ──────────────────────────────────────────┐
│  1. SQLite resolution_cache lookup                         │
│  2. Valid cache hit  → serve in < 100 ms                   │
│  3. Aging cache (70% TTL) → range-probe → refresh if dead  │
│  4. Cache miss  → sync AIOStreams call (3 s timeout)       │
│  4.5. AIOStreams down → Layer 2 fallbacks → Layer 3 debrid │
│  5. All fail → HTTP 503 + Panic page redirect              │
└─────────────────────────────────────────────────────────────┘
                          │ proxy or redirect
                          ▼
              Real-Debrid / TorBox / CDN URL
```

**Background tasks keep the cache warm:**

| Task | Schedule | Purpose |
|------|----------|---------|
| `CatalogSyncTask` | Daily @ 3 AM | Discovers new items, writes `.strm` files |
| `CatalogDiscoverTask` | Daily @ 4 AM | Syncs discover catalog from AIOStreams |
| `LinkResolverTask` | Every 15 min | Pre-resolves stream URLs |
| `DoctorTask` | Every 4h | Unified catalog reconciliation (write/adopt/retire) |
| `EpisodeExpandTask` | On playback start | Queues next N episodes for pre-resolution |
| `MetadataFallbackTask` | Daily | Writes rich `.nfo` for items without poster art |

---

## Documentation

For detailed guides, see the [Documentation Index](./docs/README.md):

- **[Configuration Guide](./docs/configuration.md)** — Setup, wizard, and settings reference
- **[Troubleshooting Guide](./docs/troubleshooting.md)** — Common issues and debug steps
- **[Discover Feature](./docs/features/discover.md)** — Netflix-style catalog browsing
- **[Security Model](./SECURITY.md)** — Authentication, threat model, best practices
- **[Developer Guide](./CLAUDE.md)** — Architecture, development, and contributing

---

## Easter Eggs 🌌

EmbyStreams is built by fans of *The Hitchhiker's Guide to the Galaxy*. The answer is, of course, **42**.

| Where | What |
|---|---|
| High Availability tab | **DON'T PANIC** in large, friendly letters |
| `GET /EmbyStreams/Panic` | HHGTTG-styled error page for playback failures |
| `GET /EmbyStreams/Answer` | `{"answer": 42, "question": "unknown", "note": "Don't Panic."}` |
| `GET /EmbyStreams/Marvin` | *"I have a brain the size of a planet..."* |

---

## License

MIT

---

**Remember: DON'T PANIC. Bring a towel. 🧣**
