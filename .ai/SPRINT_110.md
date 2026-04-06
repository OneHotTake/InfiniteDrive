# Sprint 110 — Services Layer (v3.3 Core Services)

**Version:** v3.3 | **Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 109

---

## Overview

Sprint 110 implements core services that drive v3.3 architecture. These services orchestrate item lifecycle, stream resolution, metadata hydration, and user actions.

**Services Created:**
- ItemPipelineService - Manages item lifecycle transitions
- StreamResolver - Resolves playable streams from AIOStreams
- MetadataHydrator - Fetches and enriches item metadata
- YourFilesReconciler - Detects and matches user's local files
- SourcesService - Manages enabled/disabled sources
- CollectionsService - Manages Emby BoxSet collections
- SavedService - Handles user Save/Unsave/Block actions

---

## Phase 110A — ItemPipelineService

### FIX-110A-01: Create ItemPipelineService Base

**File:** `Services/ItemPipelineService.cs`

```csharp
public class ItemPipelineService
{
    private readonly ILogger _logger;
    private readonly IDatabaseManager _db;
    private readonly StreamResolver _streamResolver;
    private readonly MetadataHydrator _metadataHydrator;
    private readonly ILibraryManager _libraryManager;

    public async Task<ItemPipelineResult> ProcessItemAsync(
        MediaItem item,
        PipelineTrigger trigger,
        CancellationToken ct = default)
    {
        // Execute pipeline: Known → Resolved → Hydrated → Created → Indexed → Active
    }

    private async Task<ItemStatus> ResolvePhaseAsync(MediaItem item, CancellationToken ct) { }
    private async Task<ItemStatus> HydratePhaseAsync(MediaItem item, CancellationToken ct) { }
    private async Task<ItemStatus> CreatePhaseAsync(MediaItem item, CancellationToken ct) { }
    private async Task<ItemStatus> IndexPhaseAsync(MediaItem item, CancellationToken ct) { }
}
```

**Acceptance Criteria:**
- [ ] Pipeline phases execute in order
- [ ] Each phase updates ItemStatus
- [ ] Failed phases log to item_pipeline_log
- [ ] CancellationToken respected throughout

### FIX-110A-02: Implement Known → Resolved Transition

**File:** `Services/ItemPipelineService.cs`

```csharp
private async Task<ItemStatus> ResolvePhaseAsync(MediaItem item, CancellationToken ct)
{
    try
    {
        var streams = await _streamResolver.ResolveStreamsAsync(item.PrimaryId.ToString(), ct);
        if (streams == null || streams.Count == 0)
        {
            item.FailureReason = FailureReason.NoStreamsFound;
            return ItemStatus.Failed;
        }

        // Log successful resolution
        await _db.LogPipelineEventAsync(item.Id, "resolve", PipelineTrigger.Sync, true);
        return ItemStatus.Resolved;
    }
    catch (Exception ex)
    {
        item.FailureReason = FailureReason.MetadataFetchFailed;
        _logger.LogError(ex, "Resolve failed for {MediaId}", item.PrimaryId);
        await _db.LogPipelineEventAsync(item.Id, "resolve", PipelineTrigger.Sync, false, ex.Message);
        return ItemStatus.Failed;
    }
}
```

**Acceptance Criteria:**
- [ ] Calls StreamResolver
- [ ] Updates ItemStatus to Resolved on success
- [ ] Sets FailureReason on failure
- [ ] Logs pipeline events

### FIX-110A-03: Implement Coalition Rule — Single JOIN Query

**File:** `Services/ItemPipelineService.cs`

```csharp
public async Task<bool> ShouldKeepItemAsync(
    string primaryId,
    string primaryIdType,
    string mediaType,
    CancellationToken ct)
{
    // CRITICAL: This MUST be a single JOIN query, not N queries
    const string sql = @"
        SELECT 1 FROM source_memberships sm
        JOIN sources s ON sm.source_id = s.id
        WHERE sm.primary_id      = @PrimaryId
          AND sm.primary_id_type = @PrimaryIdType
          AND sm.media_type      = @MediaType
          AND s.enabled          = 1
        LIMIT 1";

    var result = await _db.ExecuteScalarAsync<int?>(
        sql, new { PrimaryId = primaryId, PrimaryIdType = primaryIdType, MediaType = mediaType }, ct);
    return result.HasValue;
}
```

**Acceptance Criteria:**
- [ ] Checks all enabled sources for item
- [ ] Returns true if any source claims item
- [ ] Returns false if item has no enabled sources
- [ ] **Uses single JOIN query, not loop over sources**

---

## Phase 110B — StreamResolver

### FIX-110B-01: Create StreamResolver

**File:** `Services/StreamResolver.cs`

```csharp
public class StreamResolver
{
    private readonly AioStreamsClient _client;
    private readonly ILogger _logger;

    public async Task<List<StreamCandidate>> ResolveStreamsAsync(
        string mediaId,
        CancellationToken ct = default)
    {
        var mediaIdParsed = MediaId.Parse(mediaId);

        // 1. Query AIOStreams for streams
        var resources = await _client.GetResourcesAsync(mediaIdParsed.Value, ct);

        // 2. Apply prefix filtering (AioStreamsPrefixDefaults)
        var filtered = ApplyPrefixFilter(resources);

        // 3. Rank streams by quality
        var ranked = RankStreams(filtered);

        return ranked;
    }

    private List<StreamCandidate> ApplyPrefixFilter(List<Resource> resources) { }
    private List<StreamCandidate> RankStreams(List<Resource> resources) { }
}
```

**Acceptance Criteria:**
- [ ] Queries AIOStreams API
- [ ] Filters by configured prefixes
- [ ] Ranks streams by quality/resolution
- [ ] Returns ordered list of candidates

### FIX-110B-02: Implement Stream Ranking

**File:** `Services/StreamResolver.cs`

```csharp
private List<StreamCandidate> RankStreams(List<Resource> resources)
{
    var candidates = resources.Select(r => new StreamCandidate
    {
        Url = r.url,
        Quality = ParseQuality(r.quality),
        SizeBytes = r.size ?? 0,
        Provider = r.provider ?? "unknown"
    }).ToList();

    // Sort by: 4K > 1080p > 720p > 480p, then by size (larger preferred)
    return candidates
        .OrderByDescending(c => c.Quality)
        .ThenByDescending(c => c.SizeBytes)
        .ToList();
}

private StreamQuality ParseQuality(string quality)
{
    // Parse "4K", "1080p", "720p", etc.
    return quality switch
    {
        "4K" => StreamQuality.UHD4K,
        "1080p" => StreamQuality.FHD,
        "720p" => StreamQuality.HD,
        _ => StreamQuality.SD
    };
}
```

**Acceptance Criteria:**
- [ ] Quality parsing robust
- [ ] Fallback to SD for unknown quality
- [ ] Stable sort for reproducibility

---

## Phase 110C — MetadataHydrator

### FIX-110C-01: Create MetadataHydrator

**File:** `Services/MetadataHydrator.cs`

```csharp
public class MetadataHydrator
{
    private readonly CinemetaProvider _cinemetaProvider;

    public async Task<MediaItem> HydrateAsync(MediaItem item, CancellationToken ct = default)
    {
        var mediaId = MediaId.Parse(item.PrimaryId.ToString());

        // Fetch metadata from Cinemeta
        var metadata = await _cinemetaProvider.GetMetadataAsync(mediaId, ct);

        if (metadata != null)
        {
            item.Title = metadata.Title;
            item.Year = metadata.Year;
            // Note: Images are NOT stored on MediaItem
            // They come from Emby's own metadata provider via .nfo injection
        }

        return item;
    }
}
```

**Acceptance Criteria:**
- [ ] Queries Cinemeta for metadata
- [ ] Updates title and year
- [ ] Does NOT store images (PosterUrl, BackdropUrl, Description)
- [ ] Handles missing metadata gracefully

---

## Phase 110D — YourFilesReconciler

### FIX-110D-01: Create YourFilesReconciler — Correct Resolution Outcome

**File:** `Services/YourFilesReconciler.cs`

```csharp
public class YourFilesReconciler
{
    private readonly ILogger<YourFilesReconciler> _logger;
    private readonly IDatabaseManager _db;
    private readonly ILibraryManager _libraryManager;

    public async Task<YourFilesResult> ReconcileAsync(CancellationToken ct = default)
    {
        // 1. Scan library for "Your Files" items
        var yourFilesItems = ScanYourFiles();

        // 2. Match against media_item_ids
        var matches = new List<YourFilesMatch>();
        foreach (var item in yourFilesItems)
        {
            var match = await FindMatchingMediaItemAsync(item, ct);
            if (match != null)
            {
                matches.Add(new YourFilesMatch { YourFilesItem = item, MediaItem = match });
            }
        }

        // 3. Apply Coalition rule: match → supersede, NOT save
        foreach (var match in matches)
        {
            var mediaItem = match.MediaItem;

            // Blocked items: Your Files match is irrelevant
            if (mediaItem.Blocked)
                continue;

            // Saved items: flag conflict for admin review
            if (mediaItem.Saved)
            {
                mediaItem.SupersededConflict = true;
                await _db.UpsertMediaItemAsync(mediaItem, ct);
                _logger.LogInformation("Your Files match: item {Id} saved, flagged superseded_conflict", mediaItem.Id);
                continue;
            }

            // Not saved: supersede it, delete files
            mediaItem.Superseded = true;
            mediaItem.SupersededAt = DateTimeOffset.UtcNow;
            await _db.UpsertMediaItemAsync(mediaItem, ct);

            // Delete .strm and .nfo files
            if (!string.IsNullOrEmpty(mediaItem.StrmPath) && File.Exists(mediaItem.StrmPath))
                File.Delete(mediaItem.StrmPath);
            if (!string.IsNullOrEmpty(mediaItem.NfoPath) && File.Exists(mediaItem.NfoPath))
                File.Delete(mediaItem.NfoPath);

            // Remove from Emby
            await RemoveFromEmbyAsync(mediaItem, ct);

            _logger.LogInformation("Your Files match: item {Id} superseded, files deleted", mediaItem.Id);
        }

        return new YourFilesResult { Matches = matches };
    }

    private async Task<MediaItem?> FindMatchingMediaItemAsync(BaseItem item, CancellationToken ct)
    {
        // Multi-provider ID matching
        var providerIds = item.ProviderIds;
        foreach (var (provider, id) in providerIds)
        {
            var mediaIdType = provider.ToLower() switch
            {
                "imdb" => MediaIdType.Imdb,
                "tmdb" => MediaIdType.Tmdb,
                "tvdb" => MediaIdType.Tvdb,
                "anilist" => MediaIdType.AniList,
                "anidb" => MediaIdType.AniDB,
                "kitsuid" => MediaIdType.Kitsu,
                _ => null
            };

            if (mediaIdType != null)
            {
                return await _db.FindMediaItemByProviderIdAsync(mediaIdType, id, ct);
            }
        }
        return null;
    }

    private async Task RemoveFromEmbyAsync(MediaItem item, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(item.EmbyItemId))
        {
            var baseItem = _libraryManager.GetItemById(item.EmbyItemId);
            if (baseItem != null)
            {
                await _libraryManager.DeleteItemAsync(baseItem, ct);
            }
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Scans library for Your Files items
- [ ] Matches by multi-provider IDs
- [ ] Non-saved matches set superseded=true, NOT saved=true
- [ ] Saved matches set superseded_conflict=true
- [ ] Blocked items ignored
- [ ] .strm and .nfo files deleted for superseded items
- [ ] Items removed from Emby
- [ ] Logs all decisions

---

## Phase 110E — SourcesService

### FIX-110E-01: Create SourcesService — String IDs

**File:** `Services/SourcesService.cs`

```csharp
public class SourcesService
{
    private readonly IDatabaseManager _db;

    public async Task<List<Source>> GetSourcesAsync(CancellationToken ct = default)
    {
        return await _db.GetAllSourcesAsync(ct);
    }

    public async Task EnableSourceAsync(string sourceId, CancellationToken ct = default)
    {
        await _db.SetSourceEnabledAsync(sourceId, true, ct);
    }

    public async Task DisableSourceAsync(string sourceId, CancellationToken ct = default)
    {
        await _db.SetSourceEnabledAsync(sourceId, false, ct);
    }

    public async Task ToggleShowAsCollectionAsync(string sourceId, bool show, CancellationToken ct = default)
    {
        await _db.SetSourceShowAsCollectionAsync(sourceId, show, ct);
    }
}
```

**Acceptance Criteria:**
- [ ] Retrieves all sources
- [ ] Enables/disables sources
- [ ] Toggles ShowAsCollection flag
- [ ] All operations atomic
- [ ] All IDs are strings (TEXT UUID), not int

---

## Phase 110F — CollectionsService — Use ICollectionManager Pattern

### FIX-110F-01: Create CollectionsService

**File:** `Services/CollectionsService.cs`

```csharp
public class CollectionsService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IDatabaseManager _db;
    private readonly ILogger<CollectionsService> _logger;

    public async Task SyncCollectionsAsync(CancellationToken ct = default)
    {
        // Get sources with ShowAsCollection = true
        var collectionSources = await _db.GetSourcesWithShowAsCollectionAsync(ct);

        foreach (var source in collectionSources)
        {
            await SyncSourceCollectionAsync(source, ct);
        }
    }

    private async Task SyncSourceCollectionAsync(Source source, CancellationToken ct)
    {
        // 1. Find or create BoxSet for source
        var boxSet = await FindOrCreateBoxSetAsync(source.Name, ct);

        // 2. Get all items in this source
        var items = await _db.GetItemsBySourceAsync(source.Id, ct);

        // 3. Add items to BoxSet using ICollectionManager
        foreach (var item in items)
        {
            if (item.EmbyItemId != null)
            {
                await _collectionManager.AddToCollection(boxSet.InternalId, new[] { long.Parse(item.EmbyItemId) });
                _logger.LogDebug("Added item {EmbyItemId} to BoxSet {BoxSetName}", item.EmbyItemId, source.Name);
            }
        }
    }

    private async Task<BoxSet> FindOrCreateBoxSetAsync(string name, CancellationToken ct)
    {
        // Query for existing BoxSet with this name
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { "BoxSet" },
            Name = name,
            Recursive = true
        };
        var existing = _libraryManager.GetItemList(query).FirstOrDefault();

        if (existing != null) return (BoxSet)existing;

        // Create new BoxSet using ICollectionManager
        await _collectionManager.CreateCollection(new CollectionCreationOptions {
            Name = name,
            IsLocked = false,  // MUST be false — IsLocked=true breaks Add/Remove
            ItemIdList = Array.Empty<long>()
        });

        _logger.LogInformation("Created BoxSet: {Name}", name);
        return (BoxSet)_libraryManager.GetItemList(query).FirstOrDefault();
    }
}
```

**Acceptance Criteria:**
- [ ] Syncs collections for ShowAsCollection sources
- [ ] Creates BoxSets if needed
- [ ] Adds items to BoxSets
- [ ] Uses ICollectionManager pattern (NOT _libraryManager.AddToCollectionAsync)
- [ ] IsLocked = false (never true)
- [ ] Build succeeds

---

## Phase 110G — SavedService — Correct Unsave Behavior

### FIX-110G-01: Create SavedService

**File:** `Services/SavedService.cs`

```csharp
public class SavedService
{
    private readonly IDatabaseManager _db;

    public async Task SaveItemAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null) return;

        item.Saved = true;
        item.SavedAt = DateTimeOffset.UtcNow;
        item.SavedBy = "user";
        item.SaveReason = SaveReason.Explicit;

        await _db.UpsertMediaItemAsync(item, ct);
        await _db.LogTransitionAsync(item.PrimaryId.Value, item.PrimaryId.Value,
            item.PrimaryId.Type.ToString().ToLower(), item.MediaType,
            null, "saved", "user_save", "user", null, ct);
    }

    public async Task UnsaveItemAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null) return;

        // CRITICAL: Unsave sets saved=false only.
        // The removal pipeline evaluates grace period on its next run.
        // UnsaveItemAsync NEVER sets status = Deleted directly.

        item.Saved = false;
        item.SaveReason = null;
        item.SavedAt = null;

        await _db.UpsertMediaItemAsync(item, ct);
        await _db.LogTransitionAsync(item.PrimaryId.Value, item.PrimaryId.Value,
            item.PrimaryId.Type.ToString().ToLower(), item.MediaType,
            "saved", "active", "user_remove", "user", null, ct);
    }

    public async Task BlockItemAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null) return;

        item.Blocked = true;
        item.BlockedAt = DateTimeOffset.UtcNow;

        await _db.UpsertMediaItemAsync(item, ct);
        await _db.LogTransitionAsync(item.PrimaryId.Value, item.PrimaryId.Value,
            item.PrimaryId.Type.ToString().ToLower(), item.MediaType,
            null, "blocked", "user_block", "user", null, ct);
    }

    public async Task UnblockItemAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _db.GetMediaItemAsync(itemId, ct);
        if (item == null) return;

        // CRITICAL: Unblock sets blocked=false only.
        // Re-enters pipeline on next sync if any source claims it.
        // UnblockItemAsync NEVER sets status = Deleted directly.

        item.Blocked = false;
        item.BlockedAt = null;

        await _db.UpsertMediaItemAsync(item, ct);
        await _db.LogTransitionAsync(item.PrimaryId.Value, item.PrimaryId.Value,
            item.PrimaryId.Type.ToString().ToLower(), item.MediaType,
            "blocked", null, "user_remove", "user", null, ct);
    }
}
```

**Acceptance Criteria:**
- [ ] Save marks saved=true
- [ ] Unsave sets saved=false ONLY — never sets Deleted
- [ ] Block marks blocked=true
- [ ] Unblock sets blocked=false ONLY — never sets Deleted
- [ ] Coalition rule respected (removal pipeline handles grace)
- [ ] All IDs are strings (TEXT UUID), not int

---

## Phase 110H — Digital Release Gate

### FIX-110H-01: Digital Release Gate Implementation

**File:** `Services/DigitalReleaseGateService.cs`

The Digital Release Gate (built-in sources only) must be implemented as a discrete verification step in the sync pipeline filter chain. Before any built-in source item proceeds past Known status, EmbyStreams verifies via TMDB API:
- status == "Released"
- release_type IN (4, 5) (4 = Digital, 5 = Physical)
Theatrical-only titles are dropped with `failure_reason = 'digital_release_gate'` and re-evaluated on next sync (auto-retried = true).

This check does NOT apply to user-added Trakt/MDblist sources. It applies only to: Trending Movies, Trending Series, New Movie Releases, New & Returning Series.

```csharp
public class DigitalReleaseGateService
{
    private readonly HttpClient _http;
    private readonly ILogger<DigitalReleaseGateService> _logger;
    private readonly IDatabaseManager _db;

    // Cache results for 24 hours to avoid redundant TMDB calls
    private readonly ConcurrentDictionary<string, (bool Result, DateTimeOffset CachedAt)> _releaseCache
        = new();

    public DigitalReleaseGateService(HttpClient http, ILogger<DigitalReleaseGateService> logger, IDatabaseManager db)
    {
        _http = http;
        _logger = logger;
        _db = db;
    }

    public async Task<bool> IsDigitallyReleasedAsync(
        MediaId id,
        string mediaType,
        string sourceType,
        CancellationToken ct = default)
    {
        var mediaIdParsed = MediaId.Parse(id.ToString());

        // Series bypass gate unconditionally
        if (mediaType == "series")
        {
            _logger.LogDebug("Digital Release Gate bypassed for series media type");
            return true;
        }

        // User-added sources bypass gate unconditionally
        if (sourceType != nameof(SourceType.BuiltIn))
        {
            _logger.LogDebug("Digital Release Gate bypassed for user-added source {SourceType}", sourceType);
            return true;
        }

        // Check cache
        var cacheKey = $"release_gate:{id}";
        if (_releaseCache.TryGetValue(cacheKey, out var cached) &&
            (DateTimeOffset.UtcNow - cached.CachedAt).TotalHours < 24)
        {
            _logger.LogDebug("Release gate cache hit for {MediaId}", id);
            return cached.Result;
        }

        // Query TMDB release date
        var releaseDate = await GetTmdbReleaseDateAsync(mediaIdParsed.Value, ct);
        var isReleased = releaseDate != null && releaseDate <= DateTimeOffset.UtcNow;

        // Cache result
        _releaseCache[cacheKey] = (isReleased, DateTimeOffset.UtcNow);

        return isReleased;
    }

    private async Task<DateTimeOffset?> GetTmdbReleaseDateAsync(string tmdbId, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.themoviedb.org/3/movie/{tmdbId}?api_key={_apiKey}&append_to_response=releases";
            var response = await _http.GetStringAsync(url, ct);
            var json = JsonDocument.Parse(response);

            var releaseDates = json.RootElement.GetProperty("release_dates");
            if (releaseDates.ValueKind == JsonValueKind.Null)
                return null;

            var usRelease = releaseDates.GetProperty("us");
            var digitalRelease = usRelease.TryGetProperty("digital", out var digitalProp)
                && DateTime.TryParse(digitalProp.GetString(), out var digitalDate)
                ? DateTimeOffset.Parse(digitalDate).ToUniversalTime()
                : null;

            var theatricalRelease = usRelease.TryGetProperty("theatrical", out var theatricalProp)
                && DateTime.TryParse(theatricalProp.GetString(), out var theatricalDate)
                ? DateTimeOffset.Parse(theatricalDate).ToUniversalTime()
                : null;

            // Prefer digital release, fallback to theatrical
            return digitalRelease ?? theatricalRelease;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query TMDB release date for {TmdbId}", tmdbId);
            return null;
        }
    }

    public void ClearExpiredCache()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var (key, _) in _releaseCache.Where(kvp => kvp.Value.CachedAt < cutoff))
        {
            _releaseCache.TryRemove(key, out _);
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Gate applies only to SourceType.BuiltIn
- [ ] Series bypass gate unconditionally
- [ ] TMDB queried for release date
- [ ] Results cached 24 hours per item
- [ ] release_type IN (4,5) check (Digital, Physical)
- [ ] Theatrical-only items dropped with digital_release_gate reason
- [ ] Auto-retried flag set for failed items
- [ ] Build succeeds

---

## Sprint 110 Dependencies

- **Previous Sprint:** 109 (Foundation & Schema)
- **Blocked By:** Sprint 109
- **Blocks:** Sprint 111 (Sync Pipeline)

---

## Sprint 110 Completion Criteria

- [ ] ItemPipelineService implements all phases
- [ ] StreamResolver queries and ranks streams
- [ ] MetadataHydrator fetches metadata from Cinemeta
- [ ] YourFilesReconciler matches and supersedes (NOT saves) items
- [ ] SourcesService manages enabled/disabled sources
- [ ] CollectionsService syncs Emby BoxSets using ICollectionManager
- [ ] SavedService handles Save/Unsave/Block actions correctly
- [ ] DigitalReleaseGate service implemented
- [ ] Build succeeds
- [ ] Unit tests for all services

---

## Sprint 110 Notes

**Coalition Rule Implementation:**
- Items stay in library if ANY enabled Source claims them
- Saved items stay even if no enabled Source (Explicit SaveReason)
- Blocked items stay blocked even if enabled Source claims them

**Service Dependencies:**
- ItemPipelineService → StreamResolver, MetadataHydrator
- YourFilesReconciler → DatabaseManager, LibraryManager
- CollectionsService → ILibraryManager, ICollectionManager
- DigitalReleaseGateService → HttpClient, DatabaseManager

**Error Handling:**
- All services use CancellationToken
- Pipeline failures logged to item_pipeline_log
- Stream resolution failures logged to stream_resolution_log

**Correct Your Files Resolution (v3.3 Spec §11):**
- Non-saved matches → superseded=true, files deleted
- Saved matches → superseded_conflict=true, files NOT deleted
- Blocked items → ignored (no change)
- This is correct behavior per spec §11
