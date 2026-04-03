# What Gelato Writes to .strm Files & The Stub File Mystery

**Date:** 2026-03-29
**Subject:** Deep dive into Gelato's .strm file format and library bootstrapping
**Findings:** Surprising hybrid approach combining database + files

---

## TL;DR

**What Gelato writes to .strm files:**
1. **For catalog items:** `gelato://stub/{Id}` (fake URI to reference database entry)
2. **For stream variants:** HTTP URL (CDN or `/gelato/stream?ih=...` for torrents)

**Why stub.txt files exist:**
- Jellyfin only creates Folder items if it detects files in the directory
- Without stub.txt, empty library folders won't register in Jellyfin's database
- Stub.txt triggers a library scan and forces folder creation

**Does Emby need it?**
- Likely NO — Emby's library scanner may handle empty folders differently
- Need to verify Emby's behavior with empty sync paths

---

## .strm File Content: Two Different Cases

### Case 1: Catalog Sync (Single Primary Item + Folder Structure)

**When:** User adds a Stremio catalog to Jellyfin

**What gets created:**

```
Movies/
├── Shawshank Redemption (1994)/
│   └── Shawshank Redemption (1994).strm    ← Contains: gelato://stub/tt0111161
├── The Dark Knight (2008)/
│   └── The Dark Knight (2008).strm         ← Contains: gelato://stub/tt0468569

TV/
├── Breaking Bad (2008)/
│   ├── stub.txt                            ← Triggers library scan
│   ├── Season 01/
│   │   ├── Breaking Bad S01E01.strm        ← Contains: gelato://stub/tt0903747:1:1
│   │   └── Breaking Bad S01E02.strm        ← Contains: gelato://stub/tt0903747:1:2
```

**Code that creates this:**

```csharp
// File: GelatoManager.cs, IntoBaseItem() method, line 1193
item.Path = $"gelato://stub/{Id}";

// Then later in SaveItem(), lines 1143-1147:
item.ShortcutPath = item.Path;      // Save gelato://stub/... as shortcut
item.IsShortcut = true;              // Mark as shortcut
item.Path = GetStrmPath(...);        // Set Path to actual .strm file location

CreateStrmFile(item.Path, item.ShortcutPath);  // Write gelato://stub/... to .strm
```

**Example .strm file content (text):**
```
gelato://stub/tt0111161
```

**What this means:**
- The .strm file is a **Jellyfin shortcut** (`IsShortcut = true`)
- The `gelato://` URI is NOT a real URL — it's a **placeholder reference**
- Jellyfin resolves it back to the item in the database by matching the ID
- The actual playback happens through the database-backed Virtual Item flow (no HTTP proxy needed for primary items)

---

### Case 2: Stream Variants (Multiple Versions of Same Item)

**When:** SyncStreams adds multiple provider options for the same movie/episode

**What gets created:**

```
Movies/
├── Shawshank Redemption (1994)/
│   ├── Shawshank Redemption (1994).strm              ← Primary (gelato://stub/tt0111161)
│   ├── Shawshank Redemption (1994) {guid1}.strm      ← Stream variant 1
│   └── Shawshank Redemption (1994) {guid2}.strm      ← Stream variant 2
```

**Code that creates this:**

```csharp
// File: GelatoManager.cs, SyncStreams() method, lines 518-560
var path = s.IsFile()
    ? s.Url  // Direct file URL from debrid provider
    : $"http://127.0.0.1:{httpPort}/gelato/stream?ih={s.InfoHash}"
        + (s.FileIdx is not null ? $"&idx={s.FileIdx}" : "")
        + (s.Sources is { Count: > 0 }
            ? $"&trackers={Uri.EscapeDataString(string.Join(',', s.Sources))}"
            : "");

// Create Video item (not shortcut)
var target = new Video {
    Path = path,  // ← Store HTTP URL directly (no gelato://stub/...)
    IsVirtualItem = true,
    PrimaryVersionId = primary.Id.ToString(),  // ← Link to primary item
};

// These are NOT written to .strm files (they're virtual database items only)
// They're played via DownloadFilter HTTP proxy intercept
```

**Example .strm file content (if created):**
```
https://debrid-cdn.example.com/Shawshank.mp4

OR

http://127.0.0.1:8096/gelato/stream?ih=abc123def456&idx=0
```

**What this means:**
- Direct HTTP URLs (debrid CDN URLs)
- Or local `/gelato/stream` endpoint for torrent streaming
- DownloadFilter intercepts and proxies the request

---

## The Stub File Mystery: Why Does Gelato Need Them?

### Problem It Solves

**Jellyfin's Library Scanner Behavior:**

```
User configures library path:
/media/jellyfin/movies

Without stub.txt:
    ↓
Jellyfin scans /media/jellyfin/movies
    ↓
Finds no files yet (items haven't been added)
    ↓
Doesn't create Folder item in database
    ↓
Later, when Gelato adds items:
    ↓
Database has items but no Folder parent
    ↓
Items can't be linked properly
```

### Solution: Force Folder Creation

```csharp
// File: GelatoManager.cs, SeedFolder() method, lines 158-170
public static void SeedFolder(string path) {
    Directory.CreateDirectory(path);
    var seed = System.IO.Path.Combine(path, "stub.txt");
    if (!File.Exists(seed)) {
        File.WriteAllText(
            seed,
            "This is a seed file created by Gelato so that library scans are triggered. Do not remove."
        );
    }
    var ignore = System.IO.Path.Combine(path, ".ignore");
    File.Delete(ignore);
}

// Called from: TryGetFolder(), line 197
public Folder? TryGetFolder(string path) {
    if (string.IsNullOrWhiteSpace(path))
        return null;

    SeedFolder(path);  // ← Create stub.txt

    return _repo
        .GetItemList(new InternalItemsQuery { IsDeadPerson = true, Path = path })
        .OfType<Folder>()
        .FirstOrDefault();
}
```

**Why this works:**

1. **Create stub.txt** in the folder
2. **Jellyfin detects file system change** (new file appeared)
3. **Triggers library scan** for that folder
4. **Folder item created in database** (even though it only contains stub.txt)
5. **Gelato can now parent items** to this Folder item

### Gotchas

**The stub.txt comment explicitly says:** "Do not remove"

Why? If a user deletes stub.txt:
- Jellyfin might not detect changes in the folder
- New items might not be added to library correctly
- But it's not catastrophic — just requires a manual library scan

**Gelato also deletes `.ignore`:**
```csharp
File.Delete(ignore);  // Remove .ignore file if present
```

Why? Because if there's a `.ignore` file, Jellyfin won't scan that folder at all.

---

## Does Emby Need Stub Files?

### The Question

**Does Emby's library scanner:**
- Create Folder items even if the directory is empty?
- Require a file to exist before scanning?
- Have the same behavior as Jellyfin?

### Likely Answers

**Emby's behavior (based on architecture):**

1. **Emby's Scanner Likely Creates Folders Early**
   - Emby may create Folder items when a path is added to library
   - Doesn't wait for file system changes
   - Unlike Jellyfin's more passive scanning

2. **EmbyStreams Doesn't Use Stub Files**
   - No evidence in the codebase
   - No documentation mentioning them
   - Suggests Emby handles empty folders differently

3. **Why Emby Might Not Need Them**
   - When user configures: `SyncPathMovies = /media/embystreams/movies`
   - Emby likely creates a Movies folder in library immediately
   - Doesn't wait for files to appear
   - .strm files get added to already-existing folder

### How to Verify

To test if Emby needs stub files:

```bash
# Step 1: Create empty library path
mkdir -p /media/test-lib/movies

# Step 2: Add to Emby library without any files
# (Go to Emby Dashboard → Library → Add folder)

# Step 3: Check if folder item exists
# If it does: Emby doesn't need stub files
# If it doesn't: Emby might need stub files

# Step 4: Add a .strm file
echo "http://example.com/video.mp4" > /media/test-lib/movies/test.strm

# Step 5: Scan library
# If folder item now appears: Emby needs stimulus (like stub.txt)
```

---

## Comparison: Gelato vs EmbyStreams .strm Content

### Gelato's Approach

```
.strm file content:

For catalogs (primary items):
    gelato://stub/tt0111161

For stream variants (alternate versions):
    https://debrid-cdn.example.com/movie.mp4
    OR
    http://127.0.0.1:8096/gelato/stream?ih=infohash&idx=0

Interpretation:
    - Primary items use fake URIs (resolved via database lookup)
    - Alternate streams use real HTTP URLs (proxied by DownloadFilter)
```

### EmbyStreams' Approach

```
.strm file content:

Always:
    <movie>
        <Title>Shawshank Redemption</Title>
        <Link>http://localhost:8096/EmbyStreams/Play?imdb=tt0111161&api_key=xyz123</Link>
    </movie>

Interpretation:
    - URL points to EmbyStreams' /Play endpoint
    - Emby loads XML, extracts URL, calls /Play
    - /Play endpoint resolves stream and returns it
```

### Key Differences

| Aspect | Gelato | EmbyStreams |
|--------|--------|-------------|
| **.strm format** | Plain text (fake URI or HTTP URL) | XML with Link tag |
| **Primary items** | Fake URI (`gelato://stub/...`) | Real URL (`/Play?...`) |
| **Resolved via** | Database lookup + DownloadFilter | /Play endpoint |
| **Stub files** | Required (triggers folder creation) | Not needed (Emby handles it) |
| **Multiple streams** | Alternate versions in UI | Multiple .strm files? |

---

## Architectural Decision Tree

```
Jellyfin Plugin
    ↓
Two design paths:

PATH A (Gelato — Database-Native):
    ├─ Create items in Jellyfin database (virtual items)
    ├─ Store fake gelato://stub/... URIs in .strm files
    ├─ Use stub.txt to trigger folder creation
    ├─ DownloadFilter intercepts GetDownload calls
    ├─ Resolves from database or proxies HTTP requests
    └─ Alternate versions shown in UI

PATH B (EmbyStreams — File-Based):
    ├─ Create .strm XML files on disk
    ├─ Store real URLs in .strm content
    ├─ Emby discovers files via library scan
    ├─ Custom /Play endpoint resolves streams
    ├─ Pre-caches to SQLite
    └─ No alternate versions (or separate .strm files per variant)
```

---

## Implementation Notes for EmbyStreams

### Do We Need Stub Files?

**Answer: Probably NOT, but could be defensive**

**Current Implementation:**
1. User creates `/media/embystreams/movies` folder
2. User adds to Emby library
3. Emby creates Movies folder item
4. EmbyStreams adds .strm files
5. Library scan picks up .strm files

**Why stub.txt might help:**
- If Emby doesn't create folder items for empty directories
- Defensive measure: ensures folder always exists
- Cost: One extra file per library (negligible)

**Recommendation:**
```csharp
// In DatabaseManager or similar, when folders are initialized:
public static void EnsureFolderExists(string path) {
    Directory.CreateDirectory(path);

    // Optional: Create stub file for defensive scanning
    var stub = Path.Combine(path, ".embystreams-stub");
    if (!File.Exists(stub)) {
        File.WriteAllText(stub, "Stub file for library scan trigger. Safe to delete.");
    }
}
```

### Alternate Versions Strategy

**Current:** Likely not supported (would create multiple .strm files)

**Gelato's approach (show in UI):** Would require database injection + DownloadFilter

**Simpler approach for EmbyStreams:**
- Keep current design (one .strm per item)
- When user clicks "play stream variant", update .strm file with new URL
- Or: Add REST API to switch streams (similar to Discover AddToLibrary)

---

## References

- **Gelato Files:**
  - `GelatoManager.cs` (line 158) — SeedFolder()
  - `GelatoManager.cs` (line 1193) — IntoBaseItem() Path assignment
  - `GelatoManager.cs` (line 1143-1147) — .strm file creation
  - `GelatoManager.cs` (line 518-560) — SyncStreams() path setup

- **Jellyfin Concepts:**
  - Shortcut items (IsShortcut = true)
  - Alternate versions (LinkedAlternateVersions)
  - Virtual items (IsVirtualItem = true)
  - Library scanning behavior

- **EmbyStreams Approach:**
  - File-based discovery (no stub files observed)
  - XML-based .strm format
  - REST endpoint-based resolution

---

**Analysis Completed:** 2026-03-29
