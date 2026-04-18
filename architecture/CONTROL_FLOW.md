# InfiniteDrive — Control Flow

> Last reconciled: 2026-04-18 (post Language & Localization sprint)

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

## 4. Playback Resolution Flow

```
Emby player requests .strm file
  │
  ├── .strm contains one of:
  │   ├── /InfiniteDrive/resolve?token=<hmac_token>    ← Movies
  │   └── /InfiniteDrive/Stream?id=<id>&sig=<sig>      ← Series
  │
  ├── ResolverService (movies):
  │   ├── Parse HMAC token → extract IMDB ID + quality
  │   ├── TryGetCachedUrlAsync()
  │   │   ├── Query stream_candidates by IMDB ID
  │   │   ├── PreferLanguageMatch() — read user's PreferredMetadataLanguage
  │   │   │   via IAuthorizationContext → prefer candidates whose Languages match
  │   │   └── If cache hit → 302 redirect
  │   ├── Cache miss → ResolveWithFallbackAsync()
  │   │   → Try primary provider → secondary (circuit breaker)
  │   │   → Returns ResolutionResult
  │   └── Return 302 redirect to stream URL
  │
  ├── AioMediaSourceProvider (version picker / long-press):
  │   ├── GetMediaSources(item) → DB cache or live AIOStreams resolve
  │   ├── MapStreamToSource() → MediaSourceInfo with populated MediaStreams:
  │   │   ├── Audio streams from ParsedFile.Languages + Channels + AudioTags
  │   │   └── Subtitle streams from Subtitles[] (IsExternal, DeliveryUrl)
  │   ├── MapCandidateToSource() → audio MediaStreams from Languages field
  │   ├── SortByLanguagePreference() → boost sources matching MetadataLanguage
  │   └── Emby displays audio language names and subtitle tracks in player
  │
  └── StreamEndpointService (series):
      ├── Validate stream ID + signature
      ├── Look up cached ResolutionEntry
      ├── If expired: re-resolve via StreamResolutionHelper
      └── Return 302 redirect to stream URL
```

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
