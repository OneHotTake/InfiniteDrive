# InfiniteDrive Discover UI

## Accessing Discover

**Web Only:** The Discover UI is available in Emby's web interface at:
- Direct URL: `http://your-emby-server/web/configurationpage?name=InfiniteDiscover`
- From admin settings: Settings → Plugins → InfiniteDrive → Discover (when logged in as admin)
- Direct navigation: Navigate to `/web/configurationpage?name=InfiniteDiscover`

**Important:** This UI is **not available in Emby's mobile apps** (Android, iOS, smart TV).
You must use the web browser to access the full Discover experience.

## What You Can Do

### Discover Tab

Browse the full streaming catalog with posters, ratings, and certifications.

**Features:**
- **Browse** the catalog with pagination
- **Search** for movies and shows by title (search auto-triggers after 300ms of inactivity)
- **View details** including synopsis, genres, year, rating, and certification
- **Add to Library** with one click (creates .strm file in your library)
- **Remove from Library** from the detail modal

**Card Information:**
- Poster image
- Title
- Year of release
- IMDb rating (gold star)
- Content certification (e.g., PG-13, R)
- "In Library" badge (if already added)

### My Picks Tab

View and manage all items you've saved.

**Features:**
- **View** all items you've added to your library
- **Remove** items from your picks with the in-card "Remove" button
- **Open details** by clicking on the card (same modal as Discover tab)
- Items appear here automatically after adding from Discover tab

**Empty State:**
If you haven't saved any items, you'll see a message with a "Browse Discover" button to help you get started.

### My Lists Tab

Subscribe to and manage public Trakt and MDBList RSS feeds.

**Features:**
- **View** your custom lists with:
  - List name
  - Service icon (🎬 for Trakt, 📋 for MDBList)
  - Item count
  - Last sync date
  - Refresh button (per-list)
  - Remove button
- **Add List** by providing:
  - A name for the list
  - A valid Trakt or MDBList RSS URL
- **Refresh** individual lists to sync latest changes
- **Refresh All** to update all lists at once
- **Remove** lists you no longer need

**Valid RSS Sources:**
- Trakt: `https://trakt.tv/users/username/lists/listname/rss`
- MDBList: `https://mdblist.com/rss/list/...`

**Empty State:**
If you don't have any custom lists, you'll see a message with an "Add Your First List" button.

## Parental Controls

If your Emby account has a parental rating limit configured:
- Items above your limit are **not shown** in Browse or Search
- Unrated content may be hidden (based on server settings)
- This applies to all tabs (Discover, My Picks, My Lists)

Parental filtering is enforced on the server side — users cannot bypass it by modifying the UI.

## Adding to Your Library

When you add an item to your library from the Discover UI:

1. A `.strm` file is created in your configured media path:
   - Movies go to `SyncPathMovies` (admin setting)
   - TV Shows go to `SyncPathShows` or `SyncPathAnime` (admin setting)

2. The item appears in Emby's main Movies or TV Shows library after a library scan

3. You can play it like any other library item

4. Playback uses your configured debrid service (Real-Debrid, Premiumize, etc.)

5. The item is added to your "My Picks" tab automatically

## Keyboard Shortcuts

- **Tab**: Navigate between tabs
- **Enter**: Submit forms (Add List, Search)
- **Escape**: Close modals
- **Arrow keys**: Navigate within form inputs

## Troubleshooting

### Discover page doesn't load

**Symptoms:** Page shows blank or error message.

**Solutions:**
- Verify you're logged into Emby (the UI requires authentication)
- Try accessing directly via the full URL: `/web/configurationpage?name=InfiniteDiscover`
- Check browser console (F12) for JavaScript errors
- Ensure the plugin is enabled and the server has been restarted after installation

### Can't add to library

**Symptoms:** "Add to Library" button shows error or fails.

**Solutions:**
- Verify your media paths are configured in admin settings (Settings → Plugins → InfiniteDrive)
- Check that the configured directory exists and is writable by Emby
- Check Emby server logs for specific error messages
- Ensure you have the correct debrid service configured (Real-Debrid, etc.)

### Lists not refreshing

**Symptoms:** "Refresh" button fails or item counts don't update.

**Solutions:**
- Verify RSS URL is publicly accessible (test in a browser)
- Check that the list is still active on Trakt or MDBList
- Some feeds may rate limit; wait a few minutes and try again
- Check server logs for RSS parsing errors

### Search returns no results

**Symptoms:** Search query returns empty grid even for popular titles.

**Solutions:**
- Check spelling and try shorter search terms
- Some titles may not be available in the streaming catalog
- The search includes both local catalog and live AIOStreams queries
- If AIOStreams is experiencing issues, local results will still appear

### Images not loading

**Symptoms:** Posters show as broken images or empty placeholders.

**Solutions:**
- Check your network connection
- Image servers (TMDB, etc.) may be temporarily unavailable
- This doesn't affect playback — only UI display

### "In Library" badge not showing

**Symptoms:** You added an item but it doesn't show the "In Library" badge.

**Solutions:**
- Refresh the page (the status is cached)
- Check if the `.strm` file was actually created in your media path
- The badge reflects your personal saves, not whether the file exists

## Admin Configuration

The Discover UI uses server-level settings configured by administrators:

- **Media Paths**: Where `.strm` files are written (Movies, TV Shows, Anime)
- **Parental Controls**: Default rating limits and whether to hide unrated content
- **Debrid Service**: Real-Debrid, Premiumize, or other configured service

Regular users do not need to configure anything — just browse, search, and add to library.

## Privacy

- Your personal saves ("My Picks") are stored per-user
- Other users cannot see what you've saved
- Lists are also per-user (unless shared by admin configuration)
- Server logs may record your actions for troubleshooting

## Feedback and Support

If you encounter issues not covered here:

1. Check the [GitHub Issues](https://github.com/yourusername/InfiniteDrive/issues) page
2. Include your Emby version and plugin version
3. Provide browser console errors if available
4. Include relevant server log entries
