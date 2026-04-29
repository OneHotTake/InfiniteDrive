# Marvin State Machine (Post-Sprint 500)

> **Sprint 502 backend plumbing:** PluginConfiguration overhauled. Marvin triggered after every settings save. Cache stores full stream URLs. CacheRefreshIntervalDays controls expiry (default 30 days).

## Core Rule
Marvin is the single orchestrator. There are no other scheduled tasks visible to Emby.

## Two-Phase Behavior (Every Run)
**Phase 1 – Lightweight (always runs)**
- Process all enabled catalogs from Primary + Backup manifests.
- Poll system-wide and user lists (Trakt/MDBList/AniList only).
- If ANY new item is detected → immediately call StrmWriterService to write .strm files.

**Phase 2 – Pessimistic / Batch (controlled by Stream Resolution Batch Size)**
- Resolve real streams from AIOStreams.
- Update pre-resolved cache (full URLs stored in SQLite).
- Prune items that have fallen out of catalogs/lists (respecting playlists and self-managed collections).

## Series / Episode Expansion Rule (Sprint 500)
When a series (Movies/Series/Anime) is discovered:
1. Immediately call AIOStreams for the full episode list.
2. Write EVERY episode .strm file in one operation (no seed files ever).
3. Every Marvin run re-checks existing series for new episodes (hard-coded 6-hour interval inside MarvinTask).

## Cache Behavior
- Full stream URLs are stored in SQLite.
- Cache Refresh Interval (default 30 days) controls when Marvin re-fetches URLs.
- Proxy mode on AIOStreams instance = effectively infinite cache life.

## Rate Limiting
- Marvin Actions Per Hour (default 360).
- AIOStreams probe budget enforced.

## Trigger Points
- New item on any list/catalog → immediate Marvin run.
- After ANY settings save on ANY tab → immediate Marvin run.
