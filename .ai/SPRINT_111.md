# Sprint 111 — Sync Pipeline (v3.3 Manifest Processing)

**Version:** v3.3 | **Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 110

---

## Overview

Sprint 111 implements sync pipeline that processes source manifests and drives items through lifecycle machine: fetch → filter → diff → Known/Hydrated → Created → Indexed → Active.

**Key Components:**
- ManifestFetcher - Fetches source manifests
- ManifestFilter - Filters manifest entries
- ManifestDiff - Compares manifest vs database
- SyncTask - Orchestrates full sync pipeline

---

## Phase 111A — ManifestFetcher

### FIX-111A-01: Create ManifestFetcher

**File:** `Services/ManifestFetcher.cs`

```csharp
public class ManifestFetcher
{
    private readonly AioStreamsClient _client;
    private readonly IDatabaseManager _db;
    private readonly ILogger _logger;

    public async Task<Manifest> FetchManifestAsync(string sourceUrl, CancellationToken ct = default)
    {
        try
        {
            var manifest = await _client.GetManifestAsync(sourceUrl, ct);
            _logger.Info("Fetched manifest from {Url}: {Count} entries", sourceUrl, manifest.Entries.Count);
            return manifest;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch manifest from {Url}", sourceUrl);
            throw;
        }
    }

    public async Task<List<ManifestEntry>> FetchAllManifestsAsync(CancellationToken ct = default)
    {
        var sources = await _db.GetEnabledSourcesAsync(ct);
        var entries = new List<ManifestEntry>();

        foreach (var source in sources)
        {
            try
            {
                var manifest = await FetchManifestAsync(source.Url, ct);
                entries.AddRange(manifest.Entries);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to fetch manifest for source {Name}", source.Name);
                // Continue with other sources
            }
        }

        return entries;
    }
}
```

**Acceptance Criteria:**
- [ ] Fetches manifest from AIOStreams
- [ ] Logs fetch results
- [ ] Fetches from all enabled sources
- [ ] Handles fetch errors gracefully (continues with other sources)

---

## Phase 111B — ManifestFilter

### FIX-111B-01: Create ManifestFilter

**File:** `Services/ManifestFilter.cs`

```csharp
public class ManifestFilter
{
    private readonly PluginConfiguration _config;
    private readonly IDatabaseManager _db;
    private readonly DigitalReleaseGateService _releaseGate;
    private readonly ILogger _logger;

    public async Task<List<ManifestEntry>> FilterEntriesAsync(
        List<ManifestEntry> entries,
        SourceType sourceType,
        CancellationToken ct = default)
    {
        var filtered = new List<ManifestEntry>();

        foreach (var entry in entries)
        {
            // FILTER ORDER (CRITICAL - MUST FOLLOW THIS SEQUENCE):

            // 1. Blocked check (ALWAYS FIRST)
            if (await IsBlockedAsync(entry, ct))
            {
                _logger.Debug("Skipping blocked item: {Id}", entry.Id);
                continue;
            }

            // 2. Your Files check (skip items matching user's local files)
            if (await IsYourFilesMatchAsync(entry, ct))
            {
                _logger.Debug("Skipping Your Files match: {Id}", entry.Id);
                continue;
            }

            // 3. Digital Release Gate (built-in sources only)
            if (sourceType == SourceType.BuiltIn && !await _releaseGate.IsDigitallyReleasedAsync(entry.Id, ct))
            {
                _logger.Debug("Skipping non-digitally released item: {Id}", entry.Id);
                continue;
            }

            // 4. Duplicate check (skip items already in database)
            if (await IsDuplicateAsync(entry, ct))
            {
                _logger.Debug("Skipping duplicate item: {Id}", entry.Id);
                continue;
            }

            // 5. Cap check (respect per-source item limits)
            if (await IsOverCapAsync(entry, ct))
            {
                _logger.Debug("Skipping over-cap item: {Id}", entry.Id);
                continue;
            }

            filtered.Add(entry);
        }

        return filtered;
    }

    private async Task<bool> IsBlockedAsync(ManifestEntry entry, CancellationToken ct)
    {
        var mediaId = MediaId.Parse(entry.Id);
        var item = await _db.GetMediaItemByPrimaryIdAsync(mediaId, ct);
        return item?.Blocked == true;
    }

    private async Task<bool> IsYourFilesMatchAsync(ManifestEntry entry, CancellationToken ct)
    {
        // Check if item is superseded (indicates Your Files match)
        var mediaId = MediaId.Parse(entry.Id);
        var item = await _db.GetMediaItemByPrimaryIdAsync(mediaId, ct);
        return item?.Superseded == true;
    }

    private async Task<bool> IsDuplicateAsync(ManifestEntry entry, CancellationToken ct)
    {
        var mediaId = MediaId.Parse(entry.Id);
        return await _db.MediaItemExistsByPrimaryIdAsync(mediaId, ct);
    }

    private async Task<bool> IsOverCapAsync(ManifestEntry entry, CancellationToken ct)
    {
        // Check if source has reached MaxItems limit
        // Implementation depends on source tracking
        return false; // Placeholder - source cap enforcement
    }
}
```

**Acceptance Criteria:**
- [ ] Filters in correct order: Blocked → Your Files → Digital Release Gate → Duplicate → Cap
- [ ] All filter checks are async (no GetAwaiter().GetResult())
- [ ] Logs skip reasons at Debug level
- [ ] Returns filtered list

---

## Phase 111C — ManifestDiff

### FIX-111C-01: Create ManifestDiff

**File:** `Services/ManifestDiff.cs`

```csharp
public class ManifestDiff
{
    public class DiffResult
    {
        public List<ManifestEntry> NewItems { get; set; } = new();
        public List<MediaItem> RemovedItems { get; set; } = new();
        public List<MediaItem> ExistingItems { get; set; } = new();
    }

    public async Task<DiffResult> DiffAsync(
        List<ManifestEntry> manifestEntries,
        CancellationToken ct = default)
    {
        var result = new DiffResult();
        var existingItems = await _db.GetAllMediaItemsAsync(ct);

        var manifestIds = manifestEntries
            .Select(e => MediaId.Parse(e.Id))
            .ToHashSet();

        // Find new items (in manifest but not in DB)
        foreach (var entry in manifestEntries)
        {
            var mediaId = MediaId.Parse(entry.Id);
            var existing = existingItems.FirstOrDefault(i =>
                i.PrimaryIdType == mediaId.Type.ToString().ToLower() &&
                i.PrimaryIdValue == mediaId.Value);

            if (existing == null)
            {
                result.NewItems.Add(entry);
            }
            else
            {
                result.ExistingItems.Add(existing);
            }
        }

        // Find removed items (in DB but not in manifest)
        foreach (var item in existingItems)
        {
            var mediaId = new MediaId
            {
                Type = Enum.Parse<MediaIdType>(item.PrimaryIdType, true),
                Value = item.PrimaryIdValue
            };

            if (!manifestIds.Contains(mediaId))
            {
                result.RemovedItems.Add(item);
            }
        }

        return result;
    }
}
```

**Acceptance Criteria:**
- [ ] Identifies new items
- [ ] Identifies removed items
- [ ] Identifies existing items
- [ ] Uses MediaId for comparison (PrimaryIdType + PrimaryIdValue)

---

## Phase 111D — SyncTask

### FIX-111D-01: Create SyncTask

**File:** `Tasks/SyncTask.cs`

```csharp
public class SyncTask : IScheduledTask
{
    private readonly ManifestFetcher _fetcher;
    private readonly ManifestFilter _filter;
    private readonly ManifestDiff _diff;
    private readonly ItemPipelineService _pipeline;
    private readonly ILogger _logger;

    public string Name => "EmbyStreams Sync";
    public string Key => "embystreams_sync";
    public string Description => "Syncs manifest entries to library";
    public string Category => "EmbyStreams";

    public async Task ExecuteAsync(CancellationToken ct, IProgress<double> progress)
    {
        await Plugin.SyncLock.WaitAsync(ct);
        try
        {
            progress?.Report(0);

            // Step 1: Fetch manifest
            _logger.Info("Fetching manifest...");
            var manifest = await _fetcher.FetchAllManifestsAsync(ct);
            progress?.Report(20);

            // Step 2: Filter entries (blocked first, then your files, then release gate)
            _logger.Info("Filtering {Count} entries...", manifest.Count);
            var filtered = await _filter.FilterEntriesAsync(manifest, SourceType.Aio, ct);
            progress?.Report(40);

            // Step 3: Diff vs database
            _logger.Info("Diffing manifest vs database...");
            var diff = await _diff.DiffAsync(filtered, ct);
            progress?.Report(60);

            // Step 4: Process new items
            _logger.Info("Processing {Count} new items...", diff.NewItems.Count);
            foreach (var entry in diff.NewItems)
            {
                var item = CreateMediaItem(entry);
                var result = await _pipeline.ProcessItemAsync(item, PipelineTrigger.Sync, ct);
            }
            progress?.Report(80);

            // Step 5: Handle removed items
            _logger.Info("Handling {Count} removed items...", diff.RemovedItems.Count);
            foreach (var item in diff.RemovedItems)
            {
                await HandleRemovedItemAsync(item, ct);
            }
            progress?.Report(100);

            _logger.Info("Sync complete");
        }
        finally
        {
            Plugin.SyncLock.Release();
        }
    }

    private MediaItem CreateMediaItem(ManifestEntry entry)
    {
        var mediaId = MediaId.Parse(entry.Id);

        return new MediaItem
        {
            PrimaryId = mediaId,
            PrimaryIdType = mediaId.Type.ToString().ToLower(),
            PrimaryIdValue = mediaId.Value,
            Title = entry.Name,
            Year = entry.Year,
            MediaType = entry.Type,
            Status = ItemStatus.Known,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task HandleRemovedItemAsync(MediaItem item, CancellationToken ct)
    {
        // Check if item is saved or has enabled source (coalition rule)
        var hasEnabledSource = await _db.ItemHasEnabledSourceAsync(item.Id, ct);

        if (item.Saved || hasEnabledSource)
        {
            // Keep item - start grace period for potential removal
            _logger.Info("Starting grace period for removed item: {Id}", item.Id);
            item.GraceStartedAt = DateTimeOffset.UtcNow;
            await _db.UpdateMediaItemAsync(item, ct);
        }
        else
        {
            // Remove item from library
            await _pipeline.RemoveItemAsync(item, ct);
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Fetches manifest
- [ ] Filters entries in correct order
- [ ] Diffs vs database
- [ ] Processes new items through pipeline
- [ ] Handles removed items with grace period (not Superseded status)
- [ ] Reports progress
- [ ] Uses SyncLock for concurrency control

---

## Sprint 111 Dependencies

- **Previous Sprint:** 110 (Services Layer)
- **Blocked By:** Sprint 110
- **Blocks:** Sprint 112 (Stream Resolution and Playback)

---

## Sprint 111 Completion Criteria

- [ ] ManifestFetcher fetches from AIOStreams
- [ ] ManifestFilter filters in correct order (blocked → your files → release gate → duplicate → cap)
- [ ] ManifestFilter uses async throughout (no GetAwaiter().GetResult())
- [ ] ManifestDiff produces accurate diff
- [ ] SyncTask orchestrates full pipeline
- [ ] HandleRemovedItemAsync starts grace period (not Superseded status)
- [ ] Progress reporting works
- [ ] SyncLock prevents concurrent syncs
- [ ] Build succeeds
- [ ] E2E: Full sync cycle completes successfully

---

## Sprint 111 Notes

**Sync Pipeline Flow:**
1. Fetch manifest from all enabled sources
2. Filter entries in THIS ORDER (as specified in §6.4):
   a. Blocked items → skip (ALWAYS FIRST)
   b. Your Files items → skip (items matching user's local files)
   c. Digital Release Gate → skip if not yet digitally released (built-in sources only)
   d. Duplicate items → skip (already in database)
   e. Cap check → skip if source at MaxItems limit
3. Diff manifest vs database
4. Process new items: Known → Resolved → Hydrated → Created → Indexed → Active
5. Handle removed items: start grace period or delete

**Filter Order (CRITICAL):**

The sync pipeline MUST implement filters in this exact sequence:

```csharp
// Phase 111B: Filter entries
foreach (var entry in manifestEntries)
{
    // 1. Blocked check (ALWAYS FIRST)
    if (await IsBlockedAsync(item, ct)) continue;

    // 2. Your Files check
    if (await IsYourFilesMatchAsync(entry, ct)) continue;

    // 3. Digital Release Gate (built-in sources only)
    if (sourceType == SourceType.BuiltIn && !await _releaseGate.IsDigitallyReleasedAsync(entry.Id, ct)) continue;

    // 4. Duplicate check
    if (await IsDuplicateAsync(entry, ct)) continue;

    // 5. Cap check
    if (await IsOverCapAsync(entry, ct)) continue;

    // 6. Process entry
    ProcessEntry(entry);
}
```

**Removed Items Handling:**
- Items with Saved=true or enabled source membership: start grace period (GraceStartedAt = now)
- Items without Saved flag and no enabled source: remove from library immediately
- Removal pipeline (Sprint 115) handles grace period expiration

**Your Files Superseded Check:**

Sync pipeline checks Superseded=true flag to skip items that were already identified as matching user's local files. This prevents re-processing superseded items.

**Progress Reporting:**
- 0-20%: Fetch manifest
- 20-40%: Filter entries
- 40-60%: Diff vs database
- 60-80%: Process new items
- 80-100%: Handle removed items

**Concurrency Control:**
- SyncLock ensures only one sync runs at a time
- CancellationToken respected throughout
- Failed items logged to item_pipeline_log

**Media ID Handling:**
- MediaItem uses TEXT UUID Id (Guid.NewGuid().ToString("N"))
- PrimaryId is MediaId struct with Type and Value
- Database stores PrimaryIdType and PrimaryIdValue as separate columns
- Comparison uses composite key: (PrimaryIdType, PrimaryIdValue)
