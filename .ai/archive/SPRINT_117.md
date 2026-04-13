# Sprint 117 — Admin UI (Declarative Plugin UI)

**Version:** v4.10 | **Status:** In Progress | **Risk:** LOW | **Depends:** Sprint 116

---

## Overview

Sprint 117 implements Admin UI using Emby's **declarative Plugin UI system**. Instead of custom HTML/JS/CSS, UI is generated automatically from C# ViewModels decorated with attributes. This approach leverages Emby's native UI patterns and ensures consistency with other plugins.

**Key Architecture:**
- **Three separate pages** served via `Plugin.GetPages()`:
  1. **Wizard** — Re-entrant setup for API keys and library paths
  2. **Content Management** — Admin-only tabs for sources, collections, items, actions
  3. **My Library** — Per-user tabs for saved, blocked, watch history
- **ViewModels with attributes** for UI generation (`TabGroup`, `DataGrid`, `RunButton`, `ReadOnly`, `Required`, `DisplayName`, `Description`)
- **No custom HTML/JS/CSS** — Emby generates UI automatically from ViewModels
- **BasePluginViewModel** — Base class for all Plugin UI ViewModels

---

## Why Declarative Plugin UI?

### Original Spec Issue

The original Sprint 117 spec called for a custom tabbed configuration page with HTML/JS/CSS. However:

1. **Emby's configuration system** is built around declarative UI generation
2. **Emby Plugin UI documentation** describes ViewModels with attributes, not custom HTML
3. **Architectural mismatch** — Custom HTML doesn't integrate cleanly with Emby's plugin infrastructure
4. **Maintenance burden** — Custom UI requires ongoing updates to match Emby UI changes

### Declarative UI Benefits

- **Native Emby look and feel** — Automatic consistency with Emby's UI
- **Future-proof** — UI automatically updates with Emby versions
- **Less code** — ViewModels replace HTML/JS/CSS
- **Type safety** — Compile-time checking of UI structure
- **Accessibility** — Emby's UI generator handles accessibility automatically

### Three-Tier Architecture

The admin UI is split into three logical pages:

| Page | Access | Purpose |
|------|--------|---------|
| Wizard | All users (re-entrant) | Initial setup, API keys, library paths |
| Content Management | Admin only | Sources, collections, items, actions |
| My Library | Per-user | Saved, blocked, watch history |

This separation aligns with:
- **Wizard** — One-time setup (but re-entrant for configuration changes)
- **Content Management** — Administrative functions that affect all users
- **My Library** — User-specific personalization

---

## Implementation Plan

### Page 1: Wizard (Setup)

**File:** `Configuration/WizardViewModel.cs`

```csharp
using Emby.Plugin.UI.Attributes;
using MediaBrowser.Model.Plugins;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// Wizard ViewModel for initial setup.
    /// Re-entrant — users can return to update API keys and library paths.
    /// </summary>
    public class WizardViewModel : BasePluginViewModel
    {
        [DisplayName("API Key")]
        [Description("Your EmbyStreams API key for accessing the catalog")]
        [Required]
        public string ApiKey { get; set; } = string.Empty;

        [DisplayName("Movies Library Path")]
        [Description("Path to the EmbyStreams movies library")]
        [Required]
        public string MoviesLibraryPath { get; set; } = "/embystreams/library/movies/";

        [DisplayName("Series Library Path")]
        [Description("Path to the EmbyStreams series library")]
        [Required]
        public string SeriesLibraryPath { get; set; } = "/embystreams/library/series/";

        [DisplayName("Anime Library Path")]
        [Description("Path to the EmbyStreams anime library (AniList/AniDB)")]
        [Required]
        public string AnimeLibraryPath { get; set; } = "/embystreams/library/anime/";

        [DisplayName("Enable Auto-Sync")]
        [Description("Automatically sync catalog items every 6 hours")]
        public bool EnableAutoSync { get; set; } = true;

        [DisplayName("Sync Interval (hours)")]
        [Description("How often to auto-sync (minimum: 1 hour)")]
        [Range(1, 24)]
        public int SyncIntervalHours { get; set; } = 6;
    }
}
```

**Acceptance Criteria:**
- [ ] ViewModel inherits from BasePluginViewModel
- [ ] API key field with Required attribute
- [ ] Three library paths with Required attribute
- [ ] Auto-sync toggle
- [ ] Sync interval with Range(1, 24)
- [ ] Emby generates UI automatically
- [ ] Wizard is re-entrant (users can return)

---

### Page 2: Content Management (Admin Tabs)

**File:** `Configuration/ContentManagementViewModel.cs`

```csharp
using System.Collections.Generic;
using Emby.Plugin.UI.Attributes;
using EmbyStreams.Models;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// Content Management ViewModel for admin-only functions.
    /// Shows tabs for Sources, Collections, Items, and Actions.
    /// </summary>
    public class ContentManagementViewModel : BasePluginViewModel
    {
        #region Sources Tab

        [TabGroup("Sources", Order = 1)]
        [DataGrid]
        public List<SourceRow> Sources { get; set; } = new();

        [TabGroup("Sources", Order = 2)]
        [RunButton("Add Source")]
        public void AddSource()
        {
            // Open source creation dialog
        }

        #endregion

        #region Collections Tab

        [TabGroup("Collections", Order = 1)]
        [DataGrid]
        public List<CollectionRow> Collections { get; set; } = new();

        [TabGroup("Collections", Order = 2)]
        [RunButton("Sync All Collections")]
        public void SyncCollections()
        {
            // Trigger collection sync
        }

        #endregion

        #region Items Tab

        [TabGroup("Items", Order = 1)]
        [DataGrid]
        public List<ItemRow> AllItems { get; set; } = new();

        [TabGroup("Items", Order = 2)]
        [FilterOptions("Active", "Superseded", "Blocked")]
        public ItemStatus? StatusFilter { get; set; }

        [TabGroup("Items", Order = 3)]
        [RunButton("Refresh Items")]
        public void RefreshItems()
        {
            // Reload items with current filter
        }

        #endregion

        #region Needs Review Tab

        [TabGroup("Needs Review", Order = 1)]
        [DataGrid]
        public List<ItemRow> NeedsReview { get; set; } = new();

        [TabGroup("Needs Review", Order = 2)]
        [Description("Items with superseded_conflict = true require admin review")]
        [ReadOnly]
        public string ReviewNote =>
            "These items were explicitly saved but also match local files. " +
            "Review each item to resolve the conflict.";

        #endregion

        #region Actions Tab

        [TabGroup("Actions", Order = 1)]
        [RunButton("Sync Now")]
        public void SyncNow()
        {
            // Trigger immediate sync
        }

        [TabGroup("Actions", Order = 2)]
        [RunButton("Your Files Reconcile")]
        public void ReconcileYourFiles()
        {
            // Trigger Your Files reconciliation
        }

        [TabGroup("Actions", Order = 3)]
        [RunButton("Cleanup Removed")]
        public void CleanupRemoved()
        {
            // Cleanup items no longer in catalog
        }

        [TabGroup("Actions", Order = 4)]
        [RunButton("Sync Collections")]
        public void SyncCollectionsNow()
        {
            // Sync all collections
        }

        [TabGroup("Actions", Order = 5)]
        [RunButton("Purge Cache")]
        [Dangerous("This will delete all cached stream URLs")]
        public void PurgeCache()
        {
            // Purge stream URL cache
        }

        [TabGroup("Actions", Order = 6)]
        [RunButton("Reset Database")]
        [Dangerous("This will delete all data and require re-setup")]
        [Confirmation("Type 'RESET' to confirm database reset")]
        public void ResetDatabase()
        {
            // Reset database to empty state
        }

        #endregion
    }

    #region Row Models

    public class SourceRow
    {
        [DisplayName("Name")]
        [ReadOnly]
        public string Name { get; set; } = string.Empty;

        [DisplayName("Items")]
        [ReadOnly]
        public int ItemCount { get; set; }

        [DisplayName("Last Sync")]
        [ReadOnly]
        public DateTime? LastSyncedAt { get; set; }

        [DisplayName("Enabled")]
        public bool Enabled { get; set; }

        [DisplayName("Show as Collection")]
        public bool ShowAsCollection { get; set; }

        [RunButton("Sync")]
        public void Sync() { }

        [RunButton("Delete")]
        [Dangerous]
        public void Delete() { }
    }

    public class CollectionRow
    {
        [DisplayName("Collection Name")]
        [ReadOnly]
        public string CollectionName { get; set; } = string.Empty;

        [DisplayName("Source")]
        [ReadOnly]
        public string SourceName { get; set; } = string.Empty;

        [DisplayName("Last Synced")]
        [ReadOnly]
        public DateTime? LastSyncedAt { get; set; }

        [RunButton("Sync")]
        public void Sync() { }

        [RunButton("View in Emby")]
        public void View() { }
    }

    public class ItemRow
    {
        [DisplayName("Title")]
        [ReadOnly]
        public string Title { get; set; } = string.Empty;

        [DisplayName("Year")]
        [ReadOnly]
        public int Year { get; set; }

        [DisplayName("Type")]
        [ReadOnly]
        public string MediaType { get; set; } = string.Empty;

        [DisplayName("Status")]
        [ReadOnly]
        public ItemStatus Status { get; set; }

        [DisplayName("Save Reason")]
        [ReadOnly]
        public string? SaveReason { get; set; }

        [DisplayName("Superseded")]
        [ReadOnly]
        public bool Superseded { get; set; }

        [DisplayName("Conflict")]
        [ReadOnly]
        public bool SupersededConflict { get; set; }

        [RunButton("Details")]
        public void Details() { }

        [RunButton("Keep Saved")]
        public void KeepSaved() { }

        [RunButton("Accept Your Files")]
        public void AcceptYourFiles() { }
    }

    #endregion
}
```

**Acceptance Criteria:**
- [ ] ViewModel inherits from BasePluginViewModel
- [ ] Sources tab with DataGrid and Add Source button
- [ ] Collections tab with DataGrid and Sync All button
- [ ] Items tab with DataGrid, filter, and Refresh button
- [ ] Needs Review tab with DataGrid and review note
- [ ] Actions tab with all action buttons
- [ ] Dangerous actions marked with Dangerous attribute
- [ ] Reset Database requires "RESET" confirmation
- [ ] Emby generates UI automatically

---

### Page 3: My Library (Per-User Tabs)

**File:** `Configuration/MyLibraryViewModel.cs`

```csharp
using System.Collections.Generic;
using Emby.Plugin.UI.Attributes;
using EmbyStreams.Models;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// My Library ViewModel for per-user personalization.
    /// Shows tabs for Saved, Blocked, and Watch History.
    /// </summary>
    public class MyLibraryViewModel : BasePluginViewModel
    {
        #region Saved Tab

        [TabGroup("Saved", Order = 1)]
        [DataGrid]
        public List<ItemRow> SavedItems { get; set; } = new();

        [TabGroup("Saved", Order = 2)]
        [FilterOptions("Movies", "Series", "Anime")]
        public string? MediaTypeFilter { get; set; }

        [TabGroup("Saved", Order = 3)]
        [RunButton("Refresh")]
        public void RefreshSaved()
        {
            // Reload saved items with current filter
        }

        #endregion

        #region Blocked Tab

        [TabGroup("Blocked", Order = 1)]
        [DataGrid]
        public List<ItemRow> BlockedItems { get; set; } = new();

        [TabGroup("Blocked", Order = 2)]
        [FilterOptions("Movies", "Series", "Anime")]
        public string? MediaTypeFilter { get; set; }

        [TabGroup("Blocked", Order = 3)]
        [RunButton("Refresh")]
        public void RefreshBlocked()
        {
            // Reload blocked items with current filter
        }

        #endregion

        #region Watch History Tab

        [TabGroup("Watch History", Order = 1)]
        [DataGrid]
        public List<WatchHistoryRow> WatchHistory { get; set; } = new();

        [TabGroup("Watch History", Order = 2)]
        [FilterOptions("Watched", "Partially Watched", "Not Started")]
        public WatchStatus? StatusFilter { get; set; }

        [TabGroup("Watch History", Order = 3)]
        [RunButton("Refresh")]
        public void RefreshHistory()
        {
            // Reload watch history with current filter
        }

        #endregion
    }

    public class WatchHistoryRow
    {
        [DisplayName("Title")]
        [ReadOnly]
        public string Title { get; set; } = string.Empty;

        [DisplayName("Season")]
        [ReadOnly]
        public int? Season { get; set; }

        [DisplayName("Episode")]
        [ReadOnly]
        public int? Episode { get; set; }

        [DisplayName("Status")]
        [ReadOnly]
        public WatchStatus Status { get; set; }

        [DisplayName("Last Watched")]
        [ReadOnly]
        public DateTime? LastWatchedAt { get; set; }

        [RunButton("Re-watch")]
        public void Rewatch() { }
    }

    public enum WatchStatus
    {
        Watched,
        PartiallyWatched,
        NotStarted
    }
}
```

**Acceptance Criteria:**
- [ ] ViewModel inherits from BasePluginViewModel
- [ ] Saved tab with DataGrid, filter, and Refresh button
- [ ] Blocked tab with DataGrid, filter, and Refresh button
- [ ] Watch History tab with DataGrid, filter, and Refresh button
- [ ] Emby generates UI automatically
- [ ] Data is filtered per-user (not admin view)

---

## Base Classes and Attributes

### BasePluginViewModel

**File:** `Configuration/BasePluginViewModel.cs`

```csharp
using MediaBrowser.Model.Plugins;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// Base class for all Plugin UI ViewModels.
    /// Provides common functionality for all configuration pages.
    /// </summary>
    public abstract class BasePluginViewModel : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the plugin version.
        /// </summary>
        [DisplayName("Plugin Version")]
        [ReadOnly]
        public string PluginVersion { get; set; } = "0.51.0.0";

        /// <summary>
        /// Gets or sets the last sync timestamp.
        /// </summary>
        [DisplayName("Last Sync")]
        [ReadOnly]
        public DateTime? LastSyncAt { get; set; }

        /// <summary>
        /// Gets or sets the plugin status.
        /// </summary>
        [DisplayName("Status")]
        [ReadOnly]
        public string Status { get; set; } = "OK";
    }
}
```

**Acceptance Criteria:**
- [ ] Inherits from BasePluginConfiguration
- [ ] Provides common properties (PluginVersion, LastSyncAt, Status)
- [ ] All ViewModels inherit from this base class

---

### UI Attributes

Create custom attributes in `Configuration/Attributes/` directory:

**File:** `Configuration/Attributes/TabGroupAttribute.cs`

```csharp
using System;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Groups properties into a tab on the configuration page.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class TabGroupAttribute : Attribute
    {
        public string Name { get; }
        public int Order { get; }

        public TabGroupAttribute(string name, int order = 0)
        {
            Name = name;
            Order = order;
        }
    }
}
```

**File:** `Configuration/Attributes/DataGridAttribute.cs`

```csharp
using System;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Marks a collection property to be displayed as a data grid.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class DataGridAttribute : Attribute
    {
        public int PageSize { get; set; } = 50;
        public bool AllowSort { get; set; } = true;
        public bool AllowFilter { get; set; } = true;
    }
}
```

**File:** `Configuration/Attributes/RunButtonAttribute.cs`

```csharp
using System;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Marks a method to be displayed as a button in the UI.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class RunButtonAttribute : Attribute
    {
        public string Label { get; }
        public string? Confirmation { get; set; }

        public RunButtonAttribute(string? label = null)
        {
            Label = label ?? "Run";
        }
    }
}
```

**File:** `Configuration/Attributes/DangerousAttribute.cs`

```csharp
using System;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Marks a button as dangerous (requires confirmation).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class DangerousAttribute : Attribute
    {
        public string Message { get; }

        public DangerousAttribute(string message = "This action cannot be undone")
        {
            Message = message;
        }
    }
}
```

**File:** `Configuration/Attributes/FilterOptionsAttribute.cs`

```csharp
using System;
using System.Collections.Generic;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Defines filter options for a property.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class FilterOptionsAttribute : Attribute
    {
        public string[] Options { get; }

        public FilterOptionsAttribute(params string[] options)
        {
            Options = options;
        }
    }
}
```

**Acceptance Criteria:**
- [ ] TabGroupAttribute with Name and Order
- [ ] DataGridAttribute with PageSize, AllowSort, AllowFilter
- [ ] RunButtonAttribute with Label and Confirmation
- [ ] DangerousAttribute with Message
- [ ] FilterOptionsAttribute with Options array
- [ ] All attributes in Emby.Plugin.UI.Attributes namespace

---

## Plugin Integration

### Plugin.GetPages()

Update `Plugin.cs` to register the three pages:

```csharp
public IEnumerable<PluginPageInfo> GetPages()
{
    return new[]
    {
        new PluginPageInfo
        {
            Name = "Wizard",
            DisplayName = "EmbyStreams Setup Wizard",
            EmbeddedResourcePath = "EmbyStreams.Configuration.WizardViewModel"
        },
        new PluginPageInfo
        {
            Name = "ContentManagement",
            DisplayName = "EmbyStreams Content Management",
            EmbeddedResourcePath = "EmbyStreams.Configuration.ContentManagementViewModel"
        },
        new PluginPageInfo
        {
            Name = "MyLibrary",
            DisplayName = "EmbyStreams My Library",
            EmbeddedResourcePath = "EmbyStreams.Configuration.MyLibraryViewModel"
        }
    };
}
```

**Acceptance Criteria:**
- [ ] All three pages registered
- [ ] Wizard page accessible to all users
- [ ] Content Management page accessible to admins only
- [ ] My Library page accessible per-user

---

## Controller Support

While the UI is generated automatically from ViewModels, we still need controllers to handle:

1. **Loading data** into ViewModels
2. **Saving data** from ViewModels
3. **Handling button clicks**

### ConfigurationController

**File:** `Controllers/ConfigurationController.cs`

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Configuration;
using EmbyStreams.Data;
using EmbyStreams.Models;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Services;

namespace EmbyStreams.Controllers
{
    /// <summary>
    /// Controller for loading/saving ViewModels and handling button clicks.
    /// </summary>
    [Route("embystreams/config")]
    public class ConfigurationController : IService, IRequiresRequest
    {
        private readonly DatabaseManager _db;
        private readonly ILogger<ConfigurationController> _logger;

        public ConfigurationController(DatabaseManager db, ILogger<ConfigurationController> logger)
        {
            _db = db;
            _logger = logger;
        }

        public IRequest Request { get; set; } = null!;

        #region Wizard

        [Route("wizard")]
        public async Task<WizardViewModel> GetWizard(CancellationToken ct)
        {
            var config = Plugin.Instance!.Configuration;
            return new WizardViewModel
            {
                ApiKey = config.ApiKey,
                MoviesLibraryPath = config.MoviesLibraryPath,
                SeriesLibraryPath = config.SeriesLibraryPath,
                AnimeLibraryPath = config.AnimeLibraryPath,
                EnableAutoSync = config.EnableAutoSync,
                SyncIntervalHours = config.SyncIntervalHours,
                PluginVersion = config.Version,
                LastSyncAt = config.LastSyncAt,
                Status = config.PluginStatus
            };
        }

        [Route("wizard")]
        public async Task SaveWizard(WizardViewModel viewModel, CancellationToken ct)
        {
            var config = Plugin.Instance!.Configuration;
            config.ApiKey = viewModel.ApiKey;
            config.MoviesLibraryPath = viewModel.MoviesLibraryPath;
            config.SeriesLibraryPath = viewModel.SeriesLibraryPath;
            config.AnimeLibraryPath = viewModel.AnimeLibraryPath;
            config.EnableAutoSync = viewModel.EnableAutoSync;
            config.SyncIntervalHours = viewModel.SyncIntervalHours;
            Plugin.Instance!.SaveConfiguration();
        }

        #endregion

        #region Content Management

        [Route("content-management")]
        public async Task<ContentManagementViewModel> GetContentManagement(CancellationToken ct)
        {
            var viewModel = new ContentManagementViewModel();

            // Load sources
            var sources = await _db.GetSourcesAsync(ct);
            viewModel.Sources = sources.Select(s => new SourceRow
            {
                Name = s.Name,
                ItemCount = s.ItemCount,
                LastSyncedAt = s.LastSyncedAt,
                Enabled = s.Enabled,
                ShowAsCollection = s.ShowAsCollection
            }).ToList();

            // Load collections
            var collections = await _db.GetCollectionsAsync(ct);
            viewModel.Collections = collections.Select(c => new CollectionRow
            {
                CollectionName = c.Name,
                SourceName = c.Name,
                LastSyncedAt = c.LastSyncedAt
            }).ToList();

            // Load all items
            var allItems = await _db.GetItemsAsync(null, null, null, 50, 0, ct);
            viewModel.AllItems = allItems.Select(i => new ItemRow
            {
                Title = i.Title,
                Year = i.Year,
                MediaType = i.MediaType,
                Status = i.Status,
                SaveReason = i.SaveReason,
                Superseded = i.Superseded,
                SupersededConflict = i.SupersededConflict
            }).ToList();

            // Load needs review
            var needsReview = allItems.Where(i => i.SupersededConflict).ToList();
            viewModel.NeedsReview = needsReview.Select(i => new ItemRow
            {
                Title = i.Title,
                Year = i.Year,
                MediaType = i.MediaType,
                Status = i.Status,
                SaveReason = i.SaveReason,
                Superseded = i.Superseded,
                SupersededConflict = i.SupersededConflict
            }).ToList();

            viewModel.PluginVersion = Plugin.Instance!.Configuration.Version;
            viewModel.LastSyncAt = Plugin.Instance!.Configuration.LastSyncAt;
            viewModel.Status = Plugin.Instance!.Configuration.PluginStatus;

            return viewModel;
        }

        [Route("content-management/sources/{sourceId}/sync")]
        public async Task SyncSource(string sourceId, CancellationToken ct)
        {
            // Trigger sync for specific source
        }

        [Route("content-management/sync")]
        public async Task SyncAll(CancellationToken ct)
        {
            // Trigger sync all
        }

        [Route("content-management/purge-cache")]
        public async Task PurgeCache(CancellationToken ct)
        {
            // Purge stream URL cache
        }

        [Route("content-management/reset")]
        public async Task ResetDatabase(CancellationToken ct)
        {
            // Reset database
        }

        #endregion

        #region My Library

        [Route("my-library")]
        public async Task<MyLibraryViewModel> GetMyLibrary(CancellationToken ct)
        {
            var userId = GetUserId();
            var viewModel = new MyLibraryViewModel();

            // Load saved items for this user
            var savedItems = await _db.GetSavedItemsAsync(userId, ct);
            viewModel.SavedItems = savedItems.Select(i => new ItemRow
            {
                Title = i.Title,
                Year = i.Year,
                MediaType = i.MediaType,
                Status = i.Status
            }).ToList();

            // Load blocked items for this user
            var blockedItems = await _db.GetBlockedItemsAsync(userId, ct);
            viewModel.BlockedItems = blockedItems.Select(i => new ItemRow
            {
                Title = i.Title,
                Year = i.Year,
                MediaType = i.MediaType,
                Status = i.Status
            }).ToList();

            viewModel.PluginVersion = Plugin.Instance!.Configuration.Version;
            viewModel.LastSyncAt = Plugin.Instance!.Configuration.LastSyncAt;
            viewModel.Status = Plugin.Instance!.Configuration.PluginStatus;

            return viewModel;
        }

        private string GetUserId()
        {
            return Request?.GetUserId() ?? "default";
        }

        #endregion
    }
}
```

**Acceptance Criteria:**
- [ ] Wizard GET endpoint returns populated WizardViewModel
- [ ] Wizard POST endpoint saves configuration
- [ ] Content Management GET endpoint returns populated ContentManagementViewModel
- [ ] Content Management action endpoints handle button clicks
- [ ] My Library GET endpoint returns per-user MyLibraryViewModel
- [ ] User ID extracted from request context

---

## Sprint 117 Completion Criteria

| Criteria | Status | Notes |
|-----------|--------|-------|
| BasePluginViewModel base class created | ⏸️ Pending | Common properties for all ViewModels |
| UI attributes created (TabGroup, DataGrid, RunButton, etc.) | ⏸️ Pending | In Emby.Plugin.UI.Attributes namespace |
| WizardViewModel created with all setup fields | ⏸️ Pending | API key, library paths, sync settings |
| ContentManagementViewModel created with all tabs | ⏸️ Pending | Sources, Collections, Items, Needs Review, Actions |
| MyLibraryViewModel created with per-user tabs | ⏸️ Pending | Saved, Blocked, Watch History |
| Row models created (SourceRow, CollectionRow, ItemRow) | ⏸️ Pending | For DataGrid display |
| Plugin.GetPages() returns all three pages | ⏸️ Pending | Wizard, Content Management, My Library |
| ConfigurationController loads ViewModels | ⏸️ Pending | GET endpoints for each page |
| ConfigurationController saves ViewModels | ⏸️ Pending | POST endpoints for each page |
| ConfigurationController handles button clicks | ⏸️ Pending | Sync, Purge Cache, Reset Database, etc. |
| Build succeeds | ⏸️ Pending | 0 warnings, 0 errors |
| E2E: UI works in Emby browser | ⏸️ Pending | Three pages render correctly |

---

## Sprint 117 Notes (Declarative UI)

**Three-Tier Architecture:**
- **Wizard** — Re-entrant setup page for all users
- **Content Management** — Admin-only page for sources, collections, items, actions
- **My Library** — Per-user page for saved, blocked, watch history

**Declarative UI Pattern:**
- No custom HTML/JS/CSS
- UI generated automatically from ViewModels with attributes
- Attributes: `TabGroup`, `DataGrid`, `RunButton`, `Dangerous`, `FilterOptions`, `ReadOnly`, `Required`, `DisplayName`, `Description`
- Base class: `BasePluginViewModel` extends `BasePluginConfiguration`

**Row Models:**
- `SourceRow` — Data row for Sources grid
- `CollectionRow` — Data row for Collections grid
- `ItemRow` — Data row for Items grid
- `WatchHistoryRow` — Data row for Watch History grid

**Permissions:**
- Wizard: All users (re-entrant)
- Content Management: Admin only
- My Library: Per-user

**Database Integration:**
- Controllers load data from DatabaseManager
- Controllers save data to DatabaseManager
- ViewModels are DTOs for UI display

**Emby Integration:**
- `Plugin.GetPages()` registers the three pages
- `PluginConfiguration` stores persisted settings
- `IRequiresRequest` provides request context for user ID

**Next Steps:**
1. Create BasePluginViewModel.cs
2. Create all UI attributes in Configuration/Attributes/
3. Create all ViewModels (WizardViewModel, ContentManagementViewModel, MyLibraryViewModel)
4. Create all row models (SourceRow, CollectionRow, ItemRow, WatchHistoryRow)
5. Update Plugin.cs to register pages
6. Create ConfigurationController.cs
7. Test UI in Emby browser
