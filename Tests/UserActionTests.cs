using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace InfiniteDrive.Tests
{
    /// <summary>
    /// Tests for user actions (Save/Block/Unsave/Unblock).
    /// Sprint 121D-01: User action tests.
    /// </summary>
    public static class UserActionTests
    {
        /// <summary>
        /// Test: Save item
        /// </summary>
        public static async Task<string> TestSaveItem()
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
                    Saved = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Act - Save the item
                item.Saved = true;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Assert
                var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
                if (updated == null)
                {
                    return "FAIL: Item should be retrieved";
                }
                if (!updated.Saved)
                {
                    return "FAIL: Item should be marked as saved";
                }
                return "PASS: Item saved successfully";
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
        /// Test: Unsave item
        /// </summary>
        public static async Task<string> TestUnsaveItem()
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
                    Saved = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Act - Unsave the item
                item.Saved = false;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Assert
                var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
                if (updated == null)
                {
                    return "FAIL: Item should be retrieved";
                }
                if (updated.Saved)
                {
                    return "FAIL: Item should not be marked as saved";
                }
                return "PASS: Item unsaved successfully";
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
        /// Test: Block item
        /// </summary>
        public static async Task<string> TestBlockItem()
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
                    Blocked = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Act - Block the item
                item.Blocked = true;
                item.BlockedAt = DateTimeOffset.UtcNow;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Assert
                var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
                if (updated == null)
                {
                    return "FAIL: Item should be retrieved";
                }
                if (!updated.Blocked)
                {
                    return "FAIL: Item should be marked as blocked";
                }
                return "PASS: Item blocked successfully";
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
        /// Test: Unblock item
        /// </summary>
        public static async Task<string> TestUnblockItem()
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
                    Blocked = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Act - Unblock the item
                item.Blocked = false;
                item.BlockedAt = null;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await db.UpsertMediaItemAsync(item, CancellationToken.None);

                // Assert
                var updated = await db.GetMediaItemAsync(item.Id, CancellationToken.None);
                if (updated == null)
                {
                    return "FAIL: Item should be retrieved";
                }
                if (updated.Blocked)
                {
                    return "FAIL: Item should not be marked as blocked";
                }
                return "PASS: Item unblocked successfully";
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
