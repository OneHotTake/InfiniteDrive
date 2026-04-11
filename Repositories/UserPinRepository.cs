using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;
using SQLitePCL.pretty;

namespace InfiniteDrive.Repositories
{
    /// <summary>
    /// Repository for <c>user_item_pins</c> table.
    /// Tracks per-user pin operations on catalog items.
    /// </summary>
    public class UserPinRepository
    {
        private readonly DatabaseManager _db;
        private readonly ILogger _logger;

        private static readonly SemaphoreSlim _dbWriteGate = new(1, 1);

        public UserPinRepository(DatabaseManager db, ILogger logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Adds a new pin record.
        /// </summary>
        public async Task AddPinAsync(
            string embyUserId,
            string catalogItemId,
            string pinSource,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO user_item_pins (emby_user_id, catalog_item_id, pinned_at, pin_source)
                VALUES (@user_id, @catalog_id, @pinned_at, @pin_source);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@user_id", embyUserId);
                BindText(cmd, "@catalog_id", catalogItemId);
                BindText(cmd, "@pinned_at", System.DateTime.UtcNow.ToString("o"));
                BindText(cmd, "@pin_source", pinSource);
            }, ct);
        }

        /// <summary>
        /// Removes a pin record.
        /// </summary>
        public async Task RemovePinAsync(
            string embyUserId,
            string catalogItemId,
            CancellationToken ct = default)
        {
            const string sql = @"
                DELETE FROM user_item_pins
                WHERE emby_user_id = @user_id AND catalog_item_id = @catalog_id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@user_id", embyUserId);
                BindText(cmd, "@catalog_id", catalogItemId);
            }, ct);
        }

        /// <summary>
        /// Returns all pins for a given user.
        /// </summary>
        public async Task<List<UserItemPin>> GetPinsForUserAsync(
            string embyUserId,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, emby_user_id, catalog_item_id, pinned_at, pin_source
                FROM user_item_pins
                WHERE emby_user_id = @user_id;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@user_id", embyUserId);
            }, ReadUserPin);
        }

        /// <summary>
        /// Checks if any user has a pin on the given catalog item.
        /// Used by Deep Clean eligibility check.
        /// </summary>
        public Task<bool> HasAnyPinsAsync(
            string catalogItemId,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT COUNT(*) > 0
                FROM user_item_pins
                WHERE catalog_item_id = @catalog_id;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@catalog_id", catalogItemId);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.GetInt(0) > 0);
            return Task.FromResult(false);
        }

        /// <summary>
        /// Returns total pin count for a catalog item.
        /// Used by admin UI.
        /// </summary>
        public Task<int> GetPinCountAsync(
            string catalogItemId,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT COUNT(*) FROM user_item_pins
                WHERE catalog_item_id = @catalog_id;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@catalog_id", catalogItemId);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.GetInt(0));
            return Task.FromResult(0);
        }

        // ── ORM mapper ────────────────────────────────────────────────

        private static UserItemPin ReadUserPin(IResultSet row) => new()
        {
            Id = row.GetInt64(0),
            EmbyUserId = row.GetString(1),
            CatalogItemId = row.GetString(2),
            PinnedAt = row.GetString(3),
            PinSource = row.GetString(4),
        };

        // ── SQLite helpers ────────────────────────────────────────────

        private IDatabaseConnection OpenConnection()
            => SQLite3.Open(_db.GetDatabasePath(), ConnectionFlags.ReadWrite | ConnectionFlags.Create, null, true);

        private async Task ExecuteWriteAsync(
            string sql, Action<IStatement> bindParams, CancellationToken ct = default)
        {
            await _dbWriteGate.WaitAsync(ct);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c =>
                {
                    using var stmt = c.PrepareStatement(sql);
                    bindParams(stmt);
                    while (stmt.MoveNext()) { }
                });
            }
            finally
            {
                _dbWriteGate.Release();
            }
        }

        private Task<List<T>> QueryListAsync<T>(
            string sql, Action<IStatement>? bindParams, Func<IResultSet, T> map)
        {
            using var conn = OpenConnection();
            var results = new List<T>();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);
            foreach (var row in stmt.AsRows())
                results.Add(map(row));
            return Task.FromResult(results);
        }

        private static void BindText(IStatement stmt, string name, string value)
            => stmt.BindParameters[name].Bind(value);
    }
}
