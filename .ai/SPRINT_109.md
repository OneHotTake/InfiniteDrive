# Sprint 109 — Foundation & Database Schema (v3.3 Breaking Change)

**Version:** v3.3 | **Status:** Planning | **Risk:** HIGH (breaking change, full wipe initialization)

---

## Overview

Sprint 109 implements foundational architecture for v3.3, a breaking change that requires clean database initialization (no migration from v20). This sprint creates new schema, core domain models, and installation infrastructure.

**Key Changes from v20:**
- MediaId system replaces IMDB-only keys
- ItemStatus lifecycle machine replaces ItemState enum
- Sources model replaces Catalog model
- Saved/Blocked states replace PIN model
- Your Files detection via media_item_ids table
- 9 new database tables (8 core + 1 tracking)

**IMPORTANT: No Migration Path**
Per specification §17, there is NO migration from v20 to v3.3. This is a full wipe. Users must follow manual reset procedure via Danger Zone UI. The plugin initializes with a fresh schema on first run.

---

## Phase 109A — New Database Schema

### FIX-109A-01: Create Schema.cs with Table Definitions

**File:** `Data/Schema.cs`

Create new `Schema` class defining all v3.3 tables:

```csharp
namespace EmbyStreams.Data
{
    public static class Schema
    {
        public const int CurrentSchemaVersion = 1;

        // CREATE TABLE statements for all 9 tables
        // media_items, media_item_ids, sources, source_memberships,
        // collections, stream_resolution_log, item_pipeline_log,
        // schema_version, home_section_tracking
    }
}
```

**Tables:**

| Table | Purpose |
|-------|---------|
| `media_items` | Core item table with ItemStatus, SaveReason, FailureReason |
| `media_item_ids` | Multi-provider ID matching (imdb, tmdb, tvdb, anilist, anidb, kitsu) |
| `sources` | Enabled/Disabled sources with ShowAsCollection flag |
| `source_memberships` | Which items belong to which sources |
| `collections` | Emby BoxSet references |
| `stream_resolution_log` | Resolution history for debugging |
| `item_pipeline_log` | Item lifecycle event log |
| `schema_version` | Schema version tracking |
| `home_section_tracking` | Per-user per-rail section tracking |

**Acceptance Criteria:**
- [ ] All CREATE TABLE statements defined
- [ ] Proper indexes on frequently queried columns
- [ ] Foreign key constraints where appropriate (note: logs use (primary_id, primary_id_type, media_type), not FK)
- [ ] Comments document purpose of each table

### FIX-109A-02: Add home_section_tracking Table

**Add to Phase 109A schema:**

```sql
CREATE TABLE home_section_tracking (
    id TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    user_id TEXT NOT NULL,
    rail_type TEXT NOT NULL
        CHECK (rail_type IN ('saved','trending_movies','trending_series','new_this_week','admin_chosen')),
    emby_section_id TEXT,
    section_marker TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE (user_id, rail_type)
);
CREATE INDEX idx_home_section_user ON home_section_tracking (user_id);
```

This table tracks, per user per rail, Emby-assigned section ID and a stable marker string used to re-find section after server restart or reordering.

**Acceptance Criteria:**
- [ ] Table created in schema
- [ ] CHECK constraint for valid rail types
- [ ] Index on user_id
- [ ] UNIQUE constraint on (user_id, rail_type)

### FIX-109A-03: Create DatabaseInitializer

**File:** `Data/DatabaseInitializer.cs`

Initialize new schema from scratch:

```csharp
public class DatabaseInitializer
{
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(ILogger<DatabaseInitializer> logger)
    {
        _logger = logger;
    }

    public void Initialize(string dbPath)
    {
        using var connection = new SQLiteConnection($"Data Source={dbPath}");
        connection.Open();

        // Enable WAL mode for better concurrency
        connection.Execute("PRAGMA journal_mode=WAL;");

        foreach (var table in Schema.Tables)
        {
            connection.Execute(table.CreateSql);
            _logger.LogInformation("Created table: {TableName}", table.TableName);
        }

        // Set initial schema version
        connection.Execute(
            "INSERT INTO schema_version (version, description) VALUES (@Version, @Description)",
            new { Version = Schema.CurrentSchemaVersion, Description = "EmbyStreams v3.3 initial schema" }
        );
    }
}
```

**Acceptance Criteria:**
- [ ] Creates all 9 tables in correct order
- [ ] Enables WAL mode
- [ ] Handles table creation errors gracefully
- [ ] Logs initialization steps

---

## Phase 109B — Core Domain Models

### FIX-109B-01: MediaIdType Enum

**File:** `Models/MediaIdType.cs`

```csharp
public enum MediaIdType
{
    Tmdb,
    Imdb,
    Tvdb,
    AniList,
    AniDB,
    Kitsu
}
```

**Acceptance Criteria:**
- [ ] All supported provider types defined
- [ ] Parse from string (e.g., "imdb" → Imdb)
- [ ] Serialize to lowercase string

### FIX-109B-02: MediaId Value Type

**File:** `Models/MediaId.cs`

```csharp
public readonly struct MediaId : IEquatable<MediaId>
{
    public MediaIdType Type { get; }
    public string Value { get; }

    // Parse from "imdb:tt123456" format
    public static MediaId Parse(string input)
    {
        if (string.IsNullOrEmpty(input))
            throw new ArgumentException("MediaId cannot be empty", nameof(input));

        var parts = input.Split(':', 2);
        if (parts.Length != 2)
            throw new ArgumentException($"Invalid MediaId format: {input}", nameof(input));

        if (!Enum.TryParse<MediaIdType>(parts[0], true, out var type))
            throw new ArgumentException($"Unknown MediaIdType: {parts[0]}", nameof(input));

        return new MediaId { Type = type, Value = parts[1] };
    }

    // Implicit conversion from string
    public static implicit operator MediaId(string value)
    {
        return Parse(value);
    }

    public bool Equals(MediaId other)
    {
        return Type == other.Type && Value == other.Value;
    }

    public override bool Equals(object obj)
    {
        return obj is MediaId other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Type.GetHashCode(), Value.GetHashCode());
    }

    public override string ToString()
    {
        return $"{Type.ToString().ToLower()}:{Value}";
    }
}
```

**Acceptance Criteria:**
- [ ] Parses "type:value" format
- [ ] Equatable implementation
- [ ] Validation (non-empty Value)
- [ ] ToString returns "type:value"

### FIX-109B-03: ItemStatus Enum

**File:** `Models/ItemStatus.cs`

```csharp
public enum ItemStatus
{
    Known,      // Discovered in source manifest
    Resolved,   // Streams resolved successfully
    Hydrated,   // Metadata fetched
    Created,    // .strm file written
    Indexed,    // Added to Emby library
    Active,     // Playable (Indexed + not removed)
    Failed,     // Resolution failed
    Deleted     // Removed from library
}
```

**Acceptance Criteria:**
- [ ] All lifecycle states defined
- [ ] Transition validation methods (CanTransitionTo)

### FIX-109B-04: FailureReason Enum

**File:** `Models/FailureReason.cs`

```csharp
public enum FailureReason
{
    None,
    NoStreamsFound,
    MetadataFetchFailed,
    FileWriteError,
    EmbyIndexTimeout,
    DigitalReleaseGate,
    Blocked
}
```

**Acceptance Criteria:**
- [ ] All failure modes defined
- [ ] User-friendly display strings

### FIX-109B-05: PipelineTrigger Enum

**File:** `Models/PipelineTrigger.cs`

```csharp
public enum PipelineTrigger
{
    Sync,
    Play,
    WatchEpisode,
    UserSave,
    UserBlock,
    UserRemove,
    GraceExpiry,
    YourFiles,
    Admin,
    Retry
}
```

**Acceptance Criteria:**
- [ ] All trigger types defined
- [ ] Logging-friendly descriptions

### FIX-109B-06: SaveReason Enum

**File:** `Models/SaveReason.cs`

```csharp
public enum SaveReason
{
    Explicit,
    WatchedEpisode,
    AdminOverride
}
```

**Acceptance Criteria:**
- [ ] All save reasons defined
- [ ] Clear semantic distinction

### FIX-109B-07: SourceType Enum

**File:** `Models/SourceType.cs`

```csharp
public enum SourceType
{
    BuiltIn,
    Aio,
    Trakt,
    MdbList
}
```

**Acceptance Criteria:**
- [ ] All source types defined
- [ ] Extensible for future providers

### FIX-109B-08: MediaItem Entity

**File:** `Models/MediaItem.cs`

```csharp
public class MediaItem
{
    // Primary key - TEXT UUID, not int
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    // Primary ID with type
    public MediaId PrimaryId { get; init; } = null!;
    public string MediaType { get; init; } = null!;  // "movie", "series"

    public string Title { get; init; } = null!;
    public int? Year { get; init; }

    // Lifecycle
    public ItemStatus Status { get; set; }
    public FailureReason FailureReason { get; set; }

    // Saved state (boolean columns, not status values)
    public bool Saved { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public string? SavedBy { get; set; }  // "system:watch", "user", "admin"
    public SaveReason? SaveReason { get; set; }
    public int? SavedSeason { get; set; }  // For series: season number that was saved

    // Blocked state (boolean column, not status value)
    public bool Blocked { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Grace period (for removal pipeline)
    public DateTimeOffset? GraceStartedAt { get; set; }

    // Superseded / conflict handling
    public bool Superseded { get; set; }
    public bool SupersededConflict { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }

    // Emby integration
    public string? EmbyItemId { get; set; }  // TEXT GUID, not int
    public DateTimeOffset? EmbyIndexedAt { get; set; }
    public string? StrmPath { get; set; }
    public string? NfoPath { get; set; }
    public int WatchProgressPct { get; set; }
    public bool Favorited { get; set; }

    // Derived state
    public bool IsSaved => Saved;
    public bool IsBlocked => Blocked;
    public bool IsPlayable => Status == ItemStatus.Active || Saved;

    public string PrimaryIdValue => PrimaryId.Value;
    public string PrimaryIdType => PrimaryId.Type.ToString().ToLower();
}
```

**Acceptance Criteria:**
- [ ] All fields match spec schema §14
- [ ] Id is TEXT UUID, not int
- [ ] EmbyItemId is TEXT GUID, not int
- [ ] Saved and Blocked are boolean columns
- [ ] Images NOT stored (PosterUrl, BackdropUrl, Description removed)
- [ ] Computed properties for common queries
- [ ] All spec fields present

### FIX-109B-09: Source Entity

**File:** `Models/Source.cs`

```csharp
public class Source
{
    // Primary key - TEXT UUID, not int
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = null!;
    public string? Url { get; init; }
    public SourceType Type { get; init; }
    public bool Enabled { get; set; }
    public bool ShowAsCollection { get; set; }

    // Sync metadata
    public int MaxItems { get; set; } = 100;
    public int SyncIntervalHours { get; set; } = 6;
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? EmbyCollectionId { get; set; }  // BoxSet InternalId as string

    // Collection metadata (for ShowAsCollection sources)
    public string? CollectionName { get; set; }
}
```

**Acceptance Criteria:**
- [ ] All fields match spec schema
- [ ] Id is TEXT UUID, not int
- [ ] Enabled/ShowAsCollection flags
- [ ] Sync metadata tracking
- [ ] EmbyCollectionId for BoxSet reference
- [ ] CollectionName for display

### FIX-109B-10: AioStreamsPrefixDefaults Config

**File:** `Models/AioStreamsPrefixDefaults.cs`

```csharp
public static class AioStreamsPrefixDefaults
{
    public static readonly IReadOnlyDictionary<MediaIdType, string> DefaultPrefixMap =
        new Dictionary<MediaIdType, string>
        {
            { MediaIdType.Tmdb,    "tmdb"    },
            { MediaIdType.Imdb,    "imdb"    },
            { MediaIdType.Tvdb,    "tvdb"    },
            { MediaIdType.AniList, "anilist" },
            { MediaIdType.AniDB,   "anidb"   },
            { MediaIdType.Kitsu,   "kitsu"   },
        };
}
```

**Acceptance Criteria:**
- [ ] Default prefix mappings for all MediaIdTypes
- [ ] Correct AIOStreams prefix values
- [ ] Extensible for custom providers
- [ ] JSON serializable

---

## Phase 109D — Emby Library Provisioning on Install

### FIX-109D-01: Emby Library Provisioning on Install

On plugin install (first run only), EmbyStreams must:

1. Create **THREE** distinct directory paths on NFS mount:
   - `/embystreams/library/movies/` → TMDB/IMDB movies
   - `/embystreams/library/series/` → TMDB/IMDB series
   - `/embystreams/library/anime/` → AniList/AniDB content (separate Emby library)

2. Register **THREE** Emby libraries (not one):
   - Movies library: name="EmbyStreams Movies", path=/embystreams/library/movies/, type=Movies
   - Series library: name="EmbyStreams Series", path=/embystreams/library/series/, type=Series
   - Anime library: name="EmbyStreams Anime", path=/embystreams/library/anime/, type=Mixed (Movies+Series) with AniList/AniDB metadata providers

3. Iterate `_userManager.GetUserList(new UserQuery { IsDisabled = false })` and for each user call `UpdateUserPolicy()` to hide **all three** EmbyStreams libraries from nav panel
4. Log each affected user by name
5. Display an install notice to administrator
6. Write a flag to prevent re-applying this on subsequent plugin restarts

**Why Three Separate Libraries:**

Emby handles anime metadata completely differently from standard movies/series:
- Standard Movies/Series use TMDB/IMDB metadata providers
- Anime requires AniList/AniDB metadata providers
- Mixing them in one library breaks metadata resolution

**Acceptance Criteria:**
- [ ] Library created with correct settings on first install
- [ ] **Three** separate directories created (movies/, series/, anime/)
- [ ] **Three** separate Emby libraries registered with correct types
- [ ] Hidden for all users present at install time
- [ ] Flag prevents re-application on restart
- [ ] Admin notice displayed
- [ ] Each affected user logged by name

---

## Sprint 109 Dependencies

- **Previous Sprint:** 104 (Complete)
- **Blocked By:** None
- **Blocks:** Sprint 110 (Services Layer)

---

## Sprint 109 Completion Criteria

- [ ] All 9 database tables created with proper indexes
- [ ] home_section_tracking table for per-user section tracking
- [ ] All core domain models implemented (TEXT UUIDs, not int IDs)
- [ ] Library provisioning on install (THREE separate libraries)
- [ ] Build succeeds
- [ ] E2E: Fresh database initialized correctly

---

## Sprint 109 Notes

**Breaking Change:** This is a major breaking change. Existing users will need to:
1. See migration warning in UI (from Sprint 117)
2. Manually trigger reset via Danger Zone UI
3. Wait for reset to complete
4. Verify library is populated correctly

**No Migration Path:**
Per specification §17, there is NO automatic migration from v20. This is a clean wipe. The plugin initializes fresh schema on first run.

**Schema Version Tracking:**
- `schema_version` table seeded with version = 1 on initial setup
- Future schema migrations can add new versions without requiring full wipe

**Data Loss Risk:** LOW - this is a clean install. v20 data is not migrated.

**Library Structure (Critical - Three Separate Libraries):**
Per spec §4.1 and §15, EmbyStreams creates and manages **THREE** separate Emby libraries with THREE separate physical paths:

1. `/embystreams/library/movies/` → TMDB/IMDB movies
2. `/embystreams/library/series/` → TMDB/IMDB series
3. `/embystreams/library/anime/` → AniList/AniDB content

Each library uses the correct Emby metadata providers:
- Movies/Anime libraries → use TMDB/IMDB providers
- Series library → use TMDB/IMDB providers (with series-specific settings)
- Anime library → use AniList/AniDB providers (required for anime)

**DO NOT mix anime with standard movies/series.** This breaks Emby's metadata resolution entirely.
