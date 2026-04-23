# InfiniteDrive — Control Flow

> Last reconciled: 2026-04-23 (Sprint 410: RequiresOpening Pipeline)

## 1. Catalog Sync Pipeline (CatalogSyncTask)

```
CatalogSyncTask.Execute()
  │
  ├── Plugin.SyncLock.WaitAsync()          ← Global serialization
  │
  ├── Phase: BuildProviders
  │   └── BuildProviders(config)
  │       → Creates ICatalogProvider[] based on config:
  │         AioStreamsCatalogProvider, CinemetaDefaultProvider,
  │         RssFeedProvider, UserCatalogProvider
  │
  ├── Phase: Fetch
  │   └── FetchFromAllProvidersAsync()
  │       → Parallel fetch from all providers
  │       → Each returns CatalogFetchResult (Items + ProviderReachable)
  │       → Per-catalog SyncState updated (last sync, etag, cursor)
  │
  ├── ManifestFilter.FilterEntriesAsync()
  │   → Remove blocked items
  │   → Apply "Your Files" filter
  │   → Digital release gate (DigitalReleaseGateService)
  │
  ├── ManifestDiff.DiffAsync()
  │   → Compare fetched items against DB
  │   → Output: new items, removed items, unchanged items
  │
  ├── Process new items → ItemPipelineService.ProcessItemAsync()
  │   → Lifecycle: Known → Resolved → Hydrated → Created → Indexed → Active
  │
  ├── User catalog sync (UserCatalogSyncService)
  │
  └── finally:
      ├── Persist last_sync_time
      ├── Plugin.SyncLock.Release()
      └── Plugin.Pipeline.Clear()
```

**Pipeline phases tracked:** `Plugin.Pipeline.SetPhase("CatalogSync", "BuildProviders")`, `Plugin.Pipeline.SetPhase("CatalogSync", "Fetch")`.

## 2. Refresh Pipeline (RefreshTask)

RefreshTask is the main content pipeline. It processes queued items through six steps:

```
RefreshTask.Execute()
  │
  ├── Plugin.SyncLock.WaitAsync()
  ├── InsertRunLogAsync("RefreshTask", "start")
  │
  ├── Step 1: COLLECT
  │   ├── Plugin.Pipeline.SetPhase("Refresh", "Collect")
  │   ├── CollectStepAsync()
  │   │   → Queries catalog_items with ItemState = Queued
  │   │   → Returns List<CatalogItem>
  │   └── If empty: skip remaining steps
  │
  ├── Step 2: WRITE
  │   ├── Plugin.Pipeline.SetPhase("Refresh", "Write")
  │   ├── WriteStepAsync(collected)
  │   │   → StrmWriterService.WriteAsync() for each item
  │   │   → NamingPolicyService.BuildFolderName() for folder names
  │   │   → Creates .strm file with signed resolve URL
  │   └── Returns count of written items
  │
  ├── Step 3: HINT
  │   ├── Plugin.Pipeline.SetPhase("Refresh", "Hint")
  │   ├── HintStepAsync(writtenItems)
  │   │   → NfoWriterService.WriteSeedNfo() for each item
  │   │   → Writes minimal NFO with IDs + title for Emby matching
  │   └── Returns count of hinted items
  │
  ├── Step 4: ENRICH
  │   ├── Plugin.Pipeline.SetPhase("Refresh", "Enrich")
  │   ├── EnrichStepAsync(runStartedAt)
  │   │   → MetadataEnrichmentService.EnrichBatchAsync()
  │   │   → Fetches full metadata from AIOStreams/Cinemeta
  │   │   → NfoWriterService.WriteEnrichedNfo() on success
  │   │   → Retry: 4h → 24h → block at 3 retries
  │   └── Returns count of enriched items
  │
  ├── Step 5: NOTIFY
  │   ├── Plugin.Pipeline.SetPhase("Refresh", "Notify")
  │   ├── NotifyStepAsync()
  │   │   → Notifies Emby of new items (42-item batch bound)
  │   │   → Triggers library scan
  │   └── Returns count of notified items
  │
  ├── Step 6: VERIFY
  │   ├── Plugin.Pipeline.SetPhase("Refresh", "Verify")
  │   ├── VerifyStepAsync()
  │   │   → Verifies stream availability (42-item batch bound)
  │   │   ├── Token renewal for stream URLs
  │   │   └── StreamProbeService.ProbeUrlAsync() for verification
  │   └── Returns count of verified items
  │
  ├── UpdateRunLogAsync("complete")
  │
  └── finally:
      └── Plugin.Pipeline.Clear()
```

**Steps with items:** Write, Hint, Enrich are conditional on having collected items. Collect, Notify, Verify always run.

## 3. Marvin Pipeline (MarvinTask)

MarvinTask is the background maintenance orchestrator. It delegates all work to services.

```
MarvinTask.Execute()
  │
  ├── TryRestorePrimaryAsync()             ← Sprint 311: auto-heal failover
  │   └── If ActiveProviderState == Secondary
  │       → Probe primary provider
  │       → If healthy: restore to Primary
  │
  ├── Phase 1: VALIDATION
  │   ├── Plugin.Pipeline.SetPhase("Marvin", "Validation")
  │   └── ValidationPassAsync()
  │       → Validates existing .strm files
  │       → Checks stream URLs still resolve
  │       → Removes orphaned files
  │
  ├── Phase 2: ENRICHMENT TRICKLE
  │   ├── Plugin.Pipeline.SetPhase("Marvin", "Enrichment")
  │   └── EnrichmentTrickleAsync()
  │       → Queries items needing enrichment (retry due)
  │       → Maps to EnrichmentRequest DTOs
  │       → MetadataEnrichmentService.EnrichBatchAsync()
  │       │   → Retry schedule: 4h → 24h → block at 3
  │       │   → 2s delay between API calls
  │       │   → 429 breaks immediately
  │       └── On success: NfoWriterService.WriteEnrichedNfo()
  │
  ├── Phase 3: TOKEN RENEWAL
  │   ├── Plugin.Pipeline.SetPhase("Marvin", "TokenRenewal")
  │   └── TokenRenewalAsync()
  │       → Refreshes expired stream tokens in resolution cache
  │
  ├── Phase 4: SAVE MAINTENANCE
  │   ├── Plugin.Pipeline.SetPhase("Marvin", "SaveMaintenance")
  │   └── SaveMaintenancePassAsync()
  │       → Cleans up expired user saves
  │       → Reconciles saved items with current catalog
  │
  ├── Persist last_marvin_run_time
  ├── Persist enrichment counts
  │
  └── finally:
      └── Plugin.Pipeline.Clear()
```

## 4. Playback Resolution Flow (Sprint 410: RequiresOpening Pipeline)

**Security Architecture:** All playback is gated behind Emby's auth layer via `RequiresOpening = true`. CDN URLs never appear in .strm files or MediaSourceInfo.Path during picker display.

```
User clicks Play
  │
  ├── Emby calls IMediaSourceProvider.GetMediaSources(item)
  │   │
  │   ├── AioMediaSourceProvider.GetMediaSources(item)
  │   │   │
  │   │   ├── Identify item (IMDB ID, mediaType, season/episode)
  │   │   │
  │   │   ├── In-memory cache check (60-minute TTL)
  │   │   │
  │   │   ├── DB cache check (stream_candidates table)
  │   │   │
  │   │   └── Live resolve (if cache miss)
  │   │       └── ResolveFromAioStreams()
  │   │           ├── Try primary → secondary (circuit breaker)
  │   │           ├── Returns List<AioStreamsStream>
  │   │           └── BingePrefetchService.PrefetchNextEpisodeAsync()
  │   │
  │   └── MapStreamToSource() / MapCandidateToSource()
  │       │
  │       ├── Set RequiresOpening = true
  │       ├── Set Path = "" (CDN URL NOT exposed)
  │       ├── Set OpenToken = cdnUrl (secure token)
  │       ├── Build MediaStreams:
  │       │   ├── Audio: ParsedFile.Languages + Channels + AudioTags
  │       │   └── Subtitles: Subtitles[] (IsExternal, DeliveryUrl)
  │       └── SortByLanguagePreference()
  │
  ├── Emby displays version picker with quality options
  │
  ├── User selects version (or Emby auto-selects)
  │
  ├── Emby calls IMediaSourceProvider.OpenMediaSource(openToken, currentLiveStreams, ct)
  │   │
  │   └── AioMediaSourceProvider.OpenMediaSource()
  │       │
  │       ├── Validate openToken is HTTP/HTTPS URL
  │       │
  │       ├── Create MediaSourceInfo:
  │       │   ├── Path = openToken (CDN URL materialized here)
  │       │   ├── Protocol = Http
  │       │   ├── RequiresOpening = false (already opened)
  │       │   └── SupportsDirectStream = true
  │       │
  │       └── Return InfiniteDriveLiveStream(resolvedSource)
  │
  └── Emby plays from InfiniteDriveLiveStream.MediaSource.Path
```

**Key Security Properties:**
- `.strm` files contain placeholder URLs (content ignored)
- CDN URLs only materialize server-side in `OpenMediaSource()`
- `OpenMediaSource()` is behind Emby's auth layer
- Rollback available via `PluginConfiguration.UseRequiresOpening = false`

**Deprecated (pre-Sprint 410):**
- `ResolverService` — `/InfiniteDrive/resolve?token=` endpoint (unauthenticated)
- `StreamEndpointService` — `/InfiniteDrive/Stream?id=&sig=` endpoint (HMAC signed)
- `PlaybackTokenService.GenerateResolveToken()` / `ValidateStreamToken()`
- `PluginConfiguration.DefaultSlotKey`, `SignatureValidityDays`

**Binge Watching:** `BingePrefetchService` pre-loads next episode candidates. When Emby auto-plays next episode:
1. `GetMediaSources()` → DB hit → instant decorated sources
2. Single source → Emby auto-plays, calls `OpenMediaSource()` → instant return
3. Multiple → user sees picker (consistent behavior)

## 5. Metadata Enrichment Control Flow

```
MetadataEnrichmentService.EnrichBatchAsync()
  │
  ├── For each EnrichmentRequest:
  │   │
  │   ├── RETRY GATE
  │   │   ├── If RetryCount >= 1 and NextRetryAt > UtcNow → SKIP
  │   │   └── If RetryCount >= 3 → BLOCK (set NextRetryAt to NeverRetryUnixSeconds)
  │   │
  │   ├── RATE LIMIT
  │   │   └── 2-second delay between API calls
  │   │
  │   ├── FETCH (via injected delegate)
  │   │   └── fetchFunc(request, ct) → EnrichedMetadata? or null
  │   │
  │   ├── ON SUCCESS:
  │   │   ├── NfoWriterService.WriteEnrichedNfo()
  │   │   ├── Update DB: set enriched metadata
  │   │   └── Reset retry counters
  │   │
  │   ├── ON 429 (rate limited):
  │   │   └── Break loop immediately
  │   │
  │   └── ON FAILURE:
  │       ├── Increment RetryCount
  │       ├── Set NextRetryAt:
  │       │   ├── Retry 1 → now + 4 hours
  │       │   └── Retry 2 → now + 24 hours
  │       └── Persist to DB
  │
  └── Return EnrichmentResult(EnrichedCount, BlockedCount, SkippedCount)
```

**Callers:** MarvinTask.EnrichmentTrickleAsync() and RefreshTask.EnrichStepAsync() both delegate to this service.
