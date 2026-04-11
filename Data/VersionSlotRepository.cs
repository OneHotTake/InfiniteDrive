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
    /// Repository for the <c>version_slots</c> table.
    /// Provides CRUD operations for quality slot configuration.
    /// </summary>
    public class VersionSlotRepository
    {
        private readonly string _dbPath;
        private readonly ILogger _logger;

        // Write serialization gate (shared concern across versioned playback repos)
        private static readonly SemaphoreSlim _writeGate = new(1, 1);

        public VersionSlotRepository(string dbDirectory, ILogger logger)
        {
            _dbPath = System.IO.Path.Combine(dbDirectory, "embystreams.db");
            _logger = logger;
        }

        // ── Read operations ────────────────────────────────────────────────────

        /// <summary>
        /// Returns all 7 slots (enabled + disabled), ordered by sort_order.
        /// </summary>
        public Task<List<VersionSlot>> GetAllSlotsAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT slot_key, label, resolution, video_codecs, hdr_classes,
                       audio_preferences, enabled, is_default, sort_order,
                       created_at, updated_at
                FROM version_slots
                ORDER BY sort_order";

            return QueryListAsync(sql, null, MapSlot);
        }

        /// <summary>
        /// Returns enabled slots only, ordered by sort_order.
        /// </summary>
        public Task<List<VersionSlot>> GetEnabledSlotsAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT slot_key, label, resolution, video_codecs, hdr_classes,
                       audio_preferences, enabled, is_default, sort_order,
                       created_at, updated_at
                FROM version_slots
                WHERE enabled = 1
                ORDER BY sort_order";

            return QueryListAsync(sql, null, MapSlot);
        }

        /// <summary>
        /// Returns a single slot by key, or null if not found.
        /// </summary>
        public Task<VersionSlot?> GetSlotAsync(string slotKey, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT slot_key, label, resolution, video_codecs, hdr_classes,
                       audio_preferences, enabled, is_default, sort_order,
                       created_at, updated_at
                FROM version_slots
                WHERE slot_key = @slot_key";

            return QuerySingleAsync(sql,
                stmt => stmt.BindParameters["@slot_key"].Bind(slotKey),
                MapSlot);
        }

        /// <summary>
        /// Returns the slot where is_default = 1, or null.
        /// </summary>
        public Task<VersionSlot?> GetDefaultSlotAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT slot_key, label, resolution, video_codecs, hdr_classes,
                       audio_preferences, enabled, is_default, sort_order,
                       created_at, updated_at
                FROM version_slots
                WHERE is_default = 1";

            return QuerySingleAsync(sql, null, MapSlot);
        }

        /// <summary>
        /// Returns count of enabled slots.
        /// </summary>
        public Task<int> GetEnabledSlotCountAsync(CancellationToken ct = default)
        {
            const string sql = "SELECT COUNT(*) FROM version_slots WHERE enabled = 1";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.GetInt(0));
            return Task.FromResult(0);
        }

        // ── Write operations ───────────────────────────────────────────────────

        /// <summary>
        /// Updates a slot's enabled/is_default state and config fields.
        /// Uses INSERT OR REPLACE to handle both insert and update.
        /// </summary>
        public async Task UpsertSlotAsync(VersionSlot slot, CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO version_slots
                    (slot_key, label, resolution, video_codecs, hdr_classes,
                     audio_preferences, enabled, is_default, sort_order,
                     created_at, updated_at)
                VALUES
                    (@slot_key, @label, @resolution, @video_codecs, @hdr_classes,
                     @audio_preferences, @enabled, @is_default, @sort_order,
                     datetime('now'), datetime('now'))
                ON CONFLICT(slot_key) DO UPDATE SET
                    label             = excluded.label,
                    resolution        = excluded.resolution,
                    video_codecs      = excluded.video_codecs,
                    hdr_classes       = excluded.hdr_classes,
                    audio_preferences = excluded.audio_preferences,
                    enabled           = excluded.enabled,
                    is_default        = excluded.is_default,
                    sort_order        = excluded.sort_order,
                    updated_at        = excluded.updated_at";

            await ExecuteWriteAsync(sql, stmt =>
            {
                stmt.BindParameters["@slot_key"].Bind(slot.SlotKey);
                stmt.BindParameters["@label"].Bind(slot.Label);
                stmt.BindParameters["@resolution"].Bind(slot.Resolution);
                stmt.BindParameters["@video_codecs"].Bind(slot.VideoCodecs);
                stmt.BindParameters["@hdr_classes"].Bind(slot.HdrClasses);
                stmt.BindParameters["@audio_preferences"].Bind(slot.AudioPreferences);
                stmt.BindParameters["@enabled"].Bind(slot.Enabled ? 1 : 0);
                stmt.BindParameters["@is_default"].Bind(slot.IsDefault ? 1 : 0);
                stmt.BindParameters["@sort_order"].Bind(slot.SortOrder);
            }, ct);

            _logger.LogDebug("Upserted version slot {SlotKey} (enabled={Enabled}, default={IsDefault})",
                slot.SlotKey, slot.Enabled, slot.IsDefault);
        }

        /// <summary>
        /// Atomically sets is_default = 1 for the given slot and 0 for all others.
        /// </summary>
        public async Task SetDefaultSlotAsync(string slotKey, CancellationToken ct = default)
        {
            await ExecuteWriteAsync(conn =>
            {
                // Clear all defaults
                conn.Execute("UPDATE version_slots SET is_default = 0, updated_at = datetime('now')");

                // Set new default
                using var stmt = conn.PrepareStatement(
                    "UPDATE version_slots SET is_default = 1, updated_at = datetime('now') WHERE slot_key = @slot_key");
                stmt.BindParameters["@slot_key"].Bind(slotKey);
                while (stmt.MoveNext()) { }
            }, ct);

            _logger.LogInformation("Set default version slot to {SlotKey}", slotKey);
        }

        /// <summary>
        /// Returns true if enabling another slot would exceed the max.
        /// </summary>
        public async Task<bool> EnforceMaxSlotsAsync(int maxSlots = 8, CancellationToken ct = default)
        {
            var count = await GetEnabledSlotCountAsync(ct);
            return count < maxSlots;
        }

        // ── Row mapper ─────────────────────────────────────────────────────────

        private static VersionSlot MapSlot(IResultSet row)
        {
            return new VersionSlot
            {
                SlotKey          = row.GetString(0),
                Label            = row.GetString(1),
                Resolution       = row.GetString(2),
                VideoCodecs      = row.GetString(3),
                HdrClasses       = row.GetString(4),
                AudioPreferences = row.GetString(5),
                Enabled          = row.GetInt(6) == 1,
                IsDefault        = row.GetInt(7) == 1,
                SortOrder        = row.GetInt(8),
                CreatedAt        = row.GetString(9),
                UpdatedAt        = row.GetString(10)
            };
        }

        // ── Database helpers ───────────────────────────────────────────────────

        private IDatabaseConnection OpenConnection()
        {
            return SQLite3.Open(_dbPath, ConnectionFlags.ReadWrite, null, true);
        }

        private async Task ExecuteWriteAsync(string sql, Action<IStatement> bindParams, CancellationToken ct)
        {
            await _writeGate.WaitAsync(ct);
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
                _writeGate.Release();
            }
        }

        private async Task ExecuteWriteAsync(Action<IDatabaseConnection> action, CancellationToken ct)
        {
            await _writeGate.WaitAsync(ct);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c => action(c));
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private Task<VersionSlot?> QuerySingleAsync(
            string sql,
            Action<IStatement>? bindParams,
            Func<IResultSet, VersionSlot> map)
        {
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);
            foreach (var row in stmt.AsRows())
                return Task.FromResult<VersionSlot?>(map(row));
            return Task.FromResult<VersionSlot?>(null);
        }

        private Task<List<VersionSlot>> QueryListAsync(
            string sql,
            Action<IStatement>? bindParams,
            Func<IResultSet, VersionSlot> map)
        {
            using var conn = OpenConnection();
            var results = new List<VersionSlot>();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);
            foreach (var row in stmt.AsRows())
                results.Add(map(row));
            return Task.FromResult(results);
        }
    }
}
