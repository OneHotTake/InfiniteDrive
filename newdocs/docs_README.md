# Documentation Index

---

## First Time Users

**Start here:**

→ **[Getting Started](./getting-started.md)**
- Installation steps (plugin folder: `InfiniteDrive/`, not `EmbyStreams/`)
- Initial setup with AIOStreams manifest URL
- Verifying playback

→ **[Configuration Reference](./configuration.md)**
- All settings documented with correct field names
- Config file: `InfiniteDrive.xml` (not `EmbyStreams.xml`)
- Auto-discovered fields and bounds validation

---

## Troubleshooting

→ **[Troubleshooting Guide](./troubleshooting.md)**
- Common problems and solutions
- Debug commands and log locations

→ **[Failure Scenarios](./failure-scenarios.md)**
- What happens when AIOStreams is down
- How the failover system works (primary → secondary pivot)
- Circuit breaker states: closed → open → half-open
- Recovery procedures

---

## Features

→ **[Discover Feature](./features/discover.md)**
- Netflix-style browsing via `/InfiniteDrive/Discover`
- One-click library addition
- Search, My Picks, My Lists tabs

---

## Settings

Plugin configuration uses Emby's native declarative UI system (IHasUIPages). All settings tabs render with Emby's standard look and feel.

**7 configuration tabs:**

| Tab | Purpose |
|-----|---------|
| Health | Live dashboard with connection status, coverage, API budget, recent plays |
| Providers | AIOStreams manifest URLs, connection test |
| Libraries | Storage paths, metadata preferences, library provisioning |
| Sources | Catalog sync settings, cache tuning, proxy mode |
| Security | PluginSecret rotation, signature validity |
| Parental Controls | TMDB API key, unrated content filter |
| Repair | Diagnostic triggers, destructive actions (with confirmation) |

The Health tab refreshes server-side — clicking "Refresh Status" fetches live data from the plugin. No client-side polling.

---

## For Developers

→ **[../CLAUDE.md](../CLAUDE.md)** — Developer guide
- Project structure and architecture
- Setting up the dev environment
- Testing checklist

→ **[../RUNBOOK.md](../RUNBOOK.md)** — Development server setup

→ **[../SECURITY.md](../SECURITY.md)** — Security model
- HMAC-SHA256 playback authentication via `PlaybackTokenService`
- PluginSecret: auto-generated, rotatable
- Threat model and best practices

---

## Quick Reference

| Document | Audience | Purpose |
|----------|----------|---------|
| **getting-started.md** | Users | Installation and first-run |
| **configuration.md** | Users | All settings with correct field names |
| **troubleshooting.md** | Users | Problems and solutions |
| **failure-scenarios.md** | DevOps | Graceful degradation |
| **features/discover.md** | Users & Developers | Discover UI details |
| **SECURITY.md** | Everyone | HMAC auth, PluginSecret, threat model |
| **ARCHITECTURE.md** | Developers | Service layer, data flow, guardrails |
| **LIFECYCLE.md** | Developers | Content stage progression |
| **VERSIONED_PLAYBACK.md** | Developers | Slot definitions, rehydration |
| **../README.md** | Users | Product overview |

---

**Last Updated:** 2026-04-15
