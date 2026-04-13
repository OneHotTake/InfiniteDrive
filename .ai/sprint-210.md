# Sprint 210 — User Discover UI (Proper)

**Version:** v1.0 | **Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 209 (parental filtering) | **Target:** v0.51 | **PR:** TBD

---

## Overview

**Problem statement:** The current `InfiniteDriveChannel` is a read-only, dead-end folder browser. Users can navigate Lists and Saved folders but cannot search, add items, remove items, refresh lists, or do anything meaningful. Channels are inherently passive — Emby renders them as static folder hierarchies, and the plugin cannot add interactive UI (search bars, action buttons). The Discover REST APIs exist and are un-gated (Sprint 204), but there's no UI that calls them.

**Why now:** Sprint 209 will complete parental filtering. The core user-facing feature — Discover — has no functional UI. Users have no way to actually use the plugin's primary value proposition: browsing a streaming catalog and adding items to their library. The current channel is "extra dead code" that serves no purpose.

**High-level approach:** Build a proper user-facing Discover UI as an Emby plugin page with three tabs (Discover, My Picks, My Lists). This page will be accessible to all authenticated users via Emby's web interface. Document that this UI is web-only (native Emby apps will not have this feature).

### What the Research Found

**Emby plugin pages vs Channels:**
| Aspect | Plugin Page | Channel |
|---------|-------------|----------|
| Full UI control | ✅ HTML/JS completely custom | ❌ Emby renders static folders |
| Interactive elements | ✅ Search bars, buttons, forms | ❌ Read-only browsing |
| User authentication | ✅ Full access to user context | ✅ Has user context |
| Mobile app support | ❌ Only in Emby web | ✅ Works in all clients |
| Navigation integration | ❌ Manual URL or link | ✅ Sidebar entry |

**Trade-off:** Building a plugin page gives full UI control but means the feature only works in Emby's web interface. Native apps (Android, iOS, smart TV) won't see or use this UI. This is an acceptable trade-off for now — the alternative is no UI at all.

**Emby plugin page routing:** Plugin pages are registered via `IHasWebPages.GetPages()` and accessed via URLs like `/web/index.html#!/discover` or `/web/configurationpage?name=InfiniteDiscover`. They have full access to Emby's authentication and user context.

**Existing APIs ready to use:**
- ✅ `GET /InfiniteDrive/Discover/Browse` — Browse catalog
- ✅ `GET /InfiniteDrive/Discover/Search?q=` — Search
- ✅ `GET /InfiniteDrive/Discover/Detail?imdbId=` — Item details
- ✅ `POST /InfiniteDrive/Discover/AddToLibrary` — Add to library
- ✅ `POST /InfiniteDrive/Discover/RemoveFromLibrary` — Remove from saves
- ✅ `GET /InfiniteDrive/User/Pins` — Get user's saved/pinned items
- ✅ `GET /InfiniteDrive/User/Catalogs` — Get user's catalogs (Sprint 158)

---

## Breaking Changes

- **None.** This is purely additive — a new user-facing page.
- **InfiniteDriveChannel** will be deprecated but not deleted (for backward compatibility).

---

## Non-Goals

- Mobile app UI (web-only trade-off is documented)
- Making channels interactive (SDK limitation)
- Replacing Emby's built-in library views
- Per-user library separation (shared library is the model)

---

## Phase A — Plugin Page Infrastructure

### FIX-210A-01: Register Discover page in Plugin.cs

**File:** `Plugin.cs` (modify)
**Estimated effort:** XS
**What:**

Add new plugin page entry to `GetPages()`:

```csharp
new PluginPageInfo
{
    Name = "InfiniteDiscover",
    EmbeddedResourcePath = "InfiniteDrive.Configuration.discoverpage.html",
    IsMainConfigPage = false,  // Not main config — this is user-facing
    EnableInMainMenu = false,  // Not in admin menu — direct access only
    DisplayName = "Discover"
}
```

Add corresponding resource in `.csproj`:

```xml
<EmbeddedResource Include="Configuration\discoverpage.html" />
<EmbeddedResource Include="Configuration\discoverpage.js" />
```

### FIX-210A-02: Create discoverpage.html skeleton

**File:** `Configuration/discoverpage.html` (create)
**Estimated effort:** S
**What:**

Create a full-page Emby-compatible HTML file with:
- Three-tab navigation (Discover, My Picks, My Lists)
- Search bar (visible in Discover tab)
- Grid container for content cards
- Loading indicators
- Error message containers
- Emby theming integration (uses Emby's CSS variables)
- Responsive layout for mobile/desktop

HTML structure:

```html
<div id="InfiniteDiscoverPage" data-role="page" class="page type-interior userPage"
     data-require="emby-input,emby-button,emby-select,emby-scrollpanel"
     data-controller="__plugin/InfiniteDiscoverJS">
  <div class="content-primary">

    <div class="id-header">
      <h1 class="sectionTitle">Discover</h1>
      <div class="id-user-info">
        <span id="id-welcome">Welcome back</span>
      </div>
    </div>

    <!-- Tab Navigation -->
    <div class="id-tabs">
      <button class="id-tab active" data-tab="discover">Discover</button>
      <button class="id-tab" data-tab="picks">My Picks</button>
      <button class="id-tab" data-tab="lists">My Lists</button>
    </div>

    <!-- Tab Content Containers -->
    <div class="id-tab-content">
      <div id="id-tab-discover" class="id-tab-panel active">
        <!-- Search Bar -->
        <div class="id-search">
          <input type="search" id="id-search-input" placeholder="Search movies and shows..." />
          <button id="id-search-btn">Search</button>
        </div>
        <!-- Browse Grid -->
        <div id="id-discover-grid" class="id-grid"></div>
        <!-- Pagination -->
        <div id="id-pagination" class="id-pagination"></div>
      </div>

      <div id="id-tab-picks" class="id-tab-panel">
        <div id="id-picks-grid" class="id-grid"></div>
        <div id="id-picks-empty" class="id-empty" style="display:none">
          <p>You haven't saved any items yet.</p>
          <button id="id-browse-discover">Browse Discover</button>
        </div>
      </div>

      <div id="id-tab-lists" class="id-tab-panel">
        <div class="id-lists-toolbar">
          <button id="id-add-list-btn">Add List</button>
          <button id="id-refresh-all-btn">Refresh All</button>
        </div>
        <div id="id-lists-grid" class="id-lists-grid"></div>
      </div>
    </div>

    <!-- Item Detail Modal -->
    <div id="id-detail-modal" class="id-modal" style="display:none">
      <div class="id-modal-content">
        <button class="id-modal-close" id="id-modal-close">×</button>
        <div class="id-modal-body">
          <div id="id-detail-poster"></div>
          <div id="id-detail-info">
            <h2 id="id-detail-title"></h2>
            <p id="id-detail-meta"></p>
            <p id="id-detail-overview"></p>
            <div id="id-detail-rating"></div>
            <div id="id-detail-certification"></div>
          </div>
          <div class="id-modal-actions">
            <button id="id-detail-add-btn">Add to Library</button>
            <button id="id-detail-remove-btn" style="display:none">Remove from Library</button>
          </div>
        </div>
      </div>
    </div>

    <!-- Add List Modal -->
    <div id="id-add-list-modal" class="id-modal" style="display:none">
      <div class="id-modal-content">
        <button class="id-modal-close" id="id-add-list-close">×</button>
        <h3>Add New List</h3>
        <div class="id-form-group">
          <label>List Name</label>
          <input type="text" id="id-list-name-input" placeholder="My Awesome List" />
        </div>
        <div class="id-form-group">
          <label>RSS URL (Trakt or MDBList)</label>
          <input type="url" id="id-list-url-input" placeholder="https://trakt.tv/users/..." />
        </div>
        <div id="id-add-list-error" class="id-error"></div>
        <button id="id-add-list-submit">Add List</button>
      </div>
    </div>

  </div>
</div>
```

### FIX-210A-03: Add discoverpage.css

**File:** `Configuration/discoverpage.css` (create)
**Estimated effort:** S
**What:**

Styles for tabs, grids, cards, modals, search bar. Use Emby's CSS variables for theming.

### FIX-210A-04: Create discoverpage.js skeleton

**File:** `Configuration/discoverpage.js` (create)
**Estimated effort:** M
**What:**

Create JavaScript controller with:
- Tab switching logic
- API client wrapper for all InfiniteDrive endpoints
- State management (current tab, search query, pagination)
- Modal open/close handlers
- Error handling and loading states

---

## Phase B — Discover Tab Implementation

### FIX-210B-01: Implement Browse functionality

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** S
**What:**

On page load (Discover tab active):
- Fetch initial browse results via `GET /InfiniteDrive/Discover/Browse?limit=50&offset=0`
- Render cards in grid with posters, titles, years, ratings
- Show pagination controls if total > limit
- Implement "Load More" button

Card template:
```javascript
function renderCard(item) {
  return `
    <div class="id-card" data-imdb="${item.imdbId}" data-type="${item.mediaType}">
      <div class="id-card-poster">
        <img src="${item.posterUrl}" alt="${item.title}" loading="lazy" />
      </div>
      <div class="id-card-info">
        <h3>${item.title}</h3>
        <span class="id-card-year">${item.year || ''}</span>
        <span class="id-card-rating">${item.imdbRating ? '★ ' + item.imdbRating.toFixed(1) : ''}</span>
        ${item.certification ? `<span class="id-card-cert">${item.certification}</span>` : ''}
        ${item.inLibrary ? '<span class="id-card-in-lib">In Library</span>' : ''}
      </div>
    </div>
  `;
}
```

### FIX-210B-02: Implement Search functionality

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** S
**What:**

On search:
- Debounce search input (300ms delay)
- Call `GET /InfiniteDrive/Discover/Search?q=${query}&limit=50`
- Display loading state while fetching
- Render results in same grid as browse
- Show "No results" message if empty

```javascript
let searchTimeout;
document.getElementById('id-search-input').addEventListener('input', (e) => {
  clearTimeout(searchTimeout);
  searchTimeout = setTimeout(() => {
    const query = e.target.value.trim();
    if (query.length >= 2) {
      performSearch(query);
    }
  }, 300);
});
```

### FIX-210B-03: Implement Item Detail modal

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** S
**What:**

On card click:
- Call `GET /InfiniteDrive/Discover/Detail?imdbId=${imdbId}`
- Populate modal with full metadata (poster, title, year, overview, genres, rating, certification)
- Show "Add to Library" button (or "Remove" if already saved)
- Handle close button and backdrop click

### FIX-210B-04: Implement Add/Remove from Library

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** S
**What:**

Add to Library:
- Call `POST /InfiniteDrive/Discover/AddToLibrary?imdbId=${imdbId}&type=${type}&title=${encodeURIComponent(title)}&year=${year}`
- Show loading state on button
- On success: close modal, refresh grid, show toast "Added to library"
- On error: show error in modal, keep open

Remove from Library:
- Call `POST /InfiniteDrive/Discover/RemoveFromLibrary?imdbId=${imdbId}`
- Update UI to show "Add" button instead of "Remove"
- Show toast "Removed from library"

---

## Phase C — My Picks Tab Implementation

### FIX-210C-01: Implement My Picks display

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** S
**What:**

When My Picks tab activated:
- Call `GET /InfiniteDrive/User/Pins`
- Render saved items in grid format
- Show "empty" state if no items
- Each card has "Remove" button (in-grid action, not just modal)

Remove action:
- Call `POST /InfiniteDrive/Discover/RemoveFromLibrary`
- Remove card from grid with animation
- Show toast "Removed from picks"

---

## Phase D — My Lists Tab Implementation

### FIX-210D-01: Implement My Lists display

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** S
**What:**

When My Lists tab activated:
- Call `GET /InfiniteDrive/User/Catalogs`
- Render each list as a card showing:
  - List name (display_name)
  - Service icon (Trakt / MDBList)
  - Item count
  - Last synced date
  - Refresh button (per-list)
  - Remove button

### FIX-210D-02: Implement Add List functionality

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** M
**What:**

On "Add List" click:
- Open modal showing name and URL input fields
- On submit:
  - Validate URL is a Trakt or MDBList RSS feed
  - Call `POST /InfiniteDrive/User/Catalogs/Add?url=${encodeURIComponent(url)}&name=${encodeURIComponent(name)}`
  - Show loading state
  - On success: close modal, refresh lists, show toast
  - On error: show error in modal

### FIX-210D-03: Implement Refresh functionality

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** S
**What:**

Per-list refresh:
- Call `POST /InfiniteDrive/User/Catalogs/Refresh?catalogId=${catalogId}`
- Show spinner on list card
- On response: update item count, show toast with "Added X, updated Y"

Refresh All:
- Iterate over all lists, calling refresh for each
- Show global loading state
- Show summary toast when all complete

### FIX-210D-04: Implement Remove List functionality

**File:** `Configuration/discoverpage.js` (modify)
**Estimated effort:** XS
**What:**

On remove list click:
- Confirm with dialog (or button double-click)
- Call `POST /InfiniteDrive/User/Catalogs/Remove?catalogId=${catalogId}`
- Remove list card with animation
- Show toast "List removed"

---

## Phase E — Deprecate InfiniteDriveChannel

### FIX-210E-01: Mark InfiniteDriveChannel as obsolete

**File:** `Services/InfiniteDriveChannel.cs` (modify)
**Estimated effort:** XS
**What:**

Add obsolete documentation attribute:

```csharp
/// <summary>
/// Emby channel exposing user's Lists and Saved items.
/// Auto-discovered by Emby — no Plugin.cs registration needed.
///
/// DEPRECATED: Use the user-facing Discover page (/web/#!/discover) instead.
/// This channel provides read-only folder browsing with no interactive capabilities.
/// Left in place for backward compatibility, but users should use the Discover UI.
/// </summary>
[Obsolete("Use the user-facing Discover page instead. This channel provides only read-only browsing.", error: false)]
public class InfiniteDriveChannel : IChannel
```

---

## Phase F — Build & Verification

### FIX-210F-01: Build

**File:** Project root
**Estimated effort:** XS
**What:**

```
dotnet build -c Release
```

Expected: 0 errors, 0 warnings.

### FIX-210F-02: Grep checklist

| Pattern | Expected | Notes |
|---|---|---|
| `InfiniteDiscover` in Plugin.cs | 1 | New page registration |
| `discoverpage.html` in .csproj | 1 | Embedded resource |
| `discoverpage.js` referenced | 1 | Controller binding |
| `GET /InfiniteDrive/Discover/Browse` called in JS | ≥1 | Browse API usage |
| `GET /InfiniteDrive/Discover/Search` called in JS | ≥1 | Search API usage |
| `GET /InfiniteDrive/Discover/Detail` called in JS | ≥1 | Detail API usage |
| `POST /InfiniteDrive/Discover/AddToLibrary` called in JS | ≥1 | Add API usage |
| `GET /InfiniteDrive/User/Pins` called in JS | ≥1 | Picks API usage |
| `GET /InfiniteDrive/User/Catalogs` called in JS | ≥1 | Lists API usage |
| `Obsolete` attribute on InfiniteDriveChannel | 1 | Deprecation marker |

### FIX-210F-03: Manual test — Discover tab

1. Navigate to `/web/index.html#!/discover` (or however Emby routes to plugin pages)
2. Verify page loads with Emby theming applied
3. Verify all three tabs are present and clickable
4. Test Browse: Scroll through results, verify posters load, verify pagination works
5. Test Search: Type "batman", verify results appear, verify debouncing works
6. Test Detail: Click a card, verify modal opens with full metadata
7. Test Add: Click "Add to Library", verify button shows loading, verify toast appears, verify card shows "In Library"
8. Test Remove: On an added item, click again, verify "Remove" button appears and works
9. Verify parental filtering works (if Sprint 209 is complete) — restricted users should not see R-rated items

### FIX-210F-04: Manual test — My Picks tab

1. Switch to "My Picks" tab
2. Verify saved items appear
3. Verify each item shows "Remove" button
4. Test remove action, verify item disappears and toast appears
5. Test empty state: Remove all picks, verify empty message and "Browse Discover" button

### FIX-210F-05: Manual test — My Lists tab

1. Switch to "My Lists" tab
2. Verify existing user catalogs appear
3. Test "Add List" with a valid Trakt RSS URL
4. Verify new list appears in grid
5. Test "Refresh" on a single list, verify counts update
6. Test "Refresh All", verify all lists update
7. Test "Remove" on a list, verify confirmation and removal

---

## Documentation

### FIX-210D-01: Create UI documentation

**File:** `docs/USER_DISCOVER_UI.md` (create)
**Estimated effort:** S
**What:**

Document the new Discover UI for users:

```markdown
# InfiniteDrive Discover UI

## Accessing Discover

**Web Only:** The Discover UI is available in Emby's web interface at:
- Direct URL: `http://your-emby-server/web/index.html#!/discover`
- From sidebar: Click InfiniteDrive → Discover (if integrated)

**Important:** This UI is **not available in Emby's mobile apps** (Android, iOS, smart TV).
You must use the web browser to access the full Discover experience.

## What You Can Do

### Discover Tab
- **Browse** the full streaming catalog with posters, ratings, and certifications
- **Search** for movies and shows by title
- **View details** including synopsis, genres, year, rating
- **Add to Library** with one click (creates .strm file in your library)

### My Picks Tab
- **View** all items you've saved
- **Remove** items from your picks
- Items appear here after adding from Discover

### My Lists Tab
- **Subscribe** to public Trakt and MDBList RSS feeds
- **View** your custom lists with item counts
- **Refresh** lists to sync latest changes
- **Remove** lists you no longer need

## Parental Controls

If your Emby account has a parental rating limit:
- Items above your limit are **not shown**
- Unrated content may be hidden (based on server settings)
- This applies to all tabs (Discover, My Picks, My Lists)

## Adding to Your Library

When you add an item to your library:
1. A .strm file is created in your configured media path
2. The item appears in Emby's main Movies or TV Shows library
3. You can play it like any other library item
4. Playback uses your configured debrid service (Real-Debrid, etc.)

## Troubleshooting

### Discover page doesn't load
- Verify you're logged into Emby
- Try accessing directly via the URL
- Check browser console for errors

### Can't add to library
- Verify your media paths are configured in admin settings
- Check Emby server logs for errors

### Lists not refreshing
- Verify RSS URL is publicly accessible
- Check that list is still active on Trakt/MDBList
```

### FIX-210D-02: Update README.md

**File:** `README.md` (modify)
**Estimated effort:** XS
**What:**

Add section about Discover UI:

```markdown
## User Interface

InfiniteDrive provides a user-facing **Discover UI** accessible via Emby's web interface.

### Access
- **Web:** Available at `/web/index.html#!/discover` or via sidebar integration
- **Mobile apps:** Not supported — use web browser for full Discover experience

### Features
- Browse streaming catalog with posters, ratings, and parental filtering
- Search for movies and shows
- Add items to your library with one click
- Manage saved picks and custom lists
- Subscribe to Trakt and MDBList RSS feeds
```

---

## Rollback Plan

- `git revert` the sprint commit.
- New discover page files (`discoverpage.html`, `.js`, `.css`) are cleanly removed.
- `InfiniteDriveChannel` `[Obsolete]` attribute removed — channel continues to work.
- No database changes — safe to rollback.

---

## Completion Criteria

- [ ] `InfiniteDiscover` plugin page registered in Plugin.cs
- [ ] `discoverpage.html` with three tabs (Discover, My Picks, My Lists)
- [ ] `discoverpage.js` with all API calls (Browse, Search, Detail, Add/Remove, Pins, Catalogs)
- [ ] Search bar with debouncing
- [ ] Item detail modal with full metadata
- [ ] Add/Remove from Library functionality
- [ ] My Picks display and management
- [ ] My Lists display, add, refresh, remove
- [ ] InfiniteDriveChannel marked as `[Obsolete]`
- [ ] `docs/USER_DISCOVER_UI.md` created with web-only documentation
- [ ] README.md updated with UI section
- [ ] `dotnet build -c Release` — 0 errors, 0 warnings
- [ ] Manual tests pass for all three tabs
- [ ] Parental filtering works (if Sprint 209 complete)

---

## Open Questions / Blockers

| # | Question | Owner | Status |
|---|---|---|
| 1 | What is the correct URL for accessing plugin pages in Emby? (Need to verify Emby routing) | — | — |
| 2 | Should Discover page be integrated into existing admin config as a separate tab, or standalone? | — | — |
| 3 | What Emby auth mechanism does plugin pages use? (Need to verify API for getting current user) | — | — |

---

## Notes

**Files created:** 3
- `Configuration/discoverpage.html`
- `Configuration/discoverpage.js`
- `Configuration/discoverpage.css`
- `docs/USER_DISCOVER_UI.md`

**Files modified:** 3
- `Plugin.cs` — Add InfiniteDiscover page registration
- `Services/InfiniteDriveChannel.cs` — Add `[Obsolete]` attribute
- `InfiniteDrive.csproj` — Add embedded resources
- `README.md` — Add UI section

**Files deleted:** 0

**Risk:** MEDIUM — New UI development, web-only trade-off documented clearly.

**Mitigated by:**
1. All required APIs already exist and tested
2. InfiniteDriveChannel kept for backward compatibility
3. Web-only limitation explicitly documented
4. Incremental implementation — each tab can be tested independently
5. Error handling at every API call
6. Loading states for all async operations

---

## Design Decision: Web-Only Trade-off

The Discover UI is intentionally **web-only**. This is a deliberate trade-off:

| Factor | Chosen Path | Reason |
|---------|-------------|--------|
| UI Control | Plugin page | Need full interactivity (search, forms, actions) |
| Mobile Support | Not available | Emby channels can't provide rich UI; plugin pages are web-only |
| User Experience | Rich, interactive | Search, filters, modals, real-time feedback |

This means:
- ✅ **Web users** get full Discover experience
- ❌ **Mobile app users** only see basic channel (deprecated, read-only)
- 📖 **Documentation clearly states** this limitation

Alternative (future): Build a native plugin integration for each Emby mobile platform. This is significantly more work and outside Sprint 210 scope.
