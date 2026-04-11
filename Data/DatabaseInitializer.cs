using System;
using System.Linq;
using SQLitePCL.pretty;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Data
{
    /// <summary>
    /// Initializes and migrates the v3.3 database schema.
    /// Supports both fresh installs and additive migrations from earlier schema versions.
    /// </summary>
    public class DatabaseInitializer
    {
        private readonly ILogger<DatabaseInitializer> _logger;

        public DatabaseInitializer(ILogger<DatabaseInitializer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Creates or migrates the database schema to the current version.
        /// Fresh installs get all tables + seed data.
        /// Existing databases get additive migrations (new tables only).
        /// </summary>
        /// <param name="dbPath">Path to the SQLite database file.</param>
        public void Initialize(string dbPath)
        {
            _logger.LogInformation("Initializing v3.3 database schema at {DbPath}", dbPath);

            try
            {
                using var connection = SQLite3.Open(dbPath, ConnectionFlags.ReadWrite | ConnectionFlags.Create, null, true);

                // Enable WAL mode for better concurrency
                connection.Execute("PRAGMA journal_mode=WAL;");

                var currentVersion = GetSchemaVersion(connection);

                if (currentVersion == 0)
                {
                    InitializeFresh(connection);
                }
                else if (currentVersion < Schema.CurrentSchemaVersion)
                {
                    Migrate(connection, currentVersion);
                }
                else
                {
                    _logger.LogInformation("Schema is up to date (version {Version})", currentVersion);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database initialization failed");
                throw;
            }
        }

        // ── Fresh install ──────────────────────────────────────────────────────

        private void InitializeFresh(IDatabaseConnection connection)
        {
            _logger.LogInformation("Fresh install — creating all tables");

            // Create all tables in order
            foreach (var table in Schema.Tables)
            {
                connection.Execute(table.CreateSql);
                _logger.LogDebug("Created table: {TableName}", table.TableName);
            }

            // Seed version_slots
            SeedVersionSlots(connection);

            // Set schema version
            SetSchemaVersion(connection, Schema.CurrentSchemaVersion, "InfiniteDrive v3.3 initial schema");

            _logger.LogInformation("Fresh install complete — {Count} tables, version {Version}",
                Schema.Tables.Count, Schema.CurrentSchemaVersion);
        }

        // ── Migration ──────────────────────────────────────────────────────────

        private void Migrate(IDatabaseConnection connection, int fromVersion)
        {
            _logger.LogInformation("Migrating schema from version {From} to {To}", fromVersion, Schema.CurrentSchemaVersion);

            // Collect tables that already exist
            var existingTables = GetExistingTables(connection);

            // Create only missing tables (additive — no ALTER TABLE needed)
            foreach (var table in Schema.Tables)
            {
                if (!existingTables.Contains(table.TableName))
                {
                    connection.Execute(table.CreateSql);
                    _logger.LogInformation("Migration created table: {TableName}", table.TableName);
                }
            }

            // Seed version_slots if the table was just created (or is empty)
            if (!existingTables.Contains("version_slots"))
            {
                SeedVersionSlots(connection);
            }

            // Record migration
            SetSchemaVersion(connection, Schema.CurrentSchemaVersion,
                $"Migrated from schema version {fromVersion} to {Schema.CurrentSchemaVersion}");

            _logger.LogInformation("Migration complete — now at version {Version}", Schema.CurrentSchemaVersion);
        }

        // ── Seed data ──────────────────────────────────────────────────────────

        /// <summary>
        /// Seeds the 7 predefined quality slots into version_slots.
        /// hd_broad is enabled and set as default.
        /// </summary>
        private void SeedVersionSlots(IDatabaseConnection connection)
        {
            _logger.LogInformation("Seeding version_slots with predefined quality profiles");

            var slots = new (string Key, string Label, string Resolution, string VideoCodecs, string HdrClasses, string AudioPreferences, int Enabled, int IsDefault, int SortOrder)[]
            {
                ("hd_broad",       "HD \u00b7 Broad",       "1080p", "h264",       "",    "dd_plus_51,dd_51,aac_stereo",        1, 1, 0),
                ("best_available", "Best Available",         "highest", "any",      "any", "atmos,dd_plus_71,dd_plus_51,dd_51",  0, 0, 1),
                ("4k_dv",          "4K \u00b7 Dolby Vision", "2160p", "hevc,av1",   "dv",  "atmos,dd_plus_71,dd_plus_51",        0, 0, 2),
                ("4k_hdr",         "4K \u00b7 HDR",          "2160p", "hevc,av1",   "hdr10","atmos,dd_plus_51,dd_51",             0, 0, 3),
                ("4k_sdr",         "4K \u00b7 SDR",          "2160p", "hevc,av1",   "",    "dd_plus_51,dd_51,aac",               0, 0, 4),
                ("hd_efficient",   "HD \u00b7 Efficient",    "1080p", "hevc",       "",    "dd_plus_51,aac_stereo",              0, 0, 5),
                ("compact",        "Compact",                "720p",  "h264",       "",    "aac,dd",                             0, 0, 6),
            };

            const string sql = @"
                INSERT INTO version_slots
                    (slot_key, label, resolution, video_codecs, hdr_classes, audio_preferences, enabled, is_default, sort_order)
                VALUES
                    (@slot_key, @label, @resolution, @video_codecs, @hdr_classes, @audio_preferences, @enabled, @is_default, @sort_order)";

            foreach (var slot in slots)
            {
                using var stmt = connection.PrepareStatement(sql);
                stmt.BindParameters["@slot_key"].Bind(slot.Key);
                stmt.BindParameters["@label"].Bind(slot.Label);
                stmt.BindParameters["@resolution"].Bind(slot.Resolution);
                stmt.BindParameters["@video_codecs"].Bind(slot.VideoCodecs);
                stmt.BindParameters["@hdr_classes"].Bind(slot.HdrClasses);
                stmt.BindParameters["@audio_preferences"].Bind(slot.AudioPreferences);
                stmt.BindParameters["@enabled"].Bind(slot.Enabled);
                stmt.BindParameters["@is_default"].Bind(slot.IsDefault);
                stmt.BindParameters["@sort_order"].Bind(slot.SortOrder);
                while (stmt.MoveNext()) { }
            }

            _logger.LogInformation("Seeded {Count} version slots (hd_broad enabled + default)", slots.Length);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static int GetSchemaVersion(IDatabaseConnection connection)
        {
            try
            {
                using var stmt = connection.PrepareStatement("SELECT MAX(version) FROM schema_version");
                foreach (var row in stmt.AsRows())
                    return row.GetInt(0);
            }
            catch
            {
                // schema_version table doesn't exist yet — fresh install
            }
            return 0;
        }

        private static void SetSchemaVersion(IDatabaseConnection connection, int version, string description)
        {
            using var stmt = connection.PrepareStatement(
                "INSERT INTO schema_version (version, description) VALUES (@Version, @Description)");
            stmt.BindParameters["@Version"].Bind(version);
            stmt.BindParameters["@Description"].Bind(description);
            while (stmt.MoveNext()) { }
        }

        private static System.Collections.Generic.HashSet<string> GetExistingTables(IDatabaseConnection connection)
        {
            var tables = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var stmt = connection.PrepareStatement(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
            foreach (var row in stmt.AsRows())
                tables.Add(row.GetString(0));
            return tables;
        }
    }
}
