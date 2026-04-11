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
    /// Compares manifest entries against database to identify new and removed items.
    /// </summary>
    public class ManifestDiff
    {
        public class DiffResult
        {
            public List<ManifestEntry> NewItems { get; set; } = new();
            public List<MediaItem> RemovedItems { get; set; } = new();
            public List<MediaItem> ExistingItems { get; set; } = new();
        }

        private readonly DatabaseManager _db;
        private readonly ILogger<ManifestDiff> _logger;

        public ManifestDiff(DatabaseManager db, ILogger<ManifestDiff> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Compares manifest entries against database.
        /// </summary>
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
                var mediaId = new MediaId(
                    Enum.Parse<MediaIdType>(item.PrimaryIdType, ignoreCase: true),
                    item.PrimaryIdValue);

                if (!manifestIds.Contains(mediaId))
                {
                    result.RemovedItems.Add(item);
                }
            }

            _logger.LogInformation("[ManifestDiff] New: {Count}, Existing: {Count}, Removed: {Count}",
                result.NewItems.Count, result.ExistingItems.Count, result.RemovedItems.Count);

            return result;
        }
    }
}
