using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;
using SQLitePCL.pretty;

namespace InfiniteDrive.Data
{
    /// <summary>
    /// Repository for the <c>materialized_versions</c> table.
    /// Tracks which slots have been materialized as .strm/.nfo pairs per title.
    /// </summary>
    public class MaterializedVersionRepository
    {
        private readonly DatabaseManager _db;
        private readonly ILogger _logger;

        private static readonly SemaphoreSlim _dbWriteGate = new(1, 1);

        public MaterializedVersionRepository(DatabaseManager db, ILogger logger)
        {
            _db = db;
            _logger = logger;
        }

        // ── Reads ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all materialized slot records for a given title.
        /// </summary>
        public async Task<List<MaterializedVersion>> GetMaterializedVersionsAsync(
            string mediaItemId,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, strm_path, nfo_path,
                       strm_url_hash, is_base, materialized_at, updated_at, strm_token_expires_at
                FROM materialized_versions
                WHERE media_item_id = @mid;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
            }, ReadMaterializedVersion);
        }

        /// <summary>
        /// Returns a single materialized slot record, or null.
        /// </summary>
        public async Task<MaterializedVersion?> GetMaterializedVersionAsync(
            string mediaItemId,
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, strm_path, nfo_path,
                       strm_url_hash, is_base, materialized_at, updated_at, strm_token_expires_at
                FROM materialized_versions
                WHERE media_item_id = @mid AND slot_key = @sk;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
                BindText(cmd, "@sk", slotKey);
            }, ReadMaterializedVersion);
        }

        /// <summary>
        /// Returns all titles that have a given slot materialized (for rehydration sweeps).
        /// </summary>
        public async Task<List<MaterializedVersion>> GetAllMaterializedForSlotAsync(
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, strm_path, nfo_path,
                       strm_url_hash, is_base, materialized_at, updated_at, strm_token_expires_at
                FROM materialized_versions
                WHERE slot_key = @sk;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@sk", slotKey);
            }, ReadMaterializedVersion);
        }

        /// <summary>
        /// Returns the materialized version holding the base (unsuffixed) filename
        /// for a given title, or null.
        /// </summary>
        public async Task<MaterializedVersion?> GetBaseMaterializedVersionAsync(
            string mediaItemId,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, strm_path, nfo_path,
                       strm_url_hash, is_base, materialized_at, updated_at, strm_token_expires_at
                FROM materialized_versions
                WHERE media_item_id = @mid AND is_base = 1;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
            }, ReadMaterializedVersion);
        }

        /// <summary>
        /// Returns all materialized versions that have .strm files on disk.
        /// Used by startup detection to check and rewrite URLs in file content.
        /// </summary>
        public async Task<List<MaterializedVersion>> GetAllWithStrmPathsAsync(
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, strm_path, nfo_path,
                       strm_url_hash, is_base, materialized_at, updated_at, strm_token_expires_at
                FROM materialized_versions
                WHERE strm_path IS NOT NULL;";

            return await QueryListAsync(sql, null, ReadMaterializedVersion);
        }

        /// <summary>
        /// Returns total count of materialized version records (for UI estimates).
        /// </summary>
        public Task<int> CountMaterializedVersionsAsync(CancellationToken ct = default)
        {
            const string sql = "SELECT COUNT(*) FROM materialized_versions;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.GetInt(0));
            return Task.FromResult(0);
        }

        // ── Writes ────────────────────────────────────────────────────────────

        /// <summary>
        /// Inserts or updates a materialized version record.
        /// The UNIQUE constraint on (media_item_id, slot_key) drives upsert.
        /// </summary>
        public async Task UpsertMaterializedVersionAsync(
            MaterializedVersion version,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO materialized_versions
                    (id, media_item_id, slot_key, strm_path, nfo_path,
                     strm_url_hash, is_base, materialized_at, updated_at, strm_token_expires_at)
                VALUES
                    (@id, @mid, @sk, @strm, @nfo,
                     @hash, @is_base, @mat_at, @upd_at, @expires)
                ON CONFLICT(media_item_id, slot_key) DO UPDATE SET
                    strm_path       = excluded.strm_path,
                    nfo_path        = excluded.nfo_path,
                    strm_url_hash   = excluded.strm_url_hash,
                    is_base         = excluded.is_base,
                    updated_at      = excluded.updated_at,
                    strm_token_expires_at = excluded.strm_token_expires_at;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", version.Id);
                BindText(cmd, "@mid", version.MediaItemId);
                BindText(cmd, "@sk", version.SlotKey);
                BindText(cmd, "@strm", version.StrmPath);
                BindText(cmd, "@nfo", version.NfoPath);
                BindText(cmd, "@hash", version.StrmUrlHash);
                cmd.BindParameters["@is_base"].Bind(version.IsBase ? 1 : 0);
                BindText(cmd, "@mat_at", version.MaterializedAt);
                BindText(cmd, "@upd_at", version.UpdatedAt);
                if (version.StrmTokenExpiresAt.HasValue)
                    cmd.BindParameters["@expires"].Bind(version.StrmTokenExpiresAt.Value);
                else
                    cmd.BindParameters["@expires"].BindNull();
            }, ct);
        }

        /// <summary>
        /// Updates the strm_token_expires_at timestamp for a materialized version.
        /// Used after successful .strm file writes to track token rotation schedule.
        /// </summary>
        public async Task SetStrmTokenExpiryAsync(
            string mediaItemId,
            string slotKey,
            long expiresAtUnix,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE materialized_versions
                SET strm_token_expires_at = @expires,
                    updated_at = datetime('now')
                WHERE media_item_id = @mid AND slot_key = @sk;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
                BindText(cmd, "@sk", slotKey);
                cmd.BindParameters["@expires"].Bind(expiresAtUnix);
            }, ct);
        }

        /// <summary>
        /// Returns materialized versions with tokens expiring within specified seconds or NULL.
        /// Used by HousekeepingService for token rotation (Sprint 141).
        /// </summary>
        public async Task<List<MaterializedVersion>> GetMaterializedVersionsExpiringAsync(
            long withinSeconds,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, strm_path, nfo_path,
                       strm_url_hash, is_base, materialized_at, updated_at, strm_token_expires_at
                FROM materialized_versions
                WHERE strm_token_expires_at < @threshold
                   OR strm_token_expires_at IS NULL
                ORDER BY strm_token_expires_at ASC NULLS FIRST;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@threshold", DateTimeOffset.UtcNow.AddSeconds(withinSeconds).ToUnixTimeSeconds().ToString());
            }, ReadMaterializedVersion);
        }

        /// <summary>
        /// Removes a materialized version record for a title/slot pair.
        /// </summary>
        public async Task DeleteMaterializedVersionAsync(
            string mediaItemId,
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = "DELETE FROM materialized_versions WHERE media_item_id = @mid AND slot_key = @sk;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
                BindText(cmd, "@sk", slotKey);
            }, ct);
        }

        /// <summary>
        /// Atomically swaps the is_base flag: clears it from the current base slot
        /// and sets it on the new slot for the given title.
        /// </summary>
        public async Task SetBaseSlotAsync(
            string mediaItemId,
            string newSlotKey,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE materialized_versions
                SET is_base = CASE
                        WHEN slot_key = @new_sk THEN 1
                        ELSE 0
                    END,
                    updated_at = datetime('now')
                WHERE media_item_id = @mid;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
                BindText(cmd, "@new_sk", newSlotKey);
            }, ct);
        }

        // ── ORM mapper ────────────────────────────────────────────────────────

        private static MaterializedVersion ReadMaterializedVersion(IResultSet row) => new()
        {
            Id = row.GetString(0),
            MediaItemId = row.GetString(1),
            SlotKey = row.GetString(2),
            StrmPath = row.GetString(3),
            NfoPath = row.GetString(4),
            StrmUrlHash = row.GetString(5),
            IsBase = !row.IsDBNull(6) && row.GetInt(6) == 1,
            MaterializedAt = row.GetString(7),
            UpdatedAt = row.GetString(8),
            StrmTokenExpiresAt = row.IsDBNull(9) ? null : row.GetInt64(9),
        };

        // ── SQLite helpers ────────────────────────────────────────────────────

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

        private Task<T?> QuerySingleAsync<T>(
            string sql, Action<IStatement>? bindParams, Func<IResultSet, T> map) where T : class
        {
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);
            foreach (var row in stmt.AsRows())
                return Task.FromResult<T?>(map(row));
            return Task.FromResult<T?>(null);
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

        private static void BindNullableText(IStatement stmt, string name, string? value)
        {
            if (value == null) stmt.BindParameters[name].BindNull();
            else               stmt.BindParameters[name].Bind(value);
        }
    }
}
