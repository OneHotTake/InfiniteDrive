using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Type of rehydration operation.
    /// </summary>
    public enum RehydrationType
    {
        AddSlot,
        RemoveSlot,
        ChangeDefault
    }

    /// <summary>
    /// Result of a rehydration sweep.
    /// </summary>
    public class RehydrationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int ItemsProcessed { get; set; }
        public int ItemsSkipped { get; set; }
    }

    /// <summary>
    /// Orchestrates catalog-wide rehydration: adding slots, removing slots,
    /// and changing the default slot across all titles.
    /// <para>
    /// Each operation touches the filesystem (.strm/.nfo) and database
    /// (materialized_versions, candidates, version_snapshots) in a
    /// coordinated manner.
    /// </para>
    /// </summary>
    public class RehydrationService
    {
        private readonly ILogger _logger;

        public RehydrationService(ILogger logger)
        {
            _logger = logger;
        }

        // ── Add Slot ────────────────────────────────────────────────────────────

        /// <summary>
        /// Materializes a new slot for all active media items.
        /// <list type="number">
        ///   <item>Get enabled slots and the target slot from VersionSlotRepository</item>
        ///   <item>Get active catalog items from DatabaseManager</item>
        ///   <item>For each item: build URL, write .strm/.nfo via VersionMaterializer</item>
        ///   <item>Record in materialized_versions</item>
        ///   <item>Respect CooldownGate between items</item>
        ///   <item>Trigger library scan on completion</item>
        /// </list>
        /// </summary>
        public async Task<RehydrationResult> AddSlotAsync(
            string slotKey,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
                return Fail("Plugin not initialized");
            var config = plugin.Configuration;
            var slotRepo = plugin.VersionSlotRepository;
            var db = plugin.DatabaseManager;

            var slot = await slotRepo.GetSlotAsync(slotKey, ct);
            if (slot == null)
                return Fail($"Slot '{slotKey}' not found");

            if (!slot.Enabled)
                return Fail($"Slot '{slotKey}' is not enabled");

            var defaultSlot = await slotRepo.GetDefaultSlotAsync(ct)
                ?? await slotRepo.GetSlotAsync("hd_broad", ct);

            if (defaultSlot == null)
                return Fail("No default slot found");

            var materializer = new VersionMaterializer(_logger);
            var matRepo = new MaterializedVersionRepository(db, _logger);

            var items = await db.GetActiveCatalogItemsAsync();
            if (items == null || items.Count == 0)
                return Ok("No active catalog items to process", 0, 0);

            // Cap items per run from CooldownProfile (Sprint 155)
            var rehyCap = Plugin.Instance?.CooldownGate?.Profile.RehydrationPerRun ?? 500;
            if (items.Count > rehyCap)
            {
                _logger.LogInformation("[Rehydration] Capping to {Cap} of {Total} items (profile limit)",
                    rehyCap, items.Count);
                items = items.Take(rehyCap).ToList();
            }

            int processed = 0;
            int skipped = 0;
            int total = items.Count;

            _logger.LogInformation(
                "[Rehydration] AddSlot '{SlotKey}' starting for {Count} items",
                slotKey, total);

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = items[i];

                try
                {
                    // Determine sync path based on media type
                    var syncPath = item.MediaType == "series"
                        ? config.SyncPathShows
                        : config.SyncPathMovies;

                    if (string.IsNullOrWhiteSpace(syncPath))
                    {
                        skipped++;
                        continue;
                    }

                    // Build base path: {syncPath}/{FolderName}
                    var folderName = Services.StrmWriterService.SanitisePathPublic(
                        $"{item.Title ?? "Unknown"} ({item.Year})");

                    if (!string.IsNullOrEmpty(item.ImdbId) && item.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                        folderName += $" [imdbid-{item.ImdbId}]";

                    var basePath = Path.Combine(syncPath, folderName);
                    var baseName = Services.StrmWriterService.SanitisePathPublic(
                        item.Title ?? "Unknown");

                    // Build .strm URL with resolve token
                    // Note: Season/Episode are null for series-level catalog items
                    // Episode-level .strm files are handled by CatalogSyncTask expansion
                    var strmUrl = materializer.BuildStrmUrl(
                        config.EmbyBaseUrl,
                        item.ImdbId,
                        slot.SlotKey,
                        item.MediaType,
                        null, // Season
                        null); // Episode

                    // Write .strm file
                    var strmPath = materializer.WriteStrmFile(
                        basePath, baseName, slot, defaultSlot, strmUrl);

                    // Write .nfo file
                    var nfoRoot = item.MediaType == "series" ? "episodedetails" : "movie";
                    var nfoPath = materializer.WriteNfoFile(
                        basePath, baseName, slot, defaultSlot,
                        null, nfoRoot, item);

                    // Record in materialized_versions
                    var matVersion = new MaterializedVersion
                    {
                        Id = Guid.NewGuid().ToString(),
                        MediaItemId = item.Id,
                        SlotKey = slot.SlotKey,
                        StrmPath = strmPath,
                        NfoPath = nfoPath,
                        StrmUrlHash = VersionMaterializer.ComputeStrmUrlHash(strmUrl),
                        IsBase = slot.SlotKey == defaultSlot.SlotKey,
                    };

                    await matRepo.UpsertMaterializedVersionAsync(matVersion, ct);
                    processed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Rehydration] AddSlot failed for item {ImdbId}", item.ImdbId);
                    skipped++;
                }

                progress?.Report((double)(i + 1) / total * 100);

                // Respect cooldown gate between items (Sprint 155)
                var gate = Plugin.Instance?.CooldownGate;
                if (gate != null && i < items.Count - 1)
                    await gate.WaitAsync(CooldownKind.StreamResolve, ct);
            }

            _logger.LogInformation(
                "[Rehydration] AddSlot '{SlotKey}' complete: {Processed} processed, {Skipped} skipped",
                slotKey, processed, skipped);

            return Ok(
                $"Added slot '{slotKey}': {processed} items processed, {skipped} skipped",
                processed,
                skipped);
        }

        // ── Remove Slot ─────────────────────────────────────────────────────────

        /// <summary>
        /// Removes a slot from all titles: deletes .strm/.nfo files,
        /// materialized_versions records, candidates, and snapshots.
        /// </summary>
        public async Task<RehydrationResult> RemoveSlotAsync(
            string slotKey,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
                return Fail("Plugin not initialized");
            var db = plugin.DatabaseManager;

            var matRepo = new MaterializedVersionRepository(db, _logger);
            var candRepo = new CandidateRepository(db, _logger);
            var snapRepo = new SnapshotRepository(db, _logger);

            var materialized = await matRepo.GetAllMaterializedForSlotAsync(slotKey, ct);
            if (materialized.Count == 0)
                return Ok($"No materialized versions found for slot '{slotKey}'", 0, 0);

            int processed = 0;
            int skipped = 0;
            int total = materialized.Count;

            _logger.LogInformation(
                "[Rehydration] RemoveSlot '{SlotKey}' starting: {Count} materialized versions",
                slotKey, total);

            for (int i = 0; i < materialized.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var mv = materialized[i];

                try
                {
                    // Delete .strm file
                    if (!string.IsNullOrEmpty(mv.StrmPath) && File.Exists(mv.StrmPath))
                    {
                        File.Delete(mv.StrmPath);
                        _logger.LogDebug("[Rehydration] Deleted .strm: {Path}", mv.StrmPath);
                    }

                    // Delete .nfo file
                    if (!string.IsNullOrEmpty(mv.NfoPath) && File.Exists(mv.NfoPath))
                    {
                        File.Delete(mv.NfoPath);
                        _logger.LogDebug("[Rehydration] Deleted .nfo: {Path}", mv.NfoPath);
                    }

                    // Delete materialized_versions record
                    await matRepo.DeleteMaterializedVersionAsync(mv.MediaItemId, slotKey, ct);

                    // Delete candidates for this slot
                    await candRepo.DeleteCandidatesAsync(mv.MediaItemId, slotKey, ct);

                    // Delete snapshot for this slot
                    await snapRepo.InvalidatePlaybackUrlAsync(mv.MediaItemId, slotKey, ct);

                    processed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Rehydration] RemoveSlot failed for item {MediaItemId}", mv.MediaItemId);
                    skipped++;
                }

                progress?.Report((double)(i + 1) / total * 100);
            }

            _logger.LogInformation(
                "[Rehydration] RemoveSlot '{SlotKey}' complete: {Processed} removed, {Skipped} failed",
                slotKey, processed, skipped);

            return Ok(
                $"Removed slot '{slotKey}': {processed} items cleaned, {skipped} failed",
                processed,
                skipped);
        }

        // ── Change Default ──────────────────────────────────────────────────────

        /// <summary>
        /// Changes the default slot: swaps is_base flags in the database and
        /// renames filesystem files to reflect the new base/default naming.
        /// <list type="number">
        ///   <item>DB: Begin transaction, swap is_base flags in materialized_versions</item>
        ///   <item>DB: Update version_slots.is_default for old and new default</item>
        ///   <item>Filesystem: Rename old base pair to suffixed with old default label</item>
        ///   <item>Filesystem: Rename new default's suffixed files to base pair (no suffix)</item>
        ///   <item>DB: Commit</item>
        /// </list>
        /// </summary>
        public async Task<RehydrationResult> ChangeDefaultAsync(
            string newDefaultSlotKey,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
                return Fail("Plugin not initialized");
            var slotRepo = plugin.VersionSlotRepository;
            var db = plugin.DatabaseManager;

            var matRepo = new MaterializedVersionRepository(db, _logger);

            var newDefault = await slotRepo.GetSlotAsync(newDefaultSlotKey, ct);
            if (newDefault == null)
                return Fail($"Slot '{newDefaultSlotKey}' not found");

            if (!newDefault.Enabled)
                return Fail($"Slot '{newDefaultSlotKey}' is not enabled");

            var oldDefault = await slotRepo.GetDefaultSlotAsync(ct);
            if (oldDefault == null)
                return Fail("No current default slot found");

            // Nothing to do if same slot
            if (oldDefault.SlotKey == newDefault.SlotKey)
                return Ok("Slot is already the default", 0, 0);

            _logger.LogInformation(
                "[Rehydration] ChangeDefault: {OldDefault} -> {NewDefault}",
                oldDefault.SlotKey, newDefault.SlotKey);

            // Get all items that have materialized versions for either slot
            var oldBaseVersions = await matRepo.GetAllMaterializedForSlotAsync(oldDefault.SlotKey, ct);
            var newDefaultVersions = await matRepo.GetAllMaterializedForSlotAsync(newDefault.SlotKey, ct);

            int processed = 0;
            int totalItems = oldBaseVersions.Count + newDefaultVersions.Count;
            int current = 0;

            // Phase 1: Rename old base files to suffixed names
            foreach (var mv in oldBaseVersions)
            {
                ct.ThrowIfCancellationRequested();
                current++;

                try
                {
                    // Rename .strm: {baseName}.strm -> {baseName} - {oldDefault.FileSuffix}.strm
                    RenameVersionFiles(mv, oldDefault, addSuffix: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Rehydration] Failed to rename old base files for {MediaItemId}", mv.MediaItemId);
                }

                progress?.Report((double)current / totalItems * 50); // First 50%
            }

            // Phase 2: Rename new default suffixed files to base names
            foreach (var mv in newDefaultVersions)
            {
                ct.ThrowIfCancellationRequested();
                current++;

                try
                {
                    // Rename .strm: {baseName} - {newDefault.FileSuffix}.strm -> {baseName}.strm
                    RenameVersionFiles(mv, newDefault, addSuffix: false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Rehydration] Failed to rename new default files for {MediaItemId}", mv.MediaItemId);
                }

                progress?.Report(50 + (double)(current - oldBaseVersions.Count) / totalItems * 50);
            }

            // Phase 3: Database updates
            // Update version_slots.is_default
            await slotRepo.SetDefaultSlotAsync(newDefaultSlotKey, ct);

            // Update is_base flags for all affected items
            var allItemsWithBoth = oldBaseVersions
                .Select(mv => mv.MediaItemId)
                .Concat(newDefaultVersions.Select(mv => mv.MediaItemId))
                .Distinct()
                .ToList();

            foreach (var mediaItemId in allItemsWithBoth)
            {
                await matRepo.SetBaseSlotAsync(mediaItemId, newDefaultSlotKey, ct);
                processed++;
            }

            _logger.LogInformation(
                "[Rehydration] ChangeDefault complete: {NewDefault} is now default, {Processed} items updated",
                newDefaultSlotKey, processed);

            return Ok(
                $"Default changed to '{newDefaultSlotKey}': {processed} items updated",
                processed, 0);
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Renames version files on disk by adding or removing the slot suffix.
        /// When <paramref name="addSuffix"/> is true, renames from base to suffixed.
        /// When false, renames from suffixed to base.
        /// </summary>
        private void RenameVersionFiles(MaterializedVersion mv, VersionSlot slot, bool addSuffix)
        {
            var suffix = slot.FileSuffix;
            if (string.IsNullOrWhiteSpace(suffix))
                return;

            var suffixPart = $" - {suffix}";

            // Rename .strm
            if (!string.IsNullOrEmpty(mv.StrmPath))
            {
                var dir = Path.GetDirectoryName(mv.StrmPath) ?? "";
                var oldName = Path.GetFileName(mv.StrmPath);

                string newName;
                if (addSuffix)
                {
                    // "Avatar.strm" -> "Avatar - HD Broad.strm"
                    var ext = Path.GetExtension(oldName);
                    var basePart = Path.GetFileNameWithoutExtension(oldName);
                    newName = basePart + suffixPart + ext;
                }
                else
                {
                    // "Avatar - 4K HDR.strm" -> "Avatar.strm"
                    var ext = Path.GetExtension(oldName);
                    var basePart = Path.GetFileNameWithoutExtension(oldName);
                    if (basePart.EndsWith(suffixPart, StringComparison.Ordinal))
                        basePart = basePart[..^suffixPart.Length];
                    newName = basePart + ext;
                }

                var newPath = Path.Combine(dir, newName);
                if (File.Exists(mv.StrmPath) && mv.StrmPath != newPath)
                {
                    File.Move(mv.StrmPath, newPath, overwrite: true);
                    _logger.LogDebug("[Rehydration] Renamed: {Old} -> {New}", mv.StrmPath, newPath);
                }
            }

            // Rename .nfo
            if (!string.IsNullOrEmpty(mv.NfoPath))
            {
                var dir = Path.GetDirectoryName(mv.NfoPath) ?? "";
                var oldName = Path.GetFileName(mv.NfoPath);

                string newName;
                if (addSuffix)
                {
                    var ext = Path.GetExtension(oldName);
                    var basePart = Path.GetFileNameWithoutExtension(oldName);
                    newName = basePart + suffixPart + ext;
                }
                else
                {
                    var ext = Path.GetExtension(oldName);
                    var basePart = Path.GetFileNameWithoutExtension(oldName);
                    if (basePart.EndsWith(suffixPart, StringComparison.Ordinal))
                        basePart = basePart[..^suffixPart.Length];
                    newName = basePart + ext;
                }

                var newPath = Path.Combine(dir, newName);
                if (File.Exists(mv.NfoPath) && mv.NfoPath != newPath)
                {
                    File.Move(mv.NfoPath, newPath, overwrite: true);
                    _logger.LogDebug("[Rehydration] Renamed: {Old} -> {New}", mv.NfoPath, newPath);
                }
            }
        }

        private static RehydrationResult Ok(string message, int processed, int skipped)
            => new() { Success = true, Message = message, ItemsProcessed = processed, ItemsSkipped = skipped };

        private static RehydrationResult Fail(string message)
            => new() { Success = false, Message = message, ItemsProcessed = 0, ItemsSkipped = 0 };
    }
}
