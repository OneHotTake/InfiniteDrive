# Release History & Sprints

This document tracks all releases, completed sprints, and future roadmap. For current development status, see CLAUDE.md.

---

## Current Version

**0.20.0** — Latest release
**Branch:** master
**Status:** Production-ready

---

## Release Changelog

### [0.21.0] - 2026-03-30

**Sprint 54 — Native IChannel Search & Browse**

#### Added
- 📺 **Native Emby IChannel Integration** — Full sidebar integration for Discover feature
  - Hierarchical browsing: "Movies", "TV Series", "Recently Added", "Popular" categories
  - Native search integration with Emby's global search bar
  - Playback through `/EmbyStreams/Play` endpoint with API key validation
  - Support for both Movie and Series content types
- 🔍 **Enhanced Database Queries** — Media type filtering and sorting
  - `GetDiscoverCatalogAsync()` now supports `mediaType` and `sortBy` parameters
  - Sorting options: "imdb_rating", "title", "added_at"
  - Backward compatible with existing REST API calls

#### Technical Details
- **Adapter Pattern**: `DiscoverChannel` wraps existing `DiscoverService` logic
- **Hierarchical Navigation**: Root → Categories → Items structure
- **Search Integration**: Leverages existing FTS5 search infrastructure
- **Playback Security**: Maintains API key validation through existing endpoint

### [0.20.0] - 2026-03-29

**Major Release — Version Stability & Core Auth**

#### Added
- 🔐 **Playback API Key Authentication** — Auto-generated per-instance API key for secure .strm playback
  - Unique 32-character hex key embedded in all `.strm` file URLs
  - Validates on every playback request; returns `401 Unauthorized` on failure
  - Security model: local-only, scoped to playback only
- 🔄 **API Key Rotation** endpoint and UI
  - `POST /EmbyStreams/Setup/RotateApiKey` — generates new key, rewrites all `.strm` files
  - Rotation completes in < 5 seconds even for 1000+ files
- ⚡ **Health Check Optimization** — Eliminated excessive AIOStreams polling
  - Manifest validation runs once on server startup
  - `POST /EmbyStreams/Status/Refresh` for manual refresh
- 📖 **Comprehensive Security Documentation** (SECURITY.md)

#### Changed
- All .strm files now include API key in playback URL
- Updated README.md to highlight authentication security
- Version locked at 0.20.0 for production stability

#### Technical Details
- **New Endpoints:**
  - `POST /EmbyStreams/Setup/RotateApiKey`
  - `POST /EmbyStreams/Status/Refresh`
- **Modified Components:**
  - `Plugin.cs` — Auto-generates API key on startup
  - `PluginConfiguration.cs` — Stores PlaybackApiKey field
  - `Services/PlaybackService.cs` — Validates API key with 401 response
  - `Services/DiscoverService.cs` — Embeds API key in .strm URLs
  - `Services/SetupService.cs` — Implements key rotation logic
  - `Configuration/configurationpage.html` — Security banner + rotation UI

---

### [0.19.4] - 2026-03-25

**Discover Feature Stabilization**

#### Added
- ✨ **Netflix-style Discover Channel** in sidebar
- ✨ **Zero-config Auto-initialization** — Discover syncs on server startup
- ✨ **Auto-library-refresh** — Adding items triggers automatic scan
- ✨ **REST API for Discover:**
  - `GET /EmbyStreams/Discover/Browse`
  - `GET /EmbyStreams/Discover/Search`
  - `GET /EmbyStreams/Discover/Detail`
  - `POST /EmbyStreams/Discover/AddToLibrary`
- ✨ **Manual Discover sync trigger** via `/EmbyStreams/Trigger?task=catalog_discover`
- ✨ **Scheduled daily Discover sync** (4 AM, configurable)
- 📊 **Database schema V13** — `discover_catalog` table
- 📋 **Comprehensive Discover documentation** (DISCOVER_FEATURE.md)

#### Technical Details
- **New Components:**
  - `Services/DiscoverService.cs`
  - `Services/DiscoverChannel.cs`
  - `Services/CatalogDiscoverService.cs`
  - `Services/DiscoverInitializationService.cs`
  - `Tasks/CatalogDiscoverTask.cs`
  - `Models/DiscoverCatalogEntry.cs`
- **Database Changes:**
  - Migration: V12 → V13
  - New table: `discover_catalog`
  - Indexes: imdb_id, catalog_source, is_in_user_library

---

### [0.19.0] - 2026-03-24

**Sprint 18: Discover Feature Core**

#### Added
- 🎬 Discover feature core implementation
- Browse available streaming content from AIOStreams
- Search catalog by title
- Add items to library with one click
- 🛠️ Database layer for caching
- 🔌 Plugin infrastructure for channels/services

---

### [0.18.1] - 2026-03-20

**Sprint 16: Wizard UX Fixes**

#### Fixed
- ✅ Form state persistence across wizard steps
- ✅ Removed accidental popup dialogs
- ✅ Catalog ordering arrows now persist
- ✅ Better error messaging in wizard

---

### [0.18.0] - 2026-03-15

**Sprint 15: HA Configuration Guide**

#### Added
- 🏛️ High Availability configuration recommendations
- 📊 Configuration comparison tables
- 🎯 Interactive setup wizard with best-practice defaults

---

### [0.17.0] - 2026-03-10

**Sprint 14: UX Simplification**

#### Changed
- 🎨 Complete Settings UI redesign (minimal, task-focused)
- 📋 Setup Wizard for first-run experience
- 🔍 Health Dashboard with status + quick actions
- Removed cluttered admin panels

---

### [0.16.0] - 2026-03-05

#### Added
- 📋 Interactive Setup Wizard
- 🔍 Health Dashboard
- 📊 Configuration status tracking

---

### [0.15.0] - 2026-02-28

#### Added
- 🎯 Multi-provider failover support
- 🔄 Automatic catalog sync from multiple sources
- 🎬 Real-Debrid streaming integration
- 🗄️ SQLite caching layer

---

### [0.1.0] - 2026-01-01

**Initial Release**

#### Added
- Initial Emby plugin scaffold
- Basic catalog sync infrastructure
- Stream resolution foundation
- Playback endpoint implementation

---

## Completed Sprints

### ✅ Sprint 18 — Discover Feature

**Status:** Complete
**Release:** v0.19.4
**Duration:** 1 sprint

**Deliverables:**
- Netflix-style Discover sidebar channel (IChannel)
- REST API endpoints (Browse, Search, Detail, AddToLibrary)
- AIOStreams catalog sync service
- Auto-initialization on server startup (zero-config)
- Auto-library-refresh after adding items
- Scheduled daily sync (4 AM)
- Database schema V13 (discover_catalog table)

**Impact:**
- Users can now discover and add content without leaving Emby
- Automatic sync eliminates manual catalog management
- One-click "Add to Library" creates .strm files instantly

---

## Open/Backlog Items

### Sprint 19 — Discover Enhancements (Future)

**Design Goal:** Polish Discover with discovery recommendations, favorites, and filtering.

**Candidate Features:**
- [ ] Trending/Popular items based on AIOStreams stats
- [ ] Favorites/Watchlist in Discover
- [ ] Filter by genre/rating/year
- [ ] Recommended items (content similar to library)
- [ ] Recent additions section
- [ ] Pagination UI in sidebar channel
- [ ] Watch history integration
- [ ] User ratings/reviews from AIOStreams

---

### Sprint 20 — Multi-Account Profiles (Future)

**Design Goal:** Support multiple user profiles with independent libraries.

**Candidate Features:**
- [ ] Per-profile library folders
- [ ] Per-profile watch history
- [ ] Profile-specific recommendations
- [ ] Parental controls (age-based filtering)
- [ ] Profile switching in UI

---

### Sprint 21 — Mobile/Web Optimization (Future)

**Design Goal:** Optimize for mobile clients.

**Candidate Features:**
- [ ] Mobile-friendly Discover UI
- [ ] Thumbnail grid optimization
- [ ] Bandwidth-aware stream quality selection
- [ ] Offline support (cached metadata)
- [ ] Progressive load for large catalogs

---

## Known Limitations / Won't Do

- **[-] Cloud Sync:** User libraries don't sync across multiple Emby instances
- **[-] Authentication System:** Emby already provides auth; we don't duplicate
- **[-] DRM Protection:** We stream debrid sources only; no DRM-protected sources
- **[-] Subtitles Management:** Delegated to Emby's native subtitle handling
- **[-] Anime-Specific Logic:** Treated as series; no special casing

---

## Technical Standards

### Code Quality
- ✅ All public APIs documented with XML comments
- ✅ Nullable reference types enabled globally
- ✅ Release builds treat warnings as errors
- ✅ Services use dependency injection
- ✅ Database operations are async
- ✅ Proper error handling throughout

### Tech Stack
- **.NET 8.0** (net8.0 target framework)
- **Emby Server 4.8+** (plugin host)
- **SQLite** (local database)
- **ServiceStack** (REST routing)
- **System.Text.Json** (JSON serialization)
- **Microsoft.Extensions.Logging** (logging)

### Testing
- Manual end-to-end tests via dev server
- Emby plugin loader verification (reflection)
- Database migration testing (schema versions)
- API endpoint testing (REST client)
- UI regression testing (sidebar, settings, wizard)

---

## Contributing Guidelines

### Adding a Feature

1. Create a branch: `feature/short-description`
2. Add files in appropriate directories (Services/, Models/, Tasks/, etc.)
3. Implement IService, IChannel, IScheduledTask, or IServerEntryPoint
4. Add database migrations to DatabaseManager if needed
5. Document public APIs with XML comments
6. Test with dev server (`./start-dev-server.sh`)
7. Create pull request with detailed description
8. Increment version in `.csproj` and `plugin.json`

### Code Organization

```
/EmbyStreams/
├── Models/              # Data models
├── Services/            # REST services + channels
├── Tasks/               # Scheduled tasks
├── Data/                # Database layer
├── Configuration/       # UI assets (HTML/JS)
├── Logging/             # Logging adapters
├── plugin.json          # Plugin metadata
└── *.md                 # Documentation
```

### Commit Message Format

```
[Sprint XX]: Feature name - brief description

Detailed explanation of changes, rationale, and any breaking changes.

Files changed:
- NEW: Services/NewService.cs
- MODIFIED: Data/DatabaseManager.cs
- DELETED: OldService.cs
```

---

## Glossary

| Term | Definition |
|------|-----------|
| **AIOStreams** | Unified API for scraper-based content discovery |
| **Real-Debrid** | Premium debrid service for CDN streaming |
| **Discover** | Netflix-style browsable catalog feature |
| **Catalog Sync** | Fetching content metadata from AIOStreams and caching locally |
| **IChannel** | Emby interface for sidebar content browsing |
| **IService** | Emby interface for REST API endpoints |
| **IScheduledTask** | Emby interface for background jobs |
| **IServerEntryPoint** | Emby interface for server startup hooks |
| **.strm** | Text file containing URL; Emby plays it as video |
| **Debrid** | Service providing CDN access to torrented content |

---

**Last Updated:** 2026-03-29
**Maintainer:** EmbyStreams Contributors
