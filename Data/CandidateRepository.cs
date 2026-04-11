using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;
using SQLitePCL.pretty;

namespace InfiniteDrive.Data
{
    /// <summary>
    /// Repository for the <c>candidates</c> table.
    /// Stores normalized stream candidates per title per slot — the ranked ladder
    /// that drives playback fallback.
    ///
    /// Hard constraint: no debrid URLs are persisted. Playback reconstructs URLs
    /// from <see cref="Candidate.InfoHash"/> + <see cref="Candidate.FileIdx"/>
    /// at play time.
    /// </summary>
    public class CandidateRepository
    {
        private readonly DatabaseManager _db;
        private readonly ILogger _logger;

        // Write serialization — shared with DatabaseManager to prevent "database is locked".
        private static readonly SemaphoreSlim _dbWriteGate = new(1, 1);

        public CandidateRepository(DatabaseManager db, ILogger logger)
        {
            _db = db;
            _logger = logger;
        }

        // ── Reads ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the ranked candidate ladder for a title/slot pair.
        /// Results are ordered by rank ascending (0 = best).
        /// </summary>
        public async Task<List<Candidate>> GetCandidatesAsync(
            string mediaItemId,
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, rank,
                       service, stream_type,
                       resolution, video_codec, hdr_class, audio_codec, audio_channels,
                       file_name, file_size, bitrate_kbps, languages, source_type, is_cached,
                       fingerprint, binge_group, info_hash, file_idx,
                       confidence_score, created_at, expires_at
                FROM candidates
                WHERE media_item_id = @media_item_id AND slot_key = @slot_key
                ORDER BY rank ASC;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@media_item_id", mediaItemId);
                BindText(cmd, "@slot_key", slotKey);
            }, ReadCandidate);
        }

        /// <summary>
        /// Returns the top-ranked (rank 0) candidate for a title/slot pair, or null.
        /// </summary>
        public async Task<Candidate?> GetTopCandidateAsync(
            string mediaItemId,
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, rank,
                       service, stream_type,
                       resolution, video_codec, hdr_class, audio_codec, audio_channels,
                       file_name, file_size, bitrate_kbps, languages, source_type, is_cached,
                       fingerprint, binge_group, info_hash, file_idx,
                       confidence_score, created_at, expires_at
                FROM candidates
                WHERE media_item_id = @media_item_id AND slot_key = @slot_key
                ORDER BY rank ASC
                LIMIT 1;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@media_item_id", mediaItemId);
                BindText(cmd, "@slot_key", slotKey);
            }, ReadCandidate);
        }

        /// <summary>
        /// Looks up a candidate by its fingerprint for deduplication.
        /// </summary>
        public async Task<Candidate?> GetCandidateByFingerprintAsync(
            string fingerprint,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, media_item_id, slot_key, rank,
                       service, stream_type,
                       resolution, video_codec, hdr_class, audio_codec, audio_channels,
                       file_name, file_size, bitrate_kbps, languages, source_type, is_cached,
                       fingerprint, binge_group, info_hash, file_idx,
                       confidence_score, created_at, expires_at
                FROM candidates
                WHERE fingerprint = @fingerprint
                LIMIT 1;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@fingerprint", fingerprint);
            }, ReadCandidate);
        }

        // ── Writes ────────────────────────────────────────────────────────────

        /// <summary>
        /// Batch upsert: replaces the entire candidate ladder for a title/slot pair.
        /// Deletes existing candidates for the pair, then inserts the new set.
        /// </summary>
        public async Task UpsertCandidatesAsync(
            List<Candidate> candidates,
            CancellationToken ct = default)
        {
            if (candidates.Count == 0) return;

            await _dbWriteGate.WaitAsync(ct);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c =>
                {
                    // Group by (media_item_id, slot_key) to wipe the old ladder
                    var grouped = new Dictionary<(string, string), List<Candidate>>();
                    foreach (var cand in candidates)
                    {
                        var key = (cand.MediaItemId, cand.SlotKey);
                        if (!grouped.TryGetValue(key, out var list))
                        {
                            list = new List<Candidate>();
                            grouped[key] = list;
                        }
                        list.Add(cand);
                    }

                    foreach (var ((mediaItemId, slotKey), group) in grouped)
                    {
                        // Delete existing ladder for this pair
                        using (var del = c.PrepareStatement(
                            "DELETE FROM candidates WHERE media_item_id = @mid AND slot_key = @sk;"))
                        {
                            BindText(del, "@mid", mediaItemId);
                            BindText(del, "@sk", slotKey);
                            while (del.MoveNext()) { }
                        }

                        // Insert new candidates
                        const string insertSql = @"
                            INSERT INTO candidates
                                (id, media_item_id, slot_key, rank,
                                 service, stream_type,
                                 resolution, video_codec, hdr_class, audio_codec, audio_channels,
                                 file_name, file_size, bitrate_kbps, languages, source_type, is_cached,
                                 fingerprint, binge_group, info_hash, file_idx,
                                 confidence_score, created_at, expires_at)
                            VALUES
                                (@id, @mid, @sk, @rank,
                                 @service, @stream_type,
                                 @resolution, @video_codec, @hdr_class, @audio_codec, @audio_channels,
                                 @file_name, @file_size, @bitrate_kbps, @languages, @source_type, @is_cached,
                                 @fingerprint, @binge_group, @info_hash, @file_idx,
                                 @confidence_score, @created_at, @expires_at);";

                        foreach (var cand in group)
                        {
                            using var ins = c.PrepareStatement(insertSql);
                            BindText(ins, "@id", cand.Id);
                            BindText(ins, "@mid", cand.MediaItemId);
                            BindText(ins, "@sk", cand.SlotKey);
                            ins.BindParameters["@rank"].Bind(cand.Rank);
                            BindNullableText(ins, "@service", cand.Service);
                            BindText(ins, "@stream_type", cand.StreamType);
                            BindNullableText(ins, "@resolution", cand.Resolution);
                            BindNullableText(ins, "@video_codec", cand.VideoCodec);
                            BindNullableText(ins, "@hdr_class", cand.HdrClass);
                            BindNullableText(ins, "@audio_codec", cand.AudioCodec);
                            BindNullableText(ins, "@audio_channels", cand.AudioChannels);
                            BindNullableText(ins, "@file_name", cand.FileName);
                            BindNullableLong(ins, "@file_size", cand.FileSize);
                            BindNullableInt(ins, "@bitrate_kbps", cand.BitrateKbps);
                            BindNullableText(ins, "@languages", cand.Languages);
                            BindNullableText(ins, "@source_type", cand.SourceType);
                            ins.BindParameters["@is_cached"].Bind(cand.IsCached ? 1 : 0);
                            BindText(ins, "@fingerprint", cand.Fingerprint);
                            BindNullableText(ins, "@binge_group", cand.BingeGroup);
                            BindNullableText(ins, "@info_hash", cand.InfoHash);
                            BindNullableInt(ins, "@file_idx", cand.FileIdx);
                            ins.BindParameters["@confidence_score"].Bind(cand.ConfidenceScore);
                            BindText(ins, "@created_at", cand.CreatedAt);
                            BindText(ins, "@expires_at", cand.ExpiresAt);
                            while (ins.MoveNext()) { }
                        }
                    }
                });
            }
            finally
            {
                _dbWriteGate.Release();
            }

            _logger.LogDebug(
                "[CandidateRepository] Upserted {Count} candidates across {Groups} item/slot pairs",
                candidates.Count,
                candidates.Select(c => (c.MediaItemId, c.SlotKey)).Distinct().Count());
        }

        /// <summary>
        /// Removes all candidates for a title/slot pair.
        /// </summary>
        public async Task DeleteCandidatesAsync(
            string mediaItemId,
            string slotKey,
            CancellationToken ct = default)
        {
            const string sql = "DELETE FROM candidates WHERE media_item_id = @mid AND slot_key = @sk;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@mid", mediaItemId);
                BindText(cmd, "@sk", slotKey);
            }, ct);
        }

        /// <summary>
        /// Removes all expired candidates. Called periodically for housekeeping.
        /// </summary>
        public async Task<int> DeleteExpiredCandidatesAsync(CancellationToken ct = default)
        {
            const string sql = "DELETE FROM candidates WHERE expires_at <= datetime('now');";

            int deleted = 0;
            await _dbWriteGate.WaitAsync(ct);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c =>
                {
                    using var stmt = c.PrepareStatement(sql);
                    while (stmt.MoveNext()) { }
                    using var countStmt = c.PrepareStatement("SELECT changes();");
                    foreach (var row in countStmt.AsRows())
                        deleted = row.GetInt(0);
                });
            }
            finally
            {
                _dbWriteGate.Release();
            }

            if (deleted > 0)
                _logger.LogDebug("[CandidateRepository] Deleted {Count} expired candidates", deleted);

            return deleted;
        }

        // ── ORM mapper ────────────────────────────────────────────────────────

        private static Candidate ReadCandidate(IResultSet row) => new()
        {
            Id = row.GetString(0),
            MediaItemId = row.GetString(1),
            SlotKey = row.GetString(2),
            Rank = row.GetInt(3),
            Service = row.IsDBNull(4) ? null : row.GetString(4),
            StreamType = row.IsDBNull(5) ? "debrid" : row.GetString(5),
            Resolution = row.IsDBNull(6) ? null : row.GetString(6),
            VideoCodec = row.IsDBNull(7) ? null : row.GetString(7),
            HdrClass = row.IsDBNull(8) ? null : row.GetString(8),
            AudioCodec = row.IsDBNull(9) ? null : row.GetString(9),
            AudioChannels = row.IsDBNull(10) ? null : row.GetString(10),
            FileName = row.IsDBNull(11) ? null : row.GetString(11),
            FileSize = row.IsDBNull(12) ? null : (long?)row.GetInt64(12),
            BitrateKbps = row.IsDBNull(13) ? null : (int?)row.GetInt(13),
            Languages = row.IsDBNull(14) ? null : row.GetString(14),
            SourceType = row.IsDBNull(15) ? null : row.GetString(15),
            IsCached = !row.IsDBNull(16) && row.GetInt(16) == 1,
            Fingerprint = row.GetString(17),
            BingeGroup = row.IsDBNull(18) ? null : row.GetString(18),
            InfoHash = row.IsDBNull(19) ? null : row.GetString(19),
            FileIdx = row.IsDBNull(20) ? null : (int?)row.GetInt(20),
            ConfidenceScore = row.IsDBNull(21) ? 0.0 : row.GetDouble(21),
            CreatedAt = row.GetString(22),
            ExpiresAt = row.IsDBNull(23) ? string.Empty : row.GetString(23),
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

        private static void BindNullableInt(IStatement stmt, string name, int? value)
        {
            if (value == null) stmt.BindParameters[name].BindNull();
            else               stmt.BindParameters[name].Bind(value.Value);
        }

        private static void BindNullableLong(IStatement stmt, string name, long? value)
        {
            if (value == null) stmt.BindParameters[name].BindNull();
            else               stmt.BindParameters[name].Bind(value.Value);
        }
    }
}
