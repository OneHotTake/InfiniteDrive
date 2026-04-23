# Migration Plan: .strm Files → LocationType.Virtual

**Branch:** `virtual`
**Date:** 2026-04-23
**Status:** Draft (Awaiting Approval)

---

## Executive Summary

This migration eliminates all `.strm` file I/O operations and replaces them with direct `ILibraryManager.CreateItem()` calls using `LocationType.Virtual`. The plugin will inject items directly into Emby's database while maintaining the same user-facing behavior.

**Key Changes:**
- Remove `StrmWriterService` entirely
- New `VirtualItemService` for direct item creation
- Update all catalog sync and refresh flows
- Existing `AioMediaSourceProvider` (with RequiresOpening) remains unchanged
- User must create dummy libraries with empty folders (already done by LibraryProvisioningService)

**Backwards Compatibility:** Full migration path provided for existing users.

---

## 1. High-Level Architecture Changes

### Current Architecture (.strm approach)

```
CatalogSyncTask → ItemPipelineService → StrmWriterService.WriteAsync()
  → Creates .strm files on disk (SyncPathMovies/Shows/Anime)
  → ILibraryMonitor.ReportFileSystemChanged()
  → Emby scans and indexes .strm files
  → Playback: AioMediaSourceProvider resolves URLs via OpenMediaSource()
```

### New Architecture (LocationType.Virtual)

```
CatalogSyncTask → ItemPipelineService → VirtualItemService.CreateItemAsync()
  → ILibraryManager.CreateItem() with LocationType.Virtual
  → Items injected directly into Emby's database
  → Playback: AioMediaSourceProvider resolves URLs via OpenMediaSource() (unchanged)
```

### What Stays Unchanged

- **`AioMediaSourceProvider`** — Complete RequiresOpening pipeline (Sprint 410) remains as-is
- **`DatabaseManager`** — catalog_items table still authoritative for plugin state
- **`NfoWriterService`** — Still writes .nfo files for metadata (optional, can be deprecated)
- **`AioStreamsClient`** — No changes
- **`ResolverHealthTracker`** — No changes
- **User-facing workflows** — Discover UI, version picker, playback all identical

---

## 2. User Setup Instructions

### Before Migration (Current)

Users run the wizard which calls `LibraryProvisioningService.EnsureLibrariesProvisionedAsync()`:
1. Creates directories: `/media/infinitedrive/movies`, `/shows`, `/anime`
2. Creates Emby libraries pointing to these directories
3. Plugin writes .strm files into these directories

### After Migration (New)

Users run the wizard which calls `LibraryProvisioningService.EnsureLibrariesProvisionedAsync()`:
1. **Creates seed directories** (same as before): `/media/infinitedrive/movies`, `/shows`, `/anime`
2. **Creates stub file** in each directory (Gelato pattern): `stub.txt` with "This is a seed file..."
3. **Creates Emby libraries** pointing to these directories (unchanged)
4. Plugin injects items with `LocationType.Virtual` directly into database
5. **No .strm files written**

**Why seed directories still needed:**
- Emby libraries require at least one file location to exist
- Library scans need a path to trigger metadata refresh
- Stub file ensures library doesn't appear "empty" in Emby UI
- Follows Gelato's proven pattern for virtual items

### Migration Path for Existing Users

**Option 1: Automatic Cleanup (Recommended)**
1. Plugin detects existing .strm files during first run after migration
2. Deletes all .strm and .nfo files
3. Re-creates items as virtual
4. Triggers library refresh

**Option 2: Manual Cleanup**
1. User deletes all .strm/.nfo files manually
2. User triggers "Refresh Metadata" in Emby
3. Plugin re-creates items as virtual

---

## 3. Code Changes Required

### 3.1 New Files

#### `Services/VirtualItemService.cs` (NEW)

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Creates virtual items directly in Emby's database using LocationType.Virtual.
    /// Replaces StrmWriterService in the virtual-item architecture.
    /// </summary>
    public class VirtualItemService
    {
        private readonly ILogger<VirtualItemService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;
        private readonly DatabaseManager _db;

        public VirtualItemService(
            ILibraryManager libraryManager,
            ILogManager logManager,
            DatabaseManager db)
        {
            _libraryManager = libraryManager;
            _logManager = logManager;
            _db = db;
            _logger = new EmbyLoggerAdapter<VirtualItemService>(logManager.GetLogger("InfiniteDrive"));
        }

        /// <summary>
        /// Creates a virtual item for the given catalog item and returns the Emby BaseItem ID.
        /// </summary>
        public async Task<string?> CreateItemAsync(
            CatalogItem item,
            SourceType originSourceType,
            string? ownerUserId,
            CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            var isAnime = string.Equals(item.CatalogType, "anime", StringComparison.OrdinalIgnoreCase);
            var parentFolder = isAnime ? config.SyncPathAnime
                : item.MediaType == "movie" ? config.SyncPathMovies
                : config.SyncPathShows;

            if (string.IsNullOrWhiteSpace(parentFolder)) return null;

            var parent = ResolveParentFolder(parentFolder, config);
            if (parent == null)
            {
                _logger.LogWarning("[VirtualItemService] Parent folder not found for {Path}", parentFolder);
                return null;
            }

            BaseItem baseItem;
            if (item.MediaType == "movie")
            {
                baseItem = new Movie
                {
                    Name = item.Title,
                    Year = item.Year,
                    Path = $"infinitedrive://{item.ImdbId ?? item.Id}",
                    LocationType = LocationType.Virtual,
                    IsLocked = false,
                    IsOffline = false,
                    DateCreated = DateTime.UtcNow,
                    DateModified = DateTime.UtcNow,
                };
            }
            else // Series
            {
                baseItem = new Series
                {
                    Name = item.Title,
                    Year = item.Year,
                    Path = $"infinitedrive://{item.ImdbId ?? item.Id}",
                    LocationType = LocationType.Virtual,
                    IsLocked = false,
                    IsOffline = false,
                    DateCreated = DateTime.UtcNow,
                    DateModified = DateTime.UtcNow,
                };
            }

            // Set provider IDs
            if (!string.IsNullOrEmpty(item.ImdbId))
                baseItem.ProviderIds["imdb"] = item.ImdbId;
            if (!string.IsNullOrEmpty(item.TmdbId))
                baseItem.ProviderIds["tmdb"] = item.TmdbId;
            if (!string.IsNullOrEmpty(item.TvdbId))
                baseItem.ProviderIds["tvdb"] = item.TvdbId;
            if (!string.IsNullOrEmpty(item.Id) && item.Id.StartsWith("kitsu:"))
                baseItem.ProviderIds["kitsu"] = item.Id.Substring(6); // Strip "kitsu:" prefix

            // Set source identifier (AIO catalog, user list, etc.)
            if (!string.IsNullOrEmpty(originSourceType))
                baseItem.ProviderIds["infinitedrive_source"] = originSourceType;

            parent.AddChild(baseItem);
            await baseItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataAdded, ct);

            _logger.LogInformation(
                "[VirtualItemService] Created virtual item {Type}: {Name} (IMDB: {ImdbId})",
                item.MediaType, item.Title, item.ImdbId);

            await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);

            return baseItem.Id.ToString();
        }

        /// <summary>
        /// Creates a virtual episode for a series.
        /// </summary>
        public async Task<string?> CreateEpisodeAsync(
            CatalogItem seriesItem,
            int season,
            int episode,
            string? episodeTitle,
            CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            var parentFolder = string.Equals(seriesItem.MediaType, "anime", StringComparison.OrdinalIgnoreCase)
                ? config.SyncPathAnime
                : config.SyncPathShows;

            if (string.IsNullOrWhiteSpace(parentFolder)) return null;

            // Find series item
            var series = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series },
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        { "imdb", seriesItem.ImdbId ?? seriesItem.Id }
                    }
                })
                .FirstOrDefault() as Series;

            if (series == null)
            {
                _logger.LogWarning("[VirtualItemService] Series not found for IMDB: {ImdbId}", seriesItem.ImdbId);
                return null;
            }

            // Find or create season
            var seasonItem = series.Children
                .OfType<Season>()
                .FirstOrDefault(s => s.IndexNumber == season);

            if (seasonItem == null)
            {
                seasonItem = new Season
                {
                    Name = $"Season {season}",
                    IndexNumber = season,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = $"{series.Path}/Season {season:D2}",
                    LocationType = LocationType.Virtual,
                    IsLocked = true,
                };
                series.AddChild(seasonItem);
                await seasonItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataAdded, ct);
            }

            // Create episode
            var episodeItem = new Episode
            {
                Name = episodeTitle ?? $"Episode {episode}",
                SeriesId = series.Id,
                SeasonId = seasonItem.Id,
                IndexNumber = episode,
                ParentIndexNumber = season,
                Path = $"infinitedrive://{seriesItem.ImdbId ?? seriesItem.Id}/S{season}E{episode}",
                LocationType = LocationType.Virtual,
                IsLocked = false,
                IsOffline = false,
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
            };

            seasonItem.AddChild(episodeItem);
            await episodeItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataAdded, ct);

            _logger.LogInformation(
                "[VirtualItemService] Created virtual episode: {Series} S{Season}E{Episode}",
                series.Name, season, episode);

            return episodeItem.Id.ToString();
        }

        /// <summary>
        /// Resolves the parent Folder for a given library path.
        /// Based on Gelato's TryGetFolder pattern.
        /// </summary>
        private Folder? ResolveParentFolder(string path, PluginConfiguration config)
        {
            // Ensure seed directory exists with stub file (Gelato pattern)
            SeedFolder(path);

            return _libraryManager
                .GetItemList(new InternalItemsQuery { IsDeadPerson = true, Path = path })
                .OfType<Folder>()
                .FirstOrDefault();
        }

        /// <summary>
        /// Creates directory and stub file if missing (Gelato pattern).
        /// Ensures library scans trigger properly for virtual items.
        /// </summary>
        private static void SeedFolder(string path)
        {
            Directory.CreateDirectory(path);
            var stub = Path.Combine(path, "stub.txt");
            if (!File.Exists(stub))
            {
                File.WriteAllText(
                    stub,
                    "This is a seed file created by InfiniteDrive so that library scans are triggered. Do not remove.");
            }
        }

        private async Task PersistFirstAddedByUserIdIfNotSetAsync(
            CatalogItem item,
            string? ownerUserId,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ownerUserId)) return;
            if (!string.IsNullOrEmpty(item.FirstAddedByUserId)) return;

            await _db.SetFirstAddedByUserIdIfNotSetAsync(item.Id, ownerUserId, ct);
        }
    }
}
```

#### `Services/VirtualRemovalService.cs` (NEW)

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Removes virtual items from Emby's database.
    /// Replaces file-based deletion logic.
    /// </summary>
    public class VirtualRemovalService
    {
        private readonly ILogger<VirtualRemovalService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;
        private readonly DatabaseManager _db;

        public VirtualRemovalService(
            ILibraryManager libraryManager,
            ILogManager logManager,
            DatabaseManager db)
        {
            _libraryManager = libraryManager;
            _logManager = logManager;
            _db = db;
            _logger = new EmbyLoggerAdapter<VirtualRemovalService>(logManager.GetLogger("InfiniteDrive"));
        }

        /// <summary>
        /// Deletes a virtual item from Emby's database.
        /// </summary>
        public Task<bool> DeleteItemAsync(CatalogItem item, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(item.ImdbId))
                return Task.FromResult(false);

            var embyItem = FindEmbyItem(item.ImdbId, item.MediaType);
            if (embyItem == null)
                return Task.FromResult(true); // Already gone

            try
            {
                _libraryManager.DeleteItem(embyItem, new DeleteOptions
                {
                    DeleteFileLocation = false, // Don't delete the seed folder
                });

                _logger.LogInformation(
                    "[VirtualRemovalService] Deleted virtual item: {Type} {Name} (IMDB: {ImdbId})",
                    item.MediaType, item.Title, item.ImdbId);

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[VirtualRemovalService] Failed to delete virtual item: {Type} {Name}",
                    item.MediaType, item.Title);
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Deletes all virtual items for a series.
        /// </summary>
        public Task<bool> DeleteSeriesAsync(CatalogItem seriesItem, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(seriesItem.ImdbId))
                return Task.FromResult(false);

            var series = FindEmbyItem(seriesItem.ImdbId, "series") as Series;
            if (series == null)
                return Task.FromResult(true);

            try
            {
                _libraryManager.DeleteItem(series, new DeleteOptions
                {
                    DeleteFileLocation = false,
                });

                _logger.LogInformation(
                    "[VirtualRemovalService] Deleted virtual series: {Name} (IMDB: {ImdbId})",
                    seriesItem.Title, seriesItem.ImdbId);

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[VirtualRemovalService] Failed to delete virtual series: {Name}",
                    seriesItem.Title);
                return Task.FromResult(false);
            }
        }

        private BaseItem? FindEmbyItem(string imdbId, string mediaType)
        {
            var baseItemKind = mediaType == "movie" ? BaseItemKind.Movie : BaseItemKind.Series;

            return _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { baseItemKind },
                    HasAnyProviderId = new Dictionary<string, string> { { "imdb", imdbId } }
                })
                .FirstOrDefault();
        }
    }
}
```

#### `Services/VirtualMigrationService.cs` (NEW)

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Migrates existing .strm/.nfo files to virtual items during first run after upgrade.
    /// </summary>
    public class VirtualMigrationService
    {
        private readonly ILogger<VirtualMigrationService> _logger;
        private readonly ILogManager _logManager;
        private readonly DatabaseManager _db;
        private readonly VirtualItemService _virtualItemService;

        public VirtualMigrationService(
            ILogManager logManager,
            DatabaseManager db,
            VirtualItemService virtualItemService)
        {
            _logManager = logManager;
            _db = db;
            _virtualItemService = virtualItemService;
            _logger = new EmbyLoggerAdapter<VirtualMigrationService>(logManager.GetLogger("InfiniteDrive"));
        }

        /// <summary>
        /// Checks for existing .strm files and migrates them to virtual items if found.
        /// Returns the number of items migrated.
        /// </summary>
        public async Task<int> MigrateFromStrmFilesIfNeededAsync(CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return 0;

            // Check migration flag
            var hasMigrated = config.HasMigratedToVirtual;
            if (hasMigrated)
            {
                _logger.LogInformation("[VirtualMigrationService] Already migrated to virtual items");
                return 0;
            }

            _logger.LogInformation("[VirtualMigrationService] Checking for .strm files to migrate...");

            var count = 0;

            // Migrate movies
            if (!string.IsNullOrEmpty(config.SyncPathMovies))
                count += await MigrateDirectoryAsync(config.SyncPathMovies, ct);

            // Migrate series
            if (!string.IsNullOrEmpty(config.SyncPathShows))
                count += await MigrateDirectoryAsync(config.SyncPathShows, ct);

            // Migrate anime
            if (!string.IsNullOrEmpty(config.SyncPathAnime))
                count += await MigrateDirectoryAsync(config.SyncPathAnime, ct);

            // Mark as migrated
            config.HasMigratedToVirtual = true;
            Plugin.Instance?.SaveConfiguration();

            _logger.LogInformation("[VirtualMigrationService] Migration complete: {Count} items", count);

            // Trigger library scan for fresh metadata
            _libraryManager.QueueLibraryScan();

            return count;
        }

        private async Task<int> MigrateDirectoryAsync(string directory, CancellationToken ct)
        {
            if (!Directory.Exists(directory)) return 0;

            var count = 0;

            // Delete all .strm and .nfo files
            foreach (var file in Directory.EnumerateFiles(directory, "*.strm", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                    count++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[VirtualMigrationService] Failed to delete: {Path}", file);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.nfo", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[VirtualMigrationService] Failed to delete: {Path}", file);
                }
            }

            return count;
        }
    }
}
```

### 3.2 Modified Files

#### `PluginConfiguration.cs`

**Add:**
```csharp
/// <summary>
/// Set to true after migration from .strm files to virtual items is complete.
/// </summary>
[DataMember]
public bool HasMigratedToVirtual { get; set; } = false;

/// <summary>
/// Virtual item mode: true = use LocationType.Virtual, false = use .strm files.
/// Default true after migration.
/// </summary>
[DataMember]
public bool UseVirtualItems { get; set; } = true;
```

#### `Services/StrmWriterService.cs`

**Status:** **DEPRECATED** - Mark entire file with `[Obsolete("Use VirtualItemService instead")]` attribute. Do not delete immediately for rollback safety.

#### `Services/LibraryProvisioningService.cs`

**Add to `ProvisionOneAsync`:**
```csharp
// Add stub file for virtual item mode (Gelato pattern)
var stub = Path.Combine(path, "stub.txt");
if (!File.Exists(stub))
{
    File.WriteAllText(
        stub,
        "This is a seed file created by InfiniteDrive so that library scans are triggered. Do not remove.");
    _logger.LogInformation("[InfiniteDrive] Created stub file: {Path}", stub);
}
```

#### `Services/ItemPipelineService.cs`

**Modify `ProcessItemAsync` to conditionally call virtual or strm writer:**
```csharp
public async Task<ItemPipelineResult> ProcessItemAsync(
    MediaItem item,
    PipelineTrigger trigger,
    CancellationToken ct)
{
    // ... existing code ...

    var config = Plugin.Instance?.Configuration;
    if (config?.UseVirtualItems == true)
    {
        // New path: Create virtual item
        var itemId = await Plugin.Instance?.VirtualItemService?.CreateItemAsync(
            catalogItem,
            SourceType.Catalog,
            null,
            ct);

        // Set strm_path to virtual item ID (for compatibility)
        catalogItem.StrmPath = $"virtual://{itemId}";
    }
    else
    {
        // Old path: Write .strm file
        var strmPath = await Plugin.Instance?.StrmWriterService?.WriteAsync(
            catalogItem,
            SourceType.Catalog,
            null,
            ct);
        catalogItem.StrmPath = strmPath;
    }

    // ... rest of pipeline ...
}
```

#### `Tasks/RefreshTask.cs`

**Modify `WriteStepAsync` to conditionally use virtual or strm writer:**
```csharp
private async Task<int> WriteStepAsync(List<CatalogItem> collected, CancellationToken ct)
{
    var config = Plugin.Instance?.Configuration;
    var useVirtual = config?.UseVirtualItems == true;

    var written = 0;

    foreach (var item in collected)
    {
        string? pathOrId;
        if (useVirtual)
        {
            pathOrId = await Plugin.Instance?.VirtualItemService?.CreateItemAsync(
                item,
                SourceType.Catalog,
                null,
                ct);
        }
        else
        {
            pathOrId = await Plugin.Instance?.StrmWriterService?.WriteAsync(
                item,
                SourceType.Catalog,
                null,
                ct);
        }

        if (!string.IsNullOrEmpty(pathOrId))
        {
            item.StrmPath = pathOrId;
            written++;
        }
    }

    return written;
}
```

#### `Services/RemovalService.cs`

**Modify to use VirtualRemovalService:**
```csharp
private readonly VirtualRemovalService _virtualRemovalService;

public async Task<bool> RemoveItemAsync(string catalogItemId, CancellationToken ct)
{
    // ... find item ...

    if (Plugin.Instance?.Configuration?.UseVirtualItems == true)
    {
        return await _virtualRemovalService.DeleteItemAsync(item, ct);
    }
    else
    {
        // Old path: Delete .strm files
        if (!string.IsNullOrEmpty(item.StrmPath))
        {
            StrmWriterService.DeleteWithVersions(item.StrmPath);
        }
        return true;
    }
}
```

#### `Services/SeriesPreExpansionService.cs`

**Modify to use VirtualItemService for series expansion:**
```csharp
private async Task ExpandSeriesEpisodesAsync(
    CatalogItem seriesItem,
    List<(int season, int episode)> episodes,
    CancellationToken ct)
{
    var config = Plugin.Instance?.Configuration;
    if (config?.UseVirtualItems == true)
    {
        foreach (var (season, episode) in episodes)
        {
            await Plugin.Instance?.VirtualItemService?.CreateEpisodeAsync(
                seriesItem,
                season,
                episode,
                null,
                ct);
        }
    }
    else
    {
        // Old path: Write .strm files
        foreach (var (season, episode) in episodes)
        {
            await Plugin.Instance?.StrmWriterService?.WriteEpisodeAsync(
                seriesItem,
                season,
                episode,
                null,
                ct);
        }
    }
}
```

#### `Plugin.cs`

**Add to properties:**
```csharp
/// <summary>
/// Virtual item service — injects items directly into Emby database.
/// Replaces StrmWriterService in virtual-item mode.
/// </summary>
public Services.VirtualItemService VirtualItemService { get; private set; } = null!;

/// <summary>
/// Virtual removal service — deletes virtual items from Emby database.
/// </summary>
public Services.VirtualRemovalService VirtualRemovalService { get; private set; } = null!;

/// <summary>
/// Virtual migration service — migrates .strm files to virtual items.
/// </summary>
public Services.VirtualMigrationService VirtualMigrationService { get; private set; } = null!;
```

**Add to constructor:**
```csharp
VirtualItemService = new Services.VirtualItemService(
    libraryManager,
    logManager,
    DatabaseManager);
VirtualRemovalService = new Services.VirtualRemovalService(
    libraryManager,
    logManager,
    DatabaseManager);
VirtualMigrationService = new Services.VirtualMigrationService(
    logManager,
    DatabaseManager,
    VirtualItemService);
```

**Add to `OnApplicationPostInitAsync`:**
```csharp
// Run migration if needed
_ = Task.Run(async () =>
{
    try
    {
        await VirtualMigrationService.MigrateFromStrmFilesIfNeededAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "[InfiniteDrive] Virtual migration failed");
    }
});
```

### 3.3 Files to Eventually Deprecate

**Phase 1 (Migration Sprint):**
- `Services/StrmWriterService.cs` — Mark `[Obsolete]`, keep for rollback

**Phase 2 (Follow-up Sprint):**
- `Services/StrmWriterService.cs` — Delete entirely
- `Services/ResolverService.cs` — Already deprecated (Sprint 410)
- `Services/StreamEndpointService.cs` — Already deprecated (Sprint 410)
- `Services/PlaybackTokenService.cs` — Remove GenerateResolveToken/ValidateStreamToken methods
- Remove `DefaultSlotKey`, `SignatureValidityDays` from PluginConfiguration

---

## 4. Metadata Refresh Strategy

### Problem Without .strm Files

When using .strm files, Emby's library scan naturally detects file changes and triggers metadata refresh. With virtual items, there's no filesystem trigger.

### Solution: Explicit Refresh Calls

**Option 1: Refresh After Item Creation (Recommended)**
- Call `baseItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)` after each virtual item creation
- Emby will schedule metadata refresh automatically

**Option 2: Periodic Refresh Task**
- New `VirtualRefreshTask` that runs daily
- Queries `catalog_items` with items not refreshed in 7+ days
- Calls `_libraryManager.QueueLibraryScan()` for affected libraries

**Option 3: Manual Refresh Button**
- Add "Refresh Virtual Items" button in UI
- Triggers full library scan

**Recommended Combination:**
- Option 1 for immediate feedback after item creation
- Option 2 for catching missed refreshes
- Option 3 for user control

---

## 5. Risks and Mitigations

### Risk 1: Emby Scanner Deletes Virtual Items

**Problem:** If Emby's scanner sees no physical file, it might delete virtual items.

**Mitigation:**
- `LocationType.Virtual` explicitly tells Emby's scanner to skip filesystem validation
- Gelato proves this works in production
- Stub file in each directory ensures scanner sees "something"

### Risk 2: Existing User Migration Failure

**Problem:** Migration might leave stale .strm files or create duplicate items.

**Mitigation:**
- `HasMigratedToVirtual` flag prevents re-migration
- Migration deletes ALL .strm/.nfo files before creating virtual items
- Virtual items use unique IDs (`infinitedrive://{imdbId}`) to prevent duplicates
- Rollback flag `UseVirtualItems` allows users to revert to .strm mode

### Risk 3: Metadata Refresh Delays

**Problem:** Without .strm file triggers, metadata might be stale.

**Mitigation:**
- Explicit refresh after item creation
- Periodic refresh task
- User manual refresh option
- Monitor and log refresh status

### Risk 4: NFO File Dependency

**Problem:** Current system writes .nfo files for metadata. What happens to them?

**Decision:**
- **Phase 1:** Keep writing .nfo files (for compatibility)
- **Phase 2:** Deprecate .nfo files, rely on Emby's metadata providers
- Rationale: Emby's TMDB provider is reliable; .nfo files add complexity

### Risk 5: AioMediaSourceProvider Compatibility

**Problem:** Does `AioMediaSourceProvider` work with virtual items?

**Verification:**
- `AioMediaSourceProvider.GetMediaSources(BaseItem item)` takes any `BaseItem`
- It queries `item.Path` for IMDB ID (from virtual item's path)
- **Already works** — no changes needed

---

## 6. Breaking Changes

### For Existing Users

1. **Library Re-scan Required:** After migration, users must trigger a library scan
2. **Playback History:** Emby playback history is tied to item IDs; virtual items get new IDs
   - **Mitigation:** Consider migration script to preserve history (complex, optional)

### For Plugin Developers

1. **No More .strm Paths:** Code that assumes `item.StrmPath` is a filesystem path breaks
   - **Fix:** Virtual items use `virtual://{guid}` format
2. **LibraryMonitor No Longer Needed:** `ILibraryMonitor.ReportFileSystemChanged()` unused
   - **Fix:** Remove from dependency injection

---

## 7. Implementation Phases

### Phase 1: Virtual Item Infrastructure (This Sprint)

**Tasks:**
1. Create `VirtualItemService.cs`
2. Create `VirtualRemovalService.cs`
3. Create `VirtualMigrationService.cs`
4. Update `PluginConfiguration.cs` with migration flags
5. Update `LibraryProvisioningService.cs` to add stub files
6. Update `Plugin.cs` to register new services
7. Update `ItemPipelineService.cs` to conditionally use virtual mode
8. Update `RefreshTask.cs` to conditionally use virtual mode
9. Update `RemovalService.cs` to use `VirtualRemovalService`
10. Update `SeriesPreExpansionService.cs` for episode creation

**Testing:**
- Manual test: Create new item, verify it appears in Emby library
- Manual test: Delete item, verify it's removed
- Manual test: Create series, verify seasons/episodes appear
- Manual test: Playback works via `AioMediaSourceProvider`

### Phase 2: Migration Rollout (Follow-up Sprint)

**Tasks:**
1. Run migration on existing .strm files
2. Delete stale .strm/.nfo files
3. Trigger library refresh
4. Verify all items appear correctly
5. Roll back if issues found

### Phase 3: Cleanup (Another Sprint)

**Tasks:**
1. Delete `StrmWriterService.cs` (after 2 weeks of stable operation)
2. Remove `UseVirtualItems` flag (make virtual-only)
3. Deprecate `.nfo` file generation
4. Remove deprecated endpoints (ResolverService, StreamEndpointService)
5. Remove `DefaultSlotKey`, `SignatureValidityDays` from config

---

## 8. Rollback Plan

If issues arise after deployment:

1. **Immediate Rollback:**
   - Set `PluginConfiguration.UseVirtualItems = false`
   - Restart Emby
   - Plugin reverts to .strm file mode
   - Existing virtual items remain (harmless)
   - New items use .strm files

2. **Full Rollback:**
   - Delete all virtual items via `VirtualRemovalService`
   - Restore .strm files from backup (if available)
   - Trigger full library scan

3. **Rollback Flag:** Add `PluginConfiguration.ForceStrmMode` for emergency override

---

## 9. Verification Checklist

Before merging to main:

- [ ] Virtual items appear in Emby libraries
- [ ] Item deletion works correctly
- [ ] Series expansion creates seasons/episodes
- [ ] Playback works via `AioMediaSourceProvider`
- [ ] Version picker shows all qualities
- [ ] Discover UI adds items correctly
- [ ] Migration from .strm to virtual works
- [ ] Rollback via `UseVirtualItems = false` works
- [ ] Stub files created in all library directories
- [ ] Metadata refreshes after item creation
- [ ] Zero build errors/warnings
- [ ] Documentation updated (ARCHITECTURE.md, SERVICES.md, CONTROL_FLOW.md)

---

## 10. Open Questions

1. **NFO File Strategy:** Keep writing .nfo files (Phase 1) or deprecate immediately?
   - **Recommendation:** Keep for Phase 1, deprecate in Phase 3

2. **Playback History Preservation:** Should we attempt to preserve Emby playback history during migration?
   - **Recommendation:** No — too complex, acceptable break for major feature

3. **Binge Prefetch Service:** Does it need changes for virtual items?
   - **Answer:** No — it operates on database IDs, not file paths

4. **Library Scan Frequency:** Should we increase scan frequency for virtual items?
   - **Answer:** No — let users trigger manually; avoid unnecessary load

---

## 11. References

- **Gelato Source:** `/workspace/emby-debrid/Gelato`
  - `GelatoManager.cs` — TryGetFolder pattern
  - `CatalogImportService.cs` — Catalog to virtual item flow
  - `PluginConfiguration.cs` — Library folder management

- **InfiniteDrive Source:**
  - `Services/StrmWriterService.cs` — Current .strm writing logic (to be replaced)
  - `Services/AioMediaSourceProvider.cs` — Playback provider (unchanged)
  - `Services/ItemPipelineService.cs` — Item creation flow
  - `Services/LibraryProvisioningService.cs` — Library setup

- **Emby SDK:**
  - `LocationType` enum in `MediaBrowser.Model.Entities`
  - `ILibraryManager.CreateItem()` API
  - `BaseItem` and derived classes (`Movie`, `Series`, `Episode`)

---

**Plan Status:** Awaiting approval from user before implementation begins.
