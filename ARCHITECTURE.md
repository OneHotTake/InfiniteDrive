# InfiniteDrive Architecture (Post-Sprint 500)

## Core Principles
- Emby + AIOStreams bridge using .strm files + in-plugin resolver.
- State is **eventually consistent** (optimistic catalog/list ingestion → pessimistic stream resolution).
- **Single scheduled Emby task only**: `MarvinTask.cs`.
- Series/episode expansion is immediate and unified (see MARVIN_STATE_MACHINE.md).

## Key Components
- **MarvinTask.cs** — Central orchestrator (runs every 10 minutes by default).
- **StrmWriterService.cs** — Writes .strm files with full episode expansion.
- **StreamResolver.cs** — In-plugin playback resolver (proxies AIOStreams).
- **PluginConfiguration.cs** — All settings (pruned in Sprint 500).
- SQLite database — Persistent cache (full stream URLs, not just hints).

## Data Flow (High Level)
1. Catalog/List discovery → Marvin
2. New item found → immediate call to AIOStreams → StrmWriterService.WriteSeriesWithFullExpansionAsync() (or equivalent for movies)
3. Series rule: fetch full episode list from AIOStreams → write every episode .strm immediately (no seed files).
4. Every Marvin run re-checks existing series for new episodes (hard-coded 6-hour interval inside MarvinTask).
5. Playback → Emby calls plugin resolver → serves cached or live AIOStreams URL.

## State Machine
See MARVIN_STATE_MACHINE.md for full details.
