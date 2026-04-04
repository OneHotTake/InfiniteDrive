using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Models;
using EmbyStreams.Repositories.Interfaces;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using SQLitePCL.pretty;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Data
{
    /// <summary>
    /// Manages the EmbyStreams SQLite database at
    /// <c>{DataPath}/EmbyStreams/embystreams.db</c>.
    ///
    /// Responsibilities:
    /// <list type="bullet">
    ///   <item>Schema creation and version-tracked migration</item>
    ///   <item>Self-healing: integrity check on startup, recreate if corrupt</item>
    ///   <item>Repository methods for all five tables</item>
    /// </list>
    ///
    /// All write operations are wrapped in transactions.
    /// All parameterised queries use named parameters — no string interpolation.
    /// </summary>
    public class DatabaseManager : ICatalogRepository, IPinRepository, IResolutionCacheRepository
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const int CurrentSchemaVersion = 20;
        private const int PlaybackLogMaxRows = 500;

        private static class Tables
        {
            public const string SchemaVersion    = "schema_version";
            public const string CatalogItems     = "catalog_items";
            public const string ResolutionCache  = "resolution_cache";
            public const string StreamCandidates = "stream_candidates";
            public const string PlaybackLog      = "playback_log";
            public const string ClientCompat     = "client_compat";
            public const string ApiBudget        = "api_budget";
            public const string SyncState        = "sync_state";
            public const string CollectionMembership = "collection_membership";
        }

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly string _dbPath;
        private readonly ILogger _logger;

        // ── Write serialization gate ─────────────────────────────────────────────
        // WAL mode does NOT allow concurrent writers. This gate serializes all
        // write operations to prevent "database is locked" errors during concurrent
        // access (e.g., CatalogSyncTask and CatalogDiscoverService running at startup).
        private static readonly SemaphoreSlim _dbWriteGate = new(1, 1);


        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a DatabaseManager targeting the given directory.
        /// The database file itself is created by <see cref="Initialise"/>.
        /// </summary>
        /// <param name="dbDirectory">Folder that will contain embystreams.db.</param>
        /// <param name="logger">ILogger instance from the plugin.</param>
        public DatabaseManager(string dbDirectory, ILogger logger)
        {
            _logger = logger;
            _dbPath = Path.Combine(dbDirectory, "embystreams.db");

            Directory.CreateDirectory(dbDirectory);
        }

        // ── Public initialisation ───────────────────────────────────────────────

        /// <summary>
        /// Opens the database, runs an integrity check, and creates / migrates
        /// the schema to <see cref="CurrentSchemaVersion"/>.
        /// If corruption is detected the database file is deleted and recreated.
        /// </summary>
        public void Initialise()
        {
            if (!TryIntegrityCheck())
            {
                _logger.LogWarning(
                    "[EmbyStreams] Database integrity check failed — deleting and recreating {DbPath}",
                    _dbPath);
                File.Delete(_dbPath);
            }

            using var conn = OpenConnection();
            ApplyPragmas(conn);
            CreateSchema(conn);
            MigrateSchema(conn);
        }

        // ── catalog_items repository ────────────────────────────────────────────

        /// <summary>
        /// Inserts or updates a catalog item.  The UNIQUE constraint on
        /// (imdb_id, source) drives upsert behaviour.
        /// </summary>
        public async Task UpsertCatalogItemAsync(CatalogItem item, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO catalog_items
                    (id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type,
                     source, source_list_id, seasons_json, strm_path,
                     added_at, updated_at, removed_at,
                     local_path, local_source, item_state, pin_source, pinned_at)
                VALUES
                    (@id, @imdb_id, @tmdb_id, @unique_ids_json, @title, @year, @media_type,
                     @source, @source_list_id, @seasons_json, @strm_path,
                     @added_at, @updated_at, @removed_at,
                     @local_path, @local_source, @item_state, @pin_source, @pinned_at)
                ON CONFLICT(imdb_id, source) DO UPDATE SET
                    tmdb_id       = excluded.tmdb_id,
                    unique_ids_json = COALESCE(excluded.unique_ids_json, catalog_items.unique_ids_json),
                    title         = excluded.title,
                    year          = excluded.year,
                    seasons_json  = COALESCE(excluded.seasons_json, catalog_items.seasons_json),
                    strm_path     = COALESCE(excluded.strm_path,    catalog_items.strm_path),
                    local_path    = COALESCE(catalog_items.local_path,   excluded.local_path),
                    local_source  = COALESCE(catalog_items.local_source, excluded.local_source),
                    item_state    = COALESCE(excluded.item_state,    catalog_items.item_state),
                    pin_source    = COALESCE(excluded.pin_source,    catalog_items.pin_source),
                    pinned_at     = COALESCE(excluded.pinned_at,     catalog_items.pinned_at),
                    updated_at    = excluded.updated_at,
                    removed_at    = NULL;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id",             item.Id);
                BindText(cmd, "@imdb_id",        item.ImdbId);
                BindNullableText(cmd, "@tmdb_id",        item.TmdbId);
                BindNullableText(cmd, "@unique_ids_json", item.UniqueIdsJson);
                BindText(cmd, "@title",          item.Title);
                BindNullableInt(cmd,  "@year",           item.Year);
                BindText(cmd, "@media_type",     item.MediaType);
                BindText(cmd, "@source",         item.Source);
                BindNullableText(cmd, "@source_list_id", item.SourceListId);
                BindNullableText(cmd, "@seasons_json",   item.SeasonsJson);
                BindNullableText(cmd, "@strm_path",      item.StrmPath);
                BindText(cmd, "@added_at",       item.AddedAt);
                BindText(cmd, "@updated_at",     item.UpdatedAt);
                BindNullableText(cmd, "@removed_at",     item.RemovedAt);
                BindNullableText(cmd, "@local_path",     item.LocalPath);
                BindNullableText(cmd, "@local_source",   item.LocalSource);
                cmd.BindParameters["@item_state"].Bind((int)item.ItemState);
                BindNullableText(cmd, "@pin_source",     item.PinSource);
                BindNullableText(cmd, "@pinned_at",      item.PinnedAt);
            });
        }

        /// <summary>
        /// Returns all active (non-removed) catalog items.
        /// </summary>
        public async Task<List<CatalogItem>> GetActiveCatalogItemsAsync()
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type,
                       source, source_list_id, seasons_json, strm_path,
                       added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count,
                       item_state, pin_source, pinned_at
                FROM catalog_items
                WHERE removed_at IS NULL;";

            return await QueryListAsync(sql, null, ReadCatalogItem);
        }

        /// <summary>
        /// Returns the first active catalog item with the specified IMDB ID, or null.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByImdbIdAsync(string imdbId)
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type,
                       source, source_list_id, seasons_json, strm_path,
                       added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count
                FROM catalog_items
                WHERE imdb_id = @imdb_id AND removed_at IS NULL
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@imdb_id", imdbId),
                ReadCatalogItem);
        }


        /// <summary>
        /// Synchronous version of GetCatalogItemByImdbIdAsync for use in
        /// non-async contexts (e.g. GetEpisodeCountForSeason).
        /// </summary>
        public CatalogItem? GetCatalogItemByImdbIdSync(string imdbId)
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type,
                       source, source_list_id, seasons_json, strm_path,
                       added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count
                FROM catalog_items
                WHERE imdb_id = @imdb_id AND removed_at IS NULL
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@imdb_id", imdbId);
            foreach (var row in stmt.AsRows())
                return ReadCatalogItem(row);
            return null;
        }
        /// <summary>
        /// Returns all active catalog items from a specific source.
        /// </summary>
        public async Task<List<CatalogItem>> GetCatalogItemsBySourceAsync(string source)
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type,
                       source, source_list_id, seasons_json, strm_path,
                       added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count
                FROM catalog_items
                WHERE source = @source AND removed_at IS NULL;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@source", source),
                ReadCatalogItem);
        }

        /// <summary>
        /// Compares <paramref name="currentImdbIds"/> against the active rows for
        /// <paramref name="source"/> in the database.  Any row whose IMDB ID is no
        /// longer present in <paramref name="currentImdbIds"/> is soft-deleted by
        /// setting <c>removed_at = now()</c>.
        ///
        /// Returns the list of <c>strm_path</c> values for the removed rows so the
        /// caller can delete the files from disk.
        /// </summary>
        public async Task<List<string>> PruneSourceAsync(
            string          source,
            HashSet<string> currentImdbIds,
            CancellationToken cancellationToken = default)
        {
            var existing = await GetCatalogItemsBySourceAsync(source);
            var toRemove = existing.Where(x => !currentImdbIds.Contains(x.ImdbId)).ToList();

            if (toRemove.Count == 0)
                return new List<string>();

            // Batch update all items in single query (fixes N+1)
            var imdbIds = toRemove.Select(x => x.ImdbId).ToList();
            var idsParam = string.Join(",", imdbIds.Select((_, i) => $"@id{i}"));

            var sql = $@"
                UPDATE catalog_items
                SET removed_at = datetime('now')
                WHERE imdb_id IN ({idsParam}) AND source = @source;";

            var removed = new List<string>();
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source", source);
                for (int i = 0; i < imdbIds.Count; i++)
                {
                    BindText(cmd, $"@id{i}", imdbIds[i]);
                }
            });

            // Collect strm paths for file deletion
            foreach (var item in toRemove)
            {
                if (!string.IsNullOrEmpty(item.StrmPath))
                    removed.Add(item.StrmPath);
            }

            return removed;
        }

        /// <summary>
        /// Soft-deletes an item by setting removed_at to now.
        /// The row is never physically deleted (audit trail).
        /// </summary>
        public async Task MarkCatalogItemRemovedAsync(string imdbId, string source, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET removed_at = datetime('now')
                WHERE imdb_id = @imdb_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindText(cmd, "@source",  source);
            });
        }

        /// <summary>
        /// Updates the strm_path for an item identified by (imdb_id, source).
        /// </summary>
        public async Task UpdateStrmPathAsync(string imdbId, string source, string strmPath, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET strm_path  = @strm_path,
                    updated_at = datetime('now')
                WHERE imdb_id = @imdb_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@strm_path", strmPath);
                BindText(cmd, "@imdb_id",   imdbId);
                BindText(cmd, "@source",    source);
            });
        }

        /// <summary>
        /// Records where a catalog item currently lives on this server.
        /// Called by <c>CatalogSyncTask</c> after each sync run.
        /// </summary>
        /// <param name="imdbId">IMDB identifier.</param>
        /// <param name="source">Source key (e.g. <c>aiostreams</c>, <c>trakt</c>).</param>
        /// <param name="localPath">Absolute path to the file (real media or .strm).</param>
        /// <param name="localSource">
        /// <c>library</c> if the file is an existing media file the user already owns,
        /// <c>strm</c> if the plugin wrote it.
        /// </param>
        public async Task UpdateLocalPathAsync(
            string imdbId, string source, string? localPath, string? localSource,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET local_path   = @local_path,
                    local_source = @local_source,
                    updated_at   = datetime('now')
                WHERE imdb_id = @imdb_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindNullableText(cmd, "@local_path",   localPath);
                BindNullableText(cmd, "@local_source", localSource);
                BindText(cmd, "@imdb_id", imdbId);
                BindText(cmd, "@source",  source);
            });
        }

        /// <summary>
        /// Returns the number of active catalog items with a given <c>local_source</c> value.
        /// Pass <c>library</c> or <c>strm</c>.
        /// </summary>
        public Task<int> GetCatalogItemCountByLocalSourceAsync(string localSource)
        {
            const string sql = @"
                SELECT COUNT(*)
                FROM catalog_items
                WHERE local_source = @local_source AND removed_at IS NULL;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@local_source", localSource);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Returns the number of active catalog items that were previously managed
        /// as .strm files but have since been re-adopted into the user's real library.
        /// Identified by having both a <c>strm_path</c> and <c>local_source='library'</c>.
        /// </summary>
        public Task<int> GetReadoptedCountAsync()
        {
            const string sql = @"
                SELECT COUNT(*)
                FROM catalog_items
                WHERE local_source = 'library'
                  AND strm_path IS NOT NULL
                  AND removed_at IS NULL;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Returns all active catalog items with the given <c>local_source</c> value.
        /// Used by <see cref="Tasks.FileResurrectionTask"/> to find library-tracked
        /// items whose original file may have gone missing.
        /// </summary>
        public async Task<List<CatalogItem>> GetItemsByLocalSourceAsync(string localSource)
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type, source, source_list_id,
                       seasons_json, strm_path, added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count
                FROM catalog_items
                WHERE local_source = @local_source AND removed_at IS NULL;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@local_source", localSource),
                ReadCatalogItem);
        }

        /// <summary>
        /// Returns all active catalog items that have no .strm file written yet
        /// (strm_path IS NULL) and are not already adopted from the user's library.
        /// Used by <see cref="Tasks.CatalogSyncTask"/> as a catch-up pass so that
        /// items fetched in a prior run (before sync paths were configured) still
        /// get their .strm files written on the next run.
        /// </summary>
        /// <summary>Returns the filesystem path to the SQLite database file.</summary>
        public string GetDatabasePath() => _dbPath;

        /// <summary>
        /// Deletes all rows from <c>resolution_cache</c> (full cache wipe).
        /// Call <see cref="VacuumAsync"/> afterwards to reclaim disk space.
        /// </summary>
        public async Task ClearResolutionCacheAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync("DELETE FROM resolution_cache;", _ => { }, cancellationToken);
        }

        /// <summary>
        /// Clears the <c>last_sync_at</c> timestamp for all sources so the interval
        /// guard is bypassed on the next catalog sync run.
        /// </summary>
        public async Task ResetSyncIntervalsAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync(
                "UPDATE sync_state SET last_sync_at = NULL, consecutive_failures = 0;",
                _ => { }, cancellationToken);
        }

        /// <summary>
        /// Runs SQLite VACUUM to reclaim space after bulk deletes.
        /// This is a full-database operation — avoid calling during active playback.
        /// </summary>
        public async Task VacuumAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync("VACUUM;", _ => { }, cancellationToken);
        }

        /// <summary>
        /// Hard-deletes all rows from <c>catalog_items</c>, <c>sync_state</c>, and
        /// clears the stream_candidates table.
        /// The <c>resolution_cache</c> is intentionally preserved so that cached URLs
        /// can be reused after a re-sync.
        ///
        /// Call <see cref="VacuumAsync"/> and then trigger a catalog sync afterwards
        /// to repopulate from configured sources.
        ///
        /// Returns the list of <c>strm_path</c> values that were stored in the catalog
        /// so the caller can delete the physical files from disk.
        /// </summary>
        public async Task<List<string>> PurgeCatalogAsync(CancellationToken cancellationToken = default)
        {
            // Collect all strm_path values before deletion so the caller can wipe disk files.
            const string selectSql = @"
                SELECT strm_path FROM catalog_items
                WHERE strm_path IS NOT NULL AND strm_path != '';";

            var strmPaths = await QueryListAsync(selectSql, _ => { }, row =>
            {
                var path = row.GetString(0);
                return string.IsNullOrEmpty(path) ? null! : path;
            });
            strmPaths.RemoveAll(string.IsNullOrEmpty);

            // Hard-delete catalog rows, sync tracking, and orphaned candidates.
            // stream_candidates rows are keyed by (imdb_id, season, episode) which
            // overlap with resolution_cache — delete them to avoid orphaned rows since
            // the catalog is being wiped.
            await ExecuteWriteAsync("DELETE FROM catalog_items;",     _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM sync_state;",        _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM stream_candidates;", _ => { }, cancellationToken);

            return strmPaths;
        }

        /// <summary>
        /// Hard-deletes all rows from every table: catalog, cache, candidates,
        /// playback log, client compat, API budget, and sync state.
        /// The database schema (table structure) is preserved.
        ///
        /// After this call, run <see cref="VacuumAsync"/> and then trigger a full
        /// catalog sync to return to a working state.
        ///
        /// Returns the list of <c>strm_path</c> values that were stored in the catalog
        /// so the caller can delete the physical files from disk.
        /// </summary>
        public async Task<List<string>> ResetAllAsync(CancellationToken cancellationToken = default)
        {
            // Collect strm_path values before clearing (same as PurgeCatalogAsync).
            const string selectSql = @"
                SELECT strm_path FROM catalog_items
                WHERE strm_path IS NOT NULL AND strm_path != '';";

            var strmPaths = await QueryListAsync(selectSql, _ => { }, row =>
            {
                var path = row.GetString(0);
                return string.IsNullOrEmpty(path) ? null! : path;
            });
            strmPaths.RemoveAll(string.IsNullOrEmpty);

            await ExecuteWriteAsync("DELETE FROM catalog_items;",     _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM resolution_cache;",  _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM stream_candidates;", _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM playback_log;",      _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM client_compat;",     _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM api_budget;",        _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM sync_state;",        _ => { }, cancellationToken);

            return strmPaths;
        }

        /// <summary>
        /// Persists a key-value pair to the plugin_metadata table.
        /// Uses UPSERT semantics: updates value if key exists, inserts if not.
        /// Sprint 102A-02: Plugin metadata table and persistence.
        /// </summary>
        public async Task PersistMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync(
                "INSERT INTO plugin_metadata (key, value, updated_at) VALUES (@key, @value, @updatedAt) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;",
                cmd =>
                {
                    BindText(cmd, "@key", key);
                    BindText(cmd, "@value", value);
                    BindText(cmd, "@updatedAt", DateTimeOffset.UtcNow.ToString("o"));
                },
                cancellationToken);
        }

        /// <summary>
        /// Retrieves a value from the plugin_metadata table.
        /// Returns null if key not found.
        /// Sprint 102A-02: Plugin metadata table and persistence.
        /// </summary>
        public string? GetMetadata(string key)
        {
            const string sql = "SELECT value FROM plugin_metadata WHERE key = @key;";
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@key", key);
            foreach (var row in stmt.AsRows())
                return row.GetString(0);
            return null;
        }

        public async Task<List<CatalogItem>> GetItemsMissingStrmAsync()
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type, source, source_list_id,
                       seasons_json, strm_path, added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count
                FROM catalog_items
                WHERE removed_at IS NULL
                  AND (strm_path IS NULL OR strm_path = '')
                  AND (local_source IS NULL OR local_source != 'library');";

            return await QueryListAsync(sql, _ => { }, ReadCatalogItem);
        }

        /// <summary>
        /// Increments the resurrection counter for a catalog item by one.
        /// Called by <see cref="Tasks.FileResurrectionTask"/> each time a missing
        /// file is rebuilt as a .strm.
        /// </summary>
        public async Task IncrementResurrectionCountAsync(string imdbId, string source, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET resurrection_count = resurrection_count + 1,
                    updated_at         = datetime('now')
                WHERE imdb_id = @imdb_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindText(cmd, "@source",  source);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns the sum of all resurrection_count values across active catalog items.
        /// Shown on the health dashboard.
        /// </summary>
        public Task<int> GetTotalResurrectionCountAsync()
        {
            const string sql = @"
                SELECT COALESCE(SUM(resurrection_count), 0)
                FROM catalog_items
                WHERE removed_at IS NULL;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Returns all active series catalog items that have no episode data yet
        /// (<c>seasons_json</c> is NULL or empty).
        /// Used by <see cref="Tasks.EpisodeExpandTask"/> to find series that still
        /// need their full episode list written.
        /// </summary>
        public async Task<List<CatalogItem>> GetSeriesWithoutSeasonsJsonAsync()
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type,
                       source, source_list_id, seasons_json, strm_path,
                       added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count
                FROM catalog_items
                WHERE media_type = 'series'
                  AND removed_at IS NULL
                  AND (seasons_json IS NULL OR seasons_json = '');";

            return await QueryListAsync(sql, null, ReadCatalogItem);
        }

        /// <summary>
        /// Updates the <c>seasons_json</c> column for a catalog item.
        /// Called by <see cref="Tasks.EpisodeExpandTask"/> after writing episode .strm files.
        /// </summary>
        public async Task UpdateSeasonsJsonAsync(string imdbId, string source, string seasonsJson, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET seasons_json = @seasons_json,
                    updated_at   = datetime('now')
                WHERE imdb_id = @imdb_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@seasons_json", seasonsJson);
                BindText(cmd, "@imdb_id",      imdbId);
                BindText(cmd, "@source",       source);
            }, cancellationToken);
        }

        /// <summary>
        /// Parses the unique_ids_json column into a dictionary for fast lookup.
        /// Returns empty dictionary if the JSON is null or malformed.
        /// Format: [{"provider":"imdb","id":"tt1160419"},{"provider":"kitsu","id":"48363"}]
        /// </summary>
        public Dictionary<string, string> ParseUniqueIdsJson(string? uniqueIdsJson)
        {
            if (string.IsNullOrEmpty(uniqueIdsJson))
                return new Dictionary<string, string>();

            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<UniqueId[]>(uniqueIdsJson);
                if (ids == null) return new Dictionary<string, string>();

                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var uid in ids)
                    dict[uid.Provider] = uid.Id;
                return dict;
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private record UniqueId(string Provider, string Id);

        /// <summary>
        /// Returns a catalog item by any provider ID (not just IMDB).
        /// This enables lookup by Kitsu, AniList, MAL, TMDB, etc.
        /// Uses JSON search on unique_ids_json column.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByProviderIdAsync(string provider, string id)
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type,
                       source, source_list_id, seasons_json, strm_path,
                       added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count
                FROM catalog_items
                WHERE removed_at IS NULL
                  AND EXISTS (
                    SELECT 1 FROM json_each(unique_ids_json)
                    WHERE json_extract(json_each.value, '$.provider') = lower(@provider)
                      AND json_extract(json_each.value, '$.id') = @id
                  )
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => {
                    BindText(cmd, "@provider", provider.ToLower());
                    BindText(cmd, "@id", id);
                },
                ReadCatalogItem);
        }

        /// <summary>
        /// Returns all client compatibility profiles, ordered by client_type.
        /// Used by the health dashboard to show learned per-client streaming behaviour.
        /// </summary>
        public async Task<List<ClientCompatEntry>> GetAllClientCompatsAsync()
        {
            const string sql = @"
                SELECT client_type, supports_redirect, max_safe_bitrate,
                       preferred_quality, test_count, last_tested_at
                FROM client_compat
                ORDER BY client_type;";

            return await QueryListAsync(sql, null, ReadClientCompat);
        }

        // ── resolution_cache repository ─────────────────────────────────────────

        /// <summary>
        /// Inserts or replaces a resolution cache entry.
        /// UNIQUE constraint on (imdb_id, season, episode) drives the upsert.
        /// </summary>
        // Shared SQL constant used by both the individual and the combined-transaction upserts.
        private const string ResolutionCacheUpsertSql = @"
                INSERT INTO resolution_cache
                    (id, imdb_id, season, episode,
                     stream_url, quality_tier, file_name, file_size, file_bitrate_kbps,
                     fallback_1, fallback_1_quality, fallback_2, fallback_2_quality,
                     torrent_hash, rd_cached,
                     resolution_tier, status, resolved_at, expires_at,
                     play_count, last_played_at, retry_count, updated_at)
                VALUES
                    (@id, @imdb_id, @season, @episode,
                     @stream_url, @quality_tier, @file_name, @file_size, @file_bitrate_kbps,
                     @fallback_1, @fallback_1_quality, @fallback_2, @fallback_2_quality,
                     @torrent_hash, @rd_cached,
                     @resolution_tier, @status, @resolved_at, @expires_at,
                     @play_count, @last_played_at, @retry_count, datetime('now'))
                ON CONFLICT(imdb_id, season, episode) DO UPDATE SET
                    stream_url          = excluded.stream_url,
                    quality_tier        = excluded.quality_tier,
                    file_name           = excluded.file_name,
                    file_size           = excluded.file_size,
                    file_bitrate_kbps   = excluded.file_bitrate_kbps,
                    fallback_1          = excluded.fallback_1,
                    fallback_1_quality  = excluded.fallback_1_quality,
                    fallback_2          = excluded.fallback_2,
                    fallback_2_quality  = excluded.fallback_2_quality,
                    torrent_hash        = excluded.torrent_hash,
                    rd_cached           = excluded.rd_cached,
                    resolution_tier     = excluded.resolution_tier,
                    status              = excluded.status,
                    resolved_at         = excluded.resolved_at,
                    expires_at          = excluded.expires_at,
                    retry_count         = excluded.retry_count,
                    updated_at          = datetime('now');";

        private void BindResolutionCacheParams(IStatement cmd, ResolutionEntry entry)
        {
            BindText(cmd, "@id",                 entry.Id);
            BindText(cmd, "@imdb_id",            entry.ImdbId);
            BindNullableInt(cmd,  "@season",            entry.Season);
            BindNullableInt(cmd,  "@episode",           entry.Episode);
            BindText(cmd, "@stream_url",         entry.StreamUrl);
            BindNullableText(cmd, "@quality_tier",      entry.QualityTier);
            BindNullableText(cmd, "@file_name",         entry.FileName);
            BindNullableLong(cmd, "@file_size",         entry.FileSize);
            BindNullableInt(cmd,  "@file_bitrate_kbps", entry.FileBitrateKbps);
            BindNullableText(cmd, "@fallback_1",        entry.Fallback1);
            BindNullableText(cmd, "@fallback_1_quality",entry.Fallback1Quality);
            BindNullableText(cmd, "@fallback_2",        entry.Fallback2);
            BindNullableText(cmd, "@fallback_2_quality",entry.Fallback2Quality);
            BindNullableText(cmd, "@torrent_hash",      entry.TorrentHash);
            cmd.BindParameters["@rd_cached"].Bind(entry.RdCached);
            BindText(cmd, "@resolution_tier",    entry.ResolutionTier);
            BindText(cmd, "@status",             entry.Status);
            BindText(cmd, "@resolved_at",        entry.ResolvedAt);
            BindText(cmd, "@expires_at",         entry.ExpiresAt);
            cmd.BindParameters["@play_count"].Bind(entry.PlayCount);
            BindNullableText(cmd, "@last_played_at",    entry.LastPlayedAt);
            cmd.BindParameters["@retry_count"].Bind(entry.RetryCount);
        }

        public async Task UpsertResolutionCacheAsync(ResolutionEntry entry, CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync(ResolutionCacheUpsertSql,
                cmd => BindResolutionCacheParams(cmd, entry), cancellationToken);
        }

        /// <summary>
        /// Looks up a cached stream for the given (imdb, season, episode) triple.
        /// Returns null on cache miss.
        /// </summary>
        public async Task<ResolutionEntry?> GetCachedStreamAsync(
            string imdbId, int? season, int? episode)
        {
            const string sql = @"
                SELECT id, imdb_id, season, episode,
                       stream_url, quality_tier, file_name, file_size, file_bitrate_kbps,
                       fallback_1, fallback_1_quality, fallback_2, fallback_2_quality,
                       torrent_hash, rd_cached,
                       resolution_tier, status, resolved_at, expires_at,
                       play_count, last_played_at, retry_count
                FROM resolution_cache
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))
                LIMIT 1;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, ReadResolutionEntry);
        }

        /// <summary>
        /// Returns up to <paramref name="maxCount"/> resolution entries that need
        /// (re-)resolution for the given tier.
        ///
        /// Failed items are subject to exponential backoff based on <c>retry_count</c>:
        /// <list type="bullet">
        ///   <item>1st failure  → retry after  6 hours</item>
        ///   <item>2nd failure  → retry after 12 hours</item>
        ///   <item>3rd failure  → retry after 24 hours</item>
        ///   <item>4th+ failure → retry after 48 hours (ceiling)</item>
        /// </list>
        /// </summary>
        public async Task<List<ResolutionEntry>> GetPendingResolutionsByTierAsync(
            string tier, int maxCount)
        {
            // Exponential backoff: hours = min(48, 6 * 2^(retry_count-1))
            // SQLite expression: MIN(48, 6 * CAST(ROUND(POWER(2, MAX(0, retry_count-1))) AS INT))
            // We compute the backoff directly in SQL so no application-side filtering is needed.
            const string sql = @"
                SELECT id, imdb_id, season, episode,
                       stream_url, quality_tier, file_name, file_size, file_bitrate_kbps,
                       fallback_1, fallback_1_quality, fallback_2, fallback_2_quality,
                       torrent_hash, rd_cached,
                       resolution_tier, status, resolved_at, expires_at,
                       play_count, last_played_at, retry_count
                FROM resolution_cache
                WHERE resolution_tier = @tier
                  AND (status = 'stale' OR expires_at < datetime('now'))
                  AND NOT (
                        status = 'failed'
                        AND resolved_at > datetime('now',
                            '-' || CAST(
                                MIN(48, 6 * CAST(ROUND(POWER(2.0, MAX(0, retry_count - 1))) AS INTEGER))
                            AS TEXT) || ' hours')
                  )
                ORDER BY last_played_at DESC
                LIMIT @max_count;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@tier", tier);
                cmd.BindParameters["@max_count"].Bind(maxCount);
            }, ReadResolutionEntry);
        }

        /// <summary>
        /// Increments play_count and updates last_played_at for the specified entry.
        /// </summary>
        public async Task IncrementPlayCountAsync(
            string imdbId, int? season, int? episode, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE resolution_cache
                SET play_count     = play_count + 1,
                    last_played_at = datetime('now')
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, cancellationToken);
        }

        /// <summary>
        /// Marks a resolution entry as <c>failed</c> and increments retry_count.
        /// </summary>
        public async Task MarkStreamFailedAsync(
            string imdbId, int? season, int? episode, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE resolution_cache
                SET status      = 'failed',
                    retry_count = retry_count + 1,
                    resolved_at = datetime('now')
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, cancellationToken);
        }

        /// <summary>
        /// Marks a resolution entry as <c>stale</c>, triggering re-resolution
        /// on the next LinkResolverTask run.
        /// </summary>
        /// <summary>
        /// W7: Returns all resolution_cache entries that currently have <c>status='valid'</c>
        /// and a non-empty stream URL, for use by the dead-link background scan.
        /// Only returns entries whose TTL has not yet expired.
        /// </summary>
        public Task<List<ResolutionEntry>> GetValidCacheEntriesAsync()
        {
            const string sql = @"
                SELECT imdb_id, season, episode, stream_url, status, resolution_tier, expires_at, resolved_at
                FROM resolution_cache
                WHERE status = 'valid'
                  AND expires_at > datetime('now')
                  AND stream_url IS NOT NULL
                  AND stream_url != ''
                ORDER BY expires_at ASC;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);

            var rows = new List<ResolutionEntry>();
            foreach (var row in stmt.AsRows())
            {
                rows.Add(new ResolutionEntry
                {
                    ImdbId         = row.GetString(0),
                    Season         = row.IsDBNull(1) ? (int?)null : row.GetInt(1),
                    Episode        = row.IsDBNull(2) ? (int?)null : row.GetInt(2),
                    StreamUrl      = row.IsDBNull(3) ? string.Empty : row.GetString(3),
                    Status         = row.IsDBNull(4) ? string.Empty : row.GetString(4),
                    ResolutionTier = row.IsDBNull(5) ? string.Empty : row.GetString(5),
                    ExpiresAt      = row.IsDBNull(6) ? string.Empty : row.GetString(6),
                });
            }
            return Task.FromResult(rows);
        }

        public async Task MarkStreamStaleAsync(
            string imdbId, int? season, int? episode, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE resolution_cache
                SET status = 'stale'
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, cancellationToken);
        }

        /// <summary>
        /// Marks all entries sharing a torrent hash as stale (bulk season-pack invalidation).
        /// Called when any episode from the pack returns 403/404 during proxy.
        /// </summary>
        public async Task InvalidateByTorrentHashAsync(string torrentHash, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE resolution_cache
                SET status = 'stale'
                WHERE torrent_hash = @torrent_hash
                  AND torrent_hash IS NOT NULL;";

            await ExecuteWriteAsync(sql,
                cmd => BindText(cmd, "@torrent_hash", torrentHash), cancellationToken);
        }

        // ── stream_candidates repository ────────────────────────────────────────

        /// <summary>
        /// Atomically replaces all candidate rows for a given (imdb_id, season, episode)
        /// with the supplied list.  Runs as a single transaction: delete then bulk-insert.
        /// A no-op when <paramref name="candidates"/> is empty.
        /// </summary>
        public async Task UpsertStreamCandidatesAsync(List<StreamCandidate> candidates, CancellationToken cancellationToken = default)
        {
            if (candidates == null || candidates.Count == 0) return;

            var first = candidates[0];

            const string deleteSql = @"
                DELETE FROM stream_candidates
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            const string insertSql = @"
                INSERT INTO stream_candidates
                    (id, imdb_id, season, episode, rank,
                     provider_key, stream_type, url, headers_json,
                     quality_tier, file_name, file_size, bitrate_kbps,
                     is_cached, resolved_at, expires_at, status,
                     info_hash, file_idx, stream_key, binge_group)
                VALUES
                    (@id, @imdb_id, @season, @episode, @rank,
                     @provider_key, @stream_type, @url, @headers_json,
                     @quality_tier, @file_name, @file_size, @bitrate_kbps,
                     @is_cached, @resolved_at, @expires_at, @status,
                     @info_hash, @file_idx, @stream_key, @binge_group);";

            await _dbWriteGate.WaitAsync(cancellationToken);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c =>
                {
                    // Delete existing candidates for this item
                    using (var delStmt = c.PrepareStatement(deleteSql))
                    {
                        BindText(delStmt,        "@imdb_id", first.ImdbId);
                        BindNullableInt(delStmt, "@season",  first.Season);
                        BindNullableInt(delStmt, "@episode", first.Episode);
                        while (delStmt.MoveNext()) { }
                    }

                    // Bulk-insert new candidates
                    foreach (var cand in candidates)
                    {
                        using var insStmt = c.PrepareStatement(insertSql);
                        BindText(insStmt,        "@id",           cand.Id);
                        BindText(insStmt,        "@imdb_id",      cand.ImdbId);
                        BindNullableInt(insStmt, "@season",       cand.Season);
                        BindNullableInt(insStmt, "@episode",      cand.Episode);
                        insStmt.BindParameters["@rank"].Bind(cand.Rank);
                        BindText(insStmt,        "@provider_key", cand.ProviderKey);
                        BindText(insStmt,        "@stream_type",  cand.StreamType);
                        BindText(insStmt,        "@url",          cand.Url);
                        BindNullableText(insStmt, "@headers_json", cand.HeadersJson);
                        BindNullableText(insStmt, "@quality_tier", cand.QualityTier);
                        BindNullableText(insStmt, "@file_name",    cand.FileName);
                        BindNullableLong(insStmt, "@file_size",    cand.FileSize);
                        BindNullableInt(insStmt,  "@bitrate_kbps", cand.BitrateKbps);
                        insStmt.BindParameters["@is_cached"].Bind(cand.IsCached ? 1 : 0);
                        BindText(insStmt, "@resolved_at", cand.ResolvedAt);
                        BindText(insStmt, "@expires_at",  cand.ExpiresAt);
                        BindText(insStmt, "@status",      cand.Status);
                        BindNullableText(insStmt, "@info_hash",   cand.InfoHash);
                        BindNullableInt(insStmt,  "@file_idx",    cand.FileIdx);
                        BindNullableText(insStmt, "@stream_key",  cand.StreamKey);
                        BindNullableText(insStmt, "@binge_group", cand.BingeGroup);
                        while (insStmt.MoveNext()) { }
                    }
                });
            }
            finally
            {
                _dbWriteGate.Release();
            }
        }

        /// <summary>
        /// Atomically writes both a <see cref="ResolutionEntry"/> (resolution_cache) and
        /// its ranked <see cref="StreamCandidate"/> list (stream_candidates) in a single
        /// SQLite transaction.  Use this instead of calling the two individual upserts
        /// separately when both writes should succeed or fail together.
        /// </summary>
        public async Task UpsertResolutionResultAsync(ResolutionEntry entry, List<StreamCandidate> candidates, CancellationToken cancellationToken = default)
        {
            const string deleteSql = @"
                DELETE FROM stream_candidates
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            const string insertSql = @"
                INSERT INTO stream_candidates
                    (id, imdb_id, season, episode, rank,
                     provider_key, stream_type, url, headers_json,
                     quality_tier, file_name, file_size, bitrate_kbps,
                     is_cached, resolved_at, expires_at, status,
                     info_hash, file_idx, stream_key, binge_group)
                VALUES
                    (@id, @imdb_id, @season, @episode, @rank,
                     @provider_key, @stream_type, @url, @headers_json,
                     @quality_tier, @file_name, @file_size, @bitrate_kbps,
                     @is_cached, @resolved_at, @expires_at, @status,
                     @info_hash, @file_idx, @stream_key, @binge_group);";

            await _dbWriteGate.WaitAsync(cancellationToken);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c =>
                {
                    // 1. Upsert resolution_cache entry
                    using (var stmt = c.PrepareStatement(ResolutionCacheUpsertSql))
                    {
                        BindResolutionCacheParams(stmt, entry);
                        while (stmt.MoveNext()) { }
                    }

                    // 2. Replace stream_candidates for this item (delete then bulk-insert)
                    if (candidates != null && candidates.Count > 0)
                    {
                        var first = candidates[0];
                        using (var delStmt = c.PrepareStatement(deleteSql))
                        {
                            BindText(delStmt,        "@imdb_id", first.ImdbId);
                            BindNullableInt(delStmt, "@season",  first.Season);
                            BindNullableInt(delStmt, "@episode", first.Episode);
                            while (delStmt.MoveNext()) { }
                        }
                        foreach (var cand in candidates)
                        {
                            using var insStmt = c.PrepareStatement(insertSql);
                            BindText(insStmt,        "@id",           cand.Id);
                            BindText(insStmt,        "@imdb_id",      cand.ImdbId);
                            BindNullableInt(insStmt, "@season",       cand.Season);
                            BindNullableInt(insStmt, "@episode",      cand.Episode);
                            insStmt.BindParameters["@rank"].Bind(cand.Rank);
                            BindText(insStmt,        "@provider_key", cand.ProviderKey);
                            BindText(insStmt,        "@stream_type",  cand.StreamType);
                            BindText(insStmt,        "@url",          cand.Url);
                            BindNullableText(insStmt, "@headers_json", cand.HeadersJson);
                            BindNullableText(insStmt, "@quality_tier", cand.QualityTier);
                            BindNullableText(insStmt, "@file_name",    cand.FileName);
                            BindNullableLong(insStmt, "@file_size",    cand.FileSize);
                            BindNullableInt(insStmt,  "@bitrate_kbps", cand.BitrateKbps);
                            insStmt.BindParameters["@is_cached"].Bind(cand.IsCached ? 1 : 0);
                            BindText(insStmt, "@resolved_at", cand.ResolvedAt);
                            BindText(insStmt, "@expires_at",  cand.ExpiresAt);
                            BindText(insStmt, "@status",      cand.Status);
                            BindNullableText(insStmt, "@info_hash",   cand.InfoHash);
                            BindNullableInt(insStmt,  "@file_idx",    cand.FileIdx);
                            BindNullableText(insStmt, "@stream_key",  cand.StreamKey);
                            BindNullableText(insStmt, "@binge_group", cand.BingeGroup);
                            while (insStmt.MoveNext()) { }
                        }
                    }
                });
            }
            finally
            {
                _dbWriteGate.Release();
            }
        }

        /// <summary>
        /// Returns all candidate rows for a given (imdb_id, season, episode),
        /// ordered by rank ascending (rank 0 = primary).
        /// Excludes candidates with status <c>failed</c>.
        /// Returns an empty list when no candidates have been stored yet.
        /// </summary>
        public async Task<List<StreamCandidate>> GetStreamCandidatesAsync(
            string imdbId, int? season, int? episode)
        {
            const string sql = @"
                SELECT id, imdb_id, season, episode, rank,
                       provider_key, stream_type, url, headers_json,
                       quality_tier, file_name, file_size, bitrate_kbps,
                       is_cached, resolved_at, expires_at, status,
                       info_hash, file_idx, stream_key, binge_group
                FROM stream_candidates
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))
                  AND status != 'failed'
                ORDER BY rank ASC;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd,        "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, ReadStreamCandidate);
        }

        /// <summary>
        /// Updates the <c>status</c> of a single candidate row identified by
        /// (imdb_id, season, episode, rank).  Used to mark a URL as
        /// <c>suspect</c> or <c>failed</c> without discarding other ranks.
        /// </summary>
        public async Task UpdateCandidateStatusAsync(
            string imdbId, int? season, int? episode, int rank, string status, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE stream_candidates
                SET status = @status
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))
                  AND rank    = @rank;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd,        "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
                cmd.BindParameters["@rank"].Bind(rank);
                BindText(cmd,        "@status",  status);
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes all candidate rows for an item.
        /// Called by the background rescrape before writing fresh candidates.
        /// </summary>
        public async Task DeleteStreamCandidatesAsync(
            string imdbId, int? season, int? episode, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM stream_candidates
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd,        "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns per-stream-type counts of valid, unexpired candidates.
        /// Used by the dashboard Stream Health card.
        /// Key = stream_type; Value = count of valid unexpired rows.
        /// </summary>
        public Task<Dictionary<string, int>> GetCandidateCountsByTypeAsync()
        {
            const string sql = @"
                SELECT stream_type, COUNT(*) AS cnt
                FROM stream_candidates
                WHERE status   = 'valid'
                  AND expires_at > datetime('now')
                GROUP BY stream_type
                ORDER BY cnt DESC;";

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                var key = row.IsDBNull(0) ? "unknown" : row.GetString(0);
                result[key] = row.IsDBNull(1) ? 0 : row.GetInt(1);
            }
            return Task.FromResult(result);
        }

        // ── playback_log repository ─────────────────────────────────────────────

        /// <summary>
        /// Appends a playback event to the log.
        /// Automatically prunes the table when it exceeds <c>500</c> rows.
        /// </summary>
        public async Task LogPlaybackAsync(PlaybackEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO playback_log
                    (id, imdb_id, title, season, episode,
                     resolution_mode, quality_served, client_type, proxy_mode,
                     latency_ms, bitrate_sustained, quality_downgrade,
                     error_message, played_at)
                VALUES
                    (@id, @imdb_id, @title, @season, @episode,
                     @resolution_mode, @quality_served, @client_type, @proxy_mode,
                     @latency_ms, @bitrate_sustained, @quality_downgrade,
                     @error_message, @played_at);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id",               entry.Id);
                BindText(cmd, "@imdb_id",          entry.ImdbId);
                BindNullableText(cmd, "@title",           entry.Title);
                BindNullableInt(cmd,  "@season",          entry.Season);
                BindNullableInt(cmd,  "@episode",         entry.Episode);
                BindText(cmd, "@resolution_mode",  entry.ResolutionMode);
                BindNullableText(cmd, "@quality_served",  entry.QualityServed);
                BindNullableText(cmd, "@client_type",     entry.ClientType);
                BindNullableText(cmd, "@proxy_mode",      entry.ProxyMode);
                BindNullableInt(cmd,  "@latency_ms",      entry.LatencyMs);
                BindNullableInt(cmd,  "@bitrate_sustained",entry.BitrateSustained);
                cmd.BindParameters["@quality_downgrade"].Bind(entry.QualityDowngrade);
                BindNullableText(cmd, "@error_message",   entry.ErrorMessage);
                BindText(cmd, "@played_at",        entry.PlayedAt);
            }, cancellationToken);

            await PrunePlaybackLogAsync(PlaybackLogMaxRows);
        }

        /// <summary>
        /// Returns the most recent <paramref name="limit"/> playback log entries.
        /// </summary>
        public async Task<List<PlaybackEntry>> GetRecentPlaybackAsync(int limit)
        {
            const string sql = @"
                SELECT id, imdb_id, title, season, episode,
                       resolution_mode, quality_served, client_type, proxy_mode,
                       latency_ms, bitrate_sustained, quality_downgrade,
                       error_message, played_at
                FROM playback_log
                ORDER BY played_at DESC
                LIMIT @limit;";

            return await QueryListAsync(sql,
                cmd => cmd.BindParameters["@limit"].Bind(limit),
                ReadPlaybackEntry);
        }

        /// <summary>
        /// Deletes oldest rows when the table exceeds <paramref name="maxRows"/>.
        /// </summary>
        public async Task PrunePlaybackLogAsync(int maxRows, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM playback_log
                WHERE id IN (
                    SELECT id FROM playback_log
                    ORDER BY played_at DESC
                    LIMIT -1 OFFSET @max_rows
                );";

            await ExecuteWriteAsync(sql,
                cmd => cmd.BindParameters["@max_rows"].Bind(maxRows), cancellationToken);
        }

        // ── client_compat repository ────────────────────────────────────────────

        /// <summary>
        /// Returns the client compat profile for the given client type, or null.
        /// </summary>
        public async Task<ClientCompatEntry?> GetClientCompatAsync(string clientType)
        {
            const string sql = @"
                SELECT client_type, supports_redirect, max_safe_bitrate,
                       preferred_quality, test_count, last_tested_at
                FROM client_compat
                WHERE client_type = @client_type;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@client_type", clientType),
                ReadClientCompat);
        }

        /// <summary>
        /// Inserts or updates a client compatibility profile.
        /// </summary>
        public async Task UpsertClientCompatAsync(ClientCompatEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO client_compat
                    (client_type, supports_redirect, max_safe_bitrate,
                     preferred_quality, test_count, last_tested_at, updated_at)
                VALUES
                    (@client_type, @supports_redirect, @max_safe_bitrate,
                     @preferred_quality, @test_count, @last_tested_at, datetime('now'))
                ON CONFLICT(client_type) DO UPDATE SET
                    supports_redirect = excluded.supports_redirect,
                    max_safe_bitrate  = COALESCE(excluded.max_safe_bitrate, client_compat.max_safe_bitrate),
                    preferred_quality = COALESCE(excluded.preferred_quality, client_compat.preferred_quality),
                    test_count        = excluded.test_count,
                    last_tested_at    = excluded.last_tested_at,
                    updated_at        = datetime('now');";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@client_type",        entry.ClientType);
                cmd.BindParameters["@supports_redirect"].Bind(entry.SupportsRedirect);
                BindNullableInt(cmd,  "@max_safe_bitrate",  entry.MaxSafeBitrate);
                BindNullableText(cmd, "@preferred_quality", entry.PreferredQuality);
                cmd.BindParameters["@test_count"].Bind(entry.TestCount);
                BindNullableText(cmd, "@last_tested_at",    entry.LastTestedAt);
            }, cancellationToken);
        }

        /// <summary>
        /// Updates the learned max bitrate and redirect support for a client.
        /// Called by StreamProxyService during active streaming.
        /// </summary>
        public async Task UpdateClientCompatAsync(
            string clientType, bool supportsRedirect, int? maxBitrate, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO client_compat (client_type, supports_redirect, max_safe_bitrate, test_count, last_tested_at, updated_at)
                VALUES (@client_type, @supports_redirect, @max_safe_bitrate, 1, datetime('now'), datetime('now'))
                ON CONFLICT(client_type) DO UPDATE SET
                    supports_redirect = @supports_redirect,
                    max_safe_bitrate  = COALESCE(@max_safe_bitrate, client_compat.max_safe_bitrate),
                    test_count        = client_compat.test_count + 1,
                    last_tested_at    = datetime('now'),
                    updated_at        = datetime('now');";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@client_type",        clientType);
                cmd.BindParameters["@supports_redirect"].Bind(supportsRedirect ? 1 : 0);
                BindNullableInt(cmd, "@max_safe_bitrate", maxBitrate);
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes all learned client compatibility profiles.
        /// Used by the dashboard "Reset Client Intelligence" action to clear
        /// stale per-device proxy-mode and bitrate-cap records.
        /// </summary>
        public async Task ClearAllClientProfilesAsync(CancellationToken cancellationToken = default)
        {
            const string sql = "DELETE FROM client_compat;";
            await ExecuteWriteAsync(sql, cmd => { }, cancellationToken);
        }

        // ── api_budget repository ───────────────────────────────────────────────

        /// <summary>
        /// Increments today's API call counter by one.
        /// Auto-creates the row for today if it doesn't exist.
        /// </summary>
        public async Task IncrementApiCallCountAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO api_budget (date, calls_made, calls_budget)
                VALUES (date('now'), 1, @budget)
                ON CONFLICT(date) DO UPDATE SET
                    calls_made = api_budget.calls_made + 1;";

            var budget = Plugin.Instance?.Configuration?.ApiDailyBudget ?? 2000;
            await ExecuteWriteAsync(sql,
                cmd => cmd.BindParameters["@budget"].Bind(budget), cancellationToken);
        }

        /// <summary>
        /// Returns <c>true</c> if today's call count has reached or exceeded the
        /// configured daily budget, or if the API is currently in a back-off window.
        /// </summary>
        public Task<bool> IsBudgetExhaustedAsync()
        {
            const string sql = @"
                SELECT calls_made, calls_budget, backoff_until
                FROM api_budget
                WHERE date = date('now')
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                var callsMade    = row.GetInt(0);
                var callsBudget  = row.GetInt(1);
                var backoffUntil = row.IsDBNull(2) ? null : row.GetString(2);

                if (callsMade >= callsBudget)
                    return Task.FromResult(true);

                if (!string.IsNullOrEmpty(backoffUntil)
                    && DateTime.TryParse(backoffUntil, out var backoffDt)
                    && DateTime.UtcNow < backoffDt)
                    return Task.FromResult(true);

                return Task.FromResult(false);
            }

            return Task.FromResult(false); // No row today → fresh budget
        }

        /// <summary>
        /// Records a 429 response and sets a back-off window.
        /// </summary>
        /// <param name="backoffUntil">ISO-8601 UTC timestamp to back off until.</param>
        public async Task RecordRateLimitHitAsync(string backoffUntil, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO api_budget (date, calls_made, calls_budget, last_429_at, backoff_until)
                VALUES (date('now'), 0, @budget, datetime('now'), @backoff_until)
                ON CONFLICT(date) DO UPDATE SET
                    last_429_at  = datetime('now'),
                    backoff_until = @backoff_until;";

            var budget = Plugin.Instance?.Configuration?.ApiDailyBudget ?? 2000;
            await ExecuteWriteAsync(sql, cmd =>
            {
                cmd.BindParameters["@budget"].Bind(budget);
                BindText(cmd, "@backoff_until", backoffUntil);
            }, cancellationToken);
        }

        // ── sync_state repository ───────────────────────────────────────────────

        /// <summary>
        /// Returns the sync state for the given source key, or null if never synced.
        /// </summary>
        public async Task<SyncState?> GetSyncStateAsync(string sourceKey)
        {
            const string sql = @"
                SELECT source_key, last_sync_at, last_etag, last_cursor, item_count, status,
                       consecutive_failures, last_error, last_reached_at,
                       catalog_name, catalog_type, items_target, items_running
                FROM sync_state
                WHERE source_key = @source_key;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@source_key", sourceKey),
                ReadSyncState);
        }

        /// <summary>
        /// Inserts or replaces the full sync state for a source.
        /// Prefer <see cref="RecordSyncSuccessAsync"/> and
        /// <see cref="RecordSyncFailureAsync"/> for routine health recording.
        /// </summary>
        public async Task UpsertSyncStateAsync(SyncState state, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO sync_state
                    (source_key, last_sync_at, last_etag, last_cursor, item_count, status,
                     consecutive_failures, last_error, last_reached_at, updated_at)
                VALUES
                    (@source_key, @last_sync_at, @last_etag, @last_cursor, @item_count, @status,
                     @consecutive_failures, @last_error, @last_reached_at, datetime('now'))
                ON CONFLICT(source_key) DO UPDATE SET
                    last_sync_at         = excluded.last_sync_at,
                    last_etag            = excluded.last_etag,
                    last_cursor          = excluded.last_cursor,
                    item_count           = excluded.item_count,
                    status               = excluded.status,
                    consecutive_failures = excluded.consecutive_failures,
                    last_error           = excluded.last_error,
                    last_reached_at      = excluded.last_reached_at,
                    updated_at           = datetime('now');";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key",   state.SourceKey);
                BindNullableText(cmd, "@last_sync_at",         state.LastSyncAt);
                BindNullableText(cmd, "@last_etag",            state.LastEtag);
                BindNullableText(cmd, "@last_cursor",          state.LastCursor);
                cmd.BindParameters["@item_count"].Bind(state.ItemCount);
                BindText(cmd, "@status",                       state.Status);
                cmd.BindParameters["@consecutive_failures"].Bind(state.ConsecutiveFailures);
                BindNullableText(cmd, "@last_error",           state.LastError);
                BindNullableText(cmd, "@last_reached_at",      state.LastReachedAt);
            }, cancellationToken);
        }

        /// <summary>
        /// Records a successful sync for a source.
        /// Resets <c>consecutive_failures</c> to 0, clears <c>last_error</c>,
        /// updates <c>last_sync_at</c> and <c>last_reached_at</c>, sets status = <c>ok</c>.
        /// </summary>
        public async Task RecordSyncSuccessAsync(string sourceKey, int itemCount, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO sync_state
                    (source_key, last_sync_at, last_reached_at, item_count, status,
                     consecutive_failures, last_error, items_running, updated_at)
                VALUES
                    (@source_key, datetime('now'), datetime('now'), @item_count, 'ok', 0, NULL, @item_count, datetime('now'))
                ON CONFLICT(source_key) DO UPDATE SET
                    last_sync_at         = datetime('now'),
                    last_reached_at      = datetime('now'),
                    item_count           = @item_count,
                    status               = 'ok',
                    consecutive_failures = 0,
                    last_error           = NULL,
                    items_running        = @item_count,
                    updated_at           = datetime('now');";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key", sourceKey);
                cmd.BindParameters["@item_count"].Bind(itemCount);
            }, cancellationToken);
        }

        /// <summary>
        /// Records a failed sync attempt.
        /// Increments <c>consecutive_failures</c> and sets status to
        /// <c>warn</c> (1–2 failures) or <c>error</c> (≥ 3).
        /// Does <em>not</em> update <c>last_sync_at</c> or <c>item_count</c>
        /// — those reflect the last <em>successful</em> run only.
        /// </summary>
        public async Task RecordSyncFailureAsync(string sourceKey, string errorMessage, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO sync_state
                    (source_key, status, consecutive_failures, last_error)
                VALUES
                    (@source_key, 'warn', 1, @error)
                ON CONFLICT(source_key) DO UPDATE SET
                    consecutive_failures = sync_state.consecutive_failures + 1,
                    last_error           = @error,
                    status               = CASE
                        WHEN sync_state.consecutive_failures + 1 >= 3 THEN 'error'
                        ELSE 'warn'
                    END;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key", sourceKey);
                BindText(cmd, "@error",      errorMessage);
            }, cancellationToken);
        }

        /// <summary>
        /// Marks a catalog as actively syncing and records the target item count.
        /// Called before each catalog page-fetch begins so the dashboard can show
        /// a live "running" indicator immediately.
        /// </summary>
        public async Task RecordCatalogRunningAsync(
            string sourceKey,
            string catalogName,
            string catalogType,
            int    itemsTarget,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
        INSERT INTO sync_state
            (source_key, catalog_name, catalog_type, items_target, items_running, status)
        VALUES
            (@source_key, @catalog_name, @catalog_type, @items_target, 0, 'running')
        ON CONFLICT(source_key) DO UPDATE SET
            catalog_name  = @catalog_name,
            catalog_type  = @catalog_type,
            items_target  = @items_target,
            items_running = 0,
            status        = 'running';";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key",    sourceKey);
                BindText(cmd, "@catalog_name",  catalogName);
                BindText(cmd, "@catalog_type",  catalogType);
                cmd.BindParameters["@items_target"].Bind(itemsTarget);
            }, cancellationToken);
        }

        /// <summary>
        /// Updates the live item count for an actively-syncing catalog.
        /// Called after each fetched page.
        /// </summary>
        public async Task UpdateCatalogProgressAsync(string sourceKey, int itemsRunning, CancellationToken cancellationToken = default)
        {
            const string sql = @"
        INSERT INTO sync_state (source_key, items_running)
        VALUES (@source_key, @items_running)
        ON CONFLICT(source_key) DO UPDATE SET
            items_running = @items_running;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key",   sourceKey);
                cmd.BindParameters["@items_running"].Bind(itemsRunning);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all sync state rows ordered by source key.
        /// Used by the health dashboard to show all provider and per-catalog statuses.
        /// </summary>
        public async Task<List<SyncState>> GetAllSyncStatesAsync()
        {
            const string sql = @"
                SELECT source_key, last_sync_at, last_etag, last_cursor, item_count, status,
                       consecutive_failures, last_error, last_reached_at,
                       catalog_name, catalog_type, items_target, items_running
                FROM sync_state
                ORDER BY source_key;";

            return await QueryListAsync(sql, null, ReadSyncState);
        }

        /// <summary>
        /// Clears all sync states so the next sync will run immediately.
        /// Used to force a re-sync after configuration changes.
        /// </summary>
        public async Task ClearAllSyncStatesAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync("DELETE FROM sync_state;", _ => { }, cancellationToken);
            _logger.LogInformation("[DatabaseManager] All sync states cleared");
        }

        // ── collection_membership repository ───────────────────────────────────────────
        // Sprint 100C-01: Collection membership recording.

        /// <summary>
        /// Records that an item belongs to a collection.
        /// Upserts on (collection_name, emby_item_id).
        /// Sprint 100C-01: Collection membership recording.
        /// </summary>
        public async Task UpsertCollectionMembershipAsync(
            string collectionName,
            string embyItemId,
            string source,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO collection_membership
                    (collection_name, emby_item_id, source, last_seen)
                VALUES (@collection_name, @emby_item_id, @source, datetime('now'))
                ON CONFLICT(collection_name, emby_item_id)
                DO UPDATE SET source = @source, last_seen = datetime('now');";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@collection_name", collectionName);
                BindText(cmd, "@emby_item_id", embyItemId);
                BindText(cmd, "@source", source);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all distinct collection names with their item counts.
        /// Used by CollectionSyncTask to iterate over collections.
        /// Sprint 100C-02: Collection sync task.
        /// </summary>
        public async Task<Dictionary<string, int>> GetAllCollectionsAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT collection_name, COUNT(*) as item_count
                FROM collection_membership
                GROUP BY collection_name
                ORDER BY collection_name;";

            var results = await QueryListAsync(sql, null, row =>
                new
                {
                    Name = row.GetString(0),
                    Count = row.GetInt(1)
                });

            return results.ToDictionary(r => r.Name, r => r.Count);
        }

        /// <summary>
        /// Returns all Emby item IDs that belong to a collection.
        /// Used by CollectionSyncTask to populate BoxSet members.
        /// Sprint 100C-02: Collection sync task.
        /// </summary>
        public async Task<List<string>> GetCollectionMembersAsync(
            string collectionName,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT emby_item_id
                FROM collection_membership
                WHERE collection_name = @collection_name
                ORDER BY last_seen DESC;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@collection_name", collectionName);
            }, row => row.GetString(0));
        }

        /// <summary>
        /// Removes items from collection_membership that are no longer in the collection.
        /// Used by CollectionSyncTask after orphan deletion.
        /// Sprint 100C-02: Collection sync task.
        /// </summary>
        public async Task RemoveCollectionMembersAsync(
            string collectionName,
            List<string> itemIds,
            CancellationToken cancellationToken = default)
        {
            if (itemIds.Count == 0)
                return;

            const string sql = @"
                DELETE FROM collection_membership
                WHERE collection_name = @collection_name
                  AND emby_item_id IN ({placeholders});";

            var placeholders = string.Join(",", itemIds.Select(_ => "?"));
            var finalSql = sql.Replace("{placeholders}", placeholders);

            await ExecuteWriteAsync(finalSql, cmd =>
            {
                BindText(cmd, "@collection_name", collectionName);
                int idx = 1;
                foreach (var itemId in itemIds)
                {
                    BindText(cmd, "@item" + idx, itemId);
                    idx++;
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Clears all collection memberships for a source.
        /// Used when re-syncing catalogs.
        /// </summary>
        public async Task ClearCollectionMembershipsBySourceAsync(
            string source,
            CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync(
                "DELETE FROM collection_membership WHERE source = @source;",
                cmd => BindText(cmd, "@source", source),
                cancellationToken);
        }

        // ── Night 3 helpers: resolution queue, stats ────────────────────────────

        /// <summary>
        /// Inserts or marks stale a resolution_cache row so the
        /// <see cref="Tasks.LinkResolverTask"/> picks it up in the next run.
        /// If a valid row already exists it is bumped to stale so re-resolution
        /// is triggered with the requested tier priority.
        /// </summary>
        public async Task QueueForResolutionAsync(
            string imdbId, int? season, int? episode, string tier, CancellationToken cancellationToken = default)
        {
            // Use a two-step upsert:
            //   1. INSERT OR IGNORE — creates a placeholder row if none exists.
            //      stream_url = '' satisfies NOT NULL; status='stale' + expires_at=now
            //      ensures GetPendingResolutionsByTierAsync picks it up.
            //   2. UPDATE — if the row already existed and is valid/stale, refresh tier + stale flag.
            const string insertSql = @"
                INSERT OR IGNORE INTO resolution_cache
                    (id, imdb_id, season, episode,
                     stream_url, resolution_tier, status, resolved_at, expires_at)
                VALUES
                    (lower(hex(randomblob(16))), @imdb_id, @season, @episode,
                     '', @tier, 'stale', datetime('now'), datetime('now'));";

            const string updateSql = @"
                UPDATE resolution_cache
                SET resolution_tier = @tier,
                    status          = 'stale',
                    expires_at      = datetime('now')
                WHERE imdb_id  = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            void BindAll(IStatement cmd)
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
                BindText(cmd, "@tier", tier);
            }

            await ExecuteWriteAsync(insertSql, BindAll, cancellationToken);
            await ExecuteWriteAsync(updateSql, BindAll, cancellationToken);
        }

        /// <summary>
        /// Returns the number of active (non-removed) catalog items.
        /// </summary>
        public Task<int> GetCatalogItemCountAsync()
        {
            const string sql = @"
                SELECT COUNT(*) FROM catalog_items WHERE removed_at IS NULL;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Returns the count of catalog items in the given item state.
        /// Used by the Doctor dashboard to display state distribution.
        /// </summary>
        public Task<int> GetCatalogItemCountByItemStateAsync(ItemState state)
        {
            const string sql = @"
                SELECT COUNT(*) FROM catalog_items
                WHERE item_state = @state AND removed_at IS NULL;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            stmt.BindParameters["@state"].Bind((int)state);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Full-text search over catalog item titles (case-insensitive, prefix-friendly).
        /// Returns up to <paramref name="limit"/> active items ordered by title.
        /// </summary>
        public async Task<List<CatalogItem>> SearchCatalogAsync(string query, int limit = 20)
        {
            const string sql = @"
                SELECT id, imdb_id, tmdb_id, unique_ids_json, title, year, media_type,
                       source, source_list_id, seasons_json, strm_path,
                       added_at, updated_at, removed_at,
                       local_path, local_source, resurrection_count
                FROM catalog_items
                WHERE removed_at IS NULL
                  AND title LIKE @q ESCAPE '\'
                ORDER BY title
                LIMIT @limit;";

            // Escape % and _ in the user query so they're treated as literals,
            // then wrap with % for substring match.
            var escaped = query
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");
            var pattern = $"%{escaped}%";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@q",     pattern);
                cmd.BindParameters["@limit"].Bind(limit);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Returns per-source item counts for the health dashboard.
        /// Key = source key (e.g. <c>aiostreams</c>, <c>trakt:username</c>);
        /// Value = number of active (non-removed) catalog items.
        /// </summary>
        public Task<Dictionary<string, int>> GetCatalogCountsBySourceAsync()
        {
            const string sql = @"
                SELECT source, COUNT(*) AS cnt
                FROM catalog_items
                WHERE removed_at IS NULL
                GROUP BY source
                ORDER BY cnt DESC;";

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                var sourceKey = row.IsDBNull(0) ? "unknown" : row.GetString(0);
                var cnt       = row.IsDBNull(1) ? 0 : row.GetInt(1);
                result[sourceKey] = cnt;
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// Returns a simple stats snapshot of the resolution_cache table for the
        /// health dashboard: total rows, valid rows, stale rows, failed rows.
        /// </summary>
        public Task<ResolutionCacheStats> GetResolutionCacheStatsAsync()
        {
            const string sql = @"
                SELECT
                    COUNT(*)                                                          AS total,
                    SUM(CASE WHEN status='valid'  AND expires_at > datetime('now')
                             THEN 1 ELSE 0 END)                                       AS valid_unexpired,
                    SUM(CASE WHEN status='stale'
                              OR (status='valid' AND expires_at <= datetime('now'))
                             THEN 1 ELSE 0 END)                                       AS stale,
                    SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END)                  AS failed
                FROM resolution_cache;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                return Task.FromResult(new ResolutionCacheStats
                {
                    Total          = row.IsDBNull(0) ? 0 : row.GetInt(0),
                    ValidUnexpired = row.IsDBNull(1) ? 0 : row.GetInt(1),
                    Stale          = row.IsDBNull(2) ? 0 : row.GetInt(2),
                    Failed         = row.IsDBNull(3) ? 0 : row.GetInt(3),
                });
            }

            return Task.FromResult(new ResolutionCacheStats());
        }

        /// <summary>
        /// Returns a per-item resolution coverage breakdown for the dashboard.
        ///
        /// Only counts items with <c>local_source='strm'</c> because those are the
        /// items that actually need a cached stream URL at playback time.
        /// Library-tracked items play from their real file path — no resolution needed.
        /// </summary>
        public Task<ResolutionCoverageStats> GetResolutionCoverageAsync()
        {
            // Three correlated subqueries — fast enough for catalogs up to ~5 000 items.
            const string totalSql = @"
                SELECT COUNT(DISTINCT imdb_id)
                FROM catalog_items
                WHERE removed_at IS NULL AND local_source = 'strm';";

            const string validSql = @"
                SELECT COUNT(DISTINCT ci.imdb_id)
                FROM catalog_items ci
                WHERE ci.removed_at IS NULL AND ci.local_source = 'strm'
                  AND EXISTS (
                      SELECT 1 FROM resolution_cache rc
                      WHERE rc.imdb_id    = ci.imdb_id
                        AND rc.status     = 'valid'
                        AND rc.expires_at > datetime('now')
                  );";

            const string uncachedSql = @"
                SELECT COUNT(DISTINCT ci.imdb_id)
                FROM catalog_items ci
                WHERE ci.removed_at IS NULL AND ci.local_source = 'strm'
                  AND NOT EXISTS (
                      SELECT 1 FROM resolution_cache rc
                      WHERE rc.imdb_id = ci.imdb_id
                  );";

            static int RunScalar(IDatabaseConnection conn, string sql)
            {
                using var stmt = conn.PrepareStatement(sql);
                foreach (var row in stmt.AsRows())
                    return row.IsDBNull(0) ? 0 : row.GetInt(0);
                return 0;
            }

            using var conn = OpenConnection();
            var total    = RunScalar(conn, totalSql);
            var valid    = RunScalar(conn, validSql);
            var uncached = RunScalar(conn, uncachedSql);

            return Task.FromResult(new ResolutionCoverageStats
            {
                TotalStrm   = total,
                ValidCached = valid,
                StaleCached = Math.Max(0, total - valid - uncached),
                Uncached    = uncached,
            });
        }

        /// <summary>
        /// Returns today's API budget usage (calls made + budget limit).
        /// Returns zeros if no row exists for today.
        /// </summary>
        public Task<(int CallsMade, int CallsBudget)> GetApiBudgetTodayAsync()
        {
            const string sql = @"
                SELECT calls_made, calls_budget
                FROM api_budget
                WHERE date = date('now')
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
                return Task.FromResult((row.GetInt(0), row.GetInt(1)));

            return Task.FromResult((0, Plugin.Instance?.Configuration?.ApiDailyBudget ?? 2000));
        }

        /// <summary>
        /// U1: Returns catalog items that are currently stuck in a failed resolution state
        /// (status='failed', TTL not yet expired) so the health dashboard can surface them
        /// as "unavailable".  Joined to catalog_items to supply title and media type.
        /// Capped at <paramref name="limit"/> rows ordered by most recently failed first.
        /// </summary>
        public Task<List<(string ImdbId, string Title, int? Season, int? Episode, string ExpiresAt)>>
            GetFailedItemsAsync(int limit = 50)
        {
            const string sql = @"
                SELECT rc.imdb_id, COALESCE(ci.title, rc.imdb_id), rc.season, rc.episode, rc.expires_at
                FROM resolution_cache rc
                LEFT JOIN catalog_items ci
                       ON ci.imdb_id = rc.imdb_id AND ci.removed_at IS NULL
                WHERE rc.status = 'failed'
                  AND rc.expires_at > datetime('now')
                ORDER BY rc.expires_at DESC
                LIMIT @limit;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindNullableInt(stmt, "@limit", limit);

            var rows = new List<(string, string, int?, int?, string)>();
            foreach (var row in stmt.AsRows())
            {
                rows.Add((
                    row.GetString(0),
                    row.GetString(1),
                    row.IsDBNull(2) ? (int?)null : row.GetInt(2),
                    row.IsDBNull(3) ? (int?)null : row.GetInt(3),
                    row.IsDBNull(4) ? string.Empty : row.GetString(4)
                ));
            }
            return Task.FromResult(rows);
        }

        // ── Private: schema creation ────────────────────────────────────────────

        private void ApplyPragmas(IDatabaseConnection conn)
        {
            conn.Execute("PRAGMA journal_mode=WAL;");
            conn.Execute("PRAGMA foreign_keys=ON;");
            conn.Execute("PRAGMA synchronous=NORMAL;");
            conn.Execute("PRAGMA busy_timeout=30000;");

            // Verify and log WAL mode status
            try
            {
                using var stmt = conn.PrepareStatement("PRAGMA journal_mode;");
                var journalMode = "unknown";
                foreach (var row in stmt.AsRows())
                {
                    if (!row.IsDBNull(0))
                        journalMode = row.GetString(0);
                    break;
                }

                if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "[EmbyStreams] Database PRAGMA configured: journal_mode=WAL, foreign_keys=ON, synchronous=NORMAL, busy_timeout=30000");
                }
                else
                {
                    _logger.LogWarning(
                        "[EmbyStreams] Database journal_mode is '{JournalMode}' (expected 'wal'). WAL may not be enabled correctly.",
                        journalMode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Failed to verify journal_mode after applying PRAGMAs");
            }
        }

        private static void CreateSchema(IDatabaseConnection conn)
        {
            // Schema verbatim from SCHEMA.md v3 — do not reorder.
            const string ddl = @"
CREATE TABLE IF NOT EXISTS schema_version (
    version     INTEGER PRIMARY KEY,
    applied_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS catalog_items (
    id              TEXT PRIMARY KEY,
    imdb_id         TEXT NOT NULL,
    tmdb_id         TEXT,
    title           TEXT NOT NULL,
    year            INTEGER,
    media_type      TEXT NOT NULL CHECK(media_type IN ('movie', 'series')),
    source          TEXT NOT NULL,
    source_list_id  TEXT,
    seasons_json    TEXT,
    strm_path       TEXT,
    added_at        TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now')),
    removed_at      TEXT,
    local_path      TEXT,
    local_source    TEXT,
    resurrection_count INTEGER DEFAULT 0,
    item_state      INTEGER NOT NULL DEFAULT 0,
    pin_source      TEXT,
    pinned_at       TEXT,
    UNIQUE(imdb_id, source)
);

CREATE INDEX IF NOT EXISTS idx_catalog_imdb
    ON catalog_items(imdb_id);
CREATE INDEX IF NOT EXISTS idx_catalog_active
    ON catalog_items(removed_at)
    WHERE removed_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_catalog_media_type
    ON catalog_items(media_type, removed_at);

CREATE TABLE IF NOT EXISTS resolution_cache (
    id                  TEXT PRIMARY KEY,
    imdb_id             TEXT NOT NULL,
    season              INTEGER,
    episode             INTEGER,
    stream_url          TEXT NOT NULL,
    quality_tier        TEXT,
    file_name           TEXT,
    file_size           INTEGER,
    file_bitrate_kbps   INTEGER,
    fallback_1          TEXT,
    fallback_1_quality  TEXT,
    fallback_2          TEXT,
    fallback_2_quality  TEXT,
    torrent_hash        TEXT,
    rd_cached           INTEGER DEFAULT 1,
    resolution_tier     TEXT NOT NULL DEFAULT 'tier3'
                            CHECK(resolution_tier IN ('tier0','tier1','tier2','tier3')),
    status              TEXT NOT NULL DEFAULT 'valid'
                            CHECK(status IN ('valid','stale','failed')),
    resolved_at         TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at          TEXT NOT NULL,
    play_count          INTEGER NOT NULL DEFAULT 0,
    last_played_at      TEXT,
    retry_count         INTEGER NOT NULL DEFAULT 0,
    UNIQUE(imdb_id, season, episode)
);

CREATE INDEX IF NOT EXISTS idx_res_imdb
    ON resolution_cache(imdb_id);
CREATE INDEX IF NOT EXISTS idx_res_status
    ON resolution_cache(status, expires_at);
CREATE INDEX IF NOT EXISTS idx_res_tier
    ON resolution_cache(resolution_tier, status);
CREATE INDEX IF NOT EXISTS idx_res_torrent
    ON resolution_cache(torrent_hash)
    WHERE torrent_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_res_priority
    ON resolution_cache(resolution_tier, last_played_at DESC, status);

CREATE TABLE IF NOT EXISTS playback_log (
    id                  TEXT PRIMARY KEY,
    imdb_id             TEXT NOT NULL,
    title               TEXT,
    season              INTEGER,
    episode             INTEGER,
    resolution_mode     TEXT NOT NULL
                            CHECK(resolution_mode IN (
                                'cached','fallback_1','fallback_2',
                                'sync_resolve','failed')),
    quality_served      TEXT,
    client_type         TEXT,
    proxy_mode          TEXT,
    latency_ms          INTEGER,
    bitrate_sustained   INTEGER,
    quality_downgrade   INTEGER DEFAULT 0,
    error_message       TEXT,
    played_at           TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_play_recent
    ON playback_log(played_at DESC);

CREATE TABLE IF NOT EXISTS client_compat (
    client_type         TEXT PRIMARY KEY,
    supports_redirect   INTEGER NOT NULL DEFAULT 1,
    max_safe_bitrate    INTEGER,
    preferred_quality   TEXT,
    test_count          INTEGER NOT NULL DEFAULT 0,
    last_tested_at      TEXT
);

CREATE TABLE IF NOT EXISTS api_budget (
    date            TEXT PRIMARY KEY,
    calls_made      INTEGER NOT NULL DEFAULT 0,
    calls_budget    INTEGER NOT NULL DEFAULT 2000,
    last_429_at     TEXT,
    backoff_until   TEXT
);

CREATE TABLE IF NOT EXISTS sync_state (
    source_key      TEXT PRIMARY KEY,
    last_sync_at    TEXT,
    last_etag       TEXT,
    last_cursor     TEXT,
    item_count      INTEGER DEFAULT 0,
    status          TEXT DEFAULT 'ok'
);

INSERT OR IGNORE INTO schema_version (version) VALUES (3);
";
            foreach (var statement in ddl.Split(';'))
            {
                var sql = statement.Trim();
                if (!string.IsNullOrEmpty(sql))
                    conn.Execute(sql);
            }
        }

        private void MigrateSchema(IDatabaseConnection conn)
        {
            var version = GetSchemaVersion(conn);

            // ── V3 → V4 ─────────────────────────────────────────────────────────
            // Adds local_path and local_source to catalog_items so each row knows
            // whether the media already exists as a real file in the user's library
            // (local_source='library') or is managed as a .strm by this plugin
            // (local_source='strm').  Used by the library-aware sync and the
            // File Resurrection task (Sprint 3).
            if (version < 4)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V4", version);
                if (!ColumnExists(conn, "catalog_items", "local_path"))
                    ExecuteInline(conn, "ALTER TABLE catalog_items ADD COLUMN local_path TEXT;");
                if (!ColumnExists(conn, "catalog_items", "local_source"))
                    ExecuteInline(conn, "ALTER TABLE catalog_items ADD COLUMN local_source TEXT CHECK(local_source IN ('library', 'strm'));");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (4);");
                version = 4;
            }

            // ── V4 → V5 ─────────────────────────────────────────────────────────
            // Adds health-tracking columns to sync_state so the dashboard can show
            // per-source reliability without removing items when a catalog goes
            // temporarily unreachable.
            if (version < 5)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V5", version);
                if (!ColumnExists(conn, "sync_state", "consecutive_failures"))
                    ExecuteInline(conn, "ALTER TABLE sync_state ADD COLUMN consecutive_failures INT DEFAULT 0;");
                if (!ColumnExists(conn, "sync_state", "last_error"))
                    ExecuteInline(conn, "ALTER TABLE sync_state ADD COLUMN last_error TEXT;");
                if (!ColumnExists(conn, "sync_state", "last_reached_at"))
                    ExecuteInline(conn, "ALTER TABLE sync_state ADD COLUMN last_reached_at TEXT;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (5);");
                version = 5;
            }

            // ── V5 → V6 ─────────────────────────────────────────────────────────
            // Adds resurrection_count to catalog_items so FileResurrectionTask can
            // track how many times each item has been rebuilt as a .strm after its
            // original library file went missing.
            if (version < 6)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V6", version);
                if (!ColumnExists(conn, "catalog_items", "resurrection_count"))
                    ExecuteInline(conn, "ALTER TABLE catalog_items ADD COLUMN resurrection_count INTEGER DEFAULT 0;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (6);");
                version = 6;
            }

            // ── V6 → V7 ─────────────────────────────────────────────────────────
            // Introduces stream_candidates table — replaces flat fallback_1/fallback_2
            // columns with a ranked, provider-aware N-deep candidate list per item.
            // Existing resolution_cache rows are preserved; candidates are populated
            // on next LinkResolverTask run or first cache miss (sync resolve).
            if (version < 7)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V7", version);
                ExecuteInline(conn, @"
CREATE TABLE IF NOT EXISTS stream_candidates (
    id           TEXT PRIMARY KEY,
    imdb_id      TEXT NOT NULL,
    season       INTEGER,
    episode      INTEGER,
    rank         INTEGER NOT NULL,
    provider_key TEXT NOT NULL DEFAULT 'unknown',
    stream_type  TEXT NOT NULL DEFAULT 'debrid',
    url          TEXT NOT NULL,
    headers_json TEXT,
    quality_tier TEXT,
    file_name    TEXT,
    file_size    INTEGER,
    bitrate_kbps INTEGER,
    is_cached    INTEGER NOT NULL DEFAULT 1,
    resolved_at  TEXT NOT NULL,
    expires_at   TEXT NOT NULL,
    status       TEXT NOT NULL DEFAULT 'valid'
);");
                ExecuteInline(conn, @"
CREATE UNIQUE INDEX IF NOT EXISTS idx_candidates_key
    ON stream_candidates(imdb_id, COALESCE(season,-1), COALESCE(episode,-1), rank);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_candidates_item
    ON stream_candidates(imdb_id, season, episode, rank);");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (7);");
                version = 7;
            }

            // ── V7 → V8 ─────────────────────────────────────────────────────────
            // Adds per-catalog progress columns to sync_state so the dashboard can
            // show live progress bars (catalog name, type, item target, and
            // running count updated in real time during each sync run).
            if (version < 8)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V8", version);
                if (!ColumnExists(conn, "sync_state", "catalog_name"))
                    ExecuteInline(conn, "ALTER TABLE sync_state ADD COLUMN catalog_name TEXT;");
                if (!ColumnExists(conn, "sync_state", "catalog_type"))
                    ExecuteInline(conn, "ALTER TABLE sync_state ADD COLUMN catalog_type TEXT;");
                if (!ColumnExists(conn, "sync_state", "items_target"))
                    ExecuteInline(conn, "ALTER TABLE sync_state ADD COLUMN items_target INTEGER DEFAULT 0;");
                if (!ColumnExists(conn, "sync_state", "items_running"))
                    ExecuteInline(conn, "ALTER TABLE sync_state ADD COLUMN items_running INTEGER DEFAULT 0;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (8);");
                version = 8;
            }

            // ── V8 → V9 ─────────────────────────────────────────────────────────
            // Adds info_hash and file_idx to stream_candidates so the direct debrid
            // fallback path (Sprint 14) can re-generate a fresh CDN URL from the
            // torrent hash without calling AIOStreams — used when AIOStreams is
            // unreachable and all cached CDN URLs have expired.
            //
            // Also enables cross-item cache invalidation: when one URL from a hash is
            // confirmed dead, all candidates sharing that hash can be marked stale.
            if (version < 9)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V9", version);
                if (!ColumnExists(conn, "stream_candidates", "info_hash"))
                    ExecuteInline(conn, "ALTER TABLE stream_candidates ADD COLUMN info_hash TEXT;");
                if (!ColumnExists(conn, "stream_candidates", "file_idx"))
                    ExecuteInline(conn, "ALTER TABLE stream_candidates ADD COLUMN file_idx INTEGER;");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_candidates_hash
    ON stream_candidates(info_hash) WHERE info_hash IS NOT NULL;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (9);");
                version = 9;
            }

            // ── V9 → V10 ────────────────────────────────────────────────────────
            // Adds updated_at to resolution_cache, client_compat, and sync_state
            // so the dashboard can show when each row was last written and automated
            // tooling can detect stale records without scanning the whole table.
            if (version < 10)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V10", version);
                if (!ColumnExists(conn, "resolution_cache", "updated_at"))
                    ExecuteInline(conn, "ALTER TABLE resolution_cache ADD COLUMN updated_at TEXT;");
                if (!ColumnExists(conn, "client_compat", "updated_at"))
                    ExecuteInline(conn, "ALTER TABLE client_compat ADD COLUMN updated_at TEXT;");
                if (!ColumnExists(conn, "sync_state", "updated_at"))
                    ExecuteInline(conn, "ALTER TABLE sync_state ADD COLUMN updated_at TEXT;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (10);");
                version = 10;
            }

            // ── V12 → V13 ────────────────────────────────────────────────────────
            // Creates discover_catalog table for caching available content from AIOStreams.
            // discover_items (items user has added to library) use catalog_items table.
            if (version < 13)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V13", version);
                ExecuteInline(conn, @"
CREATE TABLE IF NOT EXISTS discover_catalog (
    id                  TEXT PRIMARY KEY,
    imdb_id             TEXT NOT NULL,
    title               TEXT NOT NULL,
    year                INTEGER,
    media_type          TEXT NOT NULL CHECK(media_type IN ('movie', 'series')),
    poster_url          TEXT,
    backdrop_url        TEXT,
    overview            TEXT,
    catalog_source      TEXT NOT NULL,
    is_in_user_library  INTEGER NOT NULL DEFAULT 0,
    added_at            TEXT NOT NULL,
    updated_at          TEXT NOT NULL DEFAULT (datetime('now'))
);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_discover_imdb
    ON discover_catalog(imdb_id);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_discover_source
    ON discover_catalog(catalog_source);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_discover_in_library
    ON discover_catalog(is_in_user_library);");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (13);");
                version = 13;
            }

            // ── V13 → V14 ────────────────────────────────────────────────────────
            // Adds genres and imdb_rating columns to discover_catalog for richer metadata.
            // Creates FTS5 virtual table for fast full-text search on title.
            // Adds INSERT/UPDATE/DELETE triggers to keep FTS index in sync.
            if (version < 14)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V14", version);
                if (!ColumnExists(conn, "discover_catalog", "genres"))
                    ExecuteInline(conn, "ALTER TABLE discover_catalog ADD COLUMN genres TEXT;");
                if (!ColumnExists(conn, "discover_catalog", "imdb_rating"))
                    ExecuteInline(conn, "ALTER TABLE discover_catalog ADD COLUMN imdb_rating REAL;");
                ExecuteInline(conn, @"
CREATE VIRTUAL TABLE IF NOT EXISTS discover_catalog_fts
USING fts5(title, content=discover_catalog, content_rowid=rowid);");
                // Populate FTS index from existing rows
                ExecuteInline(conn, @"
INSERT INTO discover_catalog_fts(rowid, title)
SELECT rowid, title FROM discover_catalog;");
                // Trigger to keep FTS in sync on INSERT
                ExecuteInline(conn, @"
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_insert
AFTER INSERT ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(rowid, title)
  VALUES (new.rowid, new.title);
END;");
                // Trigger to keep FTS in sync on UPDATE
                ExecuteInline(conn, @"
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_update
AFTER UPDATE ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(discover_catalog_fts, rowid, title)
  VALUES('delete', old.rowid, old.title);
  INSERT INTO discover_catalog_fts(rowid, title)
  VALUES (new.rowid, new.title);
END;");
                // Trigger to keep FTS in sync on DELETE
                ExecuteInline(conn, @"
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_delete
AFTER DELETE ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(discover_catalog_fts, rowid, title)
  VALUES('delete', old.rowid, old.title);
END;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (14);");
                version = 14;
            }

            // ── V14 → V15 ────────────────────────────────────────────────────────
            // Adds stream_key (stable dedup key surviving CDN URL rotation) and
            // binge_group (AIOStreams binge-group identifier) to stream_candidates.
            if (version < 15)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V15", version);
                if (!ColumnExists(conn, "stream_candidates", "stream_key"))
                    ExecuteInline(conn, "ALTER TABLE stream_candidates ADD COLUMN stream_key TEXT;");
                if (!ColumnExists(conn, "stream_candidates", "binge_group"))
                    ExecuteInline(conn, "ALTER TABLE stream_candidates ADD COLUMN binge_group TEXT;");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_candidates_stream_key
    ON stream_candidates(stream_key);");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (15);");
                version = 15;
            }

            // ── V15 → V16 ────────────────────────────────────────────────────────
            // Adds item state machine columns to catalog_items:
            // - item_state: CATALOGUED, PRESENT, RESOLVED, RETIRED, ORPHANED, PINNED
            // - pin_source: origin of PIN state (e.g. "user:discover:ISO8601_timestamp")
            // - pinned_at: UTC timestamp when item was pinned
            // All existing items default to CATALOGUED (0).
            if (version < 16)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V16", version);
                if (!ColumnExists(conn, "catalog_items", "item_state"))
                    ExecuteInline(conn, "ALTER TABLE catalog_items ADD COLUMN item_state INTEGER NOT NULL DEFAULT 0;");
                if (!ColumnExists(conn, "catalog_items", "pin_source"))
                    ExecuteInline(conn, "ALTER TABLE catalog_items ADD COLUMN pin_source TEXT;");
                if (!ColumnExists(conn, "catalog_items", "pinned_at"))
                    ExecuteInline(conn, "ALTER TABLE catalog_items ADD COLUMN pinned_at TEXT;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (16);");
                version = 16;

            // ── V16 → V17 ────────────────────────────────────────────────────────
            // Adds unique_ids_json to catalog_items for multi-provider ID support.
            // Enables fallback episode count queries when IMDB ID is unavailable.
            // Format: JSON array of {provider,id} objects.
            if (version < 17)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V17", version);
                if (!ColumnExists(conn, "catalog_items", "unique_ids_json"))
                    ExecuteInline(conn, "ALTER TABLE catalog_items ADD COLUMN unique_ids_json TEXT;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (17);");
                version = 17;
            }

            // ── V17 → V18 ────────────────────────────────────────────────────────
            // Adds collection_membership table for tracking item collection relationships.
            // Sprint 100C-01: Collection membership recording.
            // Schema: id, collection_name, emby_item_id, source, last_seen
            if (version < 18)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V18", version);
                ExecuteInline(conn, @"
CREATE TABLE IF NOT EXISTS collection_membership (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    collection_name     TEXT NOT NULL,
    emby_item_id        TEXT NOT NULL,
    source              TEXT NOT NULL,
    last_seen           TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(collection_name, emby_item_id)
);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_collection_name
    ON collection_membership(collection_name);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_collection_item
    ON collection_membership(emby_item_id);");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (18);");
                version = 18;
            }

            // ── V18 → V19 ────────────────────────────────────────────────────────
            // Adds absolute_episode_number column to stream_candidates table.
            // Sprint 101A-05: Absolute episode number storage and NFO.
            if (version < 19)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V19", version);
                ExecuteInline(conn, @"
ALTER TABLE stream_candidates ADD COLUMN absolute_episode_number INTEGER;");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (19);");
                version = 19;
            }

            // ── V19 → V20 ────────────────────────────────────────────────────────
            // Adds plugin_metadata table for persisting key-value pairs like last sync times.
            // Sprint 102A-02: Plugin metadata table and persistence.
            if (version < 20)
            {
                _logger.LogInformation("[EmbyStreams] Migrating schema V{From} → V20", version);
                ExecuteInline(conn, @"
CREATE TABLE IF NOT EXISTS plugin_metadata (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);");
                ExecuteInline(conn,
                    "INSERT OR IGNORE INTO schema_version (version) VALUES (20);");
                version = 20;
            }
            }

            // ── Safeguard: Ensure discover_catalog exists (for schema > 14 compatibility) ──
            // If database is at version > 14 (from older builds), migration won't run above.
            // This ensures discover_catalog exists regardless of schema version.
            if (!TableExists(conn, "discover_catalog"))
            {
                _logger.LogInformation("[EmbyStreams] discover_catalog missing — creating safeguard copy");
                ExecuteInline(conn, @"
CREATE TABLE IF NOT EXISTS discover_catalog (
    id                  TEXT PRIMARY KEY,
    imdb_id             TEXT NOT NULL,
    title               TEXT NOT NULL,
    year                INTEGER,
    media_type          TEXT NOT NULL CHECK(media_type IN ('movie', 'series')),
    poster_url          TEXT,
    backdrop_url        TEXT,
    overview            TEXT,
    catalog_source      TEXT NOT NULL,
    is_in_user_library  INTEGER NOT NULL DEFAULT 0,
    genres              TEXT,
    imdb_rating         REAL,
    added_at            TEXT NOT NULL,
    updated_at          TEXT NOT NULL DEFAULT (datetime('now'))
);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_discover_imdb
    ON discover_catalog(imdb_id);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_discover_source
    ON discover_catalog(catalog_source);");
                ExecuteInline(conn, @"
CREATE INDEX IF NOT EXISTS idx_discover_in_library
    ON discover_catalog(is_in_user_library);");
                ExecuteInline(conn, @"
CREATE VIRTUAL TABLE IF NOT EXISTS discover_catalog_fts
USING fts5(title, content=discover_catalog, content_rowid=rowid);");
                ExecuteInline(conn, @"
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_insert
AFTER INSERT ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(rowid, title)
  VALUES (new.rowid, new.title);
END;");
                ExecuteInline(conn, @"
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_update
AFTER UPDATE ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(discover_catalog_fts, rowid, title)
  VALUES('delete', old.rowid, old.title);
  INSERT INTO discover_catalog_fts(rowid, title)
  VALUES (new.rowid, new.title);
END;");
                ExecuteInline(conn, @"
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_delete
AFTER DELETE ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(discover_catalog_fts, rowid, title)
  VALUES('delete', old.rowid, old.title);
END;");
            }

_logger.LogDebug("[EmbyStreams] Schema at version {Version}", version);
        }

        /// <summary>
        /// Check if a table exists in the database.
        /// </summary>
        private static bool TableExists(IDatabaseConnection conn, string tableName)
        {
            try
            {
                using var stmt = conn.PrepareStatement(
                    "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;");
                stmt.BindParameters["@name"].Bind(tableName);
                foreach (var _ in stmt.AsRows())
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if a column exists in a table. Used to make migrations idempotent.
        /// </summary>
        private static bool ColumnExists(IDatabaseConnection conn, string tableName, string columnName)
        {
            try
            {
                // PRAGMA table_info returns one row per column with: cid, name, type, notnull, dflt_value, pk
                using var stmt = conn.PrepareStatement($"PRAGMA table_info('{tableName}');");
                foreach (var row in stmt.AsRows())
                {
                    if (!row.IsDBNull(1) && string.Equals(row.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static int GetSchemaVersion(IDatabaseConnection conn)
        {
            using var stmt = conn.PrepareStatement("SELECT MAX(version) FROM schema_version;");
            foreach (var row in stmt.AsRows()) return row.IsDBNull(0) ? 0 : row.GetInt(0);
            return 0;
        }

        // ── Private: integrity check ────────────────────────────────────────────

        private bool TryIntegrityCheck()
        {
            if (!File.Exists(_dbPath))
                return true; // Fresh install — nothing to check

            try
            {
                using var conn = OpenConnection();
                using var stmt = conn.PrepareStatement("PRAGMA integrity_check;");
                foreach (var row in stmt.AsRows())
                    return string.Equals(row.GetString(0), "ok", StringComparison.OrdinalIgnoreCase);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] integrity_check threw — treating as corrupt");
                return false;
            }
        }

        // ── Private: SQLitePCL.pretty helpers ───────────────────────────────────

        private IDatabaseConnection OpenConnection()
        {
            var conn = SQLite3.Open(_dbPath, ConnectionFlags.ReadWrite | ConnectionFlags.Create, null, true);
            // PRAGMAs are set once during Initialise() - don't set them per-connection
            // to avoid "database is locked" errors from PRAGMA journal_mode=WAL
            return conn;
        }

        private Task ExecuteNonQueryAsync(string sql, Action<IStatement> bindParams)
        {
            using var conn = OpenConnection();
            conn.RunInTransaction(c =>
            {
                using var stmt = c.PrepareStatement(sql);
                bindParams(stmt);
                while (stmt.MoveNext()) { }
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Executes a write operation with serialization through the write gate.
        /// This prevents "database is locked" errors when multiple threads/services
        /// try to write concurrently (e.g., CatalogSyncTask and CatalogDiscoverService).
        /// </summary>
        private async Task ExecuteWriteAsync(string sql, Action<IStatement> bindParams, CancellationToken cancellationToken = default)
        {
            await _dbWriteGate.WaitAsync(cancellationToken);
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
            string sql,
            Action<IStatement>? bindParams,
            Func<IResultSet, T> map) where T : class
        {
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);
            foreach (var row in stmt.AsRows())
                return Task.FromResult<T?>(map(row));
            return Task.FromResult<T?>(null);
        }

        private Task<List<T>> QueryListAsync<T>(
            string sql,
            Action<IStatement>? bindParams,
            Func<IResultSet, T> map)
        {
            using var conn = OpenConnection();
            var results = new List<T>();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);
            foreach (var row in stmt.AsRows())
                results.Add(map(row));
            return Task.FromResult(results);
        }

        private static void ExecuteInline(IDatabaseConnection conn, string sql)
            => conn.Execute(sql);

        // ── Private: parameter binders ──────────────────────────────────────────

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

        // ── Discover Catalog ────────────────────────────────────────────────────

        public async Task UpsertDiscoverCatalogEntryAsync(DiscoverCatalogEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
INSERT OR REPLACE INTO discover_catalog
    (id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
     genres, imdb_rating, catalog_source, is_in_user_library, added_at, updated_at)
VALUES
    (@id, @imdb_id, @title, @year, @media_type, @poster_url, @backdrop_url, @overview,
     @genres, @imdb_rating, @catalog_source, @in_library, @added_at, datetime('now'))";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", entry.Id);
                BindText(cmd, "@imdb_id", entry.ImdbId);
                BindText(cmd, "@title", entry.Title);
                BindNullableInt(cmd, "@year", entry.Year);
                BindText(cmd, "@media_type", entry.MediaType);
                BindNullableText(cmd, "@poster_url", entry.PosterUrl);
                BindNullableText(cmd, "@backdrop_url", entry.BackdropUrl);
                BindNullableText(cmd, "@overview", entry.Overview);
                BindNullableText(cmd, "@genres", entry.Genres);
                if (entry.ImdbRating.HasValue)
                    cmd.BindParameters["@imdb_rating"].Bind(entry.ImdbRating.Value);
                else
                    cmd.BindParameters["@imdb_rating"].BindNull();
                BindText(cmd, "@catalog_source", entry.CatalogSource);
                cmd.BindParameters["@in_library"].Bind(entry.IsInUserLibrary ? 1 : 0);
                BindText(cmd, "@added_at", entry.AddedAt);
            }, cancellationToken);
        }

        public async Task<List<DiscoverCatalogEntry>> GetDiscoverCatalogAsync(int limit = 100, int offset = 0)
        {
            return await GetDiscoverCatalogAsync(limit, offset, null, null);
        }

        public async Task<List<DiscoverCatalogEntry>> GetDiscoverCatalogAsync(int limit, int offset, string? mediaType = null, string? sortBy = null)
        {
            var sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, catalog_source, is_in_user_library, added_at
FROM discover_catalog";

            if (mediaType != null)
            {
                sql += " WHERE media_type = @media_type";
            }

            // Handle sorting
            sql += sortBy?.ToLowerInvariant() switch
            {
                "imdb_rating" => " ORDER BY imdb_rating DESC NULLS LAST, title ASC",
                "title" => " ORDER BY title ASC",
                "added_at" or _ => " ORDER BY added_at DESC"
            };

            sql += " LIMIT @limit OFFSET @offset";

            return await QueryListAsync(sql, cmd =>
            {
                cmd.BindParameters["@limit"].Bind(limit);
                cmd.BindParameters["@offset"].Bind(offset);
                if (mediaType != null) BindText(cmd, "@media_type", mediaType);
            }, ReadDiscoverCatalogEntry);
        }

        public Task<int> GetDiscoverCatalogCountAsync()
        {
            return GetDiscoverCatalogCountAsync(null);
        }

        public Task<int> GetDiscoverCatalogCountAsync(string? mediaType)
        {
            var sql = "SELECT COUNT(*) FROM discover_catalog";
            if (mediaType != null)
            {
                sql += " WHERE media_type = @media_type";
            }

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (mediaType != null)
            {
                stmt.BindParameters["@media_type"].Bind(mediaType);
            }

            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        public async Task<List<DiscoverCatalogEntry>> SearchDiscoverCatalogAsync(string query, string? mediaType = null)
        {
            // Escape FTS5 special characters in the query
            // Double quotes around the query make it an exact phrase match
            var ftsQuery = "\"" + query.Replace("\"", "\"\"") + "\"";

            var sql = @"
SELECT dc.id, dc.imdb_id, dc.title, dc.year, dc.media_type, dc.poster_url, dc.backdrop_url, dc.overview,
       dc.genres, dc.imdb_rating, dc.catalog_source, dc.is_in_user_library, dc.added_at
FROM discover_catalog dc
JOIN discover_catalog_fts fts ON dc.rowid = fts.rowid
WHERE discover_catalog_fts MATCH @query";
            if (mediaType != null)
                sql += " AND dc.media_type = @media_type";
            sql += " ORDER BY dc.is_in_user_library DESC, dc.title ASC LIMIT 50";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@query", ftsQuery);
                if (mediaType != null) BindText(cmd, "@media_type", mediaType);
            }, ReadDiscoverCatalogEntry);
        }

        public async Task<DiscoverCatalogEntry?> GetDiscoverCatalogEntryByImdbIdAsync(string imdbId)
        {
            const string sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE imdb_id = @imdb_id
LIMIT 1";
            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
            }, ReadDiscoverCatalogEntry);
        }

        public async Task UpdateDiscoverCatalogLibraryStatusAsync(string imdbId, bool isInLibrary, CancellationToken cancellationToken = default)
        {
            const string sql = @"
UPDATE discover_catalog
SET is_in_user_library = @in_library, updated_at = datetime('now')
WHERE imdb_id = @imdb_id";
            await ExecuteWriteAsync(sql, cmd =>
            {
                cmd.BindParameters["@in_library"].Bind(isInLibrary ? 1 : 0);
                BindText(cmd, "@imdb_id", imdbId);
            }, cancellationToken);
        }

        public async Task ClearDiscoverCatalogBySourceAsync(string catalogSource, string? mediaType = null, CancellationToken cancellationToken = default)
        {
            var sql = mediaType != null
                ? "DELETE FROM discover_catalog WHERE catalog_source = @source AND media_type = @media_type;"
                : "DELETE FROM discover_catalog WHERE catalog_source = @source;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source", catalogSource);
                if (mediaType != null) BindText(cmd, "@media_type", mediaType);
            }, cancellationToken);
        }

        // ── Channel-specific methods ─────────────────────────────────────────────

        /// <summary>
        /// Get movies from discover catalog for channel browsing.
        /// </summary>
        public List<object> GetDiscoverCatalogMovies(int startIndex = 0, int limit = 100)
        {
            var items = new List<object>();
            using var conn = OpenConnection();
            var sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE media_type = 'movie'
ORDER BY imdb_rating DESC, title ASC
LIMIT @limit OFFSET @offset";

            using var cmd = conn.PrepareStatement(sql);
            cmd.BindParameters["@limit"].Bind(limit);
            cmd.BindParameters["@offset"].Bind(startIndex);

            foreach (var row in cmd.AsRows())
            {
                var entry = ReadDiscoverCatalogEntry(row);
                var channelItem = MapToChannelItem(entry);
                items.Add(channelItem);
            }

            return items;
        }

        /// <summary>
        /// Get TV shows from discover catalog for channel browsing.
        /// </summary>
        public List<object> GetDiscoverCatalogTvShows(int startIndex = 0, int limit = 100)
        {
            var items = new List<object>();
            using var conn = OpenConnection();
            var sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE media_type = 'series'
ORDER BY imdb_rating DESC, title ASC
LIMIT @limit OFFSET @offset";

            using var cmd = conn.PrepareStatement(sql);
            cmd.BindParameters["@limit"].Bind(limit);
            cmd.BindParameters["@offset"].Bind(startIndex);

            foreach (var row in cmd.AsRows())
            {
                var entry = ReadDiscoverCatalogEntry(row);
                var channelItem = MapToChannelItem(entry);
                items.Add(channelItem);
            }

            return items;
        }

        /// <summary>
        /// Get total movie count in discover catalog.
        /// </summary>
        public int GetDiscoverCatalogMovieCount()
        {
            const string sql = "SELECT COUNT(*) FROM discover_catalog WHERE media_type = 'movie'";
            using var conn = OpenConnection();
            using var cmd = conn.PrepareStatement(sql);
            foreach (var row in cmd.AsRows())
            {
                return row.GetInt(0);
            }
            return 0;
        }

        /// <summary>
        /// Get total TV show count in discover catalog.
        /// </summary>
        public int GetDiscoverCatalogTvShowCount()
        {
            const string sql = "SELECT COUNT(*) FROM discover_catalog WHERE media_type = 'series'";
            using var conn = OpenConnection();
            using var cmd = conn.PrepareStatement(sql);
            foreach (var row in cmd.AsRows())
            {
                return row.GetInt(0);
            }
            return 0;
        }

        /// <summary>
        /// Get a specific discover catalog item by ID.
        /// </summary>
        public object? GetDiscoverCatalogItem(string id)
        {
            const string sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE id = @id
LIMIT 1";

            using var conn = OpenConnection();
            using var cmd = conn.PrepareStatement(sql);
            BindText(cmd, "@id", id);

            foreach (var row in cmd.AsRows())
            {
                var entry = ReadDiscoverCatalogEntry(row);
                return MapToChannelItem(entry);
            }

            return null;
        }

        /// <summary>
        /// Convert DiscoverCatalogEntry to a simple object for channel display.
        /// </summary>
        private object MapToChannelItem(DiscoverCatalogEntry entry)
        {
            // Return a simple anonymous object that Emby can understand
            // ChannelItemInfo might not be available in the Data project
            _logger.LogInformation("[Database] Mapping item: {Title} ({ImdbId})", entry.Title, entry.ImdbId);

            // For media items, use null or omit the Type property
            // Only folders should specify ChannelItemType.Folder
            return new
            {
                Name = entry.Title,
                Id = $"discover:{entry.ImdbId}",
                // Type = ChannelItemType.Media,  // Don't set type for media items
                ImageUrl = entry.PosterUrl,
                Overview = entry.Overview,
                CommunityRating = entry.ImdbRating ?? 0,
                ProductionYear = entry.Year ?? 0,
                PremiereDate = entry.Year.HasValue ? new DateTime(entry.Year.Value, 1, 1) : (DateTime?)null,
                GenreList = entry.Genres?.Split(',').Where(g => !string.IsNullOrWhiteSpace(g)).ToList() ?? new List<string>(),
                Tags = new List<string> { "EmbyStreams" },
                ProviderIds = new Dictionary<string, string>
                {
                    ["imdb"] = entry.ImdbId
                }
            };
        }

        // ── Private: row mappers ────────────────────────────────────────────────

        private static CatalogItem ReadCatalogItem(IResultSet r) => new CatalogItem
        {
            Id                = r.GetString(0),
            ImdbId            = r.GetString(1),
            TmdbId            = r.IsDBNull(2)  ? null : r.GetString(2),
            Title             = r.GetString(3),
            Year              = r.IsDBNull(4)  ? null : r.GetInt(4),
            MediaType         = r.GetString(5),
            Source            = r.GetString(6),
            SourceListId      = r.IsDBNull(7)  ? null : r.GetString(7),
            SeasonsJson       = r.IsDBNull(8)  ? null : r.GetString(8),
            StrmPath          = r.IsDBNull(9)  ? null : r.GetString(9),
            AddedAt           = r.GetString(10),
            UpdatedAt         = r.GetString(11),
            RemovedAt         = r.IsDBNull(12) ? null : r.GetString(12),
            LocalPath         = r.IsDBNull(13) ? null : r.GetString(13),
            LocalSource       = r.IsDBNull(14) ? null : r.GetString(14),
            ResurrectionCount = r.IsDBNull(15) ? 0    : r.GetInt(15),
            ItemState         = r.IsDBNull(16) ? ItemState.Catalogued : (ItemState)r.GetInt(16),
            PinSource         = r.IsDBNull(17) ? null : r.GetString(17),
            PinnedAt          = r.IsDBNull(18) ? null : r.GetString(18),
        };

        private static ResolutionEntry ReadResolutionEntry(IResultSet r) => new ResolutionEntry
        {
            Id               = r.GetString(0),
            ImdbId           = r.GetString(1),
            Season           = r.IsDBNull(2)  ? null : r.GetInt(2),
            Episode          = r.IsDBNull(3)  ? null : r.GetInt(3),
            StreamUrl        = r.GetString(4),
            QualityTier      = r.IsDBNull(5)  ? null : r.GetString(5),
            FileName         = r.IsDBNull(6)  ? null : r.GetString(6),
            FileSize         = r.IsDBNull(7)  ? null : r.GetInt64(7),
            FileBitrateKbps  = r.IsDBNull(8)  ? null : r.GetInt(8),
            Fallback1        = r.IsDBNull(9)  ? null : r.GetString(9),
            Fallback1Quality = r.IsDBNull(10) ? null : r.GetString(10),
            Fallback2        = r.IsDBNull(11) ? null : r.GetString(11),
            Fallback2Quality = r.IsDBNull(12) ? null : r.GetString(12),
            TorrentHash      = r.IsDBNull(13) ? null : r.GetString(13),
            RdCached         = r.GetInt(14),
            ResolutionTier   = r.GetString(15),
            Status           = r.GetString(16),
            ResolvedAt       = r.GetString(17),
            ExpiresAt        = r.GetString(18),
            PlayCount        = r.GetInt(19),
            LastPlayedAt     = r.IsDBNull(20) ? null : r.GetString(20),
            RetryCount       = r.GetInt(21),
        };

        private static StreamCandidate ReadStreamCandidate(IResultSet r) => new StreamCandidate
        {
            Id          = r.GetString(0),
            ImdbId      = r.GetString(1),
            Season      = r.IsDBNull(2)  ? null : r.GetInt(2),
            Episode     = r.IsDBNull(3)  ? null : r.GetInt(3),
            Rank        = r.GetInt(4),
            ProviderKey = r.GetString(5),
            StreamType  = r.GetString(6),
            Url         = r.GetString(7),
            HeadersJson = r.IsDBNull(8)  ? null : r.GetString(8),
            QualityTier = r.IsDBNull(9)  ? null : r.GetString(9),
            FileName    = r.IsDBNull(10) ? null : r.GetString(10),
            FileSize    = r.IsDBNull(11) ? null : r.GetInt64(11),
            BitrateKbps = r.IsDBNull(12) ? null : r.GetInt(12),
            IsCached    = r.GetInt(13) != 0,
            ResolvedAt  = r.GetString(14),
            ExpiresAt   = r.GetString(15),
            Status      = r.GetString(16),
            InfoHash    = r.IsDBNull(17) ? null : r.GetString(17),
            FileIdx     = r.IsDBNull(18) ? null : r.GetInt(18),
            StreamKey   = r.IsDBNull(19) ? null : r.GetString(19),
            BingeGroup  = r.IsDBNull(20) ? null : r.GetString(20),
        };

        private static PlaybackEntry ReadPlaybackEntry(IResultSet r) => new PlaybackEntry
        {
            Id             = r.GetString(0),
            ImdbId         = r.GetString(1),
            Title          = r.IsDBNull(2)  ? null : r.GetString(2),
            Season         = r.IsDBNull(3)  ? null : r.GetInt(3),
            Episode        = r.IsDBNull(4)  ? null : r.GetInt(4),
            ResolutionMode = r.GetString(5),
            QualityServed  = r.IsDBNull(6)  ? null : r.GetString(6),
            ClientType     = r.IsDBNull(7)  ? null : r.GetString(7),
            ProxyMode      = r.IsDBNull(8)  ? null : r.GetString(8),
            LatencyMs      = r.IsDBNull(9)  ? null : r.GetInt(9),
            BitrateSustained = r.IsDBNull(10) ? null : r.GetInt(10),
            QualityDowngrade = r.GetInt(11),
            ErrorMessage   = r.IsDBNull(12) ? null : r.GetString(12),
            PlayedAt       = r.GetString(13),
        };

        private static ClientCompatEntry ReadClientCompat(IResultSet r) => new ClientCompatEntry
        {
            ClientType      = r.GetString(0),
            SupportsRedirect = r.GetInt(1),
            MaxSafeBitrate  = r.IsDBNull(2) ? null : r.GetInt(2),
            PreferredQuality = r.IsDBNull(3) ? null : r.GetString(3),
            TestCount       = r.GetInt(4),
            LastTestedAt    = r.IsDBNull(5) ? null : r.GetString(5),
        };

        private static SyncState ReadSyncState(IResultSet r) => new SyncState
        {
            SourceKey           = r.GetString(0),
            LastSyncAt          = r.IsDBNull(1)  ? null : r.GetString(1),
            LastEtag            = r.IsDBNull(2)  ? null : r.GetString(2),
            LastCursor          = r.IsDBNull(3)  ? null : r.GetString(3),
            ItemCount           = r.GetInt(4),
            Status              = r.GetString(5),
            ConsecutiveFailures = r.IsDBNull(6)  ? 0    : r.GetInt(6),
            LastError           = r.IsDBNull(7)  ? null : r.GetString(7),
            LastReachedAt       = r.IsDBNull(8)  ? null : r.GetString(8),
            CatalogName         = r.IsDBNull(9)  ? null : r.GetString(9),
            CatalogType         = r.IsDBNull(10) ? null : r.GetString(10),
            ItemsTarget         = r.IsDBNull(11) ? 0    : r.GetInt(11),
            ItemsRunning        = r.IsDBNull(12) ? 0    : r.GetInt(12),
        };

        private static DiscoverCatalogEntry ReadDiscoverCatalogEntry(IResultSet r) => new DiscoverCatalogEntry
        {
            Id              = r.GetString(0),
            ImdbId          = r.GetString(1),
            Title           = r.GetString(2),
            Year            = r.IsDBNull(3)  ? null : r.GetInt(3),
            MediaType       = r.GetString(4),
            PosterUrl       = r.IsDBNull(5)  ? null : r.GetString(5),
            BackdropUrl     = r.IsDBNull(6)  ? null : r.GetString(6),
            Overview        = r.IsDBNull(7)  ? null : r.GetString(7),
            Genres          = r.IsDBNull(8)  ? null : r.GetString(8),
            ImdbRating      = r.IsDBNull(9)  ? null : r.GetDouble(9),
            CatalogSource   = r.GetString(10),
            IsInUserLibrary = r.GetInt(11) != 0,
            AddedAt         = r.GetString(12),
        };

        #region ICatalogRepository Explicit Implementation

        async Task<IEnumerable<CatalogItem>> ICatalogRepository.GetAllAsync(CancellationToken ct)
        {
            return await GetActiveCatalogItemsAsync();
        }

        async Task<CatalogItem?> ICatalogRepository.GetByIdAsync(string imdbId, CancellationToken ct)
        {
            return await GetCatalogItemByImdbIdAsync(imdbId);
        }

        async Task ICatalogRepository.UpsertAsync(CatalogItem item, CancellationToken ct)
        {
            await UpsertCatalogItemAsync(item, ct);
        }

        async Task ICatalogRepository.DeleteAsync(string imdbId, CancellationToken ct)
        {
            // Mark as removed for all sources since interface only provides imdbId
            const string sql = @"
                UPDATE catalog_items
                SET removed_at = datetime('now')
                WHERE imdb_id = @imdb_id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
            });
        }

        async Task<IEnumerable<CatalogItem>> ICatalogRepository.GetBySourceAsync(string sourceId, CancellationToken ct)
        {
            return await GetCatalogItemsBySourceAsync(sourceId);
        }

        #endregion

        #region IPinRepository Explicit Implementation

        async Task<bool> IPinRepository.IsPinnedAsync(string imdbId, CancellationToken ct)
        {
            var item = await GetCatalogItemByImdbIdAsync(imdbId);
            return item?.ItemState == ItemState.Pinned;
        }

        async Task IPinRepository.PinAsync(string imdbId, CancellationToken ct)
        {
            var item = await GetCatalogItemByImdbIdAsync(imdbId);
            if (item == null)
            {
                // Item doesn't exist in catalog - cannot pin
                return;
            }

            item.ItemState = ItemState.Pinned;
            item.PinSource = "user:manual";
            item.PinnedAt = DateTime.UtcNow.ToString("o");
            item.UpdatedAt = DateTime.UtcNow.ToString("o");

            await UpsertCatalogItemAsync(item, ct);
        }

        async Task IPinRepository.UnpinAsync(string imdbId, CancellationToken ct)
        {
            var item = await GetCatalogItemByImdbIdAsync(imdbId);
            if (item == null)
            {
                return;
            }

            item.ItemState = ItemState.Catalogued;
            item.PinSource = null;
            item.PinnedAt = null;
            item.UpdatedAt = DateTime.UtcNow.ToString("o");

            await UpsertCatalogItemAsync(item, ct);
        }

        async Task<IEnumerable<string>> IPinRepository.GetAllPinnedIdsAsync(CancellationToken ct)
        {
            const string sql = @"
                SELECT imdb_id
                FROM catalog_items
                WHERE item_state = @item_state AND removed_at IS NULL;";

            var result = await QueryListAsync(sql, cmd =>
            {
                BindNullableInt(cmd, "@item_state", (int)ItemState.Pinned);
            }, row => row.GetString(0));
            return result!;
        }

        #endregion

        #region IResolutionCacheRepository Explicit Implementation

        async Task<string?> IResolutionCacheRepository.GetCachedUrlAsync(
            string imdbId, int? season, int episode, CancellationToken ct)
        {
            var entry = await GetCachedStreamAsync(imdbId, season, episode);
            return entry?.StreamUrl;
        }

        async Task IResolutionCacheRepository.SetCachedUrlAsync(
            string imdbId, int? season, int episode,
            string resolvedUrl, DateTime expiresAt, CancellationToken ct)
        {
            var entry = new ResolutionEntry
            {
                ImdbId = imdbId,
                Season = season,
                Episode = episode,
                StreamUrl = resolvedUrl,
                ExpiresAt = expiresAt.ToString("o"),
                Status = "valid",
                ResolvedAt = DateTime.UtcNow.ToString("o")
            };
            await UpsertResolutionCacheAsync(entry, ct);
        }

        async Task IResolutionCacheRepository.InvalidateAsync(string imdbId, CancellationToken ct)
        {
            const string sql = @"
                UPDATE resolution_cache
                SET status = 'stale'
                WHERE imdb_id = @imdb_id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
            });
        }

        async Task IResolutionCacheRepository.PurgeExpiredAsync(CancellationToken ct)
        {
            const string sql = @"
                DELETE FROM resolution_cache
                WHERE expires_at < datetime('now');";

            await ExecuteWriteAsync(sql, _ => { });
        }

        #endregion
    }

    /// <summary>
    /// Extension methods to bridge <see cref="IStatement"/> (IEnumerator) to
    /// a LINQ-compatible <see cref="IEnumerable{IResultSet}"/> for foreach usage.
    /// <c>IStatement</c> implements <c>IEnumerator&lt;IResultSet&gt;</c>; wrapping
    /// it lets us keep the familiar <c>foreach (var row in stmt.AsRows())</c> pattern.
    /// </summary>
    internal static class StatementExtensions
    {
        internal static System.Collections.Generic.IEnumerable<IResultSet> AsRows(
            this IStatement stmt)
        {
            while (stmt.MoveNext())
                yield return stmt.Current;
        }
    }
}
