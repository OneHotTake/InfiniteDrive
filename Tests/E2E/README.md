# E2E Test Plan for v3.3

## Test Scenarios

### 1. Fresh Installation
- [ ] Plugin loads without errors
- [ ] Database initializes (schema v3)
- [ ] Libraries created:
  - `/embystreams/library/movies/` (TMDB/IMDB)
  - `/embystreams/library/series/` (TMDB/IMDB)
  - `/embystreams/library/anime/` (AniList/AniDB)
- [ ] Libraries hidden from navigation panel for existing users
- [ ] Install notice displayed on first visit
- [ ] Admin UI renders correctly
- [ ] Status badge shows "ok" (NOT "manifest")

### 2. Sync Pipeline
- [ ] User adds source (Trakt or MdbList)
- [ ] User triggers sync
- [ ] Manifest fetched
- [ ] Items processed
- [ ] Items appear in correct library (movies/, series/, or anime/)
- [ ] Status badge shows "ok"

### 3. Playback (Ranked Fallback)
- [ ] User plays item
- [ ] Stream resolves (try cache primary → cache secondary → live)
- [ ] Playback succeeds
- [ ] URL signed correctly
- [ ] Cache updated by PlaybackEventSubscriptionService (NOT in GetStreamUrlAsync)

### 4. Save/Block Actions
- [ ] User saves item
- [ ] Item marked as saved=true (boolean column)
- [ ] User unsaves item with enabled source
- [ ] Item remains Active (not Deleted)
- [ ] User unsaves item without enabled source
- [ ] Item remains Active (not Deleted)
- [ ] User blocks item
- [ ] Item marked as blocked=true (boolean column)
- [ ] User unblocks item
- [ ] Item returns to Active (not Deleted)

### 5. Your Files Detection
- [ ] User adds local file
- [ ] Plugin detects match by IMDB/TMDB/TVDB/AniList/AniDB/Kitsu
- [ ] Item marked as superseded=true (NOT saved)
- [ ] If saved + enabled source: superseded_conflict=true

### 6. Collection Management
- [ ] User enables ShowAsCollection
- [ ] Collection created via ICollectionManager
- [ ] Items synced to collection
- [ ] IsLocked = false
- [ ] Orphaned collections emptied (not deleted)

### 7. Removal (Grace Period)
- [ ] Item removed from manifest
- [ ] Item marked with grace_started_at (not Deleted)
- [ ] After 7 days, if no enabled source: Deleted
- [ ] .strm file deleted
- [ ] Item removed from Emby
- [ ] Coalition rule checked (single JOIN query)

## Test Data

Use these test items:
- IMDB: tt123456 - Test Movie 1 (2024)
- IMDB: tt234567 - Test Movie 2 (2023)
- IMDB: tt345678 - Test Series (2022)
- AniList: 12345 - Test Anime (2024)

## Test Environment

- Emby Server: v4.10.0.8 (beta)
- EmbyStreams: v3.3
- SQLite: Latest version

## Breaking Change Note

Per v3.3 spec §17:
- NO migration from v20
- Fresh database initialization only
- Manual reset via Danger Zone UI
- Three separate libraries: movies/, series/, anime/
- TEXT UUID primary keys (not int)
- Boolean columns for saved/blocked (not status enum)

## Automated Tests

### Test Infrastructure
- TestBase class for common setup/teardown
- Uses Emby LoggerFactory (not MEL Mock<ILogger>)
- Unique test database per test run

### Test Files
- SyncPipelineTests.cs - Full sync flow tests
- PlaybackTests.cs - Stream resolution and playback tests
- UserActionTests.cs - Save/Block/Unsave/Unblock tests
- YourFilesTests.cs - Your Files detection tests

### Running Tests

Tests are simple static test methods that can be run individually:

```csharp
// Example: Run all sync pipeline tests
await SyncPipelineTests.TestFetchesManifest();
await SyncPipelineTests.TestCreatesSource();
await SyncPipelineTests.TestDeletesSource();
await SyncPipelineTests.TestItemsQuery();

// Example: Run playback tests
await PlaybackTests.TestStreamUrlSigning();
await PlaybackTests.TestCacheMissBehavior();
await PlaybackTests.TestCacheHitBehavior();
await PlaybackTests.TestCacheExpiration();

// Example: Run user action tests
await UserActionTests.TestSaveItem();
await UserActionTests.TestUnsaveItem();
await UserActionTests.TestBlockItem();
await UserActionTests.TestUnblockItem();

// Example: Run Your Files tests
await YourFilesTests.TestSetSuperseded();
await YourFilesTests.TestClearSuperseded();
await YourFilesTests.TestSetSupersededConflict();
```

Each test returns a string result:
- "PASS: ..." for successful tests
- "FAIL: ..." for failed tests with details
