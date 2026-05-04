# InfiniteDrive — Control Flow

> Last reconciled: 2026-05-04 (post-Sprint 516)

## 1. Playback Resolution Flow

**Security Architecture:** All playback is gated behind Emby's auth layer via `RequiresOpening = true`. CDN URLs never appear in .strm files or MediaSourceInfo.Path during picker display.

```
User clicks Play
  |
  +-- Emby calls IMediaSourceProvider.GetMediaSources(item)
  |     |
  |     +-- AioMediaSourceProvider.GetMediaSources(item)
  |           |
  |           +-- Identify item (aio_id / IMDB ID, mediaType, season/episode)
  |           |
  |           +-- In-memory cache check (60-minute TTL)
  |           |
  |           +-- DB cache check (stream_resolution_cache table)
  |           |
  |           +-- Live resolve (if cache miss)
  |           |     +-- ResolveFromAioStreams()
  |           |     |     +-- Skip HEAD on rank-0 (saves 0-5s happy path)
  |           |     |     +-- Try primary -> secondary (circuit breaker)
  |           |     |     +-- Returns List<AioStreamsStream>
  |           |     +-- BuildFallbackStreamsFromFilename()
  |           |     +-- Fire-and-forget ProbeAndCacheAsync()
  |           |     +-- BingePrefetchService.PrefetchNextEpisodeAsync()
  |           |
  |           +-- MapStreamToSource() / MapCandidateToSource()
  |                 |
  |                 +-- Set RequiresOpening = true
  |                 +-- Set Path = "" (CDN URL NOT exposed)
  |                 +-- Set OpenToken = cdnUrl (secure token)
  |                 +-- Build MediaStreams:
  |                 |     +-- Audio: ParsedFile.Languages + Channels + AudioTags
  |                 |     +-- Subtitles: Subtitles[] (IsExternal, DeliveryUrl)
  |                 +-- SortByLanguagePreference()
  |
  +-- Emby displays version picker with quality options
  |
  +-- User selects version (or Emby auto-selects)
  |
  +-- Emby calls IMediaSourceProvider.OpenMediaSource(openToken, currentLiveStreams, ct)
  |     |
  |     +-- AioMediaSourceProvider.OpenMediaSource()
  |           |
  |           +-- Validate openToken is HTTP/HTTPS URL
  |           |
  |           +-- Create MediaSourceInfo:
  |           |     +-- Path = openToken (CDN URL materialized here)
  |           |     +-- Protocol = Http
  |           |     +-- RequiresOpening = false (already opened)
  |           |     +-- SupportsDirectStream = true
  |           |
  |           +-- Return InfiniteDriveLiveStream(resolvedSource)
  |
  +-- Emby plays from InfiniteDriveLiveStream.MediaSource.Path
```

**Key Security Properties:**
- `.strm` files contain placeholder URLs (content ignored)
- CDN URLs only materialize server-side in `OpenMediaSource()`
- `OpenMediaSource()` is behind Emby's auth layer

**Stream identity:** `infoHash + fileIdx` survives CDN URL rotation. Used for deduplication and cache lookups.

**Pre-cache sources:** Use `Path=""` + `RequiresOpening` + `OpenToken` pattern.

**Binge Watching:** `BingePrefetchService` pre-loads next episode candidates. When Emby auto-plays next episode:
1. `GetMediaSources()` -> DB hit -> instant decorated sources
2. Single source -> Emby auto-plays, calls `OpenMediaSource()` -> instant return
3. Multiple -> user sees picker (consistent behavior)

## 2. Catalog Sync Pipeline (CatalogSyncTask)

```
CatalogSyncTask.Execute()
  |
  +-- Plugin.SyncLock.WaitAsync()          <- Global serialization
  |
  +-- Phase: BuildProviders
  |     +-- BuildProviders(config)
  |         -> Creates ICatalogProvider[] based on config
  |           (defined in CatalogProviders.cs)
  |
  +-- Phase: Fetch
  |     +-- FetchFromAllProvidersAsync()
  |         -> Parallel fetch from all providers
  |         -> Each returns CatalogFetchResult (Items + ProviderReachable)
  |         -> Per-catalog SyncState updated
  |
  +-- ManifestFilter.FilterEntriesAsync()
  |     -> Remove blocked items
  |     -> Apply "Your Files" filter
  |     -> Digital release gate
  |
  +-- ManifestDiff.DiffAsync()
  |     -> Compare fetched items against DB
  |     -> Output: new items, removed items, unchanged items
  |
  +-- Process new items -> ItemPipelineService.ProcessItemAsync()
  |     -> Lifecycle transitions
  |
  +-- User catalog sync
  |
  +-- finally:
      +-- Persist last_sync_time
      +-- Plugin.SyncLock.Release()
      +-- Plugin.Pipeline.Clear()
```

## 3. Refresh Pipeline (RefreshTask)

```
RefreshTask.Execute()
  |
  +-- Plugin.SyncLock.WaitAsync()
  |
  +-- Step 1: COLLECT
  |     +-- Queries catalog_items with ItemState = Queued
  |     +-- If empty: skip remaining steps
  |
  +-- Step 2: WRITE
  |     +-- StrmWriterService.WriteAsync() for each item
  |     +-- NamingPolicyService.BuildFolderName() for folder names
  |     +-- Creates .strm file with signed resolve URL
  |
  +-- Step 3: HINT
  |     +-- NfoWriterService.WriteSeedNfo() for each item
  |     +-- Writes minimal NFO with IDs + title
  |
  +-- Step 4: ENRICH
  |     +-- MetadataEnrichmentService.EnrichBatchAsync()
  |     +-- Fetches full metadata from AIOStreams/Cinemeta
  |     +-- NfoWriterService.WriteEnrichedNfo() on success
  |     +-- Retry: 4h -> 24h -> block at 3 retries
  |
  +-- Step 5: NOTIFY
  |     +-- Notifies Emby of new items (42-item batch bound)
  |     +-- Triggers library scan
  |
  +-- Step 6: VERIFY
  |     +-- Verifies stream URLs
  |     +-- Token renewal
  |
  +-- finally:
      +-- Plugin.Pipeline.Clear()
```

**Conditional steps:** Write/Hint/Enrich only run if Collect returned items. Notify/Verify always run.

## 4. Marvin Pipeline (MarvinTask)

```
MarvinTask.Execute()
  |
  +-- TryRestorePrimaryAsync()
  |     +-- If ActiveProviderState == Secondary
  |         -> Probe primary provider
  |         -> If healthy: restore to Primary
  |
  +-- Phase 1: VALIDATION
  |     +-- ValidationPassAsync()
  |         -> Validates existing .strm files
  |         -> Checks stream URLs still resolve
  |         -> Removes orphaned files
  |
  +-- Phase 2: ENRICHMENT TRICKLE
  |     +-- EnrichmentTrickleAsync()
  |         -> Queries items needing enrichment (retry due)
  |         -> Maps to EnrichmentRequest DTOs
  |         -> MetadataEnrichmentService.EnrichBatchAsync()
  |         |     -> Retry schedule: 4h -> 24h -> block at 3
  |         |     -> 2s delay between API calls
  |         |     -> 429 breaks immediately
  |         -> On success: NfoWriterService.WriteEnrichedNfo()
  |
  +-- Phase 3: TOKEN RENEWAL
  |     +-- TokenRenewalAsync()
  |         -> Refreshes expired stream tokens in stream_resolution_cache
  |
  +-- Phase 4: SAVE MAINTENANCE
  |     +-- SaveMaintenancePassAsync()
  |         -> Cleans up expired user saves
  |         -> Reconciles saved items with current catalog
  |
  +-- Persist last_marvin_run_time
  +-- Persist enrichment counts
  |
  +-- finally:
      +-- Plugin.Pipeline.Clear()
```

## 5. Stream Resolution Cache Flow

```
GetMediaSources(item)
  |
  +-- Check in-memory cache (60-min TTL)
  |     +-- Hit: return cached MediaSourceInfo list
  |
  +-- Check stream_resolution_cache (by aio_id, season, episode)
  |     +-- Hit: MapCandidateToSource() for each cached entry
  |     +-- Return decorated sources
  |
  +-- Cache miss: Live resolve
  |     +-- AioStreamsClient fetch from AIOStreams API
  |     +-- CandidateNormalizer parses metadata (three-tier)
  |     +-- Rank streams by quality
  |     +-- Skip HEAD on rank-0 (saves 0-5s)
  |     +-- BuildFallbackStreamsFromFilename()
  |     +-- Fire-and-forget: WriteToStreamCacheAsync()
  |     |     -> Stores to stream_resolution_cache
  |     |     -> Primary key: aio_id
  |     |     -> UNIQUE: (aio_id, COALESCE(season,-1), COALESCE(episode,-1), rank)
  |     +-- Return live sources
```

## 6. Metadata Enrichment Control Flow

```
MetadataEnrichmentService.EnrichBatchAsync()
  |
  +-- For each EnrichmentRequest:
  |     |
  |     +-- RETRY GATE
  |     |     +-- If RetryCount >= 1 and NextRetryAt > UtcNow -> SKIP
  |     |     +-- If RetryCount >= 3 -> BLOCK
  |     |
  |     +-- RATE LIMIT
  |     |     +-- 2-second delay between API calls
  |     |
  |     +-- FETCH (via injected delegate)
  |     |     +-- fetchFunc(request, ct) -> EnrichedMetadata? or null
  |     |
  |     +-- ON SUCCESS:
  |     |     +-- NfoWriterService.WriteEnrichedNfo()
  |     |     +-- Update DB: set enriched metadata
  |     |     +-- Reset retry counters
  |     |
  |     +-- ON 429 (rate limited):
  |     |     +-- Break loop immediately
  |     |
  |     +-- ON FAILURE:
  |           +-- Increment RetryCount
  |           +-- Set NextRetryAt (4h, 24h)
  |           +-- Persist to DB
  |
  +-- Return EnrichmentResult(EnrichedCount, BlockedCount, SkippedCount)
```

**Callers:** MarvinTask.EnrichmentTrickleAsync() and RefreshTask.EnrichStepAsync() both delegate to this service.
