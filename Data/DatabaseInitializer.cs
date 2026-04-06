using System;
using SQLitePCL.pretty;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Data
{
    /// <summary>
    /// Initializes v3.3 database schema from scratch.
    /// This is a clean initialization with no migration from v20.
    /// </summary>
    public class DatabaseInitializer
    {
        private readonly ILogger<DatabaseInitializer> _logger;

        public DatabaseInitializer(ILogger<DatabaseInitializer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Creates all v3.3 tables and initializes schema version.
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
                _logger.LogInformation("WAL mode enabled");

                // Create all tables in order
                int createdCount = 0;
                foreach (var table in Schema.Tables)
                {
                    try
                    {
                        connection.Execute(table.CreateSql);
                        createdCount++;
                        _logger.LogDebug("Created table: {TableName}", table.TableName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create table {TableName}", table.TableName);
                        throw;
                    }
                }

                _logger.LogInformation("Created {Count} tables successfully", createdCount);

                // Set initial schema version
                using var stmt = connection.PrepareStatement(
                    "INSERT INTO schema_version (version, description) VALUES (@Version, @Description)");
                stmt.BindParameters["@Version"].Bind(Schema.CurrentSchemaVersion);
                stmt.BindParameters["@Description"].Bind("EmbyStreams v3.3 initial schema");
                while (stmt.MoveNext()) { }

                _logger.LogInformation("Schema version set to {Version}", Schema.CurrentSchemaVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database initialization failed");
                throw;
            }
        }
    }
}
