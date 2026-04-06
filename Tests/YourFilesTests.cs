using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmbyStreams.Tests
{
    /// <summary>
    /// Tests for Your Files detection.
    /// Sprint 121E-01: Your Files tests.
    /// </summary>
    public static class YourFilesTests
    {
        /// <summary>
        /// Test: Set superseded flag
        /// </summary>
        public static async Task<string> TestSetSuperseded()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"embystreams_test_{Guid.NewGuid()}.db");
            var dbDir = Path.GetDirectoryName(dbPath);

            try
            {
                var db = new DatabaseManager(dbDir, NullLogger.Instance);
                db.Initialise();

                var item = new MediaItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    PrimaryId = new MediaId(MediaIdType.Imdb, "tt123456"),
                    Title = "Test Movie",
                    MediaType = "movie",
                    Status = ItemStatus.Active,
                    Superseded = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Act - Mark as superseded
                await db.SetSupersededAsync(item.Id, true, CancellationToken.None);

                // Assert
                var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
                if (updated == null)
                {
                    return "FAIL: Item should be retrieved";
                }
                if (!updated.Superseded)
                {
                    return "FAIL: Item should be marked as superseded";
                }
                return "PASS: Item marked as superseded successfully";
            }
            finally
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        /// <summary>
        /// Test: Clear superseded flag
        /// </summary>
        public static async Task<string> TestClearSuperseded()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"embystreams_test_{Guid.NewGuid()}.db");
            var dbDir = Path.GetDirectoryName(dbPath);

            try
            {
                var db = new DatabaseManager(dbDir, NullLogger.Instance);
                db.Initialise();

                var item = new MediaItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    PrimaryId = new MediaId(MediaIdType.Imdb, "tt123456"),
                    Title = "Test Movie",
                    MediaType = "movie",
                    Status = ItemStatus.Active,
                    Superseded = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Act - Clear superseded flag
                await db.SetSupersededAsync(item.Id, false, CancellationToken.None);

                // Assert
                var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
                if (updated == null)
                {
                    return "FAIL: Item should be retrieved";
                }
                if (updated.Superseded)
                {
                    return "FAIL: Item should not be marked as superseded";
                }
                return "PASS: Superseded flag cleared successfully";
            }
            finally
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        /// <summary>
        /// Test: Set superseded conflict flag
        /// </summary>
        public static async Task<string> TestSetSupersededConflict()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"embystreams_test_{Guid.NewGuid()}.db");
            var dbDir = Path.GetDirectoryName(dbPath);

            try
            {
                var db = new DatabaseManager(dbDir, NullLogger.Instance);
                db.Initialise();

                var item = new MediaItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    PrimaryId = new MediaId(MediaIdType.Imdb, "tt123456"),
                    Title = "Test Movie",
                    MediaType = "movie",
                    Status = ItemStatus.Active,
                    Superseded = true,
                    SupersededConflict = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Act - Mark as superseded conflict
                await db.SetSupersededConflictAsync(item.Id, true, CancellationToken.None);

                // Assert
                var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
                if (updated == null)
                {
                    return "FAIL: Item should be retrieved";
                }
                if (!updated.SupersededConflict)
                {
                    return "FAIL: Item should be marked as superseded conflict";
                }
                return "PASS: Item marked as superseded conflict successfully";
            }
            finally
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }
    }
}
