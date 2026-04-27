using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using InfiniteDrive.Repositories.Interfaces;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using SQLitePCL.pretty;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Data
{
    /// <summary>
    /// Manages the InfiniteDrive SQLite database at
    /// <c>{DataPath}/InfiniteDrive/infinitedrive.db</c>.
    /// Single CREATE TABLE IF NOT EXISTS — no migrations, no schema versioning.
    /// </summary>
    public class DatabaseManager : ICatalogRepository, IResolutionCacheRepository
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const int PlaybackLogMaxRows = 500;

        private static class Tables
        {
            public const string CatalogItems     = "catalog_items";
            public const string ResolutionCache  = "resolution_cache";
            public const string StreamCandidates = "stream_candidates";
            public const string PlaybackLog      = "playback_log";
            public const string ClientCompat     = "client_compat";
            public const string ApiBudget        = "api_budget";
            public const string SyncState        = "sync_state";
            public const string CollectionMembership = "collection_membership";
            public const string HomeSectionTracking = "home_section_tracking";
        }

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly string _dbPath;
        private readonly ILogger _logger;

        // ── Column index caches (populated at init from PRAGMA table_info) ────────
        private static readonly ConcurrentDictionary<string, Dictionary<string, int>> _columnMaps
            = new(StringComparer.OrdinalIgnoreCase);

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
            _dbPath = Path.Combine(dbDirectory, "infinitedrive.db");

            Directory.CreateDirectory(dbDirectory);
        }

        // ── Public initialisation ───────────────────────────────────────────────

        /// <summary>
        /// Opens the database, runs an integrity check, and creates the schema.
        /// If corruption is detected the database file is deleted and recreated.
        /// </summary>
        public void Initialise()
        {
            if (!TryIntegrityCheck())
            {
                _logger.LogWarning(
                    "[InfiniteDrive] Database integrity check failed — deleting and recreating {DbPath}",
                    _dbPath);
                File.Delete(_dbPath);
            }

            using var conn = OpenConnection();
            ApplyPragmas(conn);
            CreateSchema(conn);
            BuildColumnMaps(conn);
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
                     local_path, local_source, item_state, pin_source, pinned_at,
                     nfo_status, retry_count, next_retry_at,
                     blocked_at, blocked_by, first_added_by_user_id,
                     tvdb_id, raw_meta_json, catalog_type, videos_json, episodes_expanded, last_verified_at)
                VALUES
                    (@id, @imdb_id, @tmdb_id, @unique_ids_json, @title, @year, @media_type,
                     @source, @source_list_id, @seasons_json, @strm_path,
                     @added_at, @updated_at, @removed_at,
                     @local_path, @local_source, @item_state, @pin_source, @pinned_at,
                     @nfo_status, @retry_count, @next_retry_at,
                     @blocked_at, @blocked_by, @first_added_by_user_id,
                     @tvdb_id, @raw_meta_json, @catalog_type, @videos_json, @episodes_expanded, @last_verified_at)
                ON CONFLICT(imdb_id, source) DO UPDATE SET
                    tmdb_id       = excluded.tmdb_id,
                    unique_ids_json = COALESCE(excluded.unique_ids_json, catalog_items.unique_ids_json),
                    title         = excluded.title,
                    year          = excluded.year,
                    media_type    = excluded.media_type,
                    seasons_json  = COALESCE(excluded.seasons_json, catalog_items.seasons_json),
                    strm_path     = COALESCE(excluded.strm_path,    catalog_items.strm_path),
                    local_path    = COALESCE(catalog_items.local_path,   excluded.local_path),
                    local_source  = COALESCE(catalog_items.local_source, excluded.local_source),
                    item_state    = COALESCE(excluded.item_state,    catalog_items.item_state),
                    pin_source    = COALESCE(excluded.pin_source,    catalog_items.pin_source),
                    pinned_at     = COALESCE(excluded.pinned_at,     catalog_items.pinned_at),
                    nfo_status    = COALESCE(excluded.nfo_status, catalog_items.nfo_status),
                    retry_count   = COALESCE(excluded.retry_count, catalog_items.retry_count),
                    next_retry_at = COALESCE(excluded.next_retry_at, catalog_items.next_retry_at),
                    blocked_at    = COALESCE(catalog_items.blocked_at, excluded.blocked_at),
                    blocked_by    = COALESCE(catalog_items.blocked_by, excluded.blocked_by),
                    updated_at    = excluded.updated_at,
                    removed_at    = NULL,
                    tvdb_id        = COALESCE(catalog_items.tvdb_id,       excluded.tvdb_id),
                    catalog_type   = COALESCE(catalog_items.catalog_type,  excluded.catalog_type),
                    raw_meta_json  = excluded.raw_meta_json,
                    videos_json    = COALESCE(excluded.videos_json, catalog_items.videos_json),
                    episodes_expanded = COALESCE(excluded.episodes_expanded, catalog_items.episodes_expanded),
                    last_verified_at = excluded.last_verified_at;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id",             item.Id);
                BindText(cmd, "@imdb_id",        item.ImdbId);
                BindNullableText(cmd, "@tmdb_id",        item.TmdbId);
                BindNullableText(cmd, "@unique_ids_json", item.UniqueIdsJson);
                BindText(cmd, "@title",          item.Title);
                BindNullableInt(cmd,  "@year",           item.Year);
                // Validate media_type - must be one of: movie, series, anime, episode, other
                var rawMediaType = item.MediaType;
                var validMediaType = string.IsNullOrEmpty(rawMediaType) ? "movie" : rawMediaType;
                if (validMediaType != "movie" && validMediaType != "series" && validMediaType != "anime" && validMediaType != "episode" && validMediaType != "other")
                {
                    _logger.LogWarning("[InfiniteDrive] Invalid MediaType '{RawMediaType}' for item {ImdbId}, defaulting to 'movie'", rawMediaType, item.ImdbId);
                    validMediaType = "movie";
                }
                BindText(cmd, "@media_type",     validMediaType);
                BindText(cmd, "@source",         item.Source);
                BindNullableText(cmd, "@source_list_id", item.SourceListId);
                BindNullableText(cmd, "@seasons_json",   item.SeasonsJson);
                BindNullableText(cmd, "@strm_path",      item.StrmPath);
                BindText(cmd, "@added_at",       string.IsNullOrEmpty(item.AddedAt) ? DateTime.UtcNow.ToString("o") : item.AddedAt);
                BindText(cmd, "@updated_at",     string.IsNullOrEmpty(item.UpdatedAt) ? DateTime.UtcNow.ToString("o") : item.UpdatedAt);
                BindNullableText(cmd, "@removed_at",     item.RemovedAt);
                BindNullableText(cmd, "@local_path",     item.LocalPath);
                BindNullableText(cmd, "@local_source",   item.LocalSource);
                cmd.BindParameters["@item_state"].Bind((int)item.ItemState);
                BindNullableText(cmd, "@pin_source",     item.PinSource);
                BindNullableText(cmd, "@pinned_at",      item.PinnedAt);
                BindNullableText(cmd, "@nfo_status",     item.NfoStatus);
                BindInt(cmd,         "@retry_count",    item.RetryCount);
                if (item.NextRetryAt.HasValue)
                    cmd.BindParameters["@next_retry_at"].Bind(item.NextRetryAt.Value);
                else
                    cmd.BindParameters["@next_retry_at"].BindNull();
                BindNullableText(cmd, "@blocked_at", item.BlockedAt);
                BindNullableText(cmd, "@blocked_by", item.BlockedBy);
                BindNullableText(cmd, "@first_added_by_user_id", item.FirstAddedByUserId);
                BindNullableText(cmd, "@tvdb_id", item.TvdbId);
                BindNullableText(cmd, "@raw_meta_json", item.RawMetaJson);
                BindNullableText(cmd, "@catalog_type", item.CatalogType);
                BindNullableText(cmd, "@videos_json", item.VideosJson);
                BindNullableInt(cmd, "@episodes_expanded", item.EpisodesExpanded.HasValue ? (item.EpisodesExpanded.Value ? 1 : 0) : null);
                if (item.LastVerifiedAt.HasValue)
                    cmd.BindParameters["@last_verified_at"].Bind(item.LastVerifiedAt.Value);
                else
                    cmd.BindParameters["@last_verified_at"].BindNull();
            });
        }

        /// <summary>
        /// Sets first_added_by_user_id only if not already set (first-writer-wins).
        /// Called by StrmWriterService when writing .strm files for user-added items.
        /// </summary>
        public async Task SetFirstAddedByUserIdIfNotSetAsync(
            string mediaItemId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET first_added_by_user_id = @user_id
                WHERE id = @media_id
                  AND first_added_by_user_id IS NULL;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@media_id", mediaItemId);
                BindText(cmd, "@user_id", userId);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all active (non-removed) catalog items.
        /// </summary>
        public async Task<List<CatalogItem>> GetActiveCatalogItemsAsync()
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND blocked_at IS NULL;";

            return await QueryListAsync(sql, null, ReadCatalogItem);
        }

        /// <summary>
        /// Returns all IMDB IDs for active (non-removed, non-blocked) catalog items.
        /// Used by IChannel for bulk library-decoration (✓ prefix on titles).
        /// </summary>
        public async Task<HashSet<string>> GetAllPinnedImdbIdsAsync()
        {
            const string sql = @"
                SELECT imdb_id FROM catalog_items
                WHERE imdb_id IS NOT NULL AND blocked_at IS NULL AND removed_at IS NULL";

            return await QueryListAsync(sql, _ => { }, row => row.GetString(0))
                .ContinueWith(t => new HashSet<string>(t.Result, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns the first active catalog item with the specified IMDB ID, or null.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByImdbIdAsync(string imdbId)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE imdb_id = @imdb_id AND removed_at IS NULL
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@imdb_id", imdbId),
                ReadCatalogItem);
        }

        /// <summary>
        /// Returns catalog items in the specified state, bounded by limit.
        /// Used by RefreshTask Notify and Verify steps.
        /// </summary>
        public async Task<List<CatalogItem>> GetCatalogItemsByStateAsync(
            ItemState state,
            int limit,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE item_state = @state AND removed_at IS NULL
                LIMIT @limit;";

            return await QueryListAsync(sql, cmd =>
            {
                BindInt(cmd, "@state", (int)state);
                BindInt(cmd, "@limit", limit);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Returns catalog items with expiring tokens (within 90 days), bounded by limit.
        /// Used by RefreshTask Verify step for token renewal.
        /// </summary>
        public async Task<List<CatalogItem>> GetCatalogItemsWithExpiringTokensAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            // TODO: Refactor to query materialized_versions table instead of catalog_items
            // The strm_token_expires_at column belongs in materialized_versions, not catalog_items
            // For now, return all written items without token expiry filtering
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE item_state = 1 AND removed_at IS NULL
                ORDER BY updated_at DESC
                LIMIT @limit;";

            return await QueryListAsync(sql, cmd =>
            {
                BindInt(cmd, "@limit", limit);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Returns catalog item by its .strm file path, or null.
        /// Used by auto-pin on playback to find the item being played.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByStrmPathAsync(string strmPath)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE strm_path = @strm_path AND removed_at IS NULL
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@strm_path", strmPath),
                ReadCatalogItem);
        }


        /// <summary>
        /// Synchronous version of GetCatalogItemByImdbIdAsync for use in
        /// non-async contexts (e.g. GetEpisodeCountForSeason).
        /// </summary>
        public CatalogItem? GetCatalogItemByImdbIdSync(string imdbId)
        {
            const string sql = @"
                SELECT * FROM catalog_items
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
                SELECT * FROM catalog_items
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
        /// Sprint 302-06: Only removes items not verified in >7 days. Items
        /// that were recently verified are kept to handle transient source errors.
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
            var sevenDaysAgo = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();

            // Sprint 302-06: Only remove items that:
            // 1. Are not in current catalog AND
            // 2. Have not been verified in >7 days OR never verified
            var toRemove = existing.Where(x =>
                !currentImdbIds.Contains(x.ImdbId) &&
                (x.LastVerifiedAt == null || x.LastVerifiedAt < sevenDaysAgo)
            ).ToList();

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
        /// Updates last_verified_at for items found in catalog sync.
        /// Used for safe removal: items are only pruned if they've been missing
        /// for >7 days.
        /// </summary>
        public async Task UpdateLastVerifiedAtAsync(
            HashSet<string> imdbIds,
            string source,
            CancellationToken cancellationToken = default)
        {
            if (imdbIds.Count == 0)
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var idsParam = string.Join(",", imdbIds.Select((_, i) => $"@id{i}"));

            var sql = $@"
                UPDATE catalog_items
                SET last_verified_at = @verified_at
                WHERE imdb_id IN ({idsParam}) AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindInt(cmd, "@verified_at", (int)now);
                BindText(cmd, "@source", source);
                for (int i = 0; i < imdbIds.Count; i++)
                {
                    BindText(cmd, $"@id{i}", imdbIds.ElementAt(i));
                }
            }, cancellationToken);
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
        /// Used to find library-tracked items whose original file may have gone missing.
        /// </summary>
        public async Task<List<CatalogItem>> GetItemsByLocalSourceAsync(string localSource)
        {
            const string sql = @"
                SELECT * FROM catalog_items
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
        /// Sprint 311: Deletes failed resolution cache entries (no_streams sentinels) for a specific IMDb ID.
        /// Returns the number of rows deleted.
        /// </summary>
        public async Task<int> ClearFailedSentinelAsync(string imdbId, CancellationToken cancellationToken = default)
        {
            const string countSql = @"
                SELECT COUNT(*) FROM resolution_cache
                WHERE imdb_id = @imdb_id AND status = 'failed';";

            const string deleteSql = @"
                DELETE FROM resolution_cache
                WHERE imdb_id = @imdb_id AND status = 'failed';";

            int count;
            using (var conn = OpenConnection())
            {
                using var stmt = conn.PrepareStatement(countSql);
                BindText(stmt, "@imdb_id", imdbId);
                count = 0;
                foreach (var row in stmt.AsRows())
                    count = row.GetInt(0);
            }

            if (count > 0)
                await ExecuteWriteAsync(deleteSql, cmd => BindText(cmd, "@imdb_id", imdbId), cancellationToken);

            return count;
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
            var safeValue = string.IsNullOrEmpty(value) ? "" : value;
            await ExecuteWriteAsync(
                "INSERT INTO plugin_metadata (key, value, updated_at) VALUES (@key, @value, @updatedAt) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;",
                cmd =>
                {
                    BindText(cmd, "@key", key);
                    BindText(cmd, "@value", safeValue);
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

        // Sprint 350: provider state persistence helpers
        public async Task SetActiveProviderAsync(string provider, CancellationToken ct = default)
        {
            await PersistMetadataAsync("active_provider", provider, ct);
        }

        public string? GetActiveProvider()
        {
            return GetMetadata("active_provider");
        }

        public async Task SetCircuitBreakerStateAsync(string json, CancellationToken ct = default)
        {
            await PersistMetadataAsync("circuit_breaker_state", json, ct);
        }

        public string? GetCircuitBreakerState()
        {
            return GetMetadata("circuit_breaker_state");
        }

        public async Task<List<CatalogItem>> GetItemsMissingStrmAsync()
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND blocked_at IS NULL
                  AND (strm_path IS NULL OR strm_path = '')
                  AND (local_source IS NULL OR local_source != 'library');";

            return await QueryListAsync(sql, _ => { }, ReadCatalogItem);
        }

        /// <summary>
        /// Increments the resurrection counter for a catalog item by one.
        /// Called each time a missing file is rebuilt as a .strm.
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
                SELECT * FROM catalog_items
                WHERE media_type = 'series'
                  AND removed_at IS NULL
                  AND (seasons_json IS NULL OR seasons_json = '');";

            return await QueryListAsync(sql, null, ReadCatalogItem);
        }

        /// <summary>
        /// Returns all <c>media_items</c> rows for series that have been indexed by Emby
        /// (emby_item_id is set) and have a strm_path. Used by SeriesGapDetector
        /// as the bounded input set for gap scanning.
        /// </summary>
        public async Task<List<MediaItem>> GetIndexedSeriesAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                WHERE mi.media_type = 'series'
                  AND mi.emby_item_id IS NOT NULL AND mi.emby_item_id != ''
                  AND mi.strm_path IS NOT NULL AND mi.strm_path != '';";

            return await QueryListAsync(sql, null, ReadMediaItem);
        }

        /// <summary>
        /// Returns series catalog items where <c>seasons_json</c> contains gap data
        /// (at least one season with <c>missingEpisodeNumbers</c>).
        /// Used by <see cref="Services.SeriesGapRepairService"/> to find repair candidates.
        /// </summary>
        public async Task<List<CatalogItem>> GetSeriesWithGapsAsync(int limit, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE media_type IN ('series', 'anime')
                  AND seasons_json IS NOT NULL
                  AND seasons_json != ''
                  AND seasons_json != '[]'
                  AND json_extract(seasons_json, '$[0].missingEpisodeNumbers') IS NOT NULL
                  AND strm_path IS NOT NULL
                  AND removed_at IS NULL
                LIMIT @limit;";

            return await QueryListAsync(sql,
                cmd => BindInt(cmd, "@limit", limit),
                ReadCatalogItem);
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
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND EXISTS (
                    SELECT 1 FROM json_each(unique_ids_json)
                    WHERE lower(json_extract(json_each.value, '$.provider')) = lower(@provider)
                      AND lower(json_extract(json_each.value, '$.id')) = lower(@id)
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
        /// Returns just the raw_meta_json string for a catalog item matched by provider ID.
        /// Skips the full CatalogItem mapper — one column, no index fragility.
        /// </summary>
        public async Task<string?> GetRawMetaJsonByProviderIdAsync(string provider, string id)
        {
            const string sql = @"
                SELECT raw_meta_json FROM catalog_items
                WHERE removed_at IS NULL
                  AND EXISTS (
                    SELECT 1 FROM json_each(unique_ids_json)
                    WHERE lower(json_extract(json_each.value, '$.provider')) = lower(@provider)
                      AND lower(json_extract(json_each.value, '$.id')) = lower(@id)
                  )
                LIMIT 1";

            return await QuerySingleAsync(sql,
                cmd =>
                {
                    BindText(cmd, "@provider", provider);
                    BindText(cmd, "@id", id);
                },
                row => row.IsDBNull(0) ? null! : row.GetString(0));
        }

        /// <summary>
        /// Last-resort catalog lookup by title (and optionally year / mediaType).
        /// Tries exact match first, then LIKE fallback.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByTitleAsync(string title, int? year, string? mediaType)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND title = @title
                  AND (@year IS NULL OR year = @year)
                  AND (@mediaType IS NULL OR media_type = @mediaType)
                LIMIT 1";

            var result = await QuerySingleAsync(sql,
                cmd =>
                {
                    BindText(cmd, "@title", title);
                    BindNullableInt(cmd, "@year", year);
                    BindNullableText(cmd, "@mediaType", mediaType);
                },
                ReadCatalogItem).ConfigureAwait(false);

            if (result != null) return result;

            const string sqlLike = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND title LIKE '%' || @title || '%'
                  AND (@mediaType IS NULL OR media_type = @mediaType)
                LIMIT 1";

            return await QuerySingleAsync(sqlLike,
                cmd =>
                {
                    BindText(cmd, "@title", title);
                    BindNullableText(cmd, "@mediaType", mediaType);
                },
                ReadCatalogItem).ConfigureAwait(false);
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
                     info_hash, file_idx, stream_key, binge_group,
                     languages, subtitles_json)
                VALUES
                    (@id, @imdb_id, @season, @episode, @rank,
                     @provider_key, @stream_type, @url, @headers_json,
                     @quality_tier, @file_name, @file_size, @bitrate_kbps,
                     @is_cached, @resolved_at, @expires_at, @status,
                     @info_hash, @file_idx, @stream_key, @binge_group,
                     @languages, @subtitles_json);";

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
                        BindNullableText(insStmt, "@languages",      cand.Languages);
                        BindNullableText(insStmt, "@subtitles_json",  cand.SubtitlesJson);
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
                     info_hash, file_idx, stream_key, binge_group,
                     languages, subtitles_json)
                VALUES
                    (@id, @imdb_id, @season, @episode, @rank,
                     @provider_key, @stream_type, @url, @headers_json,
                     @quality_tier, @file_name, @file_size, @bitrate_kbps,
                     @is_cached, @resolved_at, @expires_at, @status,
                     @info_hash, @file_idx, @stream_key, @binge_group,
                     @languages, @subtitles_json);";

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
                            BindNullableText(insStmt, "@languages",   cand.Languages);
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
                       info_hash, file_idx, stream_key, binge_group,
                       languages, subtitles_json, probe_json
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
        /// Gets cached ffprobe JSON for a stream candidate by its stream_key.
        /// </summary>
        public async Task<string?> GetProbeJsonAsync(string streamKey)
        {
            if (string.IsNullOrEmpty(streamKey)) return null;
            const string sql = @"
                SELECT probe_json FROM stream_candidates
                WHERE stream_key = @stream_key AND probe_json IS NOT NULL
                LIMIT 1";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@stream_key", streamKey),
                r => r.GetString(0)).ConfigureAwait(false);
        }

        /// <summary>
        /// Saves ffprobe JSON for all candidates sharing the same stream_key.
        /// </summary>
        public async Task SaveProbeJsonAsync(string streamKey, string probeJson)
        {
            if (string.IsNullOrEmpty(streamKey) || string.IsNullOrEmpty(probeJson)) return;
            const string sql = @"
                UPDATE stream_candidates
                SET probe_json = @probe_json
                WHERE stream_key = @stream_key";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@stream_key", streamKey);
                BindText(cmd, "@probe_json", probeJson);
            }).ConfigureAwait(false);
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

        // ── ingestion_state repository (Sprint 142) ─────────────────────────────

        public async Task<IngestionState?> GetIngestionStateAsync(
            string sourceId,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT source_id, last_poll_at, last_found_at, watermark
                FROM ingestion_state
                WHERE source_id = @sid;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@sid", sourceId);
            }, row => new IngestionState
            {
                SourceId = row.GetString(0),
                LastPollAt = row.GetString(1),
                LastFoundAt = row.GetString(2),
                Watermark = row.GetString(3),
            });
        }

        public async Task UpsertIngestionStateAsync(
            IngestionState state,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO ingestion_state (source_id, last_poll_at, last_found_at, watermark)
                VALUES (@sid, @poll, @found, @watermark)
                ON CONFLICT(source_id) DO UPDATE SET
                    last_poll_at = excluded.last_poll_at,
                    last_found_at = excluded.last_found_at,
                    watermark = excluded.watermark;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@sid", state.SourceId);
                BindText(cmd, "@poll", state.LastPollAt);
                BindText(cmd, "@found", state.LastFoundAt);
                BindText(cmd, "@watermark", state.Watermark);
            }, ct);
        }

        // ── refresh_run_log repository (Sprint 142) ─────────────────────────────

        public Task<long> InsertRunLogAsync(
            string worker,
            string step,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO refresh_run_log (worker, step, status)
                VALUES (@worker, @step, 'started');";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@worker", worker);
            BindText(stmt, "@step", step);
            stmt.MoveNext();
            return Task.FromResult(GetLastInsertRowId(conn));
        }

        public async Task UpdateRunLogAsync(
            long id,
            string status,
            int itemsAffected,
            string? notes,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE refresh_run_log
                SET status = @status,
                    items_affected = @affected,
                    notes = @notes
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@status", status);
                BindInt(cmd, "@affected", itemsAffected);
                BindNullableText(cmd, "@notes", notes);
                BindInt(cmd, "@id", (int)id);
            }, ct);
        }

        public async Task<RefreshRunLog?> GetLatestRunAsync(
            string worker,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, run_at, worker, step, status, items_affected, notes
                FROM refresh_run_log
                WHERE worker = @worker
                ORDER BY id DESC
                LIMIT 1;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@worker", worker);
            }, row => new RefreshRunLog
            {
                Id = row.GetInt64(0),
                RunAt = row.GetString(1),
                Worker = row.GetString(2),
                Step = row.GetString(3),
                Status = row.GetString(4),
                ItemsAffected = row.GetInt(5),
                Notes = row.IsDBNull(6) ? null : row.GetString(6),
            });
        }

        // ── Sprint 142: NFO status methods ─────────────────────────────────────

        public async Task UpdateNfoStatusAsync(
            string imdbId,
            string source,
            string nfoStatus,
            int? retryCount,
            string? nextRetryAt,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET nfo_status = @nfo_status,
                    retry_count = COALESCE(@retry_count, retry_count + 1),
                    next_retry_at = @next_retry_at
                WHERE imdb_id = @imdb_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@nfo_status", nfoStatus);
                if (retryCount.HasValue)
                    BindInt(cmd, "@retry_count", retryCount.Value);
                else
                    cmd.BindParameters["@retry_count"].BindNull();
                if (nextRetryAt != null)
                    BindText(cmd, "@next_retry_at", nextRetryAt);
                else
                    cmd.BindParameters["@next_retry_at"].BindNull();
                BindText(cmd, "@imdb_id", imdbId);
                BindText(cmd, "@source", source);
            }, ct);
        }

        public async Task<List<CatalogItem>> GetItemsByNfoStatusAsync(
            string nfoStatus,
            int limit,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE nfo_status = @nfo_status
                  AND removed_at IS NULL
                  AND blocked_at IS NULL
                LIMIT @limit;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@nfo_status", nfoStatus);
                BindInt(cmd, "@limit", limit);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Sets the nfo_status for a catalog item by id.
        /// Used by MarvinTask for enrichment retry backoff.
        /// </summary>
        public async Task SetNfoStatusAsync(string itemId, string nfoStatus, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET nfo_status = @nfo_status, updated_at = datetime('now')
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@nfo_status", nfoStatus);
                BindText(cmd, "@id", itemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Updates retry_count and next_retry_at for a catalog item by id.
        /// Used by MarvinTask for enrichment retry backoff.
        /// </summary>
        public async Task UpdateItemRetryInfoAsync(
            string itemId,
            int retryCount,
            long? nextRetryAt,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET retry_count = @retry_count,
                    next_retry_at = @next_retry_at,
                    updated_at = datetime('now')
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindInt(cmd, "@retry_count", retryCount);
                if (nextRetryAt.HasValue)
                    BindInt(cmd, "@next_retry_at", (int)nextRetryAt.Value);
                else
                    cmd.BindParameters["@next_retry_at"].BindNull();
                BindText(cmd, "@id", itemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all catalog items with blocked_at IS NOT NULL (admin-blocked tombstones).
        /// Used by AdminService for the Blocked Items tab.
        /// </summary>
        public async Task<List<CatalogItem>> GetBlockedItemsAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE blocked_at IS NOT NULL
                ORDER BY blocked_at DESC;";

            return await QueryListAsync(sql, null, ReadCatalogItem);
        }

        /// <summary>
        /// Clears the blocked_at/blocked_by tombstone and resets the item to NeedsEnrich.
        /// Used by AdminService Unblock action.
        /// </summary>
        public async Task UnblockItemAsync(string itemId, CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET blocked_at  = NULL,
                    blocked_by  = NULL,
                    nfo_status  = 'NeedsEnrich',
                    retry_count = 0,
                    next_retry_at = NULL,
                    updated_at  = datetime('now')
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", itemId);
            }, ct);

            _logger.LogInformation("[DatabaseManager] Unblocked item {Id}", itemId);
        }

        /// <summary>
        /// Returns the set of IMDB IDs the user has pinned (via any pin source).
        /// Used by DiscoverService to compute per-user InLibrary status.
        /// </summary>
        /// <summary>
        /// Returns catalog items by a list of IDs.
        /// </summary>
        public async Task<List<CatalogItem>> GetCatalogItemsByIdsAsync(List<string> ids, CancellationToken ct = default)
        {
            if (ids == null || ids.Count == 0)
                return new List<CatalogItem>();

            var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
            var sql = $@"
                SELECT * FROM catalog_items
                WHERE id IN ({placeholders});";

            return await QueryListAsync(sql, cmd =>
            {
                for (var i = 0; i < ids.Count; i++)
                    BindText(cmd, $"@id{i}", ids[i]);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Queries for a scalar integer value (COUNT, SUM, etc.).
        /// Used by MarvinTask for counting Blocked and NeedsEnrich items.
        /// </summary>
        public Task<int> QueryScalarIntAsync(string sql, CancellationToken cancellationToken = default)
        {
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);

            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));

            return Task.FromResult(0);
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
        /// Used by the Marvin dashboard to display state distribution.
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
                SELECT * FROM catalog_items
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
                        "[InfiniteDrive] Database PRAGMA configured: journal_mode=WAL, foreign_keys=ON, synchronous=NORMAL, busy_timeout=30000");
                }
                else
                {
                    _logger.LogWarning(
                        "[InfiniteDrive] Database journal_mode is '{JournalMode}' (expected 'wal'). WAL may not be enabled correctly.",
                        journalMode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Failed to verify journal_mode after applying PRAGMAs");
            }
        }

        private void CreateSchema(IDatabaseConnection conn)
        {
            const string ddl = @"
-- ── catalog_items (JSON-first) ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS catalog_items (
    id                      TEXT PRIMARY KEY,
    imdb_id                 TEXT NOT NULL,
    tmdb_id                 TEXT,
    title                   TEXT NOT NULL,
    year                    INTEGER,
    media_type              TEXT NOT NULL CHECK(media_type IN ('movie', 'series', 'anime', 'episode', 'other')),
    source                  TEXT NOT NULL,
    source_list_id          TEXT,
    seasons_json            TEXT,
    strm_path               TEXT,
    added_at                TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at              TEXT NOT NULL DEFAULT (datetime('now')),
    removed_at              TEXT,
    local_path              TEXT,
    local_source            TEXT,
    resurrection_count      INTEGER DEFAULT 0,
    item_state              INTEGER NOT NULL DEFAULT 0,
    pin_source              TEXT,
    pinned_at               TEXT,
    unique_ids_json         TEXT,
    nfo_status              TEXT,
    retry_count             INTEGER DEFAULT 0,
    next_retry_at           INTEGER,
    blocked_at              TEXT,
    blocked_by              TEXT,
    first_added_by_user_id  TEXT NULL,
    tvdb_id                 TEXT,
    raw_meta_json           TEXT,
    catalog_type            TEXT,
    videos_json             TEXT,
    episodes_expanded       INTEGER,
    last_verified_at        INTEGER,
    UNIQUE(imdb_id, source)
);
CREATE INDEX IF NOT EXISTS idx_catalog_imdb ON catalog_items(imdb_id);
CREATE INDEX IF NOT EXISTS idx_catalog_active ON catalog_items(removed_at) WHERE removed_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_catalog_media_type ON catalog_items(media_type, removed_at);

-- ── resolution_cache ────────────────────────────────────────────────────────
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
    resolution_tier     TEXT NOT NULL DEFAULT 'tier3' CHECK(resolution_tier IN ('tier0','tier1','tier2','tier3')),
    status              TEXT NOT NULL DEFAULT 'valid' CHECK(status IN ('valid','stale','failed')),
    resolved_at         TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at          TEXT NOT NULL,
    play_count          INTEGER NOT NULL DEFAULT 0,
    last_played_at      TEXT,
    retry_count         INTEGER NOT NULL DEFAULT 0,
    updated_at          TEXT,
    UNIQUE(imdb_id, season, episode)
);
CREATE INDEX IF NOT EXISTS idx_res_imdb ON resolution_cache(imdb_id);
CREATE INDEX IF NOT EXISTS idx_res_status ON resolution_cache(status, expires_at);
CREATE INDEX IF NOT EXISTS idx_res_tier ON resolution_cache(resolution_tier, status);
CREATE INDEX IF NOT EXISTS idx_res_torrent ON resolution_cache(torrent_hash) WHERE torrent_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_res_priority ON resolution_cache(resolution_tier, last_played_at DESC, status);

-- ── playback_log ────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS playback_log (
    id                  TEXT PRIMARY KEY,
    imdb_id             TEXT NOT NULL,
    title               TEXT,
    season              INTEGER,
    episode             INTEGER,
    resolution_mode     TEXT NOT NULL CHECK(resolution_mode IN ('cached','fallback_1','fallback_2','sync_resolve','failed')),
    quality_served      TEXT,
    client_type         TEXT,
    proxy_mode          TEXT,
    latency_ms          INTEGER,
    bitrate_sustained   INTEGER,
    quality_downgrade   INTEGER DEFAULT 0,
    error_message       TEXT,
    played_at           TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_play_recent ON playback_log(played_at DESC);

-- ── client_compat ───────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS client_compat (
    client_type         TEXT PRIMARY KEY,
    supports_redirect   INTEGER NOT NULL DEFAULT 1,
    max_safe_bitrate    INTEGER,
    preferred_quality   TEXT,
    test_count          INTEGER NOT NULL DEFAULT 0,
    last_tested_at      TEXT,
    updated_at          TEXT
);

-- ── api_budget ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS api_budget (
    date            TEXT PRIMARY KEY,
    calls_made      INTEGER NOT NULL DEFAULT 0,
    calls_budget    INTEGER NOT NULL DEFAULT 2000,
    last_429_at     TEXT,
    backoff_until   TEXT
);

-- ── sync_state ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS sync_state (
    source_key          TEXT PRIMARY KEY,
    last_sync_at        TEXT,
    last_etag           TEXT,
    last_cursor         TEXT,
    item_count          INTEGER DEFAULT 0,
    status              TEXT DEFAULT 'ok',
    consecutive_failures INT DEFAULT 0,
    last_error          TEXT,
    last_reached_at     TEXT,
    catalog_name        TEXT,
    catalog_type        TEXT,
    items_target        INTEGER DEFAULT 0,
    items_running       INTEGER DEFAULT 0,
    updated_at          TEXT
);

-- ── stream_candidates ───────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS stream_candidates (
    id                      TEXT PRIMARY KEY,
    imdb_id                 TEXT NOT NULL,
    season                  INTEGER,
    episode                 INTEGER,
    rank                    INTEGER NOT NULL,
    provider_key            TEXT NOT NULL DEFAULT 'unknown',
    stream_type             TEXT NOT NULL DEFAULT 'debrid',
    url                     TEXT NOT NULL,
    headers_json            TEXT,
    quality_tier            TEXT,
    file_name               TEXT,
    file_size               INTEGER,
    bitrate_kbps            INTEGER,
    is_cached               INTEGER NOT NULL DEFAULT 1,
    resolved_at             TEXT NOT NULL,
    expires_at              TEXT NOT NULL,
    status                  TEXT NOT NULL DEFAULT 'valid',
    info_hash               TEXT,
    file_idx                INTEGER,
    stream_key              TEXT,
    binge_group             TEXT,
    absolute_episode_number INTEGER,
    languages               TEXT,
    subtitles_json          TEXT,
    probe_json              TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_candidates_key ON stream_candidates(imdb_id, COALESCE(season,-1), COALESCE(episode,-1), rank);
CREATE INDEX IF NOT EXISTS idx_candidates_item ON stream_candidates(imdb_id, season, episode, rank);

-- ── discover_catalog ────────────────────────────────────────────────────────
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
    certification       TEXT,
    added_at            TEXT NOT NULL,
    updated_at          TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_discover_imdb ON discover_catalog(imdb_id);
CREATE INDEX IF NOT EXISTS idx_discover_source ON discover_catalog(catalog_source);
CREATE INDEX IF NOT EXISTS idx_discover_in_library ON discover_catalog(is_in_user_library);
CREATE VIRTUAL TABLE IF NOT EXISTS discover_catalog_fts USING fts5(title, content=discover_catalog, content_rowid=rowid);
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_insert AFTER INSERT ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(rowid, title) VALUES (new.rowid, new.title);
END;
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_update AFTER UPDATE ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(discover_catalog_fts, rowid, title) VALUES('delete', old.rowid, old.title);
  INSERT INTO discover_catalog_fts(rowid, title) VALUES (new.rowid, new.title);
END;
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_delete AFTER DELETE ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(discover_catalog_fts, rowid, title) VALUES('delete', old.rowid, old.title);
END;

-- ── collection_membership ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS collection_membership (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    collection_name     TEXT NOT NULL,
    emby_item_id        TEXT NOT NULL,
    source              TEXT NOT NULL,
    last_seen           TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(collection_name, emby_item_id)
);

-- ── plugin_metadata ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plugin_metadata (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- ── home_section_tracking ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS home_section_tracking (
    id              TEXT PRIMARY KEY,
    user_id         TEXT NOT NULL,
    rail_type       TEXT NOT NULL,
    emby_section_id TEXT,
    section_marker  TEXT NOT NULL,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(user_id, rail_type)
);

-- ── version_slots ───────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS version_slots (
    slot_key        TEXT PRIMARY KEY,
    label           TEXT NOT NULL,
    resolution      TEXT NOT NULL,
    video_codecs    TEXT NOT NULL DEFAULT 'any',
    hdr_classes     TEXT NOT NULL DEFAULT '',
    audio_preferences TEXT NOT NULL,
    enabled         INTEGER NOT NULL DEFAULT 0,
    is_default      INTEGER NOT NULL DEFAULT 0,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ── ingestion_state ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS ingestion_state (
    source_id      TEXT PRIMARY KEY,
    last_poll_at   TEXT,
    last_found_at  TEXT,
    watermark      TEXT
);

-- ── refresh_run_log ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS refresh_run_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_at          TEXT NOT NULL DEFAULT (datetime('now')),
    worker          TEXT NOT NULL,
    step            TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'started',
    items_affected  INTEGER DEFAULT 0,
    notes           TEXT
);

-- ── media_items ─────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS media_items (
    id                  TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    primary_id_type     TEXT NOT NULL,
    primary_id          TEXT NOT NULL,
    media_type          TEXT NOT NULL CHECK (media_type IN ('movie','series')),
    title               TEXT NOT NULL,
    year                INTEGER,
    status              TEXT NOT NULL CHECK (status IN ('known','resolved','hydrated','created','indexed','active','failed','deleted')),
    failure_reason      TEXT CHECK (failure_reason IN ('none','no_streams_found','metadata_fetch_failed','file_write_error','emby_index_timeout','digital_release_gate','blocked')),
    saved               INTEGER NOT NULL DEFAULT 0,
    saved_at            TEXT,
    blocked             INTEGER NOT NULL DEFAULT 0,
    blocked_at          TEXT,
    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at          TEXT NOT NULL DEFAULT (datetime('now')),
    grace_started_at    TEXT,
    superseded          INTEGER NOT NULL DEFAULT 0,
    superseded_conflict INTEGER NOT NULL DEFAULT 0,
    superseded_at       TEXT,
    emby_item_id        TEXT,
    emby_indexed_at     TEXT,
    strm_path           TEXT,
    nfo_path            TEXT,
    watch_progress_pct  INTEGER NOT NULL DEFAULT 0,
    favorited           INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_media_items_primary ON media_items(primary_id_type, primary_id);
CREATE INDEX IF NOT EXISTS idx_media_items_status ON media_items(status);
CREATE INDEX IF NOT EXISTS idx_media_items_saved ON media_items(saved);
CREATE INDEX IF NOT EXISTS idx_media_items_blocked ON media_items(blocked);
CREATE INDEX IF NOT EXISTS idx_media_items_emby_id ON media_items(emby_item_id) WHERE emby_item_id IS NOT NULL;

-- ── media_item_ids ──────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS media_item_ids (
    id          TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id TEXT NOT NULL,
    id_type     TEXT NOT NULL CHECK (id_type IN ('tmdb','imdb','tvdb','anilist','anidb','kitsu')),
    id_value    TEXT NOT NULL,
    is_primary  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_media_item_ids_item ON media_item_ids(media_item_id);
CREATE INDEX IF NOT EXISTS idx_media_item_ids_type_value ON media_item_ids(id_type, id_value);
CREATE UNIQUE INDEX IF NOT EXISTS idx_media_item_ids_unique ON media_item_ids(media_item_id, id_type, id_value);

-- ── source_memberships ──────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS source_memberships (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    source_id       TEXT NOT NULL,
    media_item_id   TEXT NOT NULL,
    user_catalog_id TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (source_id, media_item_id)
);
CREATE INDEX IF NOT EXISTS idx_source_memberships_source ON source_memberships(source_id);
CREATE INDEX IF NOT EXISTS idx_source_memberships_item ON source_memberships(media_item_id);
CREATE INDEX IF NOT EXISTS idx_source_memberships_user_catalog ON source_memberships(user_catalog_id);

-- ── sources ─────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS sources (
    id                  TEXT PRIMARY KEY,
    name                TEXT NOT NULL,
    url                 TEXT,
    type                TEXT NOT NULL CHECK(type IN ('BuiltIn', 'Aio', 'UserRss')),
    enabled             INTEGER NOT NULL DEFAULT 1,
    show_as_collection  INTEGER NOT NULL DEFAULT 0,
    max_items           INTEGER NOT NULL DEFAULT 100,
    sync_interval_hours INTEGER NOT NULL DEFAULT 6,
    last_synced_at      TEXT,
    emby_collection_id  TEXT,
    collection_name     TEXT,
    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at          TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_sources_enabled ON sources(enabled);

-- ── collections ─────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS collections (
    id                  TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    source_id           TEXT NOT NULL,
    name                TEXT NOT NULL,
    emby_collection_id  TEXT,
    collection_name     TEXT,
    last_synced_at      TEXT,
    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at          TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_collections_source_id ON collections(source_id);

-- ── candidates ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS candidates (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    rank            INTEGER NOT NULL,
    service         TEXT,
    stream_type     TEXT NOT NULL DEFAULT 'debrid',
    resolution      TEXT,
    video_codec     TEXT,
    hdr_class       TEXT,
    audio_codec     TEXT,
    audio_channels  TEXT,
    file_name       TEXT,
    file_size       INTEGER,
    bitrate_kbps    INTEGER,
    languages       TEXT,
    source_type     TEXT,
    is_cached       INTEGER NOT NULL DEFAULT 0,
    fingerprint     TEXT NOT NULL,
    binge_group     TEXT,
    info_hash       TEXT,
    file_idx        INTEGER,
    confidence_score REAL NOT NULL DEFAULT 0.0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at      TEXT NOT NULL,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_candidates_item_slot ON candidates(media_item_id, slot_key, rank);
CREATE INDEX IF NOT EXISTS idx_candidates_fingerprint ON candidates(fingerprint);
CREATE INDEX IF NOT EXISTS idx_candidates_expires ON candidates(expires_at);
CREATE UNIQUE INDEX IF NOT EXISTS idx_candidates_unique ON candidates(media_item_id, slot_key, fingerprint);

-- ── version_snapshots ───────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS version_snapshots (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    candidate_id    TEXT NOT NULL,
    snapshot_at     TEXT NOT NULL DEFAULT (datetime('now')),
    playback_url    TEXT,
    playback_url_cached_at TEXT,
    playback_url_expires_at TEXT,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (media_item_id, slot_key)
);
CREATE INDEX IF NOT EXISTS idx_snapshots_item ON version_snapshots(media_item_id);

-- ── materialized_versions ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS materialized_versions (
    id              TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    media_item_id   TEXT NOT NULL,
    slot_key        TEXT NOT NULL,
    strm_path       TEXT NOT NULL,
    nfo_path        TEXT NOT NULL,
    strm_url_hash   TEXT NOT NULL,
    is_base         INTEGER NOT NULL DEFAULT 0,
    materialized_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now')),
    strm_token_expires_at INTEGER,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (media_item_id, slot_key)
);
CREATE INDEX IF NOT EXISTS idx_materialized_item ON materialized_versions(media_item_id);
CREATE INDEX IF NOT EXISTS idx_materialized_slot ON materialized_versions(slot_key);
CREATE INDEX IF NOT EXISTS idx_materialized_base ON materialized_versions(is_base) WHERE is_base = 1;

-- ── item_pipeline_log ──────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS item_pipeline_log (
    primary_id       TEXT NOT NULL,
    primary_id_type  TEXT NOT NULL,
    media_type       TEXT NOT NULL,
    phase            TEXT NOT NULL,
    trigger          TEXT NOT NULL,
    success          INTEGER NOT NULL,
    details          TEXT,
    timestamp        TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_pipeline_timestamp ON item_pipeline_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_pipeline_primary_id ON item_pipeline_log(primary_id);

-- ── stream_resolution_log ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS stream_resolution_log (
    primary_id       TEXT NOT NULL,
    primary_id_type  TEXT NOT NULL,
    media_type       TEXT NOT NULL,
    media_id         TEXT NOT NULL,
    stream_count     INTEGER NOT NULL,
    selected_stream  TEXT,
    duration_ms      INTEGER NOT NULL,
    timestamp        TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_resolution_timestamp ON stream_resolution_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_resolution_primary_id ON stream_resolution_log(primary_id);

-- ── user_catalogs ───────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS user_catalogs (
    id                 TEXT PRIMARY KEY,
    owner_user_id      TEXT NOT NULL,
    source_type        TEXT NOT NULL DEFAULT 'external_list',
    service            TEXT NOT NULL,
    list_url           TEXT NOT NULL,
    display_name       TEXT NOT NULL,
    active             INTEGER NOT NULL DEFAULT 1,
    last_synced_at     TEXT,
    last_sync_status   TEXT,
    created_at         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_user_catalogs_owner ON user_catalogs(owner_user_id);
CREATE INDEX IF NOT EXISTS idx_user_catalogs_active ON user_catalogs(active) WHERE active = 1;

-- ── user_item_saves ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS user_item_saves (
    id            TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    user_id       TEXT NOT NULL,
    media_item_id TEXT NOT NULL,
    save_reason   TEXT CHECK (save_reason IN ('explicit','watched_episode','admin_override')),
    saved_season  INTEGER,
    saved_at      TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (user_id, media_item_id)
);
CREATE INDEX IF NOT EXISTS idx_user_saves_user ON user_item_saves(user_id);
CREATE INDEX IF NOT EXISTS idx_user_saves_item ON user_item_saves(media_item_id);

-- ── stream_cache ────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS stream_cache (
    media_id      TEXT PRIMARY KEY NOT NULL,
    url           TEXT NOT NULL,
    url_secondary TEXT,
    created_at    TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_cache_expires ON stream_cache(expires_at);

-- ── cached_streams ──────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS cached_streams (
    tmdb_key     TEXT PRIMARY KEY,
    imdb_id      TEXT NOT NULL,
    media_type   TEXT NOT NULL,
    season       INTEGER,
    episode      INTEGER,
    item_id      TEXT,
    variants     TEXT NOT NULL,
    cached_at    TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at   TEXT NOT NULL,
    status       TEXT NOT NULL DEFAULT 'valid'
);
CREATE INDEX IF NOT EXISTS idx_cs_imdb ON cached_streams(imdb_id, season, episode);
CREATE INDEX IF NOT EXISTS idx_cs_item ON cached_streams(item_id) WHERE item_id IS NOT NULL;
";
            // Split on semicolons but preserve BEGIN...END blocks in triggers.
            var statements = SplitDdl(ddl);
            foreach (var statement in statements)
            {
                var sql = statement.Trim();
                if (!string.IsNullOrEmpty(sql))
                    conn.Execute(sql);
            }

            // Seed version_slots
            SeedVersionSlots(conn);

            _logger.LogInformation("[InfiniteDrive] Schema created successfully");
        }

        private void SeedVersionSlots(IDatabaseConnection conn)
        {
            var slots = new (string Key, string Label, string Resolution, string VideoCodecs, string HdrClasses, string AudioPreferences, int Enabled, int IsDefault, int SortOrder)[]
            {
                ("hd_broad",       "HD · Broad",       "1080p", "h264", "",    "dd_plus_51,dd_51,aac_stereo",        1, 1, 0),
                ("best_available", "Best Available",    "highest", "any", "any", "atmos,dd_plus_71,dd_plus_51,dd_51", 0, 0, 1),
                ("4k_dv",          "4K · Dolby Vision", "2160p", "hevc,av1", "dv",   "atmos,dd_plus_71,dd_plus_51",     0, 0, 2),
                ("4k_hdr",         "4K · HDR",          "2160p", "hevc,av1", "hdr10","atmos,dd_plus_51,dd_51",          0, 0, 3),
                ("4k_sdr",         "4K · SDR",          "2160p", "hevc,av1", "",    "dd_plus_51,dd_51,aac",             0, 0, 4),
                ("hd_efficient",   "HD · Efficient",    "1080p", "hevc",     "",    "dd_plus_51,aac_stereo",            0, 0, 5),
                ("compact",        "Compact",           "720p",  "h264",     "",    "aac,dd",                           0, 0, 6),
            };

            const string sql = @"
                INSERT OR IGNORE INTO version_slots
                    (slot_key, label, resolution, video_codecs, hdr_classes, audio_preferences, enabled, is_default, sort_order)
                VALUES
                    (@slot_key, @label, @resolution, @video_codecs, @hdr_classes, @audio_preferences, @enabled, @is_default, @sort_order)";

            foreach (var slot in slots)
            {
                using var stmt = conn.PrepareStatement(sql);
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
                _logger.LogWarning(ex, "[InfiniteDrive] integrity_check threw — treating as corrupt");
                return false;
            }
        }

        // ── Private: SQLitePCL.pretty helpers ───────────────────────────────────

        private IDatabaseConnection OpenConnection()
        {
            var conn = SQLite3.Open(_dbPath, ConnectionFlags.ReadWrite | ConnectionFlags.Create, null, true);
            // Set busy_timeout on every connection so SQLite waits for locks instead of
            // immediately failing. This prevents "database is locked" errors when
            // SQLitePCL.pretty runs PRAGMA optimize on dispose while a write is active.
            conn.Execute("PRAGMA busy_timeout=30000;");
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

        internal Task<List<T>> QueryListAsync<T>(
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

        /// <summary>
        /// Split DDL into individual statements, respecting BEGIN...END blocks
        /// in triggers so that internal semicolons are not treated as statement boundaries.
        /// </summary>
        private static List<string> SplitDdl(string ddl)
        {
            var result = new List<string>();
            var current = new System.Text.StringBuilder();
            bool insideTrigger = false;

            foreach (var line in ddl.Split('\n'))
            {
                var trimmed = line.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("--"))
                {
                    current.AppendLine(line);
                    continue;
                }

                // Detect trigger body entry: line contains CREATE TRIGGER ... BEGIN
                if (!insideTrigger &&
                    trimmed.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase) &&
                    trimmed.Contains("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    insideTrigger = true;
                }

                // Detect trigger body exit: line is exactly END or END;
                if (insideTrigger &&
                    (trimmed.Equals("END", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("END;", StringComparison.OrdinalIgnoreCase)))
                {
                    insideTrigger = false;
                    current.AppendLine(line);
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.AppendLine(line);

                // Statement boundary: semicolon at end of line AND not inside trigger
                if (!insideTrigger && trimmed.EndsWith(";"))
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }

            // Remaining content
            if (current.Length > 0)
            {
                var remainder = current.ToString().Trim();
                if (!string.IsNullOrEmpty(remainder))
                    result.Add(remainder);
            }

            return result;
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

        private static void BindInt(IStatement stmt, string name, int value)
            => stmt.BindParameters[name].Bind(value);

        private static void BindNullableLong(IStatement stmt, string name, long? value)
        {
            if (value == null) stmt.BindParameters[name].BindNull();
            else               stmt.BindParameters[name].Bind(value.Value);
        }

        private static long GetLastInsertRowId(IDatabaseConnection conn)
        {
            using var stmt = conn.PrepareStatement("SELECT last_insert_rowid();");
            foreach (var row in stmt.AsRows())
                return row.GetInt64(0);
            return -1;
        }

        // ── Discover Catalog ────────────────────────────────────────────────────

        public async Task UpsertDiscoverCatalogEntryAsync(DiscoverCatalogEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
INSERT OR REPLACE INTO discover_catalog
    (id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
     genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at, updated_at)
VALUES
    (@id, @imdb_id, @title, @year, @media_type, @poster_url, @backdrop_url, @overview,
     @genres, @imdb_rating, @certification, @catalog_source, @in_library, @added_at, datetime('now'))";
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
                BindNullableText(cmd, "@certification", entry.Certification);
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
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE NOT EXISTS (
    SELECT 1 FROM catalog_items ci WHERE ci.imdb_id = discover_catalog.imdb_id AND ci.blocked_at IS NOT NULL
)";

            if (mediaType != null)
            {
                sql += " AND media_type = @media_type";
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

        public async Task<List<DiscoverCatalogEntry>> GetDiscoverCatalogBySourceAsync(
            string catalogSource, string? genre = null, int limit = 42, int offset = 0)
        {
            var sql = @"SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE catalog_source = @source
  AND is_in_user_library = 0";
            if (genre != null) sql += " AND genres LIKE '%' || @genre || '%'";
            sql += " ORDER BY added_at DESC LIMIT @limit OFFSET @offset";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@source", catalogSource);
                if (genre != null) BindText(cmd, "@genre", genre);
                cmd.BindParameters["@limit"].Bind(limit);
                cmd.BindParameters["@offset"].Bind(offset);
            }, ReadDiscoverCatalogEntry);
        }

        public async Task<List<string>> GetDiscoverMediaTypesAsync()
        {
            const string sql = "SELECT DISTINCT media_type FROM discover_catalog WHERE is_in_user_library = 0 ORDER BY media_type";
            return await QueryListAsync(sql, _ => { }, row => row.GetString(0));
        }

        public async Task<List<string>> GetDiscoverCatalogSourcesAsync(string mediaType)
        {
            const string sql = "SELECT DISTINCT catalog_source FROM discover_catalog WHERE media_type = @media_type AND is_in_user_library = 0 ORDER BY catalog_source";
            return await QueryListAsync(sql, cmd => BindText(cmd, "@media_type", mediaType), row => row.GetString(0));
        }

        public Task<int> GetDiscoverCatalogCountAsync()
        {
            return GetDiscoverCatalogCountAsync(null);
        }

        public Task<int> GetDiscoverCatalogCountAsync(string? mediaType)
        {
            var sql = @"SELECT COUNT(*) FROM discover_catalog
WHERE NOT EXISTS (
    SELECT 1 FROM catalog_items ci WHERE ci.imdb_id = discover_catalog.imdb_id AND ci.blocked_at IS NOT NULL
)";
            if (mediaType != null)
            {
                sql += " AND media_type = @media_type";
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
       dc.genres, dc.imdb_rating, dc.certification, dc.catalog_source, dc.is_in_user_library, dc.added_at
FROM discover_catalog dc
JOIN discover_catalog_fts fts ON dc.rowid = fts.rowid
WHERE discover_catalog_fts MATCH @query
  AND NOT EXISTS (
    SELECT 1 FROM catalog_items ci WHERE ci.imdb_id = dc.imdb_id AND ci.blocked_at IS NOT NULL
  )";
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
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE imdb_id = @imdb_id
  AND NOT EXISTS (
    SELECT 1 FROM catalog_items ci WHERE ci.imdb_id = @imdb_id AND ci.blocked_at IS NOT NULL
  )
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

        /// <summary>
        /// Updates the certification (MPAA/TV rating) for a specific discover catalog item.
        /// Sprint 209: Used when fetching certifications from TMDB.
        /// </summary>
        public async Task UpdateDiscoverCertificationAsync(string imdbId, string? certification, CancellationToken cancellationToken = default)
        {
            const string sql = @"
UPDATE discover_catalog
SET certification = @certification, updated_at = datetime('now')
WHERE imdb_id = @imdb_id";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindNullableText(cmd, "@certification", certification);
                BindText(cmd, "@imdb_id", imdbId);
            }, cancellationToken);
        }

        /// <summary>
        /// Gets discover catalog items that need certification fetched (certification IS NULL).
        /// Returns list of (imdb_id, tmdb_id) tuples.
        /// Sprint 209: Used to batch-fetch certifications from TMDB.
        /// </summary>
        public async Task<List<(string ImdbId, string? TmdbId)>> GetDiscoverCatalogNeedingCertificationAsync(int limit, CancellationToken cancellationToken = default)
        {
            const string sql = @"
SELECT dc.imdb_id, ci.tmdb_id
FROM discover_catalog dc
LEFT JOIN catalog_items ci ON dc.imdb_id = ci.imdb_id
WHERE dc.certification IS NULL
LIMIT @limit";

            var results = new List<(string, string?)>();
            using var conn = OpenConnection();
            using var cmd = conn.PrepareStatement(sql);
            cmd.BindParameters["@limit"].Bind(limit);

            foreach (var row in cmd.AsRows())
            {
                var imdbId = row.GetString(0);
                var tmdbId = row.IsDBNull(1) ? null : row.GetString(1);
                results.Add((imdbId, tmdbId));
            }

            return await Task.FromResult(results);
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
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
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
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
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
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
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
                Tags = new List<string> { "InfiniteDrive" },
                ProviderIds = new Dictionary<string, string>
                {
                    ["imdb"] = entry.ImdbId
                }
            };
        }

        // ── Private: column maps + row mappers ──────────────────────────────────

        /// <summary>
        /// Populates column-name-to-index maps for all tables via PRAGMA table_info.
        /// Called once during Initialise(). SELECT * returns columns in this order.
        /// </summary>
        private static void BuildColumnMaps(IDatabaseConnection conn)
        {
            foreach (var table in new[]
            {
                Tables.CatalogItems, Tables.ResolutionCache, Tables.StreamCandidates,
                Tables.PlaybackLog, Tables.ClientCompat, Tables.SyncState,
                "discover_catalog", "materialized_versions", "user_catalogs",
                "media_items", "sources", "collection_membership"
            })
            {
                try
                {
                    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    using var stmt = conn.PrepareStatement($"PRAGMA table_info('{table}');");
                    foreach (var row in stmt.AsRows())
                    {
                        // PRAGMA table_info columns: 0=cid, 1=name, 2=type, 3=notnull, 4=dflt, 5=pk
                        var name = row.GetString(1);
                        var cid = row.GetInt(0);
                        map[name] = cid;
                    }
                    if (map.Count > 0)
                        _columnMaps[table] = map;
                }
                catch
                {
                    // Table may not exist yet — skip
                }
            }
        }

        private static Dictionary<string, int> ColMap(string table)
            => _columnMaps.TryGetValue(table, out var m) ? m
               : throw new InvalidOperationException($"No column map for table '{table}'. Call BuildColumnMaps first.");

        private static string? GetStr(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetString(i) : null;

        private static string GetReqStr(Dictionary<string, int> map, IResultSet r, string col)
            => r.GetString(map[col]);

        private static int? GetInt(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt(i) : null;

        private static int GetReqInt(Dictionary<string, int> map, IResultSet r, string col)
            => r.GetInt(map[col]);

        private static long? GetLong(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt64(i) : null;

        private static bool? GetBool(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt(i) == 1 : null;

        private static double? GetDouble(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetDouble(i) : null;

        private static CatalogItem ReadCatalogItem(IResultSet r)
        {
            var m = ColMap(Tables.CatalogItems);
            return new CatalogItem
            {
                Id                = GetReqStr(m, r, "id"),
                ImdbId            = GetReqStr(m, r, "imdb_id"),
                TmdbId            = GetStr(m, r, "tmdb_id"),
                Title             = GetReqStr(m, r, "title"),
                Year              = GetInt(m, r, "year"),
                MediaType         = GetReqStr(m, r, "media_type"),
                Source            = GetReqStr(m, r, "source"),
                SourceListId      = GetStr(m, r, "source_list_id"),
                SeasonsJson       = GetStr(m, r, "seasons_json"),
                StrmPath          = GetStr(m, r, "strm_path"),
                AddedAt           = GetReqStr(m, r, "added_at"),
                UpdatedAt         = GetReqStr(m, r, "updated_at"),
                RemovedAt         = GetStr(m, r, "removed_at"),
                LocalPath         = GetStr(m, r, "local_path"),
                LocalSource       = GetStr(m, r, "local_source"),
                ResurrectionCount = GetInt(m, r, "resurrection_count") ?? 0,
                ItemState         = GetInt(m, r, "item_state") is int s ? (ItemState)s : ItemState.Catalogued,
                PinSource         = GetStr(m, r, "pin_source"),
                PinnedAt          = GetStr(m, r, "pinned_at"),
                UniqueIdsJson     = GetStr(m, r, "unique_ids_json"),
                NfoStatus         = GetStr(m, r, "nfo_status"),
                RetryCount        = GetInt(m, r, "retry_count") ?? 0,
                NextRetryAt       = GetLong(m, r, "next_retry_at"),
                StrmTokenExpiresAt = GetLong(m, r, "strm_token_expires_at"),
                BlockedAt         = GetStr(m, r, "blocked_at"),
                BlockedBy         = GetStr(m, r, "blocked_by"),
                FirstAddedByUserId = GetStr(m, r, "first_added_by_user_id"),
                TvdbId            = GetStr(m, r, "tvdb_id"),
                RawMetaJson       = GetStr(m, r, "raw_meta_json"),
                CatalogType       = GetStr(m, r, "catalog_type"),
                VideosJson        = GetStr(m, r, "videos_json"),
                EpisodesExpanded  = GetBool(m, r, "episodes_expanded"),
                LastVerifiedAt    = GetLong(m, r, "last_verified_at"),
            };
        }

        // These mappers use queries with explicit column lists — positional is safe.
        // Name-based lookup is only used with SELECT * queries (ReadCatalogItem).

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
            Languages     = r.IsDBNull(21) ? null : r.GetString(21),
            SubtitlesJson = r.IsDBNull(22) ? null : r.GetString(22),
            ProbeJson     = r.IsDBNull(23) ? null : r.GetString(23),
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

        // discover_catalog queries use explicit column lists — positional mapping is safe
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
            Certification   = r.IsDBNull(10) ? null : r.GetString(10),
            CatalogSource   = r.GetString(11),
            IsInUserLibrary = r.GetInt(12) != 0,
            AddedAt         = r.GetString(13),
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

        // ── New Schema Operations (media_items, sources, source_memberships, etc.) ─────────────────

        /// <summary>
        /// Gets a media item by ID.
        /// </summary>
        public async Task<MediaItem?> GetMediaItemAsync(string itemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items
                WHERE id = @ItemId
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@ItemId", itemId),
                ReadMediaItem);
        }

        /// <summary>
        /// Finds a media item by provider ID (TMDB, IMDB, TVDB, AniList, AniDB, Kitsu).
        /// </summary>
        public async Task<MediaItem?> FindMediaItemByProviderIdAsync(string idType, string idValue, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                INNER JOIN media_item_ids mii ON mi.id = mii.media_item_id
                WHERE mii.id_type = @IdType
                  AND mii.id_value = @IdValue
                  AND mii.is_primary = 1
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd =>
                {
                    BindText(cmd, "@IdType", idType.ToLowerInvariant());
                    BindText(cmd, "@IdValue", idValue);
                },
                ReadMediaItem);
        }

        /// <summary>
        /// Finds media items by source membership.
        /// </summary>
        public async Task<List<MediaItem>> FindMediaItemsBySourceAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                INNER JOIN source_memberships sm ON mi.id = sm.media_item_id
                WHERE sm.source_id = @SourceId;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@SourceId", sourceId),
                ReadMediaItem);
        }

        /// <summary>
        /// Gets media items by saved state.
        /// </summary>
        public async Task<List<MediaItem>> GetItemsBySavedAsync(bool saved, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                WHERE mi.saved = @Saved
                ORDER BY mi.title;";

            return await QueryListAsync(sql,
                cmd => BindInt(cmd, "@Saved", saved ? 1 : 0),
                ReadMediaItem);
        }

        // ── Per-user saves (user_item_saves) ────────────────────────────────

        /// <summary>
        /// Gets saved media items for a specific user.
        /// </summary>
        public async Task<List<MediaItem>> GetSavedItemsByUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM user_item_saves us
                JOIN media_items mi ON us.media_item_id = mi.id
                WHERE us.user_id = @UserId
                ORDER BY us.saved_at DESC;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@UserId", userId),
                ReadMediaItem);
        }

        /// <summary>
        /// Inserts a per-user save (idempotent via INSERT OR IGNORE).
        /// </summary>
        public async Task UpsertUserSaveAsync(string userId, string mediaItemId, string? saveReason, int? savedSeason, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT OR IGNORE INTO user_item_saves (user_id, media_item_id, save_reason, saved_season)
                VALUES (@UserId, @MediaItemId, @SaveReason, @SavedSeason);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@UserId", userId);
                BindText(cmd, "@MediaItemId", mediaItemId);
                BindNullableText(cmd, "@SaveReason", saveReason);
                BindNullableInt(cmd, "@SavedSeason", savedSeason);
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes a per-user save.
        /// </summary>
        public async Task DeleteUserSaveAsync(string userId, string mediaItemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM user_item_saves
                WHERE user_id = @UserId AND media_item_id = @MediaItemId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@UserId", userId);
                BindText(cmd, "@MediaItemId", mediaItemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Checks if a user has saved a specific item.
        /// </summary>
        public Task<bool> HasUserSaveAsync(string userId, string mediaItemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT EXISTS(
                    SELECT 1 FROM user_item_saves
                    WHERE user_id = @UserId AND media_item_id = @MediaItemId
                );";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@UserId", userId);
            BindText(stmt, "@MediaItemId", mediaItemId);
            return Task.FromResult(stmt.AsRows().Any());
        }

        /// <summary>
        /// Re-syncs the denormalized saved flag on media_items from user_item_saves.
        /// Sets saved=1 if any user save exists, saved=0 otherwise. Updates saved_at.
        /// </summary>
        public async Task SyncGlobalSavedFlagAsync(string mediaItemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET saved = (SELECT CASE WHEN EXISTS(
                    SELECT 1 FROM user_item_saves WHERE media_item_id = @Id
                ) THEN 1 ELSE 0 END),
                saved_at = (SELECT MAX(saved_at) FROM user_item_saves WHERE media_item_id = @Id)
                WHERE id = @Id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", mediaItemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes all user save records for a given media item.
        /// Used by admin block action to clear all per-user saves.
        /// </summary>
        public async Task DeleteAllUserSavesForItemAsync(string mediaItemId, CancellationToken ct = default)
        {
            const string sql = @"
                DELETE FROM user_item_saves
                WHERE media_item_id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", mediaItemId);
            }, ct);
        }

        /// <summary>
        /// Gets a media item by its primary ID (e.g. IMDB ID).
        /// Used by admin block action to resolve IMDB ID → media_item.
        /// </summary>
        public async Task<MediaItem?> GetMediaItemByPrimaryIdAsync(string primaryId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items
                WHERE primary_id = @PrimaryId
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@PrimaryId", primaryId),
                ReadMediaItem);
        }

        /// <summary>
        /// Gets a media item by its internal UUID (media_items.id).
        /// Used by AdminService when blocking/searching by internal ID.
        /// </summary>
        public async Task<MediaItem?> GetMediaItemByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items
                WHERE id = @Id
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@Id", id),
                ReadMediaItem);
        }

        /// <summary>
        /// Searches media items by title, returning non-blocked items only.
        /// Used by AdminService for the block search UI.
        /// </summary>
        public async Task<List<MediaItem>> SearchMediaItemsByTitleAsync(string query, int limit, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items
                WHERE title LIKE @Query
                  AND blocked = 0
                ORDER BY title
                LIMIT @Limit;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@Query", $"%{query}%");
                BindInt(cmd, "@Limit", limit);
            }, ReadMediaItem);
        }

        /// <summary>
        /// Blocks a catalog item by its IMDB ID.
        /// Sets blocked_at and blocked_by, clearing nfo_status.
        /// </summary>
        public async Task BlockCatalogItemByImdbIdAsync(string imdbId, string blockedBy, CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET blocked_at  = datetime('now'),
                    blocked_by  = @blocked_by,
                    updated_at  = datetime('now')
                WHERE imdb_id = @imdb_id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindText(cmd, "@blocked_by", blockedBy);
            }, ct);

            _logger.LogInformation("[DatabaseManager] Blocked catalog item {ImdbId}", imdbId);
        }

        /// <summary>
        /// Gets orphaned user saves where the media_item no longer exists.
        /// For Marvin cleanup.
        /// </summary>
        public Task<List<(string SaveId, string UserId, string MediaItemId)>> GetOrphanedUserSavesAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT us.id, us.user_id, us.media_item_id
                FROM user_item_saves us
                LEFT JOIN media_items mi ON us.media_item_id = mi.id
                WHERE mi.id IS NULL;";

            var results = new List<(string, string, string)>();
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                results.Add((row.GetString(0), row.GetString(1), row.GetString(2)));
            }
            return Task.FromResult(results);
        }

        /// <summary>
        /// Deletes a user save by row ID. For Marvin cleanup.
        /// </summary>
        public async Task DeleteUserSaveByIdAsync(string saveId, CancellationToken cancellationToken = default)
        {
            const string sql = "DELETE FROM user_item_saves WHERE id = @Id;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", saveId);
            }, cancellationToken);
        }

        /// <summary>
        /// Gets media items with active grace period.
        /// </summary>
        public async Task<List<MediaItem>> GetItemsByGraceStartedAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                WHERE mi.grace_started_at IS NOT NULL
                ORDER BY mi.grace_started_at;";

            return await QueryListAsync(sql, null, ReadMediaItem);
        }

        /// <summary>
        /// Gets media items with pagination and filtering (for API).
        /// </summary>
        public async Task<List<MediaItem>> GetItemsAsync(
            Models.ItemStatus? status,
            string orderBy,
            string orderDirection,
            int limit,
            int offset,
            CancellationToken cancellationToken = default)
        {
            var whereClause = status.HasValue ? "WHERE mi.status = @Status" : "";
            var orderClause = $"ORDER BY mi.{orderBy} {orderDirection.ToUpperInvariant()}";

            const string sqlTemplate = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                {0}
                {1}
                LIMIT @Limit OFFSET @Offset;";

            var sql = string.Format(sqlTemplate, whereClause, orderClause);

            return await QueryListAsync(sql, cmd =>
            {
                if (status.HasValue)
                    BindText(cmd, "@Status", status.Value.ToString());
                BindInt(cmd, "@Limit", limit);
                BindInt(cmd, "@Offset", offset);
            }, ReadMediaItem);
        }

        /// <summary>
        /// Gets total count of media items (for API pagination).
        /// </summary>
        public Task<int> GetItemCountAsync(Models.ItemStatus? status, CancellationToken cancellationToken = default)
        {
            var whereClause = status.HasValue ? "WHERE status = @Status" : "";
            var sql = $"SELECT COUNT(*) FROM media_items {whereClause};";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (status.HasValue)
                BindText(stmt, "@Status", status.Value.ToString());

            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Searches media items by query string (for API).
        /// </summary>
        public async Task<List<MediaItem>> SearchItemsAsync(string query, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                WHERE mi.title LIKE @Query
                   OR mi.primary_id LIKE @Query
                ORDER BY mi.title
                LIMIT 100;";

            var searchPattern = $"%{query}%";
            return await QueryListAsync(sql, cmd => BindText(cmd, "@Query", searchPattern), ReadMediaItem);
        }

        /// <summary>
        /// Inserts or updates a media item.
        /// </summary>
        public async Task UpsertMediaItemAsync(MediaItem item, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO media_items
                    (id, primary_id_type, primary_id, media_type, title, year,
                     status, failure_reason, saved, saved_at,
                     blocked, blocked_at, created_at, updated_at, grace_started_at,
                     superseded, superseded_conflict, superseded_at,
                     emby_item_id, emby_indexed_at, strm_path, nfo_path,
                     watch_progress_pct, favorited)
                VALUES
                    (@id, @primary_id_type, @primary_id, @media_type, @title, @year,
                     @status, @failure_reason, @saved, @saved_at,
                     @blocked, @blocked_at, @created_at, @updated_at, @grace_started_at,
                     @superseded, @superseded_conflict, @superseded_at,
                     @emby_item_id, @emby_indexed_at, @strm_path, @nfo_path,
                     @watch_progress_pct, @favorited)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    failure_reason = excluded.failure_reason,
                    saved = excluded.saved,
                    saved_at = excluded.saved_at,
                    blocked = excluded.blocked,
                    blocked_at = excluded.blocked_at,
                    updated_at = excluded.updated_at,
                    grace_started_at = excluded.grace_started_at,
                    superseded = excluded.superseded,
                    superseded_conflict = excluded.superseded_conflict,
                    superseded_at = excluded.superseded_at,
                    emby_item_id = excluded.emby_item_id,
                    emby_indexed_at = excluded.emby_indexed_at,
                    strm_path = excluded.strm_path,
                    nfo_path = excluded.nfo_path,
                    watch_progress_pct = excluded.watch_progress_pct,
                    favorited = excluded.favorited;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", item.Id);
                BindText(cmd, "@primary_id_type", item.PrimaryId.Type.ToLowerString());
                BindText(cmd, "@primary_id", item.PrimaryId.Value);
                BindText(cmd, "@media_type", item.MediaType);
                BindText(cmd, "@title", item.Title);
                BindNullableInt(cmd, "@year", item.Year);
                BindText(cmd, "@status", item.Status.ToString().ToLowerInvariant());
                BindNullableText(cmd, "@failure_reason", item.FailureReason != FailureReason.None ? item.FailureReason.ToString().ToLowerInvariant() : null);
                BindInt(cmd, "@saved", item.Saved ? 1 : 0);
                BindNullableText(cmd, "@saved_at", item.SavedAt?.ToString("o"));
                BindInt(cmd, "@blocked", item.Blocked ? 1 : 0);
                BindNullableText(cmd, "@blocked_at", item.BlockedAt?.ToString("o"));
                BindText(cmd, "@created_at", item.CreatedAt.ToString("o"));
                BindText(cmd, "@updated_at", item.UpdatedAt.ToString("o"));
                BindNullableText(cmd, "@grace_started_at", item.GraceStartedAt?.ToString("o"));
                BindInt(cmd, "@superseded", item.Superseded ? 1 : 0);
                BindInt(cmd, "@superseded_conflict", item.SupersededConflict ? 1 : 0);
                BindNullableText(cmd, "@superseded_at", item.SupersededAt?.ToString("o"));
                BindNullableText(cmd, "@emby_item_id", item.EmbyItemId);
                BindNullableText(cmd, "@emby_indexed_at", item.EmbyIndexedAt?.ToString("o"));
                BindNullableText(cmd, "@strm_path", item.StrmPath);
                BindNullableText(cmd, "@nfo_path", item.NfoPath);
                BindInt(cmd, "@watch_progress_pct", item.WatchProgressPct);
                BindInt(cmd, "@favorited", item.Favorited ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Checks if an item has any enabled source (Coalition rule).
        /// Uses single JOIN query per spec.
        /// </summary>
        public Task<bool> ItemHasEnabledSourceAsync(string itemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT 1 FROM source_memberships sm
                JOIN sources s ON sm.source_id = s.id
                WHERE sm.media_item_id = @ItemId
                  AND s.enabled = 1
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@ItemId", itemId);
            return Task.FromResult(stmt.AsRows().Any());
        }

        /// <summary>
        /// Updates a media item.
        /// </summary>
        public async Task UpdateMediaItemAsync(MediaItem item, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET primary_id_type = @primary_id_type,
                    primary_id = @primary_id,
                    media_type = @media_type,
                    title = @title,
                    year = @year,
                    status = @status,
                    failure_reason = @failure_reason,
                    saved = @saved,
                    saved_at = @saved_at,
                    blocked = @blocked,
                    blocked_at = @blocked_at,
                    grace_started_at = @grace_started_at,
                    superseded = @superseded,
                    superseded_conflict = @superseded_conflict,
                    superseded_at = @superseded_at,
                    emby_item_id = @emby_item_id,
                    emby_indexed_at = @emby_indexed_at,
                    strm_path = @strm_path,
                    nfo_path = @nfo_path,
                    watch_progress_pct = @watch_progress_pct,
                    favorited = @favorited,
                    updated_at = datetime('now')
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", item.Id);
                BindText(cmd, "@primary_id_type", item.PrimaryId.Type.ToLowerString());
                BindText(cmd, "@primary_id", item.PrimaryId.Value);
                BindText(cmd, "@media_type", item.MediaType);
                BindText(cmd, "@title", item.Title);
                BindNullableInt(cmd, "@year", item.Year);
                BindText(cmd, "@status", item.Status.ToString().ToLowerInvariant());
                BindNullableText(cmd, "@failure_reason", item.FailureReason != FailureReason.None ? item.FailureReason.ToString().ToLowerInvariant() : null);
                BindInt(cmd, "@saved", item.Saved ? 1 : 0);
                BindNullableText(cmd, "@saved_at", item.SavedAt?.ToString("o"));
                BindInt(cmd, "@blocked", item.Blocked ? 1 : 0);
                BindNullableText(cmd, "@blocked_at", item.BlockedAt?.ToString("o"));
                BindNullableText(cmd, "@grace_started_at", item.GraceStartedAt?.ToString("o"));
                BindInt(cmd, "@superseded", item.Superseded ? 1 : 0);
                BindInt(cmd, "@superseded_conflict", item.SupersededConflict ? 1 : 0);
                BindNullableText(cmd, "@superseded_at", item.SupersededAt?.ToString("o"));
                BindNullableText(cmd, "@emby_item_id", item.EmbyItemId);
                BindNullableText(cmd, "@emby_indexed_at", item.EmbyIndexedAt?.ToString("o"));
                BindNullableText(cmd, "@strm_path", item.StrmPath);
                BindNullableText(cmd, "@nfo_path", item.NfoPath);
                BindInt(cmd, "@watch_progress_pct", item.WatchProgressPct);
                BindInt(cmd, "@favorited", item.Favorited ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Gets all sources.
        /// </summary>
        public async Task<List<Source>> GetAllSourcesAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, url, type, enabled, show_as_collection,
                       max_items, sync_interval_hours, last_synced_at,
                       emby_collection_id, collection_name, created_at, updated_at
                FROM sources
                ORDER BY name;";

            return await QueryListAsync(sql, null, ReadSource);
        }

        /// <summary>
        /// Gets enabled sources only.
        /// </summary>
        public async Task<List<Source>> GetEnabledSourcesAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, url, type, enabled, show_as_collection,
                       max_items, sync_interval_hours, last_synced_at,
                       emby_collection_id, collection_name, created_at, updated_at
                FROM sources
                WHERE enabled = 1
                ORDER BY name;";

            return await QueryListAsync(sql, null, ReadSource);
        }

        /// <summary>
        /// Gets sources with ShowAsCollection = true.
        /// </summary>
        public async Task<List<Source>> GetSourcesWithShowAsCollectionAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, url, type, enabled, show_as_collection,
                       max_items, sync_interval_hours, last_synced_at,
                       emby_collection_id, collection_name, created_at, updated_at
                FROM sources
                WHERE show_as_collection = 1
                ORDER BY name;";

            return await QueryListAsync(sql, null, ReadSource);
        }

        /// <summary>
        /// Sets the enabled state of a source.
        /// </summary>
        public async Task SetSourceEnabledAsync(string sourceId, bool enabled, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE sources
                SET enabled = @Enabled,
                    updated_at = datetime('now')
                WHERE id = @SourceId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@SourceId", sourceId);
                cmd.BindParameters["@Enabled"].Bind(enabled ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Sets the ShowAsCollection flag for a source.
        /// </summary>
        public async Task SetSourceShowAsCollectionAsync(string sourceId, bool show, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE sources
                SET show_as_collection = @Show,
                    updated_at = datetime('now')
                WHERE id = @SourceId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@SourceId", sourceId);
                cmd.BindParameters["@Show"].Bind(show ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Gets a single source by ID.
        /// </summary>
        public async Task<Source?> GetSourceAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, url, type, enabled, show_as_collection,
                       max_items, sync_interval_hours, last_synced_at,
                       emby_collection_id, collection_name, created_at, updated_at
                FROM sources
                WHERE id = @SourceId;";

            return await QuerySingleAsync(sql, cmd => BindText(cmd, "@SourceId", sourceId), ReadSource);
        }

        /// <summary>
        /// Upserts a source (inserts or updates).
        /// </summary>
        public async Task UpsertSourceAsync(Source source, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO sources (id, name, url, type, enabled, show_as_collection,
                                   max_items, sync_interval_hours, last_synced_at,
                                   emby_collection_id, collection_name, created_at, updated_at)
                VALUES (@Id, @Name, @Url, @Type, @Enabled, @ShowAsCollection,
                        @MaxItems, @SyncIntervalHours, @LastSyncedAt,
                        @EmbyCollectionId, @CollectionName, @CreatedAt, @UpdatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    name = @Name,
                    url = @Url,
                    type = @Type,
                    enabled = @Enabled,
                    show_as_collection = @ShowAsCollection,
                    max_items = @MaxItems,
                    sync_interval_hours = @SyncIntervalHours,
                    last_synced_at = @LastSyncedAt,
                    emby_collection_id = @EmbyCollectionId,
                    collection_name = @CollectionName,
                    updated_at = @UpdatedAt;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", source.Id);
                BindText(cmd, "@Name", source.Name);
                BindText(cmd, "@Url", source.Url);
                BindText(cmd, "@Type", source.Type.ToString());
                cmd.BindParameters["@Enabled"].Bind(source.Enabled ? 1 : 0);
                cmd.BindParameters["@ShowAsCollection"].Bind(source.ShowAsCollection ? 1 : 0);
                BindInt(cmd, "@MaxItems", source.MaxItems);
                BindInt(cmd, "@SyncIntervalHours", source.SyncIntervalHours);
                BindText(cmd, "@LastSyncedAt", source.LastSyncedAt?.ToString("o"));
                BindText(cmd, "@EmbyCollectionId", source.EmbyCollectionId);
                BindText(cmd, "@CollectionName", source.CollectionName);
                BindText(cmd, "@CreatedAt", source.CreatedAt.ToString("o"));
                BindText(cmd, "@UpdatedAt", source.UpdatedAt.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes a source by ID.
        /// </summary>
        public async Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"DELETE FROM sources WHERE id = @SourceId;";
            await ExecuteWriteAsync(sql, cmd => BindText(cmd, "@SourceId", sourceId), cancellationToken);
        }

        // ── Log Insert Methods (Sprint 120) ─────────────────────────────────────────

        /// <summary>
        /// Inserts a pipeline log entry.
        /// </summary>
        public Task InsertPipelineLogAsync(Models.PipelineLogEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO item_pipeline_log (primary_id, primary_id_type, media_type, phase, trigger, success, details, timestamp)
                VALUES (@PrimaryId, @PrimaryIdType, @MediaType, @Phase, @Trigger, @Success, @Details, @Timestamp);";

            return ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@PrimaryId", entry.PrimaryId);
                BindText(cmd, "@PrimaryIdType", entry.PrimaryIdType);
                BindText(cmd, "@MediaType", entry.MediaType);
                BindText(cmd, "@Phase", entry.Phase);
                BindText(cmd, "@Trigger", entry.Trigger);
                cmd.BindParameters["@Success"].Bind(entry.Success ? 1 : 0);
                BindNullableText(cmd, "@Details", entry.Details);
                BindText(cmd, "@Timestamp", entry.Timestamp.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Inserts a resolution log entry.
        /// </summary>
        public Task InsertResolutionLogAsync(Models.ResolutionLogEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO stream_resolution_log (primary_id, primary_id_type, media_type, media_id, stream_count, selected_stream, duration_ms, timestamp)
                VALUES (@PrimaryId, @PrimaryIdType, @MediaType, @MediaId, @StreamCount, @SelectedStream, @DurationMs, @Timestamp);";

            return ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@PrimaryId", entry.PrimaryId);
                BindText(cmd, "@PrimaryIdType", entry.PrimaryIdType);
                BindText(cmd, "@MediaType", entry.MediaType);
                BindText(cmd, "@MediaId", entry.MediaId);
                BindInt(cmd, "@StreamCount", entry.StreamCount);
                BindNullableText(cmd, "@SelectedStream", entry.SelectedStream);
                BindInt(cmd, "@DurationMs", (int)entry.DurationMs);
                BindText(cmd, "@Timestamp", entry.Timestamp.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Gets pipeline log count.
        /// </summary>
        public Task<int> GetPipelineLogCountAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"SELECT COUNT(*) FROM item_pipeline_log;";

            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var stmt = conn.PrepareStatement(sql);
                var rows = stmt.AsRows();
                foreach (var row in rows)
                {
                    return row.GetInt(0);
                }
                return 0;
            }, cancellationToken);
        }

        /// <summary>
        /// Gets resolution log count.
        /// </summary>
        public Task<int> GetResolutionLogCountAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"SELECT COUNT(*) FROM stream_resolution_log;";

            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var stmt = conn.PrepareStatement(sql);
                var rows = stmt.AsRows();
                foreach (var row in rows)
                {
                    return row.GetInt(0);
                }
                return 0;
            }, cancellationToken);
        }

        /// <summary>
        /// Prunes pipeline logs before the specified timestamp.
        /// </summary>
        public Task<int> PrunePipelineLogsAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
        {
            const string sql = @"DELETE FROM item_pipeline_log WHERE timestamp < @Before;";

            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                var beforeText = before.ToString("o");
                conn.RunInTransaction(c =>
                {
                    using var stmt = c.PrepareStatement(sql);
                    BindText(stmt, "@Before", beforeText);
                    while (stmt.MoveNext()) { }
                });
                return conn.Changes;
            }, cancellationToken);
        }

        /// <summary>
        /// Prunes resolution logs before the specified timestamp.
        /// </summary>
        public Task<int> PruneResolutionLogsAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
        {
            const string sql = @"DELETE FROM stream_resolution_log WHERE timestamp < @Before;";

            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                var beforeText = before.ToString("o");
                conn.RunInTransaction(c =>
                {
                    using var stmt = c.PrepareStatement(sql);
                    BindText(stmt, "@Before", beforeText);
                    while (stmt.MoveNext()) { }
                });
                return conn.Changes;
            }, cancellationToken);
        }

        // ── Log Query Methods (Sprint 120) ─────────────────────────────────────────

        /// <summary>
        /// Gets pipeline logs with optional filters (for API).
        /// </summary>
        public Task<List<Models.PipelineLogEntry>> GetPipelineLogsAsync(
            string? primaryId,
            string? primaryIdType,
            string? mediaType,
            string? trigger,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var whereClauses = new List<string>();
            if (!string.IsNullOrEmpty(primaryId))
                whereClauses.Add("primary_id = @PrimaryId");
            if (!string.IsNullOrEmpty(primaryIdType))
                whereClauses.Add("primary_id_type = @PrimaryIdType");
            if (!string.IsNullOrEmpty(mediaType))
                whereClauses.Add("media_type = @MediaType");
            if (!string.IsNullOrEmpty(trigger))
                whereClauses.Add("trigger = @Trigger");

            var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            var sql = $@"
                SELECT primary_id, primary_id_type, media_type, phase, trigger, success, details, timestamp
                FROM item_pipeline_log
                {whereClause}
                ORDER BY timestamp DESC
                LIMIT @Limit;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (!string.IsNullOrEmpty(primaryId))
                BindText(stmt, "@PrimaryId", primaryId);
            if (!string.IsNullOrEmpty(primaryIdType))
                BindText(stmt, "@PrimaryIdType", primaryIdType);
            if (!string.IsNullOrEmpty(mediaType))
                BindText(stmt, "@MediaType", mediaType);
            if (!string.IsNullOrEmpty(trigger))
                BindText(stmt, "@Trigger", trigger);
            BindInt(stmt, "@Limit", limit);

            var results = new List<Models.PipelineLogEntry>();
            foreach (var row in stmt.AsRows())
            {
                results.Add(new Models.PipelineLogEntry
                {
                    PrimaryId = row.GetString(0),
                    PrimaryIdType = row.GetString(1),
                    MediaType = row.GetString(2),
                    Phase = row.GetString(3),
                    Trigger = row.GetString(4),
                    Success = row.GetInt(5) == 1,
                    Details = row.IsDBNull(6) ? null : row.GetString(6),
                    Timestamp = DateTimeOffset.Parse(row.GetString(7))
                });
            }
            return Task.FromResult(results);
        }

        /// <summary>
        /// Gets resolution logs with optional filters (for API).
        /// </summary>
        public Task<List<Models.ResolutionLogEntry>> GetResolutionLogsAsync(
            string? primaryId,
            string? primaryIdType,
            string? mediaType,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var whereClauses = new List<string>();
            if (!string.IsNullOrEmpty(primaryId))
                whereClauses.Add("primary_id = @PrimaryId");
            if (!string.IsNullOrEmpty(primaryIdType))
                whereClauses.Add("primary_id_type = @PrimaryIdType");
            if (!string.IsNullOrEmpty(mediaType))
                whereClauses.Add("media_type = @MediaType");

            var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            var sql = $@"
                SELECT primary_id, primary_id_type, media_type, media_id, stream_count, selected_stream, duration_ms, timestamp
                FROM stream_resolution_log
                {whereClause}
                ORDER BY timestamp DESC
                LIMIT @Limit;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (!string.IsNullOrEmpty(primaryId))
                BindText(stmt, "@PrimaryId", primaryId);
            if (!string.IsNullOrEmpty(primaryIdType))
                BindText(stmt, "@PrimaryIdType", primaryIdType);
            if (!string.IsNullOrEmpty(mediaType))
                BindText(stmt, "@MediaType", mediaType);
            BindInt(stmt, "@Limit", limit);

            var results = new List<Models.ResolutionLogEntry>();
            foreach (var row in stmt.AsRows())
            {
                results.Add(new Models.ResolutionLogEntry
                {
                    PrimaryId = row.GetString(0),
                    PrimaryIdType = row.GetString(1),
                    MediaType = row.GetString(2),
                    MediaId = row.GetString(3),
                    StreamCount = row.GetInt(4),
                    SelectedStream = row.IsDBNull(5) ? null : row.GetString(5),
                    DurationMs = row.GetInt(6),
                    Timestamp = DateTimeOffset.Parse(row.GetString(7))
                });
            }
            return Task.FromResult(results);
        }

        /// <summary>
        /// Gets recent logs from both pipeline and resolution tables (for API).
        /// </summary>
        public Task<List<Models.RecentLogEntry>> GetRecentLogsAsync(
            string? level,
            int limit,
            CancellationToken cancellationToken = default)
        {
            // Note: Log level filtering is not implemented in DB schema
            // This is a placeholder implementation
            var sql = @"
                SELECT 'pipeline' as log_type, timestamp, phase, success, details
                FROM item_pipeline_log
                UNION ALL
                SELECT 'resolution' as log_type, timestamp, selected_stream, null as details
                FROM stream_resolution_log
                ORDER BY timestamp DESC
                LIMIT @Limit;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindInt(stmt, "@Limit", limit);

            var results = new List<Models.RecentLogEntry>();
            foreach (var row in stmt.AsRows())
            {
                var logType = row.GetString(0);
                var timestamp = DateTimeOffset.Parse(row.GetString(1));
                var message = logType == "pipeline" ? $"Phase: {row.GetString(2)}, Success: {row.GetInt(3) == 1}" : $"Stream: {row.GetString(2)}";
                var details = row.IsDBNull(3) ? null : row.GetString(3);

                results.Add(new Models.RecentLogEntry
                {
                    Timestamp = timestamp,
                    LogType = logType,
                    Level = "info", // Default level since not stored in DB
                    Message = message,
                    Details = details
                });
            }
            return Task.FromResult(results);
        }

        // ── Media Item Operations ───────────────────────────────────────────────────────

        /// <summary>
        /// Sets the superseded flag for a media item.
        /// </summary>
        public async Task SetSupersededAsync(string itemId, bool superseded, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET superseded = @Superseded,
                    updated_at = datetime('now')
                WHERE id = @ItemId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@ItemId", itemId);
                cmd.BindParameters["@Superseded"].Bind(superseded ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Sets the superseded_conflict flag for a media item.
        /// </summary>
        public async Task SetSupersededConflictAsync(string itemId, bool supersededConflict, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET superseded_conflict = @SupersededConflict,
                    updated_at = datetime('now')
                WHERE id = @ItemId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@ItemId", itemId);
                cmd.BindParameters["@SupersededConflict"].Bind(supersededConflict ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Sets the superseded_at timestamp for a media item.
        /// </summary>
        public async Task SetSupersededAtAsync(string itemId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET superseded_at = @SupersededAt,
                    updated_at = datetime('now')
                WHERE id = @ItemId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@ItemId", itemId);
                BindText(cmd, "@SupersededAt", timestamp.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Upserts a source membership.
        /// </summary>
        public async Task UpsertSourceMembershipAsync(string sourceId, string mediaItemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO source_memberships (source_id, media_item_id)
                VALUES (@SourceId, @MediaItemId)
                ON CONFLICT(source_id, media_item_id) DO NOTHING;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@SourceId", sourceId);
                BindText(cmd, "@MediaItemId", mediaItemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes all source memberships for a source.
        /// </summary>
        public async Task DeleteSourceMembershipsForSourceAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM source_memberships
                WHERE source_id = @SourceId;";

            await ExecuteWriteAsync(sql,
                cmd => BindText(cmd, "@SourceId", sourceId),
                cancellationToken);
        }

        // ── user_catalogs CRUD (Sprint 158) ─────────────────────────────────────

        /// <summary>
        /// Inserts a new user catalog row. Returns the generated UUID.
        /// </summary>
        public async Task<string> CreateUserCatalogAsync(
            string ownerUserId,
            string service,
            string listUrl,
            string displayName,
            CancellationToken ct = default)
        {
            var id = Guid.NewGuid().ToString();
            const string sql = @"
                INSERT INTO user_catalogs
                    (id, owner_user_id, source_type, service, list_url, display_name, active, created_at)
                VALUES
                    (@id, @owner_user_id, 'external_list', @service, @list_url, @display_name, 1, @created_at);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", id);
                BindText(cmd, "@owner_user_id", ownerUserId);
                BindText(cmd, "@service", service);
                BindText(cmd, "@list_url", listUrl);
                BindText(cmd, "@display_name", displayName);
                BindText(cmd, "@created_at", DateTimeOffset.UtcNow.ToString("o"));
            }, ct);

            return id;
        }

        /// <summary>
        /// Returns user catalogs for the given owner. Pass activeOnly=true to filter inactive rows.
        /// </summary>
        public Task<IReadOnlyList<Models.UserCatalog>> GetUserCatalogsByOwnerAsync(
            string ownerUserId,
            bool activeOnly,
            CancellationToken ct = default)
        {
            var sql = activeOnly
                ? "SELECT id, owner_user_id, source_type, service, list_url, display_name, active, last_synced_at, last_sync_status, created_at FROM user_catalogs WHERE owner_user_id = @owner_user_id AND active = 1 ORDER BY created_at;"
                : "SELECT id, owner_user_id, source_type, service, list_url, display_name, active, last_synced_at, last_sync_status, created_at FROM user_catalogs WHERE owner_user_id = @owner_user_id ORDER BY created_at;";

            var list = QueryListAsync(sql,
                cmd => BindText(cmd, "@owner_user_id", ownerUserId),
                ReadUserCatalog);
            return Task.FromResult<IReadOnlyList<Models.UserCatalog>>(list.Result);
        }

        /// <summary>
        /// Returns all active user catalogs across all users (used by CatalogSyncTask).
        /// </summary>
        public Task<IReadOnlyList<Models.UserCatalog>> GetAllActiveUserCatalogsAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, owner_user_id, source_type, service, list_url, display_name, active,
                       last_synced_at, last_sync_status, created_at
                FROM user_catalogs WHERE active = 1 ORDER BY created_at;";

            var list = QueryListAsync(sql, null, ReadUserCatalog);
            return Task.FromResult<IReadOnlyList<Models.UserCatalog>>(list.Result);
        }

        /// <summary>
        /// Returns a single user catalog by ID, or null if not found.
        /// </summary>
        public Task<Models.UserCatalog?> GetUserCatalogByIdAsync(string catalogId, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, owner_user_id, source_type, service, list_url, display_name, active,
                       last_synced_at, last_sync_status, created_at
                FROM user_catalogs WHERE id = @id;";

            return QuerySingleAsync(sql,
                cmd => BindText(cmd, "@id", catalogId),
                ReadUserCatalog);
        }

        /// <summary>
        /// Returns total count of active user catalogs across all non-SERVER owners.
        /// Used by the admin Lists tab to warn when reducing the per-user limit.
        /// </summary>
        public async Task<int> GetActiveUserCatalogCountAsync()
        {
            const string sql = "SELECT COUNT(*) FROM user_catalogs WHERE active = 1 AND owner_user_id <> 'SERVER';";
            return await QueryScalarIntAsync(sql);
        }

        private static Models.UserCatalog ReadUserCatalog(IResultSet row) =>
            new Models.UserCatalog
            {
                Id             = row.GetString(0),
                OwnerUserId    = row.GetString(1),
                SourceType     = row.GetString(2),
                Service        = row.GetString(3),
                ListUrl        = row.GetString(4),
                DisplayName    = row.GetString(5),
                Active         = row.GetInt(6) == 1,
                LastSyncedAt   = row.IsDBNull(7) ? null : row.GetString(7),
                LastSyncStatus = row.IsDBNull(8) ? null : row.GetString(8),
                CreatedAt      = row.GetString(9),
            };

        /// <summary>
        /// Sets the active flag on a user catalog (soft-delete or restore).
        /// </summary>
        public async Task SetUserCatalogActiveAsync(string catalogId, bool active, CancellationToken ct = default)
        {
            const string sql = "UPDATE user_catalogs SET active = @active WHERE id = @id;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindInt(cmd, "@active", active ? 1 : 0);
                BindText(cmd, "@id", catalogId);
            }, ct);
        }

        /// <summary>
        /// Updates last_synced_at and last_sync_status after a sync run.
        /// </summary>
        public async Task UpdateUserCatalogSyncStatusAsync(
            string catalogId,
            DateTimeOffset syncedAt,
            string status,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE user_catalogs
                SET last_synced_at = @synced_at, last_sync_status = @status
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@synced_at", syncedAt.ToString("o"));
                BindText(cmd, "@status", status);
                BindText(cmd, "@id", catalogId);
            }, ct);
        }

        /// <summary>
        /// Returns the count of active source memberships for a catalog item.
        /// Counts system-catalog memberships plus user-catalog memberships whose
        /// user_catalog is still active. Used by Deep Clean / deprecation flow.
        /// </summary>
        public Task<int> CountActiveClaimsAsync(string catalogItemId, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT COUNT(*) FROM source_memberships sm
                WHERE sm.media_item_id = @item_id
                  AND (sm.user_catalog_id IS NULL
                   OR EXISTS (SELECT 1 FROM user_catalogs uc
                              WHERE uc.id = sm.user_catalog_id AND uc.active = 1));";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@item_id", catalogItemId);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Upserts a source membership, optionally linking it to a user catalog.
        /// Pass null for userCatalogId for system-catalog memberships.
        /// </summary>
        public async Task UpsertSourceMembershipWithCatalogAsync(
            string sourceId,
            string mediaItemId,
            string? userCatalogId,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO source_memberships (source_id, media_item_id, user_catalog_id)
                VALUES (@SourceId, @MediaItemId, @UserCatalogId)
                ON CONFLICT(source_id, media_item_id) DO UPDATE SET
                    user_catalog_id = COALESCE(excluded.user_catalog_id, source_memberships.user_catalog_id);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@SourceId", sourceId);
                BindText(cmd, "@MediaItemId", mediaItemId);
                BindNullableText(cmd, "@UserCatalogId", userCatalogId);
            }, ct);
        }

        /// <summary>
        /// Gets all collection as entities.
        /// </summary>
        public async Task<List<Models.Collection>> GetAllCollectionsListAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, emby_collection_id, source_id,
                       enabled, collection_name, last_synced_at, created_at, updated_at
                FROM collections
                ORDER BY name;";

            return await QueryListAsync(sql, null, ReadCollection);
        }

        /// <summary>
        /// Gets a collection by source ID.
        /// </summary>
        public async Task<Models.Collection?> GetCollectionBySourceIdAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, emby_collection_id, source_id,
                       enabled, collection_name, last_synced_at, created_at, updated_at
                FROM collections
                WHERE source_id = @SourceId
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@SourceId", sourceId),
                ReadCollection);
        }

        /// <summary>
        /// Upserts a collection.
        /// </summary>
        public async Task UpsertCollectionAsync(Models.Collection collection, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO collections (id, name, emby_collection_id, source_id, enabled, collection_name, last_synced_at, created_at, updated_at)
                VALUES (@Id, @Name, @EmbyCollectionId, @SourceId, @Enabled, @CollectionName, @LastSyncedAt, @CreatedAt, @UpdatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    emby_collection_id = excluded.emby_collection_id,
                    source_id = excluded.source_id,
                    enabled = excluded.enabled,
                    collection_name = excluded.collection_name,
                    last_synced_at = excluded.last_synced_at,
                    updated_at = excluded.updated_at;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", collection.Id);
                BindText(cmd, "@Name", collection.Name);
                BindNullableText(cmd, "@EmbyCollectionId", collection.EmbyCollectionId);
                BindNullableText(cmd, "@SourceId", collection.SourceId);
                cmd.BindParameters["@Enabled"].Bind(collection.Enabled ? 1 : 0);
                BindNullableText(cmd, "@CollectionName", collection.CollectionName);
                BindNullableText(cmd, "@LastSyncedAt", collection.LastSyncedAt?.ToString("o"));
                BindText(cmd, "@CreatedAt", collection.CreatedAt.ToString("o"));
                BindText(cmd, "@UpdatedAt", collection.UpdatedAt.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes a collection by ID.
        /// </summary>
        public async Task DeleteCollectionAsync(string collectionId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM collections
                WHERE id = @Id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", collectionId);
            }, cancellationToken);
        }

        /// <summary>
        /// Logs a pipeline event.
        /// </summary>
        public async Task LogPipelineEventAsync(
            string primaryId,
            string primaryIdType,
            string mediaType,
            string triggerType,
            string? fromStatus,
            string? toStatus,
            bool success,
            string? errorMessage = null,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO item_pipeline_log
                    (primary_id, primary_id_type, media_type, trigger_type, from_status, to_status, result, error_message)
                VALUES
                    (@PrimaryId, @PrimaryIdType, @MediaType, @TriggerType, @FromStatus, @ToStatus, @Result, @ErrorMessage);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@PrimaryId", primaryId);
                BindText(cmd, "@PrimaryIdType", primaryIdType);
                BindText(cmd, "@MediaType", mediaType);
                BindText(cmd, "@TriggerType", triggerType.ToLowerInvariant());
                BindNullableText(cmd, "@FromStatus", fromStatus?.ToLowerInvariant());
                BindNullableText(cmd, "@ToStatus", toStatus?.ToLowerInvariant());
                BindText(cmd, "@Result", success ? "success" : "failed");
                BindNullableText(cmd, "@ErrorMessage", errorMessage);
            }, cancellationToken);
        }

        // ── Reader methods for new schema types ───────────────────────────────────────────────

        private static MediaItem ReadMediaItem(IResultSet r)
        {
            var idTypeStr = r.GetString(1);
            var idValue = r.GetString(2);
            var idType = MediaIdTypeExtensions.Parse(idTypeStr);

            return new MediaItem
            {
                Id = r.GetString(0),
                PrimaryId = new MediaId(idType, idValue),
                MediaType = r.GetString(3),
                Title = r.GetString(4),
                Year = r.IsDBNull(5) ? null : (int?)r.GetInt(5),
                Status = Enum.Parse<ItemStatus>(r.GetString(6), ignoreCase: true),
                FailureReason = r.IsDBNull(7) ? FailureReason.None : Enum.Parse<FailureReason>(r.GetString(7), ignoreCase: true),
                Saved = r.GetInt(8) == 1,
                SavedAt = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9)),
                Blocked = r.GetInt(10) == 1,
                BlockedAt = r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)),
                CreatedAt = DateTimeOffset.Parse(r.GetString(12)),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(13)),
                GraceStartedAt = r.IsDBNull(14) ? null : DateTimeOffset.Parse(r.GetString(14)),
                Superseded = r.GetInt(15) == 1,
                SupersededConflict = r.GetInt(16) == 1,
                SupersededAt = r.IsDBNull(17) ? null : DateTimeOffset.Parse(r.GetString(17)),
                EmbyItemId = r.GetString(18),
                EmbyIndexedAt = r.IsDBNull(19) ? null : DateTimeOffset.Parse(r.GetString(19)),
                StrmPath = r.GetString(20),
                NfoPath = r.GetString(21),
                WatchProgressPct = r.GetInt(22),
                Favorited = r.GetInt(23) == 1
            };
        }

        private static Source ReadSource(IResultSet r)
        {
            return new Source
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Url = r.GetString(2),
                Type = Models.SourceTypeExtensions.Parse(r.GetString(3)),
                Enabled = r.GetInt(4) == 1,
                ShowAsCollection = r.GetInt(5) == 1,
                MaxItems = r.GetInt(6),
                SyncIntervalHours = r.GetInt(7),
                LastSyncedAt = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)),
                EmbyCollectionId = r.GetString(9),
                CollectionName = r.GetString(10),
                CreatedAt = DateTimeOffset.Parse(r.GetString(11)),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(12))
            };
        }

        private static Models.Collection ReadCollection(IResultSet r)
        {
            return new Models.Collection
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                EmbyCollectionId = r.GetString(2),
                SourceId = r.GetString(3),
                Enabled = r.GetInt(4) == 1,
                CollectionName = r.IsDBNull(5) ? null : r.GetString(5),
                LastSyncedAt = r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
                CreatedAt = DateTimeOffset.Parse(r.GetString(7)),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(8))
            };
        }

        /// <summary>
        /// Gets all media items from database.
        /// </summary>
        public async Task<List<MediaItem>> GetAllMediaItemsAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id_value, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items;";

            return await QueryListAsync(sql, null, ReadMediaItem);
        }

        /// <summary>
        /// Checks if a media item exists by primary ID.
        /// </summary>
        public Task<bool> MediaItemExistsByPrimaryIdAsync(MediaId mediaId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT 1 FROM media_items
                WHERE primary_id_type = @IdType
                  AND primary_id_value = @IdValue
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@IdType", mediaId.Type.ToLowerString());
            BindText(stmt, "@IdValue", mediaId.Value);
            return Task.FromResult(stmt.AsRows().Any());
        }

        // ── Stream cache operations (Sprint 112B) ───────────────────────────────────

        /// <summary>
        /// Gets cached stream entry for a media ID.
        /// </summary>
        public Task<(string? Url, string? UrlSecondary)> GetCachedStreamAsync(string mediaId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT url, url_secondary
                FROM stream_cache
                WHERE media_id = @MediaId
                  AND expires_at > datetime('now');";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@MediaId", mediaId);

            foreach (var row in stmt.AsRows())
            {
                var url = row.IsDBNull(0) ? null : row.GetString(0);
                var urlSecondary = row.IsDBNull(1) ? null : row.GetString(1);
                return Task.FromResult((url, urlSecondary));
            }

            return Task.FromResult<(string?, string?)>((null, null));
        }

        /// <summary>
        /// Sets primary cached URL for a media ID.
        /// </summary>
        public async Task SetCachedStreamPrimaryAsync(string mediaId, string url, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO stream_cache (media_id, url, created_at, expires_at)
                VALUES (@MediaId, @Url, datetime('now'), @ExpiresAt)
                ON CONFLICT(media_id) DO UPDATE SET
                    url = excluded.url,
                    created_at = datetime('now'),
                    expires_at = excluded.expires_at;";

            var expiresAt = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromHours(24));

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@MediaId", mediaId);
                BindText(cmd, "@Url", url);
                BindText(cmd, "@ExpiresAt", expiresAt.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Sets secondary cached URL for a media ID.
        /// </summary>
        public async Task SetCachedStreamSecondaryAsync(string mediaId, string url, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE stream_cache
                SET url_secondary = @Url,
                    expires_at = @ExpiresAt
                WHERE media_id = @MediaId;";

            var expiresAt = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromHours(24));

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@MediaId", mediaId);
                BindText(cmd, "@Url", url);
                BindText(cmd, "@ExpiresAt", expiresAt.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes cached stream entry for a media ID.
        /// </summary>
        public async Task DeleteCachedStreamAsync(string mediaId, CancellationToken cancellationToken = default)
        {
            const string sql = "DELETE FROM stream_cache WHERE media_id = @MediaId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@MediaId", mediaId);
            }, cancellationToken);
        }

        /// <summary>
        /// Purges all expired cache entries.
        /// </summary>
        public async Task PurgeExpiredCacheAsync(CancellationToken cancellationToken = default)
        {
            const string sql = "DELETE FROM stream_cache WHERE expires_at <= datetime('now');";

            await ExecuteWriteAsync(sql, _ => { }, cancellationToken);
            _logger?.LogInformation("[DatabaseManager] Purged expired cache entries");
        }

        #region Home Section Tracking (Sprint 118C)

        /// <summary>
        /// Inserts a new home section tracking record.
        /// Sprint 118: Home Screen Rails.
        /// </summary>
        public async Task InsertHomeSectionTrackingAsync(
            InfiniteDrive.Models.HomeSectionTracking tracking,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO home_section_tracking
                    (id, user_id, rail_type, emby_section_id, section_marker, created_at, updated_at)
                VALUES (@id, @user_id, @rail_type, @emby_section_id, @section_marker, @created_at, @updated_at);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", tracking.Id);
                BindText(cmd, "@user_id", tracking.UserId);
                BindText(cmd, "@rail_type", tracking.RailType);
                BindNullableText(cmd, "@emby_section_id", tracking.EmbySectionId);
                BindText(cmd, "@section_marker", tracking.SectionMarker);
                BindText(cmd, "@created_at", tracking.CreatedAt.ToString("o"));
                BindText(cmd, "@updated_at", tracking.UpdatedAt.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Gets a home section tracking record by user ID and rail type.
        /// Sprint 118: Home Screen Rails.
        /// </summary>
        public async Task<InfiniteDrive.Models.HomeSectionTracking?> GetHomeSectionTrackingAsync(
            string userId,
            string railType,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, user_id, rail_type, emby_section_id, section_marker, created_at, updated_at
                FROM home_section_tracking
                WHERE user_id = @user_id AND rail_type = @rail_type
                LIMIT 1;";

            var results = await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@user_id", userId);
                BindText(cmd, "@rail_type", railType);
            }, row => new InfiniteDrive.Models.HomeSectionTracking
            {
                Id = row.GetString(0),
                UserId = row.GetString(1),
                RailType = row.GetString(2),
                EmbySectionId = row.IsDBNull(3) ? null : row.GetString(3),
                SectionMarker = row.GetString(4),
                CreatedAt = DateTimeOffset.Parse(row.GetString(5)),
                UpdatedAt = DateTimeOffset.Parse(row.GetString(6))
            });

            return results.FirstOrDefault();
        }

        /// <summary>
        /// Updates an existing home section tracking record.
        /// Sprint 118: Home Screen Rails.
        /// </summary>
        public async Task UpdateHomeSectionTrackingAsync(
            InfiniteDrive.Models.HomeSectionTracking tracking,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE home_section_tracking
                SET emby_section_id = @emby_section_id,
                    updated_at = @updated_at
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", tracking.Id);
                BindNullableText(cmd, "@emby_section_id", tracking.EmbySectionId);
                BindText(cmd, "@updated_at", tracking.UpdatedAt.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Gets all home section tracking records for a user.
        /// Sprint 118: Home Screen Rails.
        /// </summary>
        public async Task<List<InfiniteDrive.Models.HomeSectionTracking>> GetAllHomeSectionTrackingAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, user_id, rail_type, emby_section_id, section_marker, created_at, updated_at
                FROM home_section_tracking
                WHERE user_id = @user_id
                ORDER BY rail_type;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@user_id", userId);
            }, row => new InfiniteDrive.Models.HomeSectionTracking
            {
                Id = row.GetString(0),
                UserId = row.GetString(1),
                RailType = row.GetString(2),
                EmbySectionId = row.IsDBNull(3) ? null : row.GetString(3),
                SectionMarker = row.GetString(4),
                CreatedAt = DateTimeOffset.Parse(row.GetString(5)),
                UpdatedAt = DateTimeOffset.Parse(row.GetString(6))
            });
        }

        #endregion

        // ── Version Slots ────────────────────────────────────────────────────────

        /// <summary>Returns all version slot rows ordered by sort_order.</summary>
        public Task<List<InfiniteDrive.Models.VersionSlot>> GetVersionSlotsAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT slot_key, label, resolution, hdr_classes, enabled, is_default, sort_order
                FROM version_slots
                ORDER BY sort_order;";

            return QueryListAsync(sql, null, row => new InfiniteDrive.Models.VersionSlot
            {
                SlotKey    = row.GetString(0),
                Label      = row.GetString(1),
                Resolution = row.GetString(2),
                HdrClasses = row.IsDBNull(3) ? string.Empty : row.GetString(3),
                Enabled    = row.GetInt(4) != 0,
                IsDefault  = row.GetInt(5) != 0,
                SortOrder  = row.GetInt(6),
            });
        }

        /// <summary>Enables or disables a single version slot by key.</summary>
        public Task SetVersionSlotEnabledAsync(string slotKey, bool enabled, CancellationToken ct = default)
        {
            const string sql = "UPDATE version_slots SET enabled = @enabled, updated_at = datetime('now') WHERE slot_key = @slot_key AND is_default = 0;";
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindInt(stmt, "@enabled", enabled ? 1 : 0);
            BindText(stmt, "@slot_key", slotKey);
            stmt.MoveNext();
            return Task.CompletedTask;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  cached_streams — pre-cached stream metadata
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves the TMDB ID for an IMDB ID by joining media_item_ids.
        /// Returns null if no TMDB ID is found.
        /// </summary>
        public async Task<string?> GetTmdbIdForImdbAsync(string imdbId)
        {
            const string sql = @"
                SELECT tmdb_ids.id_value
                FROM media_item_ids AS imdb_ids
                INNER JOIN media_item_ids AS tmdb_ids
                    ON tmdb_ids.media_item_id = imdb_ids.media_item_id
                    AND tmdb_ids.id_type = 'tmdb'
                WHERE lower(imdb_ids.id_type) = 'imdb'
                  AND lower(imdb_ids.id_value) = lower(@imdb_id)
                LIMIT 1";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@imdb_id", imdbId),
                r => r.GetString(0)).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a cached stream entry by its TMDB key.
        /// Returns null if not found or expired.
        /// </summary>
        public async Task<CachedStreamEntry?> GetCachedStreamsAsync(string tmdbKey)
        {
            const string sql = @"
                SELECT tmdb_key, imdb_id, media_type, season, episode, item_id,
                       variants, cached_at, expires_at, status
                FROM cached_streams
                WHERE tmdb_key = @tmdb_key
                  AND status = 'valid'
                  AND expires_at > datetime('now')";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@tmdb_key", tmdbKey),
                ReadCachedStreamEntry).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a cached stream entry by IMDB ID (+ optional season/episode).
        /// Fallback when TMDB key is not known.
        /// </summary>
        public async Task<CachedStreamEntry?> GetCachedStreamsByImdbAsync(
            string imdbId, int? season, int? episode)
        {
            const string sql = @"
                SELECT tmdb_key, imdb_id, media_type, season, episode, item_id,
                       variants, cached_at, expires_at, status
                FROM cached_streams
                WHERE imdb_id = @imdb_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))
                  AND status = 'valid'
                  AND expires_at > datetime('now')
                LIMIT 1";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindNullableInt(cmd, "@season", season);
                BindNullableInt(cmd, "@episode", episode);
            }, ReadCachedStreamEntry).ConfigureAwait(false);
        }

        /// <summary>Inserts or replaces a cached stream entry.</summary>
        public async Task UpsertCachedStreamAsync(CachedStreamEntry entry)
        {
            const string sql = @"
                INSERT OR REPLACE INTO cached_streams
                    (tmdb_key, imdb_id, media_type, season, episode, item_id,
                     variants, cached_at, expires_at, status)
                VALUES
                    (@tmdb_key, @imdb_id, @media_type, @season, @episode, @item_id,
                     @variants, @cached_at, @expires_at, @status)";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@tmdb_key", entry.TmdbKey);
                BindText(cmd, "@imdb_id", entry.ImdbId);
                BindText(cmd, "@media_type", entry.MediaType);
                BindNullableInt(cmd, "@season", entry.Season);
                BindNullableInt(cmd, "@episode", entry.Episode);
                BindNullableText(cmd, "@item_id", entry.ItemId);
                BindText(cmd, "@variants", entry.VariantsJson);
                BindText(cmd, "@cached_at", entry.CachedAt);
                BindText(cmd, "@expires_at", entry.ExpiresAt);
                BindText(cmd, "@status", entry.Status);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns media items that have no corresponding cached_streams row.
        /// For movies: one row per movie. For series: expands episodes from
        /// catalog_items.videos_json via join on IMDB.
        /// </summary>
        public async Task<List<UncachedItem>> GetUncachedItemsAsync(
            int limit, CancellationToken ct)
        {
            // Movies: media_items with media_type='movie' that are active and have no cached_streams row
            const string moviesSql = @"
                SELECT DISTINCT
                    ids.id_value AS imdb_id,
                    tmdb_ids.id_value AS tmdb_id,
                    'movie' AS media_type,
                    NULL AS season,
                    NULL AS episode,
                    mi.title
                FROM media_items mi
                INNER JOIN media_item_ids ids
                    ON ids.media_item_id = mi.id AND ids.id_type = 'imdb'
                LEFT JOIN media_item_ids tmdb_ids
                    ON tmdb_ids.media_item_id = mi.id AND tmdb_ids.id_type = 'tmdb'
                WHERE mi.media_type = 'movie'
                  AND mi.status IN ('active','indexed','created')
                  AND mi.blocked = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM cached_streams cs
                      WHERE cs.imdb_id = ids.id_value
                        AND cs.media_type = 'movie'
                        AND cs.status = 'valid'
                  )
                ORDER BY mi.created_at DESC
                LIMIT @limit";

            // Series: get series items and expand their episodes from catalog_items.videos_json
            const string seriesSql = @"
                SELECT DISTINCT
                    ids.id_value AS imdb_id,
                    tmdb_ids.id_value AS tmdb_id,
                    'series' AS media_type,
                    (season_num) AS season,
                    (episode_num) AS episode,
                    mi.title
                FROM media_items mi
                INNER JOIN media_item_ids ids
                    ON ids.media_item_id = mi.id AND ids.id_type = 'imdb'
                LEFT JOIN media_item_ids tmdb_ids
                    ON tmdb_ids.media_item_id = mi.id AND tmdb_ids.id_type = 'tmdb'
                INNER JOIN catalog_items ci
                    ON lower(ci.imdb_id) = lower(ids.id_value)
                CROSS JOIN json_each(
                    CASE
                        WHEN ci.videos_json IS NOT NULL AND ci.videos_json != ''
                        THEN ci.videos_json
                        ELSE '[]'
                    END
                ) AS video
                CROSS JOIN json_extract(video.value, '$.season') AS season_num
                CROSS JOIN json_extract(video.value, '$.episode') AS episode_num
                WHERE mi.media_type = 'series'
                  AND mi.status IN ('active','indexed','created')
                  AND mi.blocked = 0
                  AND season_num IS NOT NULL
                  AND episode_num IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM cached_streams cs
                      WHERE cs.imdb_id = ids.id_value
                        AND cs.media_type = 'series'
                        AND cs.season = season_num
                        AND cs.episode = episode_num
                        AND cs.status = 'valid'
                  )
                ORDER BY mi.created_at DESC
                LIMIT @limit";

            var results = new List<UncachedItem>();

            // Fetch movies
            var movies = await QueryListAsync(moviesSql,
                cmd => BindInt(cmd, "@limit", limit / 2),
                r => new UncachedItem
                {
                    ImdbId = r.GetString(0),
                    TmdbId = r.IsDBNull(1) ? null : r.GetString(1),
                    MediaType = r.GetString(2),
                    Title = r.IsDBNull(5) ? "" : r.GetString(5),
                }).ConfigureAwait(false);
            results.AddRange(movies);

            if (ct.IsCancellationRequested) return results;

            // Fetch series episodes
            var remaining = Math.Max(0, limit - results.Count);
            if (remaining > 0)
            {
                var episodes = await QueryListAsync(seriesSql,
                    cmd => BindInt(cmd, "@limit", remaining),
                    r => new UncachedItem
                    {
                        ImdbId = r.GetString(0),
                        TmdbId = r.IsDBNull(1) ? null : r.GetString(1),
                        MediaType = r.GetString(2),
                        Season = r.IsDBNull(3) ? (int?)null : r.GetInt(3),
                        Episode = r.IsDBNull(4) ? (int?)null : r.GetInt(4),
                        Title = r.IsDBNull(5) ? "" : r.GetString(5),
                    }).ConfigureAwait(false);
                results.AddRange(episodes);
            }

            return results;
        }

        private static CachedStreamEntry ReadCachedStreamEntry(IResultSet r)
        {
            return new CachedStreamEntry
            {
                TmdbKey = r.GetString(0),
                ImdbId = r.GetString(1),
                MediaType = r.GetString(2),
                Season = r.IsDBNull(3) ? (int?)null : r.GetInt(3),
                Episode = r.IsDBNull(4) ? (int?)null : r.GetInt(4),
                ItemId = r.IsDBNull(5) ? null : r.GetString(5),
                VariantsJson = r.GetString(6),
                CachedAt = r.GetString(7),
                ExpiresAt = r.GetString(8),
                Status = r.GetString(9),
            };
        }
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