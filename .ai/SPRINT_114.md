# Sprint 114 — Your Files Detection (v3.3)

**Version:** v3.3 | **Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 113

---

## Overview

Sprint 114 implements "Your Files" detection and conflict resolution. When users add their own media files to library, plugin matches them against media_item_ids and supersedes matching items.

**Key Components:**
- YourFilesScanner - Scans library for user-added files
- YourFilesMatcher - Multi-provider ID matching
- YourFilesConflictResolver - Handles conflict resolution
- YourFilesTask - Scheduled task for periodic scanning

---

## Phase 114A — YourFilesScanner

### FIX-114A-01: Create YourFilesScanner

**File:** `Services/YourFilesScanner.cs`

```csharp
public class YourFilesScanner
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    public async Task<List<BaseItem>> ScanAsync(CancellationToken ct = default)
    {
        _logger.Info("Scanning library for 'Your Files'...");

        // Query all items that are NOT EmbyStreams-managed
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemType.Movie, BaseItemType.Episode },
            // Filter out items with EmbyStreams metadata
            DtoOptions = new Controller.Dto.DtoOptions(false)
        };

        var allItems = _libraryManager.GetItems(query).ToList();

        // Filter: exclude items we created (have .strm files)
        var yourFilesItems = allItems
            .Where(item => !IsEmbyStreamsItem(item))
            .ToList();

        _logger.Info("Found {Count} 'Your Files' items", yourFilesItems.Count);

        return yourFilesItems;
    }

    private bool IsEmbyStreamsItem(BaseItem item)
    {
        // Check if item has .strm extension
        if (item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if item has EmbyStreams provider ID
        if (item.ProviderIds.ContainsKey("embystreams"))
        {
            return true;
        }

        return false;
    }
}
```

**Acceptance Criteria:**
- [ ] Scans all library items
- [ ] Filters out EmbyStreams items (.strm files)
- [ ] Filters out items with embystreams provider ID
- [ ] Returns user-added files
- [ ] Uses Emby ILogger

---

## Phase 114B — YourFilesMatcher

### FIX-114B-01: Create YourFilesMatcher

**File:** `Services/YourFilesMatcher.cs`

```csharp
public class YourFilesMatcher
{
    private readonly IDatabaseManager _db;
    private readonly ILogger _logger;

    public async Task<List<YourFilesMatch>> MatchAsync(
        List<BaseItem> yourFilesItems,
        CancellationToken ct = default)
    {
        var matches = new List<YourFilesMatch>();

        foreach (var item in yourFilesItems)
        {
            var matchedItem = await FindMatchingMediaItemAsync(item, ct);
            if (matchedItem != null)
            {
                matches.Add(new YourFilesMatch
                {
                    YourFilesItem = item,
                    MediaItem = matchedItem,
                    MatchType = DetermineMatchType(item)
                });
            }
        }

        _logger.Info("Matched {Count} 'Your Files' items", matches.Count);

        return matches;
    }

    private async Task<MediaItem?> FindMatchingMediaItemAsync(BaseItem item, CancellationToken ct)
    {
        // Multi-provider ID matching via media_item_ids table
        var providerIds = item.ProviderIds;

        // Try IMDB first (most reliable)
        if (providerIds.TryGetValue("imdb", out var imdbId))
        {
            var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                MediaIdType.Imdb, imdbId, ct);
            if (mediaItem != null) return mediaItem;
        }

        // Try TMDB
        if (providerIds.TryGetValue("tmdb", out var tmdbId))
        {
            var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                MediaIdType.Tmdb, tmdbId, ct);
            if (mediaItem != null) return mediaItem;
        }

        // Try Tvdb
        if (providerIds.TryGetValue("tvdb", out var tvdbId))
        {
            var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                MediaIdType.Tvdb, tvdbId, ct);
            if (mediaItem != null) return mediaItem;
        }

        // Try AniList
        if (providerIds.TryGetValue("anilist", out var anilistId))
        {
            var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                MediaIdType.AniList, anilistId, ct);
            if (mediaItem != null) return mediaItem;
        }

        // Try AniDB
        if (providerIds.TryGetValue("anidb", out var anidbId))
        {
            var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                MediaIdType.AniDB, anidbId, ct);
            if (mediaItem != null) return mediaItem;
        }

        // Try Kitsu
        if (providerIds.TryGetValue("kitsu", out var kitsuId))
        {
            var mediaItem = await _db.FindMediaItemByProviderIdAsync(
                MediaIdType.Kitsu, kitsuId, ct);
            if (mediaItem != null) return mediaItem;
        }

        return null;
    }

    private YourFilesMatchType DetermineMatchType(BaseItem item)
    {
        // Prefer higher-quality provider IDs
        if (item.ProviderIds.ContainsKey("imdb"))
            return YourFilesMatchType.Imdb;

        if (item.ProviderIds.ContainsKey("tmdb"))
            return YourFilesMatchType.Tmdb;

        if (item.ProviderIds.ContainsKey("tvdb"))
            return YourFilesMatchType.Tvdb;

        if (item.ProviderIds.ContainsKey("anilist"))
            return YourFilesMatchType.AniList;

        if (item.ProviderIds.ContainsKey("anidb"))
            return YourFilesMatchType.AniDB;

        if (item.ProviderIds.ContainsKey("kitsu"))
            return YourFilesMatchType.Kitsu;

        return YourFilesMatchType.Other;
    }
}

public enum YourFilesMatchType
{
    Imdb,
    Tmdb,
    Tvdb,
    AniList,
    AniDB,
    Kitsu,
    Other
}

public record YourFilesMatch(
    BaseItem YourFilesItem,
    MediaItem MediaItem,
    YourFilesMatchType MatchType
);
```

**Acceptance Criteria:**
- [ ] Matches by IMDB first
- [ ] Falls back to TMDB, Tvdb, AniList, AniDB, Kitsu
- [ ] Determines match type for logging
- [ ] Returns all matches
- [ ] Uses Emby ILogger

---

## Phase 114C — YourFilesConflictResolver

### FIX-114C-01: Create YourFilesConflictResolver

**File:** `Services/YourFilesConflictResolver.cs`

```csharp
public class YourFilesConflictResolver
{
    private readonly IDatabaseManager _db;
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;

    public async Task<ConflictResolution> ResolveAsync(
        YourFilesMatch match,
        CancellationToken ct = default)
    {
        var mediaItem = match.MediaItem;

        // Check current status: Blocked items are never superseded
        if (mediaItem.Blocked)
        {
            _logger.Info("Item {ItemId} is Blocked, ignoring Your Files match", mediaItem.Id);
            return ConflictResolution.KeepBlocked;
        }

        // Check coalition rule: does item have enabled source?
        var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(mediaItem.Id, ct);

        if (hasEnabledSource)
        {
            // Coalition rule: keep available but supersede stream
            // Set superseded=true to indicate user's local file takes precedence
            await _db.SetSupersededAsync(mediaItem.Id, true, ct);

            // CRITICAL: If item is also Saved, mark as superseded_conflict
            // This signals admin review needed (user saved vs your files match)
            if (mediaItem.Saved)
            {
                await _db.SetSupersededConflictAsync(mediaItem.Id, true, ct);
                _logger.Warn("Item {ItemId} is Saved AND has enabled source, superseded_conflict=true", mediaItem.Id);
                return ConflictResolution.SupersededConflict;
            }

            await _db.SetSupersededAtAsync(mediaItem.Id, DateTimeOffset.UtcNow, ct);
            _logger.Info("Item {ItemId} has enabled source, superseded=true (Your Files match)", mediaItem.Id);
            return ConflictResolution.SupersededWithEnabledSource;
        }

        // No enabled source: supersede and remove .strm file
        await _db.SetSupersededAsync(mediaItem.Id, true, ct);
        await _db.SetSupersededAtAsync(mediaItem.Id, DateTimeOffset.UtcNow, ct);
        await DeleteStrmFileAsync(mediaItem, ct);

        _logger.Info("Item {ItemId} matched Your Files, superseded=true and deleted .strm (no enabled source)", mediaItem.Id);

        return ConflictResolution.SupersededWithoutEnabledSource;
    }

    private async Task DeleteStrmFileAsync(MediaItem item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.StrmPath) || !File.Exists(item.StrmPath))
        {
            return;
        }

        try
        {
            File.Delete(item.StrmPath);
            _logger.Debug("Deleted .strm file for superseded item: {Path}", item.StrmPath);

            // Remove from Emby library
            if (!string.IsNullOrEmpty(item.EmbyItemId))
            {
                var embyItemId = Guid.Parse(item.EmbyItemId);
                var baseItem = _libraryManager.GetItemById(embyItemId);
                if (baseItem != null)
                {
                    await _libraryManager.DeleteItemAsync(baseItem, ct);
                    _logger.Debug("Removed superseded item from Emby library: {EmbyItemId}", embyItemId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete .strm file for superseded item {ItemId}", item.Id);
        }
    }
}

public enum ConflictResolution
{
    KeepBlocked,
    SupersededWithEnabledSource,
    SupersededWithoutEnabledSource,
    SupersededConflict
}
```

**Acceptance Criteria:**
- [ ] Keeps Blocked items as Blocked
- [ ] Keeps Saved items with enabled source as SupersededConflict
- [ ] Keeps Active items with enabled source as SupersededWithEnabledSource
- [ ] Supersedes items without enabled source (SupersededWithoutEnabledSource)
- [ ] Sets superseded=true flag (NOT saved=true)
- [ ] Sets superseded_conflict=true when Saved + enabled source
- [ ] Logs all resolution decisions
- [ ] All IDs are string TEXT UUIDs
- [ ] Uses Emby ILogger

---

## Phase 114D — YourFilesTask

### FIX-114D-01: Create YourFilesTask

**File:** `Tasks/YourFilesTask.cs`

```csharp
public class YourFilesTask : IScheduledTask
{
    private readonly YourFilesScanner _scanner;
    private readonly YourFilesMatcher _matcher;
    private readonly YourFilesConflictResolver _resolver;
    private readonly ILogger _logger;

    public string Name => "EmbyStreams Your Files Reconciler";
    public string Key => "embystreams_yourfiles";
    public string Description => "Reconciles 'Your Files' with EmbyStreams items";
    public string Category => "EmbyStreams";

    public async Task ExecuteAsync(CancellationToken ct, IProgress<double> progress)
    {
        await Plugin.SyncLock.WaitAsync(ct);
        try
        {
            progress?.Report(0);

            _logger.Info("Starting Your Files reconciliation...");

            // Step 1: Scan library
            var yourFilesItems = await _scanner.ScanAsync(ct);
            progress?.Report(25);

            // Step 2: Match against media_item_ids
            var matches = await _matcher.MatchAsync(yourFilesItems, ct);
            progress?.Report(50);

            // Step 3: Resolve conflicts
            var resolutions = new List<ConflictResolution>();
            foreach (var match in matches)
            {
                var resolution = await _resolver.ResolveAsync(match, ct);
                resolutions.Add(resolution);
            }
            progress?.Report(75);

            // Step 4: Report results
            var summary = new YourFilesSummary
            {
                TotalScanned = yourFilesItems.Count,
                TotalMatches = matches.Count,
                KeptBlocked = resolutions.Count(r => r == ConflictResolution.KeepBlocked),
                SupersededWithEnabledSource = resolutions.Count(r => r == ConflictResolution.SupersededWithEnabledSource),
                SupersededWithoutEnabledSource = resolutions.Count(r => r == ConflictResolution.SupersededWithoutEnabledSource),
                SupersededConflict = resolutions.Count(r => r == ConflictResolution.SupersededConflict)
            };
            progress?.Report(100);

            _logger.Info("Your Files reconciliation complete: {Summary}", summary);
        }
        finally
        {
            Plugin.SyncLock.Release();
        }
    }
}

public record YourFilesSummary(
    int TotalScanned,
    int TotalMatches,
    int KeptBlocked,
    int SupersededWithEnabledSource,
    int SupersededWithoutEnabledSource,
    int SupersededConflict
);
```

**Acceptance Criteria:**
- [ ] Scans library
- [ ] Matches items
- [ ] Resolves conflicts correctly
- [ ] Reports summary with correct resolution counts
- [ ] Uses SyncLock
- [ ] Reports progress
- [ ] Uses Emby ILogger

---

## Sprint 114 Dependencies

- **Previous Sprint:** 113 (Saved/Blocked User Actions)
- **Blocked By:** Sprint 113
- **Blocks:** Sprint 115 (Removal Pipeline)

---

## Sprint 114 Completion Criteria

- [ ] YourFilesScanner scans library
- [ ] YourFilesMatcher matches by multi-provider IDs
- [ ] YourFilesConflictResolver resolves conflicts correctly
- [ ] YourFilesConflictResolver sets superseded=true (NOT saved=true)
- [ ] YourFilesConflictResolver sets superseded_conflict=true for Saved + enabled source
- [ ] YourFilesTask orchestrates full reconciliation
- [ ] All IDs are string TEXT UUIDs
- [ ] Build succeeds
- [ ] E2E: Your Files matched and superseded correctly

---

## Sprint 114 Notes

**Matching Priority:**
1. IMDB (most reliable)
2. TMDB
3. Tvdb
4. AniList
5. AniDB
6. Kitsu
7. Other (unknown provider)

**Your Files Conflict Resolution (v3.3 Spec §11):**

Per v3.3 spec §11, Your Files match behavior:
- Blocked → Keep Blocked (user override, never superseded)
- Saved + enabled source → SupersededConflict (needs admin review, saved=true preserved)
- Active + enabled source → SupersededWithEnabledSource (superseded=true, user's file takes precedence)
- No enabled source → SupersededWithoutEnabledSource (superseded=true, delete .strm, remove from Emby)

**CRITICAL: Your Files does NOT save items.**

Your Files match sets `superseded=true` flag, NOT `saved=true`. This is the correct behavior per spec §11.

- Non-saved superseded items have their .strm files deleted and are removed from Emby
- Saved superseded items remain in library but are flagged as `superseded_conflict=true` for admin review
- This preserves the user's explicit Save intent while signaling the Your Files conflict

**Superseded Conflict (Saved + Enabled Source + Your Files Match):**

When all three conditions are true:
1. User explicitly saved the item (`saved=true`)
2. Item has enabled source (from AIOStreams manifest)
3. User added their own file (Your Files match)

The plugin sets `superseded_conflict=true` to signal admin review needed. The item remains Saved per user's explicit action, but the superseded flag indicates user's local file is available.

**Task Scheduling:**
- Run every 6 hours
- Uses SyncLock to avoid conflicts with sync
- Progress reporting for UI

**Database Methods Required:**
- `SetSupersededAsync(string itemId, bool superseded, CancellationToken ct)`
- `SetSupersededConflictAsync(string itemId, bool supersededConflict, CancellationToken ct)`
- `SetSupersededAtAsync(string itemId, DateTimeOffset timestamp, CancellationToken ct)`
- `FindMediaItemByProviderIdAsync(MediaIdType type, string value, CancellationToken ct)` - queries media_item_ids table

**Media ID Types:**
- All item IDs are string TEXT UUIDs (from media_items.id)
- Provider ID matching uses media_item_ids table
- MediaIdType enum: Tmdb, Imdb, Tvdb, AniList, AniDB, Kitsu
