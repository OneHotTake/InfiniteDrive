# Sprint 118 — Home Screen Rails (v3.3 ContentSection API)

**Version:** v3.3 | **Status:** Blocked | **Risk:** LOW | **Depends:** Sprint 117

---

## Overview

Sprint 118 implements home screen rails using Emby's native ContentSection API and IUserManager. Rails provide easy access to saved items, trending content, and new releases directly on the Emby home screen.

**Key Architecture (v3.3 Spec §7.2):**
- Uses Emby ContentSection API (NOT custom RailProvider)
- Uses IUserManager for per-user rail management
- Uses Emby BoxSets as rail containers (NOT custom data structures)
- Per-user per-rail tracking via home_section_tracking table
- Marker pattern via Subtitle for stable section identity
- No JavaScript widget injection (NOT the old approach)

**Rail Types:**
- Saved rail: User's saved items (real Emby BoxSet named "Saved")
- Trending Movies rail: Trending movie items
- Trending Series rail: Trending series items
- New This Week rail: New movie releases
- Admin Chosen rail: Admin-curated content (custom)

---

## Phase 118A — HomeSectionTracker

### FIX-118A-01: Create HomeSectionTracker

**File:** `Services/HomeSectionTracker.cs`

```csharp
public class HomeSectionTracker
{
    private readonly IDatabaseManager _db;
    private readonly IUserManager _userManager;
    private readonly ILogger<HomeSectionTracker> _logger;

    // Rail types (match spec §7.2)
    public enum RailType
    {
        Saved,
        TrendingMovies,
        TrendingSeries,
        NewThisWeek,
        AdminChosen
    }

    // Section markers for stable identity (via Subtitle field)
    private static readonly Dictionary<RailType, string> SectionMarkers = new()
    {
        { RailType.Saved, "embystreams__saved" },
        { RailType.TrendingMovies, "embystreams__trending_movies" },
        { RailType.TrendingSeries, "embystreams__trending_series" },
        { RailType.NewThisWeek, "embystreams__new_this_week" },
        { RailType.AdminChosen, "embystreams__admin_chosen" }
    };

    public async Task InitializeRailForUserAsync(
        string userId,
        RailType railType,
        CancellationToken ct = default)
    {
        var marker = SectionMarkers[railType];

        // Check if rail already tracked for this user
        var existing = await _db.GetHomeSectionTrackingAsync(userId, railType, ct);
        if (existing != null)
        {
            _logger.LogDebug("Rail {RailType} already tracked for user {UserId}", railType, userId);
            return;
        }

        // Create home section tracking entry
        await _db.InsertHomeSectionTrackingAsync(new HomeSectionTracking
        {
            UserId = userId,
            RailType = railType.ToString().ToLower(),
            EmbySectionId = null, // Will be set after section creation
            SectionMarker = marker
        }, ct);

        _logger.LogInformation("Initialized rail {RailType} for user {UserId}", railType, userId);
    }

    public async Task TrackSectionIdAsync(
        string userId,
        RailType railType,
        string embySectionId,
        CancellationToken ct = default)
    {
        await _db.UpdateHomeSectionTrackingAsync(userId, railType, embySectionId, ct);
        _logger.LogDebug("Tracked section ID {SectionId} for rail {RailType}, user {UserId}",
            embySectionId, railType, userId);
    }

    public async Task<string?> GetSectionIdAsync(
        string userId,
        RailType railType,
        CancellationToken ct = default)
    {
        var tracking = await _db.GetHomeSectionTrackingAsync(userId, railType, ct);
        return tracking?.EmbySectionId;
    }

    public async Task InitializeForAllUsersAsync(CancellationToken ct = default)
    {
        var users = _userManager.GetUserList(new UserQuery { IsDisabled = false });

        foreach (var user in users)
        {
            foreach (RailType railType in Enum.GetValues<RailType>())
            {
                await InitializeRailForUserAsync(user.Id, railType, ct);
            }
        }

        _logger.LogInformation("Initialized rails for {Count} users", users.Count);
    }
}
```

**Acceptance Criteria:**
- [ ] Tracks per-user per-rail state
- [ ] Uses section markers for stable identity
- [ ] Initializes rails for all users
- [ ] Persists Emby section IDs for re-finding sections

---

## Phase 118B — ContentSectionProvider

### FIX-118B-01: Create ContentSectionProvider

**File:** `Services/ContentSectionProvider.cs`

```csharp
public class ContentSectionProvider : IContentSection
{
    private readonly HomeSectionTracker _tracker;
    private readonly IDatabaseManager _db;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ContentSectionProvider> _logger;

    public string Name => "EmbyStreams";
    public string Id => "embystreams";

    // Supported rail types
    public HomeSectionTracker.RailType[] SupportedRails => new[]
    {
        HomeSectionTracker.RailType.Saved,
        HomeSectionTracker.RailType.TrendingMovies,
        HomeSectionTracker.RailType.TrendingSeries,
        HomeSectionTracker.RailType.NewThisWeek,
        HomeSectionTracker.RailType.AdminChosen
    };

    public async Task<IEnumerable<ContentSectionList>> GetSectionsAsync(
        ContentSectionListQuery query,
        CancellationToken ct)
    {
        var userId = query.User.Id;
        var sections = new List<ContentSectionList>();

        foreach (var railType in SupportedRails)
        {
            try
            {
                var section = await GetOrCreateSectionAsync(userId, railType, ct);
                if (section != null)
                {
                    sections.Add(section);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create rail {RailType} for user {UserId}",
                    railType, userId);
            }
        }

        return sections;
    }

    private async Task<ContentSectionList?> GetOrCreateSectionAsync(
        string userId,
        HomeSectionTracker.RailType railType,
        CancellationToken ct)
    {
        var marker = HomeSectionTracker.SectionMarkers[railType];

        // Check if we already have a tracked section ID
        var trackedSectionId = await _tracker.GetSectionIdAsync(userId, railType, ct);
        if (!string.IsNullOrEmpty(trackedSectionId))
        {
            // Find section by marker (for server restart/reorder cases)
            var existingSection = await FindSectionByMarkerAsync(userId, marker, ct);
            if (existingSection != null)
            {
                await _tracker.TrackSectionIdAsync(userId, railType, existingSection.Id, ct);
                return existingSection;
            }
        }

        // Get items for this rail
        var items = await GetRailItemsAsync(railType, ct);
        if (items.Count == 0)
        {
            return null; // Don't show empty rails
        }

        // Create or update section
        var section = await CreateSectionAsync(userId, railType, marker, items, ct);
        await _tracker.TrackSectionIdAsync(userId, railType, section.Id, ct);

        return section;
    }

    private async Task<ContentSectionList?> FindSectionByMarkerAsync(
        string userId,
        string marker,
        CancellationToken ct)
    {
        // Query Emby's home sections for this user
        var sections = await _libraryManager.GetHomeSectionsAsync(userId, ct);

        return sections.FirstOrDefault(s =>
            s.Type == HomeSectionType.UserViews &&
            s.ProviderId == "embystreams" &&
            s.DisplayPreferences?.Subtitle == marker);
    }

    private async Task<ContentSectionList> CreateSectionAsync(
        string userId,
        HomeSectionTracker.RailType railType,
        string marker,
        List<BaseItem> items,
        CancellationToken ct)
    {
        var title = railType switch
        {
            HomeSectionTracker.RailType.Saved => "Saved",
            HomeSectionTracker.RailType.TrendingMovies => "Trending Movies",
            HomeSectionTracker.RailType.TrendingSeries => "Trending Series",
            HomeSectionTracker.RailType.NewThisWeek => "New This Week",
            HomeSectionTracker.RailType.AdminChosen => "Admin Chosen",
            _ => "EmbyStreams"
        };

        return new ContentSectionList
        {
            Id = $"embystreams_{railType.ToString().ToLower()}_{userId}",
            Type = HomeSectionType.UserViews,
            Name = title,
            ProviderId = "embystreams",
            DisplayPreferences = new DisplayPreferences
            {
                Subtitle = marker, // Stable identity for re-finding
                CustomStyle = "embystreams-rail"
            },
            Items = items.Take(20).ToList() // Limit to 20 items per rail
        };
    }

    private async Task<List<BaseItem>> GetRailItemsAsync(
        HomeSectionTracker.RailType railType,
        CancellationToken ct)
    {
        return railType switch
        {
            HomeSectionTracker.RailType.Saved => await GetSavedItemsAsync(ct),
            HomeSectionTracker.RailType.TrendingMovies => await GetTrendingMoviesAsync(ct),
            HomeSectionTracker.RailType.TrendingSeries => await GetTrendingSeriesAsync(ct),
            HomeSectionTracker.RailType.NewThisWeek => await GetNewThisWeekAsync(ct),
            HomeSectionTracker.RailType.AdminChosen => await GetAdminChosenAsync(ct),
            _ => new List<BaseItem>()
        };
    }

    private async Task<List<BaseItem>> GetSavedItemsAsync(CancellationToken ct)
    {
        // Get the "Saved" BoxSet (created by SavedBoxSetService)
        const string savedBoxSetMarker = "embystreams__saved";

        var savedBoxSet = await FindBoxSetByMarkerAsync(savedBoxSetMarker, ct);
        if (savedBoxSet == null)
        {
            return new List<BaseItem>();
        }

        return savedBoxSet.GetLinkedChildren().ToList();
    }

    private async Task<List<BaseItem>> GetTrendingMoviesAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT mi.* FROM media_items mi
            WHERE mi.status = 'active' AND mi.media_type = 'movie'
            ORDER BY mi.updated_at DESC
            LIMIT 20";

        var items = await _db.QueryAsync<MediaItem>(sql, ct);

        // Convert MediaItems to BaseItems (EmbyItemId)
        var baseItems = new List<BaseItem>();
        foreach (var item in items)
        {
            if (item.EmbyItemId != Guid.Empty)
            {
                var baseItem = _libraryManager.GetItemById(item.EmbyItemId);
                if (baseItem != null)
                {
                    baseItems.Add(baseItem);
                }
            }
        }

        return baseItems;
    }

    private async Task<List<BaseItem>> GetTrendingSeriesAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT mi.* FROM media_items mi
            WHERE mi.status = 'active' AND mi.media_type = 'series'
            ORDER BY mi.updated_at DESC
            LIMIT 20";

        var items = await _db.QueryAsync<MediaItem>(sql, ct);

        var baseItems = new List<BaseItem>();
        foreach (var item in items)
        {
            if (item.EmbyItemId != Guid.Empty)
            {
                var baseItem = _libraryManager.GetItemById(item.EmbyItemId);
                if (baseItem != null)
                {
                    baseItems.Add(baseItem);
                }
            }
        }

        return baseItems;
    }

    private async Task<List<BaseItem>> GetNewThisWeekAsync(CancellationToken ct)
    {
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);

        const string sql = @"
            SELECT mi.* FROM media_items mi
            WHERE mi.status = 'active' AND mi.created_at > @WeekAgo
            ORDER BY mi.created_at DESC
            LIMIT 20";

        var items = await _db.QueryAsync<MediaItem>(sql, new { WeekAgo = weekAgo }, ct);

        var baseItems = new List<BaseItem>();
        foreach (var item in items)
        {
            if (item.EmbyItemId != Guid.Empty)
            {
                var baseItem = _libraryManager.GetItemById(item.EmbyItemId);
                if (baseItem != null)
                {
                    baseItems.Add(baseItem);
                }
            }
        }

        return baseItems;
    }

    private async Task<List<BaseItem>> GetAdminChosenAsync(CancellationToken ct)
    {
        // Admin-chosen items are those with SaveReason = AdminOverride
        const string sql = @"
            SELECT mi.* FROM media_items mi
            WHERE mi.status = 'active' AND mi.save_reason = 'adminoverride'
            ORDER BY mi.updated_at DESC
            LIMIT 20";

        var items = await _db.QueryAsync<MediaItem>(sql, ct);

        var baseItems = new List<BaseItem>();
        foreach (var item in items)
        {
            if (item.EmbyItemId != Guid.Empty)
            {
                var baseItem = _libraryManager.GetItemById(item.EmbyItemId);
                if (baseItem != null)
                {
                    baseItems.Add(baseItem);
                }
            }
        }

        return baseItems;
    }

    private async Task<BoxSet?> FindBoxSetByMarkerAsync(string marker, CancellationToken ct)
    {
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemType.BoxSet },
            Recursive = true
        }).OfType<BoxSet>();

        return boxSets.FirstOrDefault(bs =>
            bs.DisplayPreferences?.Subtitle == marker &&
            bs.ProviderId == "embystreams");
    }
}
```

**Acceptance Criteria:**
- [ ] Implements IContentSection
- [ ] Creates all 5 rail types
- [ ] Uses marker pattern for stable identity
- [ ] Limits to 20 items per rail
- [ ] Returns empty rails (hidden) if no items
- [ ] All SQL status comparisons use lowercase string literals
- [ ] All SQL save_reason comparisons use lowercase string literals

---

## Phase 118C — DatabaseManager HomeSectionTracking Methods

### FIX-118C-01: Add HomeSectionTracking Methods to DatabaseManager

**File:** `Data/DatabaseManager.cs`

```csharp
public async Task<HomeSectionTracking?> GetHomeSectionTrackingAsync(
    string userId,
    HomeSectionTracker.RailType railType,
    CancellationToken ct = default)
{
    const string sql = @"
        SELECT * FROM home_section_tracking
        WHERE user_id = @UserId AND rail_type = @RailType";

    return await QueryFirstOrDefaultAsync<HomeSectionTracking>(sql,
        new { UserId = userId, RailType = railType.ToString().ToLower() }, ct);
}

public async Task InsertHomeSectionTrackingAsync(
    HomeSectionTracking tracking,
    CancellationToken ct = default)
{
    const string sql = @"
        INSERT INTO home_section_tracking (user_id, rail_type, section_marker, created_at, updated_at)
        VALUES (@UserId, @RailType, @SectionMarker, datetime('now'), datetime('now'))";

    await ExecuteAsync(sql, tracking, ct);
}

public async Task UpdateHomeSectionTrackingAsync(
    string userId,
    HomeSectionTracker.RailType railType,
    string embySectionId,
    CancellationToken ct = default)
{
    const string sql = @"
        UPDATE home_section_tracking
        SET emby_section_id = @EmbySectionId, updated_at = datetime('now')
        WHERE user_id = @UserId AND rail_type = @RailType";

    await ExecuteAsync(sql,
        new { UserId = userId, RailType = railType.ToString().ToLower(), EmbySectionId = embySectionId }, ct);
}

public async Task<List<HomeSectionTracking>> GetAllHomeSectionTrackingAsync(
    CancellationToken ct = default)
{
    const string sql = @"
        SELECT * FROM home_section_tracking
        ORDER BY user_id, rail_type";

    return await QueryAsync<HomeSectionTracking>(sql, ct);
}
```

**Acceptance Criteria:**
- [ ] Get tracking by user and rail type
- [ ] Insert new tracking entry
- [ ] Update tracking with Emby section ID
- [ ] Get all tracking entries

---

## Phase 118D — Plugin Registration

### FIX-118D-01: Register ContentSectionProvider in Plugin

**File:** `Plugin.cs`

```csharp
public class Plugin : BasePlugin, IHasWebPages
{
    private readonly HomeSectionTracker _sectionTracker;
    private readonly ContentSectionProvider _sectionProvider;

    public override async Task OnInstalledAsync(CancellationToken ct)
    {
        // Initialize home section tracking for all existing users
        await _sectionTracker.InitializeForAllUsersAsync(ct);

        _logger.LogInformation("EmbyStreams home screen rails initialized");
    }

    public override async Task OnUserAddedAsync(User user, CancellationToken ct)
    {
        // Initialize rails for new user
        foreach (HomeSectionTracker.RailType railType in Enum.GetValues<HomeSectionTracker.RailType>())
        {
            await _sectionTracker.InitializeRailForUserAsync(user.Id, railType, ct);
        }

        _logger.LogInformation("Initialized rails for new user {UserId}", user.Id);
    }
}
```

**Acceptance Criteria:**
- [ ] Registers ContentSectionProvider with Emby
- [ ] Initializes rails on plugin install
- [ ] Initializes rails for new users
- [ ] Uses IContentSection interface correctly

---

## Sprint 118 Dependencies

- **Previous Sprint:** 117 (Admin UI)
- **Blocked By:** Sprint 117
- **Blocks:** Sprint 119 (API Endpoints)

---

## Sprint 118 Completion Criteria

- [ ] HomeSectionTracker tracks per-user per-rail state
- [ ] ContentSectionProvider implements IContentSection
- [ ] All 5 rail types implemented
- [ ] Marker pattern used for stable identity
- [ ] DatabaseManager home_section_tracking methods implemented
- [ ] Plugin registration initializes rails
- [ ] Build succeeds
- [ ] E2E: Rails appear on Emby home screen

---

## Sprint 118 Notes

**Architecture Change (v3.3 Spec §7.2):**
- OLD approach (v20): Custom RailProvider + JavaScript widget injection
- NEW approach (v3.3): Emby ContentSection API + IUserManager
- This is a BREAKING CHANGE - completely different architecture

**Rail Types:**
- Saved: User's saved items (real Emby BoxSet named "Saved")
- Trending Movies: Trending movie items
- Trending Series: Trending series items
- New This Week: New movie releases (last 7 days)
- Admin Chosen: Admin-curated content (SaveReason = AdminOverride)

**Marker Pattern (Stable Identity):**
- Each rail has a marker string stored in Subtitle field
- Markers: embystreams__saved, embystreams__trending_movies, etc.
- Used to re-find sections after server restart or UI reordering
- Critical for per-user state persistence

**Per-User Per-Rail Tracking:**
- home_section_tracking table stores:
  - user_id: Emby user ID
  - rail_type: Rail type (saved, trending_movies, etc.)
  - emby_section_id: Emby-assigned section ID
  - section_marker: Stable marker string
- Unique constraint on (user_id, rail_type)

**No RecentlyResolved Rail:**
- Per spec §7.2, there is NO RecentlyResolved rail
- Old v20 approach had this rail, but it's removed in v3.3
- Focus on: Saved, Trending, New This Week, Admin Chosen

**Integration with Saved BoxSet:**
- Saved rail uses real Emby BoxSet named "Saved"
- BoxSet maintained by SavedBoxSetService (Sprint 113E)
- Marker: embystreams__saved (same as in SavedBoxSetService)
- This ensures Saved rail always reflects current saved items

**ContentSection API Requirements:**
- Implement IContentSection interface
- Return ContentSectionList objects
- Support per-user queries via ContentSectionListQuery
- Use DisplayPreferences.Subtitle for marker pattern
- Limit items per rail to 20 (Emby recommendation)

**No JavaScript Widget:**
- Do NOT inject JavaScript into Emby's home screen
- Do NOT create custom HTML elements
- All rail content delivered via ContentSection API
- Emby handles rendering, caching, and UI updates

**Status String Casing (CRITICAL):**
All SQL queries comparing status or save_reason values must use lowercase strings.
The v3.3 schema stores these as lowercase TEXT (e.g. 'active', 'failed', 'deleted',
'adminoverride'). Using Pascal case in SQL comparisons will silently return zero rows.
