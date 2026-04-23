# Documentation Index

Welcome to EmbyStreams documentation. Choose the guide that matches your needs:

---

## 🚀 First Time Users

**Start here if you're new to EmbyStreams:**

→ **[Configuration Guide](./configuration.md)**
- How to install the plugin
- Initial setup with the wizard
- Configuring AIOStreams, debrid provider, library paths
- Understanding settings options

---

## ❓ Troubleshooting

**Something isn't working?**

→ **[Troubleshooting Guide](./troubleshooting.md)**
- Common problems and solutions
- Debug commands and logs
- How to check plugin health
- When to contact support

→ **[Failure Scenarios](./failure-scenarios.md)**
- What happens when AIOStreams is down
- How the failover system works
- Expected behavior during outages
- Recovery procedures

---

## ✨ Features

**Detailed guides for specific features:**

→ **[Discover Feature](./features/discover.md)**
- How to use the Netflix-style Discover sidebar
- Browse and search available content
- Add movies/shows to your library with one click
- Architecture and implementation details

---

## Settings

Plugin configuration uses Emby's native declarative UI system (IHasUIPages). All settings tabs render with Emby's standard look and feel — no custom HTML/JS required.

**7 configuration tabs:**

| Tab | Purpose |
|-----|---------|
| Health | Live dashboard with connection status, coverage, API budget, recent plays |
| Providers | AIOStreams manifest URLs, connection test |
| Libraries | Storage paths, metadata preferences, library provisioning |
| Sources | Catalog sync settings, cache tuning, proxy mode |
| Security | Plugin secret rotation, signature validity |
| Parental Controls | TMDB API key, unrated content filter |
| Repair | Diagnostic triggers, destructive actions (with confirmation) |

The Health tab refreshes server-side — clicking "Refresh Status" fetches live data from the plugin and returns a fresh view. No client-side polling.

---

## 🔧 For Developers

**If you're contributing to EmbyStreams:**

→ **[../CLAUDE.md](../CLAUDE.md)** — Developer guide
- Project structure and architecture
- Setting up the dev environment
- Common development tasks
- Testing checklist

→ **[../RUNBOOK.md](../RUNBOOK.md)** — Development server setup
- Building and running the dev server
- Troubleshooting dev environment
- Port configuration
- Verification commands

→ **[../HISTORY.md](../HISTORY.md)** — Release history and sprints
- Completed features by version
- Sprint tracking
- Backlog and roadmap
- Contributing guidelines

→ **[../SECURITY.md](../SECURITY.md)** — Security model
- API key authentication
- Threat model
- Best practices for secure deployment

---

## 📚 Quick Reference

| Document | For | Purpose |
|----------|-----|---------|
| **configuration.md** | Users | Setup and settings |
| **troubleshooting.md** | Users | Problems and solutions |
| **failure-scenarios.md** | DevOps | How system fails gracefully |
| **features/discover.md** | Users & Developers | Discover feature details |
| **../README.md** | Users | Product overview |
| **../CLAUDE.md** | Developers | Development guide |
| **../RUNBOOK.md** | Developers | Dev environment setup |
| **../HISTORY.md** | Developers | Sprints and releases |
| **../SECURITY.md** | Everyone | Security and auth |

---

## 🤔 Not Sure Where to Start?

**Ask yourself:**

- **"How do I install/configure this?"** → Configuration Guide
- **"Something isn't working"** → Troubleshooting Guide
- **"What does the Discover feature do?"** → Discover Feature
- **"How do I set up development?"** → RUNBOOK.md
- **"What are the security implications?"** → SECURITY.md
- **"What was in the last release?"** → HISTORY.md

---

## 🔗 Related Documents

**Root-level documentation:**
- **README.md** — Product overview, features, getting started
- **CLAUDE.md** — Developer guide and project principles
- **RUNBOOK.md** — Dev server setup and testing
- **SECURITY.md** — Security model and best practices
- **HISTORY.md** — Release notes, sprint history, roadmap

**Architecture documentation:**
- **[../architecture/OVERVIEW.md](../architecture/OVERVIEW.md)** — High-level system architecture
- **[../architecture/CONTROL_FLOW.md](../architecture/CONTROL_FLOW.md)** — Pipeline and flow diagrams
- **[../architecture/SERVICES.md](../architecture/SERVICES.md)** — Service inventory and contracts

**Playback and security:**
- **[REQUIRES_OPENING_PIPELINE.md](REQUIRES_OPENING_PIPELINE.md)** — Secure playback via RequiresOpening + OpenMediaSource (Sprint 410)
- **[STREAM_RESOLUTION.md](STREAM_RESOLUTION.md)** — Deprecated resolution pipeline (pre-Sprint 410)

---

**Last Updated:** 2026-04-23
