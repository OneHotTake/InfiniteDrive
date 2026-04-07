# Sprint 122 — Versioned Playback: Schema & Data Model

**Version:** v3.3 | **Status:** Plan | **Risk:** HIGH | **Depends:** Sprint 121

---

## Overview

Sprint 122 establishes the database schema and core data model for versioned playback. This is the foundation layer — no UI, no file materialization, no playback changes. Pure data structures and CRUD.

**Key Principle:** All schema changes are additive. No existing tables or columns are modified. Existing behavior is completely unaffected.

---

## Phase 122A — Database Schema Extension

### FIX-122A-01: Add `version_slots` Table

**File:** `Data/Schema.cs` (modify)
**What:** Add a new table storing the global slot configuration (7 predefined slots).

```sql
CREATE TABLE version_slots (
    slot_key        TEXT PRIMARY KEY,
    label           TEXT NOT NULL,
    resolution      TEXT NOT NULL,
    video_codecs    TEXT NOT NULL DEFAULT 'any',     -- comma-separated allowlist
    hdr_classes     TEXT NOT NULL DEFAULT '',         -- empty = SDR only, 'any' = accept all
    audio_preferences TEXT NOT NULL,                  -- comma-separated in preference order
    enabled         INTEGER NOT NULL DEFAULT 0,
    is_default      INTEGER NOT NULL DEFAULT 0,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);
```

**Seed data (inserted after table creation):**

| slot_key | label | resolution | video_codecs | hdr_classes | audio_preferences | enabled | is_default | sort_order |
|---|---|---|---|---|---|---|---|---|
| `hd_broad` | HD · Broad | 1080p | h264 | | dd_plus_51,dd_51,aac_stereo | 1 | 1 | 0 |
| `best_available` | Best Available | highest | any | any | atmos,dd_plus_71,dd_plus_51,dd_51 | 0 | 0 | 1 |
| `4k_dv` | 4K · Dolby Vision | 2160p | hevc,av1 | dv | atmos,dd_plus_71,dd_plus_51 | 0 | 0 | 2 |
| `4k_hdr` | 4K · HDR | 2160p | hevc,av1 | hdr10 | atmos,dd_plus_51,dd_51 | 0 | 0 | 3 |
| `4k_sdr` | 4K · SDR | 2160p | hevc,av1 | | dd_plus_51,dd_51,aac | 0 | 0 | 4 |
| `hd_efficient` | HD · Efficient | 1080p | hevc | | dd_plus_51,aac_stereo | 0 | 0 | 5 |
| `compact` | Compact | 720p | h264 | | aac,dd | 0 | 0 | 6 |

**Depends on:** None
**Must not break:** Existing tables unchanged.

---

### FIX-122A-02: Add `candidates` Table

**File:** `Data/Schema.cs` (modify)
**What:** Stores normalized candidates per title per slot — the ranked ladder that drives playback fallback.

```sql
CREATE TABLE candidates (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    rank            INTEGER NOT NULL,

    -- Provider identity
    service         TEXT,
    stream_type     TEXT NOT NULL DEFAULT 'debrid',

    -- Normalized technical metadata
    resolution      TEXT,
    video_codec     TEXT,
    hdr_class       TEXT,
    audio_codec     TEXT,
    audio_channels  TEXT,

    -- Source metadata
    file_name       TEXT,
    file_size       INTEGER,
    bitrate_kbps    INTEGER,
    languages       TEXT,
    source_type     TEXT,
    is_cached       INTEGER NOT NULL DEFAULT 0,

    -- Stable identity
    fingerprint     TEXT NOT NULL,
    binge_group     TEXT,
    stream_url      TEXT NOT NULL,
    info_hash       TEXT,
    file_idx        INTEGER,

    -- Scoring
    confidence_score REAL NOT NULL DEFAULT 0.0,

    -- Lifecycle
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at      TEXT NOT NULL,

    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE
);
CREATE INDEX idx_candidates_item_slot ON candidates(media_item_id, slot_key, rank);
CREATE INDEX idx_candidates_fingerprint ON candidates(fingerprint);
CREATE INDEX idx_candidates_expires ON candidates(expires_at);
CREATE UNIQUE INDEX idx_candidates_unique ON candidates(media_item_id, slot_key, fingerprint);
```

**Depends on:** None
**Must not break:** No existing table modified.

---

### FIX-122A-03: Add `version_snapshots` Table

**File:** `Data/Schema.cs` (modify)
**What:** Tracks the selected top candidate per title per slot, plus ephemeral playback URL cache.

```sql
CREATE TABLE version_snapshots (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    candidate_id    TEXT NOT NULL,
    snapshot_at     TEXT NOT NULL DEFAULT (datetime('now')),

    -- Playback URL cache (short TTL, ephemeral)
    playback_url    TEXT,
    playback_url_cached_at TEXT,
    playback_url_expires_at TEXT,

    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (media_item_id, slot_key)
);
CREATE INDEX idx_snapshots_item ON version_snapshots(media_item_id);
```

**Depends on:** None
**Must not break:** No existing table modified.

---

### FIX-122A-04: Add `materialized_versions` Table

**File:** `Data/Schema.cs` (modify)
**What:** Tracks which slots have been materialized as .strm/.nfo pairs per title.

```sql
CREATE TABLE materialized_versions (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    strm_path       TEXT NOT NULL,
    nfo_path        TEXT NOT NULL,
    strm_url_hash   TEXT NOT NULL,          -- SHA1 of the .strm content URL for change detection
    is_base         INTEGER NOT NULL DEFAULT 0,  -- 1 if this slot holds the base (unsuffixed) filename
    materialized_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (media_item_id, slot_key)
);
CREATE INDEX idx_materialized_item ON materialized_versions(media_item_id);
CREATE INDEX idx_materialized_slot ON materialized_versions(slot_key);
CREATE INDEX idx_materialized_base ON materialized_versions(is_base) WHERE is_base = 1;
```

**Depends on:** None
**Must not break:** No existing table modified.

---

### FIX-122A-05: Schema Migration (v1 → v2)

**File:** `Data/DatabaseInitializer.cs` (modify)
**What:** Add migration path from schema version 1 to version 2. On existing databases, the 4 new tables are created. On fresh installs, all tables including the 4 new ones are created and `version_slots` is seeded.

**Key logic:**
- Fresh install: schema_version = 2, all tables created, 7 slots seeded
- Migration from v1: ALTER TABLE not needed (all new tables), just CREATE the 4 new tables + seed `version_slots` + update schema_version to 2
- `hd_broad` is always enabled + default after seeding

**Depends on:** FIX-122A-01 through FIX-122A-04
**Must not break:** Existing data untouched. Migration is purely additive.

---

## Phase 122B — Domain Models

### FIX-122B-01: VersionSlot Model

**File:** `Models/VersionSlot.cs` (create)

```csharp
public class VersionSlot
{
    public string SlotKey { get; set; }
    public string Label { get; set; }
    public string Resolution { get; set; }
    public string VideoCodecs { get; set; }      // comma-separated
    public string HdrClasses { get; set; }        // comma-separated
    public string AudioPreferences { get; set; }   // comma-separated ordered
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }

    // Derived helpers
    public List<string> VideoCodecList => ...;
    public List<string> HdrClassList => ...;
    public List<string> AudioPreferenceList => ...;

    // File naming
    public string FileSuffix => Label.Replace("·", " ").Trim();
    public bool IsHdBroad => SlotKey == "hd_broad";
}
```

**Depends on:** None
**Must not break:** New file.

---

### FIX-122B-02: Candidate Model

**File:** `Models/Candidate.cs` (create)

```csharp
public class Candidate
{
    // Identity
    public string Id { get; set; }
    public string MediaItemId { get; set; }
    public string SlotKey { get; set; }
    public int Rank { get; set; }

    // Provider
    public string Service { get; set; }
    public string StreamType { get; set; }

    // Normalized technical metadata
    public string Resolution { get; set; }
    public string VideoCodec { get; set; }
    public string HdrClass { get; set; }
    public string AudioCodec { get; set; }
    public string AudioChannels { get; set; }

    // Source
    public string FileName { get; set; }
    public long? FileSize { get; set; }
    public int? BitrateKbps { get; set; }
    public string Languages { get; set; }
    public string SourceType { get; set; }
    public bool IsCached { get; set; }

    // Stable identity
    public string Fingerprint { get; set; }
    public string BingeGroup { get; set; }
    public string StreamUrl { get; set; }
    public string InfoHash { get; set; }
    public int? FileIdx { get; set; }

    // Scoring
    public double ConfidenceScore { get; set; }

    // Lifecycle
    public string CreatedAt { get; set; }
    public string ExpiresAt { get; set; }
}
```

**Depends on:** None
**Must not break:** New file. Distinct from existing `StreamCandidate` which serves the pre-versioned playback path.

---

### FIX-122B-03: VersionSnapshot Model

**File:** `Models/VersionSnapshot.cs` (create)

```csharp
public class VersionSnapshot
{
    public string Id { get; set; }
    public string MediaItemId { get; set; }
    public string SlotKey { get; set; }
    public string CandidateId { get; set; }
    public string SnapshotAt { get; set; }

    // Ephemeral playback URL cache
    public string PlaybackUrl { get; set; }
    public string PlaybackUrlCachedAt { get; set; }
    public string PlaybackUrlExpiresAt { get; set; }

    // Derived
    public bool HasValidPlaybackUrl =>
        !string.IsNullOrEmpty(PlaybackUrl) &&
        !string.IsNullOrEmpty(PlaybackUrlExpiresAt) &&
        DateTime.TryParse(PlaybackUrlExpiresAt, out var exp) &&
        exp > DateTime.UtcNow;
}
```

**Depends on:** None
**Must not break:** New file.

---

### FIX-122B-04: MaterializedVersion Model

**File:** `Models/MaterializedVersion.cs` (create)

```csharp
public class MaterializedVersion
{
    public string Id { get; set; }
    public string MediaItemId { get; set; }
    public string SlotKey { get; set; }
    public string StrmPath { get; set; }
    public string NfoPath { get; set; }
    public string StrmUrlHash { get; set; }
    public bool IsBase { get; set; }
    public string MaterializedAt { get; set; }
    public string UpdatedAt { get; set; }
}
```

**Depends on:** None
**Must not break:** New file.

---

## Phase 122C — Repository Layer

### FIX-122C-01: VersionSlotRepository

**File:** `Data/VersionSlotRepository.cs` (create)

**Methods:**
- `GetAllSlotsAsync()` — all 7 slots (enabled + disabled)
- `GetEnabledSlotsAsync()` — enabled slots only, ordered by sort_order
- `GetSlotAsync(slotKey)` — single slot by key
- `GetDefaultSlotAsync()` — the slot where is_default = true
- `UpsertSlotAsync(slot)` — update a slot's enabled/is_default state
- `SetDefaultSlotAsync(slotKey)` — atomically set is_default = 1 for one slot, 0 for all others
- `GetEnabledSlotCountAsync()` — count of enabled slots
- `EnforceMaxSlotsAsync(maxSlots = 8)` — validation helper

**Service-layer enforcement (not in repo):**
- `hd_broad` cannot be disabled — check at service level
- Maximum 8 enabled — check at service level
- Default must be an enabled slot — check at service level

**Depends on:** FIX-122A-01, FIX-122B-01
**Must not break:** New file.

---

### FIX-122C-02: CandidateRepository

**File:** `Data/CandidateRepository.cs` (create)

**Methods:**
- `GetCandidatesAsync(mediaItemId, slotKey)` — ranked ladder for a title/slot
- `GetTopCandidateAsync(mediaItemId, slotKey)` — rank 0 candidate
- `UpsertCandidatesAsync(candidates)` — batch upsert (replace ladder for a title/slot)
- `DeleteCandidatesAsync(mediaItemId, slotKey)` — remove ladder
- `DeleteExpiredCandidatesAsync()` — cleanup
- `GetCandidateByFingerprintAsync(fingerprint)` — dedup lookup

**Depends on:** FIX-122A-02, FIX-122B-02
**Must not break:** New file.

---

### FIX-122C-03: SnapshotRepository

**File:** `Data/SnapshotRepository.cs` (create)

**Methods:**
- `GetSnapshotAsync(mediaItemId, slotKey)` — snapshot for a title/slot
- `UpsertSnapshotAsync(snapshot)` — insert or update
- `CachePlaybackUrlAsync(mediaItemId, slotKey, url, ttlMinutes)` — update ephemeral cache
- `GetCachedPlaybackUrlAsync(mediaItemId, slotKey)` — return cached URL if not expired
- `InvalidatePlaybackUrlAsync(mediaItemId, slotKey)` — clear cached URL
- `GetAllSnapshotsForItemAsync(mediaItemId)` — all slot snapshots for a title

**Depends on:** FIX-122A-03, FIX-122B-03
**Must not break:** New file.

---

### FIX-122C-04: MaterializedVersionRepository

**File:** `Data/MaterializedVersionRepository.cs` (create)

**Methods:**
- `GetMaterializedVersionsAsync(mediaItemId)` — all materialized slots for a title
- `GetMaterializedVersionAsync(mediaItemId, slotKey)` — single slot materialization
- `UpsertMaterializedVersionAsync(version)` — record materialization
- `DeleteMaterializedVersionAsync(mediaItemId, slotKey)` — remove record
- `GetAllMaterializedForSlotAsync(slotKey)` — all titles with a slot materialized (for rehydration)
- `GetBaseMaterializedVersionAsync(mediaItemId)` — the slot holding the base filename
- `SetBaseSlotAsync(mediaItemId, newSlotKey)` — atomically swap is_base flag
- `GetStrmPathsNeedingUrlUpdateAsync(currentUrlPrefix)` — find .strm files with stale server address
- `CountMaterializedVersionsAsync()` — total materialized records (for UI estimates)

**Depends on:** FIX-122A-04, FIX-122B-04
**Must not break:** New file.

---

## Phase 122D — Candidate Normalizer & Slot Matcher

### FIX-122D-01: CandidateNormalizer Service

**File:** `Services/CandidateNormalizer.cs` (create)

**What:** Parses raw `AioStreamsStream` payloads into normalized `Candidate` objects.

**Parsing priority for technical metadata:**
1. `parsedFile` fields → use directly if available (most reliable)
2. `behaviorHints.filename` → parse filename for quality markers
3. `description` / `title` → parse quality string from stream title

**Normalization rules:**
- **Resolution**: `4K`/`2160p` → `2160p`, `1080p`/`FHD` → `1080p`, `720p`/`HD` → `720p`
- **Video codec**: `x264`/`AVC` → `h264`, `x265`/`HEVC` → `hevc`, `AV1` → `av1`
- **HDR class**: `DV`/`Dolby Vision` → `dv`, `HDR10+` → `hdr10_plus`, `HDR` → `hdr10`
- **Audio codec**: `Atmos` → `atmos`, `DD+`/`Dolby Digital Plus` → `dd_plus`, `DD`/`Dolby Digital` → `dd`, `AAC` → `aac`
- **Audio channels**: `7.1` → `7.1`, `5.1` → `5.1`, `2.0`/`stereo` → `stereo`
- **Source type**: `REMUX` → `remux`, `Bluray` → `bluray`, `WEB-DL`/`WEBRip` → `web`
- **Bitrate**: `file_size / (estimated_runtime_seconds * 1000)` when both available

**Fingerprint**: `SHA1(stream_id + ":" + bingeGroup + ":" + filename + ":" + videoSize)`

**Key method:**
```csharp
public List<Candidate> NormalizeStreams(
    string mediaItemId,
    List<AioStreamsStream> rawStreams,
    int estimatedRuntimeSeconds = 0)
```

Returns candidates keyed by `slot_key = null` (unslotted). Slot matching is separate.

**Depends on:** FIX-122B-02
**Must not break:** New file. Does not modify `AioStreamsClient` or existing parsing.

---

### FIX-122D-02: SlotMatcher Service

**File:** `Services/SlotMatcher.cs` (create)

**What:** Filters and ranks normalized candidates against each slot's matching policy.

**Matching logic per slot:**
1. Filter by resolution band:
   - `1080p` → accept `1080p` only
   - `2160p` → accept `2160p` only
   - `720p` → accept `720p` and lower
   - `highest` → accept any
2. Filter by video codec allowlist (`any` = accept all)
3. Filter by HDR class:
   - Empty list → SDR only (reject any HDR/DV)
   - `any` → accept all HDR classes
   - Specific list → accept only those classes
4. Rank remaining candidates by:
   - Audio preference order (position in slot's list, lower = better)
   - Then by bitrate (descending)
   - Then by confidence score (descending)
   - Then by cache status (cached first)
5. Assign rank (0 = best)
6. If no candidates match → return empty (silent absence)

**Key method:**
```csharp
public Dictionary<string, List<Candidate>> MatchToAllSlots(
    List<VersionSlot> enabledSlots,
    List<Candidate> normalizedCandidates)
```

Returns slot_key → ranked candidate list.

**Depends on:** FIX-122B-01, FIX-122B-02, FIX-122C-01
**Must not break:** New file.

---

## Sprint 122 Dependencies

- **Previous Sprint:** 121 (E2E Validation)
- **Blocked By:** Sprint 121
- **Blocks:** Sprint 123 (File Materialization)

---

## Sprint 122 Completion Criteria

- [ ] 4 new database tables added (version_slots, candidates, version_snapshots, materialized_versions)
- [ ] Schema version bumped to 2 with migration from version 1
- [ ] 7 slots seeded into version_slots table on fresh install
- [ ] 5 new models (VersionSlot, Candidate, VersionSnapshot, MaterializedVersion + existing VersionSlot)
- [ ] 4 new repository classes with full CRUD
- [ ] CandidateNormalizer parses all AIOStreams fields correctly
- [ ] SlotMatcher filters and ranks candidates per slot policy
- [ ] Existing StreamCandidate, PlaybackService, CatalogSyncTask untouched
- [ ] Build succeeds (0 warnings, 0 errors)
