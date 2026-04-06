using System;
using System.IO;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmbyStreams.Tests
{
    /// <summary>
    /// Tests for the sync pipeline.
    /// Sprint 121B-01: Full sync pipeline tests.
    /// </summary>
    public static class SyncPipelineTests
    {
        /// <summary>
        /// Test: Fetches manifest - Gets all sources from database
        /// </summary>
        public static async Task<string> TestFetchesManifest()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"embystreams_test_{Guid.NewGuid()}.db");
            var dbDir = Path.GetDirectoryName(dbPath);

            try
            {
                // Act
                var db = new DatabaseManager(dbDir, NullLogger.Instance);
                db.Initialise();
                var sources = await db.GetAllSourcesAsync();

                // Assert
                if (sources == null)
                {
                    return "FAIL: Sources list should not be null";
                }
                // Note: Empty list is expected for fresh database
                return $"PASS: Sources fetched successfully (count: {sources.Count})";
            }
            finally
            {
                // Cleanup
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        /// <summary>
        /// Test: Creates source - Tests source creation and retrieval
        /// </summary>
        public static async Task<string> TestCreatesSource()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"embystreams_test_{Guid.NewGuid()}.db");
            var dbDir = Path.GetDirectoryName(dbPath);
            var source = new Source
            {
                Name = "Test Source",
                Url = "https://example.com/test.json",
                Type = SourceType.Trakt,
                Enabled = true,
                MaxItems = 100,
                SyncIntervalHours = 6,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            try
            {
                // Act
                var db = new DatabaseManager(dbDir, NullLogger.Instance);
                db.Initialise();
                await db.UpsertSourceAsync(source);
                var retrieved = await db.GetSourceAsync(source.Id);

                // Assert
                if (retrieved == null)
                {
                    return "FAIL: Source should be retrieved";
                }
                if (source.Name != retrieved.Name)
                {
                    return $"FAIL: Source name mismatch. Expected: {source.Name}, Got: {retrieved.Name}";
                }
                if (source.Type != retrieved.Type)
                {
                    return $"FAIL: Source type mismatch. Expected: {source.Type}, Got: {retrieved.Type}";
                }
                return "PASS: Source created and retrieved successfully";
            }
            finally
            {
                // Cleanup
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        /// <summary>
        /// Test: Deletes source - Tests source deletion
        /// </summary>
        public static async Task<string> TestDeletesSource()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"embystreams_test_{Guid.NewGuid()}.db");
            var dbDir = Path.GetDirectoryName(dbPath);
            var source = new Source
            {
                Name = "Test Source",
                Url = "https://example.com/test.json",
                Type = SourceType.Trakt,
                Enabled = true,
                MaxItems = 100,
                SyncIntervalHours = 6,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            try
            {
                var db = new DatabaseManager(dbDir, NullLogger.Instance);
                db.Initialise();
                await db.UpsertSourceAsync(source);

                // Act
                await db.DeleteSourceAsync(source.Id);
                var retrieved = await db.GetSourceAsync(source.Id);

                // Assert
                if (retrieved != null)
                {
                    return "FAIL: Source should be deleted";
                }
                return "PASS: Source deleted successfully";
            }
            finally
            {
                // Cleanup
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        /// <summary>
        /// Test: Items query - Tests media item retrieval with pagination
        /// </summary>
        public static async Task<string> TestItemsQuery()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"embystreams_test_{Guid.NewGuid()}.db");
            var dbDir = Path.GetDirectoryName(dbPath);
            var testItem = new MediaItem
            {
                PrimaryId = "imdb:tt123456",
                Title = "Test Movie",
                Year = 2024,
                MediaType = "movie",
                Status = ItemStatus.Known,
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            try
            {
                var db = new DatabaseManager(dbDir, NullLogger.Instance);
                db.Initialise();
                await db.UpsertMediaItemAsync(testItem);

                // Act
                var items = await db.GetItemsAsync(null, "title", "asc", 10, 0);
                var count = await db.GetItemCountAsync(null);

                // Assert
                if (items == null)
                {
                    return "FAIL: Items list should not be null";
                }
                if (count < 1)
                {
                    return "FAIL: Item count should be at least 1";
                }
                return $"PASS: Items queried successfully (count: {items.Count}, total: {count})";
            }
            finally
            {
                // Cleanup
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }
    }
}
