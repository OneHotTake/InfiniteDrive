# Sprint 76 — Stremio-Kai Repository Analysis

**Date:** 2026-04-02
**Source:** `/home/onehottake/research/Stremio-Kai`

## Overview

Stremio-Kai is a **browser extension overlay** for the Stremio web app. It is NOT a server-side addon — it intercepts and enhances Stremio's web UI by injecting metadata modules, UI improvements, and cross-provider ID resolution directly into the browser.

## Architecture

- **Runtime:** Browser extension (injects into Stremio web client)
- **Module System:** Global namespace `window.MetadataModules` with service-locator pattern
- **Initialization:** Bootstrapper pattern — each module checks idempotency before init
- **Communication:** Custom DOM events (`kai-pref-changed`, `metadata-modules-ready`)
- **Storage:** localStorage for preferences, IndexedDB for metadata cache

## Key Directories

```
portable_config/webmods/
├── Metadata/
│   ├── anime-detection.js      — 3-tier anime classification
│   ├── id-conversion.js        — Haglund API + LRU cache
│   ├── id-lookup.js            — Cross-reference any ID type
│   ├── metadata-storage.js     — IndexedDB with multi-entry indexes
│   ├── metadata-service.js     — Jikan API + parallel fetching
│   ├── rate-limiter.js         — Queue-based per-API rate limiting
│   ├── fetch-utils.js          — Retry with exponential backoff
│   ├── details-enhancer.js     — Hybrid episode matching for anime
│   ├── route-detector.js       — ID format detection/parsing
│   ├── dom-processor.js        — ID extraction from DOM
│   ├── config.js               — API endpoints, timeouts, selectors
│   └── bootstrapper.js         — Module orchestration
├── Settings/
│   └── enhanced-metadata.js    — UI settings injection
└── UI/
    └── Hero Banner/            — UI enhancement modules
```

## Module Registration Pattern

```javascript
window.MetadataModules = window.MetadataModules || {};
window.MetadataModules.moduleName = {
    init: function() { /* idempotent */ }
};

// Service locator
function getModule(name) {
    return window.MetadataModules[name];
}
```

## Configuration Model

- **Static config objects** per module (`config.js` files)
- **User preferences** via `preferences.js` with localStorage persistence
- **API key management** via `apiKeys` module with real-time validation
- **No feature flags** — features enabled by API key availability
- **Update notification** via `update-notification.js`

## Key External APIs

| API | Purpose | Rate Limit |
|-----|---------|-----------|
| Haglund (`arm.haglund.dev`) | ID conversion between providers | 2/sec |
| Jikan (MyAnimeList) | Anime metadata enrichment | 3/sec |
| TMDB | Movie/series metadata | 5/sec |
| MDBList | Aggregated ratings/lists | Per-key |
| Cinemeta | Stremio catalog metadata | No limit |

## Extensibility

- New providers: Implement fetcher interface → register via `window.MetadataModules`
- New ID types: Add to `route-detector.js` parse patterns
- New metadata sources: Add fetcher + register with metadata-service
- Event-driven: Modules listen for `metadata-modules-ready` to coordinate init

## Relevance to EmbyStreams

Stremio-Kai's patterns are directly applicable but require adaptation:
- Browser extension patterns → server-side C# equivalents
- `window.MetadataModules` namespace → DI container registration
- localStorage/IndexedDB → SQLite with JSON columns
- DOM events → C# events / `IEventConsumer`
- `Promise.allSettled` → `Task.WhenAll`
