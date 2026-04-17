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
    /// Repository for the <c>version_snapshots</c> table.
    /// Tracks the selected top candidate per title per slot, plus an ephemeral
    /// playback URL cache.
    ///
    /// The snapshot stores only the top candidate ID. The full fallback ladder
    /// (ranked candidates 0..N) lives in the <c>candidates</c> table via
    /// <see cref="CandidateRepository"/>.
    /// </summary>
    public class SnapshotRepository
    {
        private readonly DatabaseManager _db;
        private readonly ILogger _logger;

        private static readonly SemaphoreSlim _dbWriteGate = new(1, 1);

        public SnapshotRepository(DatabaseManager db, ILogger logger)
        {
            _db = db;
            _logger = logger;
        }

        // ── Reads ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the snapshot for a title/slot pair, or null.
        /// </summary>
        public async Task<VersionSnapshot?> GetSnapshotAsync(
            string mediaItemId,
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, candidate_id, snapshot_at,
                       playback_url, playback_url_cached_at, playback_url_expires_at
                FROM version_snapshots
                WHERE media_item_id = @mid AND slot_key = @sk;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
                BindText(cmd, "@sk", slotKey);
            }, ReadSnapshot);
        }

        /// <summary>
        /// Returns all slot snapshots for a given title.
        /// </summary>
        public async Task<List<VersionSnapshot>> GetAllSnapshotsForItemAsync(
            string mediaItemId,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, candidate_id, snapshot_at,
                       playback_url, playback_url_cached_at, playback_url_expires_at
                FROM version_snapshots
                WHERE media_item_id = @mid;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
            }, ReadSnapshot);
        }

        /// <summary>
        /// Returns the cached playback URL if it has not expired, or null.
        /// </summary>
        public Task<string?> GetCachedPlaybackUrlAsync(
            string mediaItemId,
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT playback_url
                FROM version_snapshots
                WHERE media_item_id = @mid
                  AND slot_key = @sk
                  AND playback_url IS NOT NULL
                  AND playback_url_expires_at > datetime('now');";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@mid", mediaItemId);
            BindText(stmt, "@sk", slotKey);
            foreach (var row in stmt.AsRows())
                return Task.FromResult<string?>(row.IsDBNull(0) ? null : row.GetString(0));
            return Task.FromResult<string?>(null);
        }

        // ── Writes ────────────────────────────────────────────────────────────

        /// <summary>
        /// Inserts or updates a snapshot. The UNIQUE constraint on
        /// (media_item_id, slot_key) drives upsert behaviour.
        /// </summary>
        public async Task UpsertSnapshotAsync(
            VersionSnapshot snapshot,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO version_snapshots
                    (id, media_item_id, slot_key, candidate_id, snapshot_at,
                     playback_url, playback_url_cached_at, playback_url_expires_at)
                VALUES
                    (@id, @mid, @sk, @cid, @at,
                     @url, @url_cached, @url_expires)
                ON CONFLICT(media_item_id, slot_key) DO UPDATE SET
                    candidate_id             = excluded.candidate_id,
                    snapshot_at              = excluded.snapshot_at,
                    playback_url             = COALESCE(excluded.playback_url, version_snapshots.playback_url),
                    playback_url_cached_at   = COALESCE(excluded.playback_url_cached_at, version_snapshots.playback_url_cached_at),
                    playback_url_expires_at  = COALESCE(excluded.playback_url_expires_at, version_snapshots.playback_url_expires_at);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", snapshot.Id);
                BindText(cmd, "@mid", snapshot.MediaItemId);
                BindText(cmd, "@sk", snapshot.SlotKey);
                BindText(cmd, "@cid", snapshot.CandidateId);
                BindText(cmd, "@at", snapshot.SnapshotAt);
                BindNullableText(cmd, "@url", snapshot.PlaybackUrl);
                BindNullableText(cmd, "@url_cached", snapshot.PlaybackUrlCachedAt);
                BindNullableText(cmd, "@url_expires", snapshot.PlaybackUrlExpiresAt);
            }, ct);
        }

        /// <summary>
        /// Updates the ephemeral playback URL cache for a title/slot pair.
        /// </summary>
        public async Task CachePlaybackUrlAsync(
            string mediaItemId,
            string slotKey,
            string url,
            int ttlMinutes,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE version_snapshots
                SET playback_url             = @url,
                    playback_url_cached_at   = datetime('now'),
                    playback_url_expires_at  = datetime('now', '+' || @ttl || ' minutes')
                WHERE media_item_id = @mid AND slot_key = @sk;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@url", url);
                cmd.BindParameters["@ttl"].Bind(ttlMinutes);
                BindText(cmd, "@mid", mediaItemId);
                BindText(cmd, "@sk", slotKey);
            }, ct);
        }

        /// <summary>
        /// Clears the cached playback URL for a title/slot pair.
        /// </summary>
        public async Task InvalidatePlaybackUrlAsync(
            string mediaItemId,
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE version_snapshots
                SET playback_url            = NULL,
                    playback_url_cached_at  = NULL,
                    playback_url_expires_at = NULL
                WHERE media_item_id = @mid AND slot_key = @sk;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
                BindText(cmd, "@sk", slotKey);
            }, ct);
        }

        // ── ORM mapper ────────────────────────────────────────────────────────

        private static VersionSnapshot ReadSnapshot(IResultSet row) => new()
        {
            Id = row.GetString(0),
            MediaItemId = row.GetString(1),
            SlotKey = row.GetString(2),
            CandidateId = row.GetString(3),
            SnapshotAt = row.GetString(4),
            PlaybackUrl = row.IsDBNull(5) ? null : row.GetString(5),
            PlaybackUrlCachedAt = row.IsDBNull(6) ? null : row.GetString(6),
            PlaybackUrlExpiresAt = row.IsDBNull(7) ? null : row.GetString(7),
        };

        // ── SQLite helpers ────────────────────────────────────────────────────

        private IDatabaseConnection OpenConnection()
        {
            var conn = SQLite3.Open(_db.GetDatabasePath(), ConnectionFlags.ReadWrite | ConnectionFlags.Create, null, true);
            conn.Execute("PRAGMA journal_mode=WAL;");
            conn.Execute("PRAGMA busy_timeout=30000;");
            return conn;
        }

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
