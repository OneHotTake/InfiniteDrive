# Sprint 121 — E2E Validation (v3.3)

**Version:** v3.3 | **Status:** Complete ✓ | **Risk:** LOW | **Depends:** Sprint 120

---

## Overview

Sprint 121 implements comprehensive end-to-end testing for v3.3. This sprint validates all major flows and ensures fresh installation works correctly.

**Key Components:**
- Test Infrastructure - Test setup and helpers
- Sync Pipeline Tests - Full sync flow
- Playback Tests - Stream resolution and playback
- User Action Tests - Save/Block/Unsave/Unblock
- Your Files Tests - Your Files detection
- Collection Tests - Collection management

---

## Phase 121A — Test Infrastructure

### FIX-121A-01: Create Test Base Class

**File:** `Tests/TestBase.cs`

```csharp
[TestFixture]
public abstract class TestBase
{
    protected string TestDbPath { get; private set; } = null!;
    protected ILogger MockLogger { get; private set; } = null!;
    protected PluginConfiguration TestConfig { get; private set; } = null!;

    [SetUp]
    public void SetUp()
    {
        TestDbPath = Path.Combine(Path.GetTempPath(), $"embystreams_test_{Guid.NewGuid()}.db");
        // CRITICAL: Use Emby LoggerFactory, not MEL Mock<ILogger>
        MockLogger = LoggerFactory.CreateLogger<TestBase>();
        TestConfig = new PluginConfiguration();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(TestDbPath))
        {
            File.Delete(TestDbPath);
        }
    }

    protected DatabaseManager CreateTestDatabase()
    {
        var db = new DatabaseManager(Path.GetDirectoryName(TestDbPath)!, MockLogger);
        db.Initialise();
        return db;
    }

    protected MediaItem CreateTestItem(string mediaId = "imdb:tt123456")
    {
        return new MediaItem
        {
            MediaId = mediaId,
            Title = "Test Movie",
            Year = 2024,
            MediaType = "movie",
            Status = ItemStatus.Known,
            // CRITICAL: MediaItem.Id is string TEXT UUID, not int
            Id = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
```

**Acceptance Criteria:**
- [ ] Test database path unique per test
- [ ] Uses Emby LoggerFactory for logger (not MEL Mock<ILogger>)
- [ ] Test configuration
- [ ] Cleanup after each test
- [ ] Helper methods for common operations
- [ ] MediaItem.Id is string TEXT UUID (not int)

---

## Phase 121B — Sync Pipeline Tests

### FIX-121B-01: Test Full Sync Pipeline

**File:** `Tests/SyncPipelineTests.cs`

```csharp
[TestFixture]
public class SyncPipelineTests : TestBase
{
    [Test]
    public async Task FetchesManifest()
    {
        // Arrange
        var fetcher = new ManifestFetcher(new Mock<AioStreamsClient>().Object, MockLogger);

        // Act
        var manifest = await fetcher.FetchManifestAsync("http://example.com/manifest.json", CancellationToken.None);

        // Assert
        Assert.That(manifest, Is.Not.Null);
        Assert.That(manifest.Entries, Is.Not.Empty);
    }

    [Test]
    public async Task FiltersManifest()
    {
        // Arrange
        var filter = new ManifestFilter(TestConfig, new Mock<IDatabaseManager>().Object);
        var entries = new List<ManifestEntry>
        {
            new ManifestEntry { Id = "imdb:tt123456", Name = "Test Movie", Year = 2024 }
        };

        // Act
        var filtered = filter.FilterEntries(entries);

        // Assert
        Assert.That(filtered.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task DiffsManifest()
    {
        // Arrange
        var db = CreateTestDatabase();
        await db.UpsertMediaItemAsync(CreateTestItem("imdb:tt123456"), CancellationToken.None);

        var diff = new ManifestDiff(db);
        var entries = new List<ManifestEntry>
        {
            new ManifestEntry { Id = "imdb:tt123456", Name = "Test Movie", Year = 2024 },
            new ManifestEntry { Id = "imdb:tt234567", Name = "New Movie", Year = 2025 }
        };

        // Act
        var result = await diff.DiffAsync(entries, CancellationToken.None);

        // Assert
        Assert.That(result.NewItems.Count, Is.EqualTo(1)); // tt234567 is new
        Assert.That(result.RemovedItems.Count, Is.EqualTo(0)); // nothing removed
        Assert.That(result.ExistingItems.Count, Is.EqualTo(1)); // tt123456 exists
    }

    [Test]
    public async Task ProcessesItemThroughPipeline()
    {
        // Arrange
        var db = CreateTestDatabase();
        var item = CreateTestItem();

        var mockResolver = new Mock<StreamResolver>();
        mockResolver.Setup(r => r.ResolveStreamsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamCandidate>
            {
                new StreamCandidate { Url = "http://example.com/stream.m3u8", Quality = StreamQuality.FHD }
            });

        var mockHydrator = new Mock<MetadataHydrator>();
        mockHydrator.Setup(h => h.HydrateAsync(It.IsAny<MediaItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaItem i, CancellationToken ct) => i);

        var pipeline = new ItemPipelineService(
            MockLogger,
            db,
            mockResolver.Object,
            mockHydrator.Object,
            new Mock<ILibraryManager>().Object);

        // Act
        var result = await pipeline.ProcessItemAsync(item, PipelineTrigger.ScheduledSync, CancellationToken.None);

        // Assert
        Assert.That(result.FinalStatus, Is.EqualTo(ItemStatus.Active));
    }
}
```

**Acceptance Criteria:**
- [ ] Fetches manifest from AIOStreams
- [ ] Filters manifest entries
- [ ] Diffs manifest vs database
- [ ] Processes items through pipeline
- [ ] Updates item status correctly

---

## Phase 121C — Playback Tests (Ranked Fallback Resolution)

### FIX-121C-01: Test Ranked Fallback Stream Resolution

**File:** `Tests/PlaybackTests.cs`

```csharp
[TestFixture]
public class PlaybackTests : TestBase
{
    [Test]
    public async Task ReturnsCachedStream_PrimaryCacheFirst()
    {
        // Arrange
        var db = CreateTestDatabase();
        var cache = new StreamCache(db, MockLogger);
        await cache.SetPrimaryAsync("imdb:tt123456", "http://cached.com/stream.m3u8", CancellationToken.None);

        var resolver = new Mock<StreamResolver>();
        var playback = new PlaybackService(
            resolver.Object,
            cache,
            new Mock<StreamUrlSigner>().Object,
            MockLogger);

        // Act
        var url = await playback.GetStreamUrlAsync("imdb:tt123456", CancellationToken.None);

        // Assert
        Assert.That(url, Is.Not.Null);
        Assert.That(url, Does.Contain("cached.com"));
        resolver.Verify(r => r.ResolveStreamsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ReturnsCachedStream_SecondaryCacheFallback()
    {
        // Arrange
        var db = CreateTestDatabase();
        var cache = new StreamCache(db, MockLogger);
        await cache.SetSecondaryAsync("imdb:tt123456", "http://cached2.com/stream.m3u8", CancellationToken.None);

        var resolver = new Mock<StreamResolver>();
        var playback = new PlaybackService(
            resolver.Object,
            cache,
            new Mock<StreamUrlSigner>().Object,
            MockLogger);

        // Act
        var url = await playback.GetStreamUrlAsync("imdb:tt123456", CancellationToken.None);

        // Assert: Should return secondary cached URL
        Assert.That(url, Is.Not.Null);
        Assert.That(url, Does.Contain("cached2.com"));
        resolver.Verify(r => r.ResolveStreamsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ResolvesOnCacheMiss_DoesNotCacheResult()
    {
        // Arrange
        var db = CreateTestDatabase();
        var cache = new StreamCache(db, MockLogger);

        var resolver = new Mock<StreamResolver>();
        resolver.Setup(r => r.ResolveStreamsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamCandidate>
            {
                new StreamCandidate { Url = "http://resolved.com/stream.m3u8", Quality = StreamQuality.FHD }
            });

        var signer = new Mock<StreamUrlSigner>();
        signer.Setup(s => s.Sign(It.IsAny<string>()))
            .Returns<string>(url => $"{url}|signed");

        var playback = new PlaybackService(
            resolver.Object,
            cache,
            signer.Object,
            MockLogger);

        // Act
        var url = await playback.GetStreamUrlAsync("imdb:tt123456", CancellationToken.None);

        // Assert
        Assert.That(url, Is.Not.Null);
        Assert.That(url, Does.Contain("resolved.com"));
        Assert.That(url, Does.Contain("signed"));

        // CRITICAL: Cache should NOT be updated in GetStreamUrlAsync
        // Cache is only updated by PlaybackEventSubscriptionService on playback start
        var cached = await cache.GetPrimaryAsync("imdb:tt123456", CancellationToken.None);
        Assert.That(cached, Is.Null);
    }

    [Test]
    public async Task RankedFallback_PrimaryThenSecondaryThenLive()
    {
        // Arrange
        var db = CreateTestDatabase();
        var cache = new StreamCache(db, MockLogger);

        var resolver = new Mock<StreamResolver>();
        resolver.Setup(r => r.ResolveStreamsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamCandidate>
            {
                new StreamCandidate { Url = "http://live.com/stream.m3u8", Quality = StreamQuality.FHD }
            });

        var signer = new Mock<StreamUrlSigner>();
        signer.Setup(s => s.Sign(It.IsAny<string>()))
            .Returns<string>(url => $"{url}|signed");

        var playback = new PlaybackService(
            resolver.Object,
            cache,
            signer.Object,
            MockLogger);

        // Act - Both caches empty, should fall back to live resolution
        var url = await playback.GetStreamUrlAsync("imdb:tt123456", CancellationToken.None);

        // Assert
        Assert.That(url, Does.Contain("live.com"));
        resolver.Verify(r => r.ResolveStreamsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SignsUrls()
    {
        // Arrange
        var signer = new StreamUrlSigner(TestConfig);
        TestConfig.PluginSecret = "test-secret";

        var url = "http://example.com/stream.m3u8";

        // Act
        var signed = signer.Sign(url);

        // Assert
        Assert.That(signed, Is.Not.EqualTo(url));
        Assert.That(signed, Does.Contain(url));
        Assert.That(signed, Does.Contain("|"));
        Assert.That(signer.Verify(signed), Is.True);
    }
}
```

**Acceptance Criteria:**
- [ ] Returns primary cached stream (no live resolution)
- [ ] Falls back to secondary cached URL
- [ ] Falls back to live resolution on cache miss
- [ ] Does NOT cache result in GetStreamUrlAsync (cache updated by PlaybackEventSubscriptionService)
- [ ] Signs URLs correctly
- [ ] Verifies signatures

---

## Phase 121D — User Action Tests

### FIX-121D-01: Test Save/Block Actions

**File:** `Tests/UserActionTests.cs`

```csharp
[TestFixture]
public class UserActionTests : TestBase
{
    [Test]
    public async Task SaveItem()
    {
        // Arrange
        var db = CreateTestDatabase();
        var item = CreateTestItem();
        item.Status = ItemStatus.Active;
        await db.UpsertMediaItemAsync(item, CancellationToken.None);

        var service = new SavedActionService(
            new Mock<ISavedRepository>().Object,
            db,
            MockLogger);

        // Act
        var result = await service.SaveItemAsync(item.Id, CancellationToken.None);

        // Assert
        Assert.That(result.Success, Is.True);

        var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
        // CRITICAL: Check saved=true boolean column, NOT status enum
        Assert.That(updated.Saved, Is.True);
        Assert.That(updated.SaveReason, Is.EqualTo(SaveReason.UserManual));
    }

    [Test]
    public async Task UnsaveItemWithEnabledSource()
    {
        // Arrange: Item has enabled source
        var db = CreateTestDatabase();
        var item = CreateTestItem();
        item.Status = ItemStatus.Active;
        item.Saved = true;
        await db.UpsertMediaItemAsync(item, CancellationToken.None);

        // Mock source membership - CRITICAL: source ID is TEXT UUID, not int
        var sourceId = Guid.NewGuid().ToString();
        await db.AddSourceMembershipAsync(sourceId, item.Id, CancellationToken.None);

        var service = new SavedActionService(
            new Mock<ISavedRepository>().Object,
            db,
            MockLogger);

        // Act
        var result = await service.UnsaveItemAsync(item.Id, CancellationToken.None);

        // Assert: Should remain Active (coalition rule)
        var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
        Assert.That(updated.Status, Is.EqualTo(ItemStatus.Active));
        Assert.That(updated.Saved, Is.False);
    }

    [Test]
    public async Task UnsaveItemWithoutEnabledSource_SetsActive()
    {
        // Arrange: Item has no enabled source
        var db = CreateTestDatabase();
        var item = CreateTestItem();
        item.Status = ItemStatus.Active;
        item.Saved = true;
        await db.UpsertMediaItemAsync(item, CancellationToken.None);

        var service = new SavedActionService(
            new Mock<ISavedRepository>().Object,
            db,
            MockLogger);

        // Act
        var result = await service.UnsaveItemAsync(item.Id, CancellationToken.None);

        // Assert: Should be marked as Active (NOT Deleted)
        // Per v3.3 spec §8.3: Unsave sets Active, not Deleted
        var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
        Assert.That(updated.Status, Is.EqualTo(ItemStatus.Active));
        Assert.That(updated.Saved, Is.False);
    }

    [Test]
    public async Task BlockItem()
    {
        // Arrange
        var db = CreateTestDatabase();
        var item = CreateTestItem();
        item.Status = ItemStatus.Active;
        await db.UpsertMediaItemAsync(item, CancellationToken.None);

        var service = new SavedActionService(
            new Mock<ISavedRepository>().Object,
            db,
            MockLogger);

        // Act
        var result = await service.BlockItemAsync(item.Id, CancellationToken.None);

        // Assert
        Assert.That(result.Success, Is.True);

        var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
        // CRITICAL: Check blocked=true boolean column, NOT status enum
        Assert.That(updated.Blocked, Is.True);
        Assert.That(updated.Status, Is.EqualTo(ItemStatus.Active));
    }

    [Test]
    public async Task UnblockItem_SetsActive()
    {
        // Arrange
        var db = CreateTestDatabase();
        var item = CreateTestItem();
        item.Status = ItemStatus.Active;
        item.Blocked = true;
        await db.UpsertMediaItemAsync(item, CancellationToken.None);

        var service = new SavedActionService(
            new Mock<ISavedRepository>().Object,
            db,
            MockLogger);

        // Act
        var result = await service.UnblockItemAsync(item.Id, CancellationToken.None);

        // Assert
        Assert.That(result.Success, Is.True);

        var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
        // CRITICAL: Unblocked items are Active, not Deleted
        Assert.That(updated.Status, Is.EqualTo(ItemStatus.Active));
        Assert.That(updated.Blocked, Is.False);
    }
}
```

**Acceptance Criteria:**
- [ ] Save sets saved=true boolean column (NOT ItemStatus.Saved)
- [ ] Unsave respects Coalition rule (has enabled source)
- [ ] Unsave sets Active (NOT Deleted) - per v3.3 spec §8.3
- [ ] Block sets blocked=true boolean column (NOT ItemStatus.Blocked)
- [ ] Unblock sets Active (NOT Deleted)
- [ ] All MediaItem.Id are string TEXT UUIDs

---

## Phase 121E — Your Files Tests

### FIX-121E-01: Test Your Files Detection

**File:** `Tests/YourFilesTests.cs`

```csharp
[TestFixture]
public class YourFilesTests : TestBase
{
    [Test]
    public async Task MatchesByImdb()
    {
        // Arrange
        var db = CreateTestDatabase();
        var item = CreateTestItem("imdb:tt123456");
        await db.UpsertMediaItemAsync(item, CancellationToken.None);
        await db.UpsertMediaItemIdAsync(item.Id, MediaIdType.Imdb, "tt123456", CancellationToken.None);

        var matcher = new YourFilesMatcher(db, MockLogger);

        var yourFilesItem = new Mock<BaseItem>();
        yourFilesItem.Setup(i => i.ProviderIds).Returns(new Dictionary<string, string>
        {
            { "imdb", "tt123456" }
        });

        // Act
        var match = await matcher.FindMatchingMediaItemAsync(yourFilesItem.Object, CancellationToken.None);

        // Assert
        Assert.That(match, Is.Not.Null);
        Assert.That(match.Id, Is.EqualTo(item.Id));
    }

    [Test]
    public async Task SetsSupersededForYourFilesMatch()
    {
        // Arrange: Item has no enabled source
        var db = CreateTestDatabase();
        var item = CreateTestItem("imdb:tt123456");
        item.Status = ItemStatus.Active;
        await db.UpsertMediaItemAsync(item, CancellationToken.None);

        var resolver = new YourFilesConflictResolver(db, MockLogger, new Mock<ILibraryManager>().Object);

        var match = new YourFilesMatch(
            new Mock<BaseItem>().Object,
            item,
            YourFilesMatchType.Imdb
        );

        // Act
        var resolution = await resolver.ResolveAsync(match, CancellationToken.None);

        // Assert: Should be superseded (NOT saved)
        // Per v3.3 spec §11: Your Files match sets superseded=true, not saved=true
        Assert.That(resolution, Is.EqualTo(ConflictResolution.SupersededWithoutEnabledSource));

        var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
        Assert.That(updated.Superseded, Is.True);
        Assert.That(updated.Saved, Is.False); // CRITICAL: NOT saved
    }

    [Test]
    public async Task SupersededConflict_SavedPlusEnabledSource()
    {
        // Arrange: Item is saved AND has enabled source
        var db = CreateTestDatabase();
        var item = CreateTestItem("imdb:tt123456");
        item.Status = ItemStatus.Active;
        item.Saved = true;
        await db.UpsertMediaItemAsync(item, CancellationToken.None);

        // Mock enabled source - CRITICAL: source ID is TEXT UUID, not int
        var sourceId = Guid.NewGuid().ToString();
        await db.AddSourceMembershipAsync(sourceId, item.Id, CancellationToken.None);

        var resolver = new YourFilesConflictResolver(db, MockLogger, new Mock<ILibraryManager>().Object);

        var match = new YourFilesMatch(
            new Mock<BaseItem>().Object,
            item,
            YourFilesMatchType.Imdb
        );

        // Act
        var resolution = await resolver.ResolveAsync(match, CancellationToken.None);

        // Assert: Should be SupersededConflict
        Assert.That(resolution, Is.EqualTo(ConflictResolution.SupersededConflict));

        var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
        Assert.That(updated.Superseded, Is.True);
        Assert.That(updated.SupersededConflict, Is.True);
        Assert.That(updated.Saved, Is.True); // Saved preserved
    }
}
```

**Acceptance Criteria:**
- [ ] Matches by IMDB
- [ ] Sets superseded=true for Your Files matches (NOT saved=true)
- [ ] Sets superseded_conflict=true for Saved + enabled source
- [ ] Respects Coalition rule
- [ ] All MediaItem.Id are string TEXT UUIDs

---

## Phase 121F — E2E Test Plan

### FIX-121F-01: Create E2E Test Plan

**File:** `Tests/E2E/README.md`

```markdown
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
```

**Acceptance Criteria:**
- [ ] All test scenarios defined
- [ ] Test data documented
- [ ] Test environment specified
- [ ] Breaking change notes included

---

## Sprint 121 Dependencies

- **Previous Sprint:** 120 (Logging)
- **Blocked By:** Sprint 120
- **Blocks:** None (v3.3 complete)

---

## Sprint 121 Completion Criteria

- [ ] Test infrastructure created
- [ ] Uses Emby LoggerFactory (not MEL Mock<ILogger>)
- [ ] Sync pipeline tests pass
- [ ] Playback tests pass (ranked fallback resolution, no caching in GetStreamUrlAsync)
- [ ] User action tests pass (saved/blocked are boolean, unsave sets Active, not Deleted)
- [ ] Your Files tests pass (superseded, not saved)
- [ ] All MediaItem.Id are string TEXT UUIDs
- [ ] E2E test plan created
- [ ] Build succeeds
- [ ] All tests pass

---

## Sprint 121 Notes

**Test Framework:**
- NUnit for test runner
- Moq for mocking
- FluentAssertions for readable assertions

**Test Coverage:**
- Sync Pipeline: fetch → filter → diff → process
- Playback: ranked fallback (cache primary → cache secondary → live)
- User Actions: save (saved=true), block (blocked=true), unsave (Active), unblock (Active)
- Your Files: detection, matching, superseded (not saved), superseded_conflict
- Collections: creation, sync, orphan pruning
- Removal: grace period, coalition rule

**Emby Logger Pattern (CRITICAL):**

Use Emby's LoggerFactory, not MEL's Mock<ILogger>:

```csharp
// CORRECT:
MockLogger = LoggerFactory.CreateLogger<TestBase>();

// WRONG (do NOT use):
MockLogger = new Mock<ILogger>().Object;
```

**ID Types (v3.3 Breaking Change):**
- MediaItem.Id: **string** TEXT UUID
- Source.Id: **string** TEXT UUID
- All database primary keys are TEXT UUIDs

**Boolean Columns vs Status Enum:**
- Check `saved` boolean column, NOT ItemStatus.Saved
- Check `blocked` boolean column, NOT ItemStatus.Blocked
- SaveItemAsync sets `saved = true`, status remains Active
- BlockItemAsync sets `blocked = true`, status remains Active
- UnsaveItemAsync sets `saved = false`, status becomes Active (NOT Deleted)
- UnblockItemAsync sets `blocked = false`, status becomes Active (NOT Deleted)

**Your Files Superseded Behavior:**
- Sets `superseded = true` (NOT `saved = true`)
- Sets `superseded_conflict = true` when Saved + enabled source + Your Files match
- Preserves `saved = true` if item was saved

**Playback Cache Behavior (v3.3 Spec §12):**
- Ranked fallback: cache primary → cache secondary → live
- GetStreamUrlAsync does NOT cache results
- Cache updated by PlaybackEventSubscriptionService on playback start
- Cache TTL: 24 hours

**Three-Library Provisioning:**
- `/embystreams/library/movies/` → TMDB/IMDB movies
- `/embystreams/library/series/` → TMDB/IMDB series
- `/embystreams/library/anime/` → AniList/AniDB content
- Items placed in correct library based on media type and ID type

**No Migration Tests (v3.3 Breaking Change):**
- Per spec §17, there is NO migration from v20
- Fresh database initialization only
- Manual reset via Danger Zone UI
- No automatic migration logic needed
- All tests use fresh database setup

**E2E Validation:**
- Manual test scenarios
- Automated E2E scripts
- Test data
- Test environment
