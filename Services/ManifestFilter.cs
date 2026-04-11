using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Filters manifest entries based on various criteria.
    /// </summary>
    public class ManifestFilter
    {
        private readonly DigitalReleaseGateService _releaseGate;
        private readonly DatabaseManager _db;
        private readonly ILogger<ManifestFilter> _logger;

        public ManifestFilter(
            DigitalReleaseGateService releaseGate,
            DatabaseManager db,
            ILogger<ManifestFilter> logger)
        {
            _releaseGate = releaseGate;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Filters entries in the correct order: Blocked → Your Files → Digital Release Gate → Duplicate → Cap.
        /// </summary>
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
                    _logger.LogDebug("[ManifestFilter] Skipping blocked item: {Id}", entry.Id);
                    continue;
                }

                // 2. Your Files check (skip items matching user's local files)
                if (await IsYourFilesMatchAsync(entry, ct))
                {
                    _logger.LogDebug("[ManifestFilter] Skipping Your Files match: {Id}", entry.Id);
                    continue;
                }

                // 3. Digital Release Gate (built-in sources only)
                if (sourceType == SourceType.BuiltIn && !await IsDigitallyReleasedAsync(entry, ct))
                {
                    _logger.LogDebug("[ManifestFilter] Skipping non-digitally released item: {Id}", entry.Id);
                    continue;
                }

                // 4. Duplicate check (skip items already in database)
                if (await IsDuplicateAsync(entry, ct))
                {
                    _logger.LogDebug("[ManifestFilter] Skipping duplicate item: {Id}", entry.Id);
                    continue;
                }

                // 5. Cap check (respect per-source item limits)
                if (await IsOverCapAsync(entry, ct))
                {
                    _logger.LogDebug("[ManifestFilter] Skipping over-cap item: {Id}", entry.Id);
                    continue;
                }

                filtered.Add(entry);
            }

            return filtered;
        }

        /// <summary>
        /// Checks if an item is blocked.
        /// </summary>
        private async Task<bool> IsBlockedAsync(ManifestEntry entry, CancellationToken ct)
        {
            var mediaId = MediaId.Parse(entry.Id);
            var item = await _db.FindMediaItemByProviderIdAsync(
                mediaId.Type.ToString(),
                mediaId.Value,
                ct);
            return item?.Blocked == true;
        }

        /// <summary>
        /// Checks if item matches Your Files (is superseded).
        /// </summary>
        private async Task<bool> IsYourFilesMatchAsync(ManifestEntry entry, CancellationToken ct)
        {
            var mediaId = MediaId.Parse(entry.Id);
            var item = await _db.FindMediaItemByProviderIdAsync(
                mediaId.Type.ToString(),
                mediaId.Value,
                ct);
            return item?.Superseded == true;
        }

        /// <summary>
        /// Checks if item is digitally released.
        /// </summary>
        private async Task<bool> IsDigitallyReleasedAsync(ManifestEntry entry, CancellationToken ct)
        {
            var mediaId = MediaId.Parse(entry.Id);
            return await _releaseGate.IsDigitallyReleasedAsync(
                mediaId,
                entry.Type,
                SourceType.BuiltIn.ToString(),
                ct);
        }

        /// <summary>
        /// Checks if item is a duplicate (already in database).
        /// </summary>
        private async Task<bool> IsDuplicateAsync(ManifestEntry entry, CancellationToken ct)
        {
            var mediaId = MediaId.Parse(entry.Id);
            return await _db.MediaItemExistsByPrimaryIdAsync(mediaId, ct);
        }

        /// <summary>
        /// Checks if adding this item would exceed source cap.
        /// </summary>
        private async Task<bool> IsOverCapAsync(ManifestEntry entry, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(entry.SourceId))
                return false;

            // Check if source has reached MaxItems limit
            var source = (await _db.GetEnabledSourcesAsync(ct))
                .FirstOrDefault(s => s.Id == entry.SourceId);

            if (source == null || source.MaxItems <= 0)
                return false;

            // Count existing items for this source
            var existingItems = await _db.FindMediaItemsBySourceAsync(source.Id, ct);
            return existingItems.Count >= source.MaxItems;
        }
    }
}
